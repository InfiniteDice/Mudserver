using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MudCore.Entities;
using MudCore.Templates;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using MudServer;
using MudUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DbWriteQueue = global::MudServer.DbWriteQueue;


namespace MudCore.Actions
{
    // Encapsulates admin command logic previously in CommandHandler.
    // Construct with the same runtime dependencies and call from CommandHandler.
    public partial class AdminService
    {
        private readonly World.World _world;
        private readonly SqliteConnection _db;

        private static bool setVerboseServerLogs = false;

        public AdminService(World.World world, SqliteConnection dbConnection)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _db = dbConnection ?? throw new ArgumentNullException(nameof(dbConnection));
        }

        // --------------------------------------------------------------------
        // NEW: admin coin exchange command
        // Syntax:
        //   - exchange <from> to <to>           (convert all of <from> that can be converted)
        //   - exchange <n> <from> to <to>       (convert exactly n units of <from>)
        // Accepts "to" or "for" as separator, and unit names: copper/silver/gold (or c/s/g).
        //
        // Examples:
        //   exchange copper to silver
        //   exchange 234 copper to silver
        //   exchange 5 silver to copper
        //
        // This operates on the invoking player's inventory (admin-only for now).
        // --------------------------------------------------------------------
        public Message? HandleExchange(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null || _world.ItemFactory == null) return MessageHandler.System("Server item factory unavailable.");

            // Tokenize and find separator - allow "to" or "for"
            var toks = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (toks.Count < 3) return MessageHandler.System("Usage: exchange [<n>] <from> to <to>");

            // remove leading "exchange"
            if (string.Equals(toks[0], "exchange", StringComparison.OrdinalIgnoreCase))
                toks.RemoveAt(0);

            int sepIdx = toks.FindIndex(t => string.Equals(t, "to", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "for", StringComparison.OrdinalIgnoreCase));
            if (sepIdx <= 0 || sepIdx >= toks.Count - 1)
                return MessageHandler.System("Usage: exchange [<n>] <from> to <to>");

            // Left side (amount?/from), right side (to unit)
            var left = toks.Take(sepIdx).ToArray();
            var right = toks.Skip(sepIdx + 1).ToArray();

            if (right.Length == 0) return MessageHandler.System("Missing target unit (gold/silver/copper).");

            string toUnit = right[0];
            string fromUnit;
            long requestedUnits = -1; // -1 => convert all available

            if (left.Length == 1)
            {
                // either "copper" or "100" (invalid)
                if (int.TryParse(left[0], out var n))
                    return MessageHandler.System("Specify a unit: exchange <n> <from> to <to>");
                fromUnit = left[0];
            }
            else if (left.Length >= 2)
            {
                // Expect "<n> <from>"
                if (!int.TryParse(left[0], out var n))
                    return MessageHandler.System("Invalid amount. Usage: exchange [<n>] <from> to <to>");
                requestedUnits = Math.Max(0, n);
                fromUnit = left[1];
            }
            else
            {
                return MessageHandler.System("Usage: exchange [<n>] <from> to <to>");
            }

            // Normalize unit names
            bool TryResolveUnit(string token, out string unitKey)
            {
                unitKey = token?.Trim().ToLowerInvariant() ?? "";
                if (unitKey == "c" || unitKey == "copper") { unitKey = "copper"; return true; }
                if (unitKey == "s" || unitKey == "silver") { unitKey = "silver"; return true; }
                if (unitKey == "g" || unitKey == "gold") { unitKey = "gold"; return true; }
                return false;
            }

            if (!TryResolveUnit(fromUnit, out var fromKey))
                return MessageHandler.System($"Unknown unit '{fromUnit}'. Use copper/silver/gold.");

            if (!TryResolveUnit(toUnit, out var toKey))
                return MessageHandler.System($"Unknown unit '{toUnit}'. Use copper/silver/gold.");

            if (fromKey == toKey) return MessageHandler.System("Nothing to do: same unit specified for from/to.");

            // Map to template IDs and copper values
            (string templateId, long copperValue) GetTemplateInfo(string unit)
            {
                return unit switch
                {
                    "copper" => ("coin_copper", 1L),
                    "silver" => ("coin_silver", (long)Currency.CopperPerSilver),
                    "gold" => ("coin_gold", Currency.CopperPerGold),
                    _ => ("", 0L)
                };
            }

            var fromInfo = GetTemplateInfo(fromKey);
            var toInfo = GetTemplateInfo(toKey);

            if (string.IsNullOrEmpty(fromInfo.templateId) || string.IsNullOrEmpty(toInfo.templateId))
                return MessageHandler.System("Unsupported currency unit.");

            // Sum available from-units in player's inventory
            int availableFromUnits = 0;
            try
            {
                availableFromUnits = player.Inventory?
                    .Where(i => i != null && string.Equals(i.TemplateID, fromInfo.templateId, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => Math.Max(1, i.Quantity)) ?? 0;
            }
            catch
            {
                availableFromUnits = 0;
            }

            if (availableFromUnits == 0)
                return MessageHandler.System($"You have no {fromKey} to exchange.");

            long unitsToConvert = requestedUnits < 0 ? availableFromUnits : Math.Min(availableFromUnits, requestedUnits);
            if (unitsToConvert <= 0) return MessageHandler.System("Nothing to convert.");

            // Compute total copper represented by the unitsToConvert
            long totalCopper = unitsToConvert * fromInfo.copperValue;

            // Compute how many whole target units can be produced
            long targetUnits = totalCopper / toInfo.copperValue;
            if (targetUnits == 0)
            {
                // Nothing convertible (e.g., trying to convert 1 silver -> gold)
                return MessageHandler.System($"Not enough {fromKey} to convert to {toKey}.");
            }

            // Compute consumed from-units (integer)
            long consumedFromUnits = (targetUnits * toInfo.copperValue) / fromInfo.copperValue;
            // Safety clamp
            consumedFromUnits = Math.Min(consumedFromUnits, unitsToConvert);

            // Perform inventory modifications:
            // 1) consume consumedFromUnits of 'from' coin from player's inventory
            // 2) add targetUnits of 'to' coin to player's inventory (merge if stackable)
            try
            {
                ConsumeFromCoinStacks(player, fromInfo.templateId, (int)consumedFromUnits);

                // Add target coins: prefer merging into existing stack if present
                AddCoinsToPlayer(player, toInfo.templateId, (int)targetUnits);

                // Persist DB and notify clients
                _world.SaveDirtyRooms(_db); // no-op for inventories but keep consistent (rooms may be marked)
                MessageHandler.EnqueuePlayerUpdate(player);

                // Compute leftover (if the admin requested a specific amount less than available, that amount may leave remainder)
                long leftoverFromUnits = unitsToConvert - consumedFromUnits;
                string msg;
                if (requestedUnits < 0)
                {
                    // Converted as much as possible from all available of 'from'
                    // There may still be leftover from-units that didn't make a full target
                    // e.g., converting all copper: 234c -> 2s and 34c remain (leftoverFromUnits is 34 when from=copper)
                    long leftoverCopper = (availableFromUnits * fromInfo.copperValue) - (targetUnits * toInfo.copperValue);
                    var leftoverCurrency = Currency.FromCopper(leftoverCopper);
                    msg = $"Converted {consumedFromUnits} {fromKey} into {targetUnits} {toKey}. Leftover: {leftoverCurrency.Silver} silver {leftoverCurrency.Copper} copper (if applicable).";
                }
                else
                {
                    // Admin specified exact amountUnits; leftover is relative to that requested amount
                    if (leftoverFromUnits > 0)
                    {
                        msg = $"Converted {consumedFromUnits} {fromKey} into {targetUnits} {toKey}. {leftoverFromUnits} {fromKey} were not convertible and remain.";
                    }
                    else
                    {
                        msg = $"Converted {consumedFromUnits} {fromKey} into {targetUnits} {toKey}.";
                    }
                }

                return MessageHandler.System(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXCHANGE][ERROR] {ex}");
                return MessageHandler.System("Exchange failed due to a server error.");
            }
        }

        // Helper: consume 'unitsToConsume' items of templateId from player's inventory (updates DB + in-memory)
        private void ConsumeFromCoinStacks(Player player, string templateId, int unitsToConsume)
        {
            if (unitsToConsume <= 0) return;
            var stacks = player.Inventory
                .Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.ItemID)
                .ToList();

            int remaining = unitsToConsume;
            foreach (var st in stacks)
            {
                if (remaining <= 0) break;
                int take = Math.Min(st.Quantity, remaining);
                st.Quantity -= take;
                remaining -= take;

                if (st.Quantity <= 0)
                {
                    // remove item
                    MudPersistence.ItemPersistence.DeleteItem(_db, st.ItemID);
                }
                else
                {
                    // persist updated quantity
                    MudPersistence.ItemPersistence.SaveItem(_db, st);
                }
            }

            // remove zero-qty stacks from in-memory inventory
            player.Inventory.RemoveAll(i => i != null && i.Quantity <= 0);
        }

        // Helper: add coin stacks (merge with existing stack when possible)
        private void AddCoinsToPlayer(Player player, string templateId, int unitsToAdd)
        {
            if (unitsToAdd <= 0) return;

            // Try to find existing stack to merge into
            var existing = player.Inventory.FirstOrDefault(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Quantity += unitsToAdd;
                MudPersistence.ItemPersistence.SaveItem(_db, existing);
                return;
            }

            // No existing stack, create a new item via ItemFactory
            var roomId = player.CurrentRoom?.RoomID ?? 0;
            var item = _world.ItemFactory.CreateItem(templateId, "Player", player.DbPlayerID, roomId, unitsToAdd);
            // Ensure owner fields are correct
            item.OwnerType = "Player";
            item.OwnerID = player.DbPlayerID;
            MudPersistence.ItemPersistence.SaveItem(_db, item);

            // Add to player's in-memory inventory (if not present)
            player.Inventory.Add(item);
        }
        // --------------------------------------------------------------------

        /// <summary>
        /// Block an IP address
        /// </summary>
        public Message? HandleBlockIP(Player player, string command)
        {
            if (!player.IsAdmin) return MessageHandler.System("Admin only.");

            var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return MessageHandler.System("Usage: blockip <ip_address> [reason]");

            var ip = parts[1].Split(' ')[0];
            var reason = parts.Length > 1 ? string.Join(" ", parts[1].Split(' ').Skip(1)) : "Admin block";

            MudNetworking.SecurityLogger.BlockIP(ip, reason);
            return MessageHandler.System($"IP {ip} has been blocked.");
        }

        /// <summary>
        /// Unblock an IP address
        /// </summary>
        public Message? HandleUnblockIP(Player player, string command)
        {
            if (!player.IsAdmin) return MessageHandler.System("Admin only.");

            var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return MessageHandler.System("Usage: unblockip <ip_address>");

            var ip = parts[1].Trim();
            MudNetworking.SecurityLogger.UnblockIP(ip);
            return MessageHandler.System($"IP {ip} has been unblocked.");
        }

        public Message? HandleCreateArea(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            string dir = "";
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateArea <Direction>");

            dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            // Prompt for area name
            player.PendingAction = new PendingAction
            {
                Command = "CreateArea",
                Direction = dir
            };

            return MessageHandler.System("Enter name for the new area:");
        }

        public Message? HandleCreateRoom(Player player, string command)
        {
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateRoom <Direction>");

            string dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            if (player.CurrentRoom.Exits.Any(e =>
                    e.Direction.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            {
                return MessageHandler.System($"Cannot create room. An exit already exists in direction {dir}.");
            }

            player.PendingAction = new PendingAction
            {
                Command = "CreateRoom",
                Direction = dir
            };

            return MessageHandler.System("Enter name for the new room:");
        }

        public Message? HandleEditRoom(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null)
                return MessageHandler.System("You are not inside a valid room.");

            player.PendingAction = new PendingAction
            {
                Command = "EditRoom",
                Step = "SelectField"
            };

            return MessageHandler.System(
                $"Editing room: {room.Name}\n" +
                "Choose a field to edit:\n" +
                "1) Name\n" +
                "2) Description\n" +
                "3) DistantDescription\n" +
                "4) Generate Description from Prompt\n" +
                "5) SetRoomFlag\n" +
                "Type the option name/number, or type EndEdit to finish."
            );
        }

        

        // ===== NEW: HandleEditArea =====
        // Syntax: editarea [areaid]
        // If areaid not provided, uses the area of the player's current room.
        // Allows editing: 1) Name, 2) IsPvPAllowed, 3) Climate, 4) Owner
        public Message? HandleEditArea(Player player, string command)
        {
            if (player.CurrentRoom == null)
                return MessageHandler.System("You are not in a room.");

            var area = _world.Areas?.FirstOrDefault(a => a?.AreaID == player.CurrentRoom.AreaID);
            if (area == null)
                return MessageHandler.System("Area not found.");

            player.PendingAction = new PendingAction
            {
                Command = "EditArea",
                Step = "SelectField"
            };

            return MessageHandler.System(
                $"Editing area: {area.Name}\n" +
                "Choose a field to edit:\n" +
                "1) Name\n" +
                "2) IsPvPAllowed\n" +
                "3) Climate\n" +
                "4) Owner\n" +
                "5) MaxNpcs\n" +
                "Type the option name/number, or type EndEdit to finish."
            );
        }

        public Message? HandleEditExit(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: editexit <direction>");

            string dir = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(dir))
                return MessageHandler.System("Usage: editexit <direction>");

            // Normalize direction (store raw string on pending; matching will normalize on lookup)
            player.PendingAction = new PendingAction
            {
                Command = "EditExit",
                Direction = dir,
                Step = "SelectField"
            };

            var room = player.CurrentRoom;
            string exitName = dir;
            if (room != null)
            {
                // Try to resolve a friendly title for the exit if present
                var match = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    exitName = match.Direction;
            }

            // Added option 2: CreateExitToRoom (link this room to a pre-existing room by id).
            return MessageHandler.System(
                $"Editing exit: {exitName}\n" +
                "Choose a field to edit:\n" +
                "1) HasDoor\n" +
                "2) CreateExitToRoom (link an exit from this room to an existing RoomID)\n" +
                "3) EditDoor (edit DoorType and Flags - only available when exit has a door)\n" +
                "Type the option name/number, or type EndEdit to finish."
            );
        }//END HandleEditExit

        public Message? HandleCreateNpc(Player player, string templateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            try
            {
                // Create in-memory NPC via NpcFactory
                var newNpc = _world.NpcFactory.CreateNpc(templateId, room);

                // Persist the NPC and assign NpcID
                int newId = MudPersistence.NpcPersistence.InsertNpc(_db, newNpc);
                newNpc.NpcID = newId;

                // Persist initial TraderState for traders (if any) so Flags column contains TraderState on creation.
                try
                {
                    try
                    {
                        if (MudCore.AI.ItemTraderManager.TryGet(newNpc, out var traderInstance))
                        {
                            try { traderInstance.PersistState(_db); }
                            catch (Exception exPers) { Console.WriteLine($"[ADMIN] Failed to persist TraderState for NpcID={newNpc.NpcID}: {exPers.Message}"); }
                        }
                    }
                    catch { /* best-effort */ }
                }
                catch { /* best-effort */ }

                // Diagnostic: when environment var set, dump DB row + in-memory values to help debug missing fields
                try
                {
                    var diag = (Environment.GetEnvironmentVariable("NPC_CREATE_DIAG") ?? "").Trim();
                    if (diag == "1")
                    {
                        try
                        {
                            Console.WriteLine($"[NPC-DIAG] Created Npc in-memory snapshot: NpcID={newNpc.NpcID} TemplateID='{newNpc.TemplateID}' Name='{newNpc.Name}' RoomID={newNpc.CurrentRoomID}");
                            try { Console.WriteLine($"[NPC-DIAG] In-memory Flags: {System.Text.Json.JsonSerializer.Serialize(newNpc.Flags ?? new System.Collections.Generic.List<string>())}"); } catch { }
                            try { Console.WriteLine($"[NPC-DIAG] In-memory AiBehaviors (legacy strings): {System.Text.Json.JsonSerializer.Serialize(newNpc.AiBehaviors ?? new System.Collections.Generic.List<string>())}"); } catch { }
                            try { Console.WriteLine($"[NPC-DIAG] Template AiBehaviorConfigs: {System.Text.Json.JsonSerializer.Serialize(newNpc.Template?.AiBehaviorConfigs ?? new System.Collections.Generic.List<NpcTemplate.AiBehaviorConfig>())}"); } catch { }
                            try { Console.WriteLine($"[NPC-DIAG] Template.Flags: {System.Text.Json.JsonSerializer.Serialize(newNpc.Template?.Flags ?? new System.Collections.Generic.List<string>())}"); } catch { }

                            using var cmd = _db.CreateCommand();
                            cmd.CommandText = "SELECT * FROM Npc WHERE NpcID = @id;";
                            cmd.Parameters.AddWithValue("@id", newNpc.NpcID);
                            using var rdr = cmd.ExecuteReader();
                            if (rdr.Read())
                            {
                                var cols = new System.Text.StringBuilder();
                                cols.AppendLine($"[NPC-DIAG] DB row for NpcID={newNpc.NpcID}:");
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    try
                                    {
                                        var name = rdr.GetName(i);
                                        object val = rdr.IsDBNull(i) ? "(null)" : rdr.GetValue(i);
                                        cols.AppendLine($"  {name} = {val}");
                                    }
                                    catch (Exception exCol)
                                    {
                                        cols.AppendLine($"  (col {i}) read error: {exCol.Message}");
                                    }
                                }
                                Console.WriteLine(cols.ToString());
                            }
                            else
                            {
                                Console.WriteLine($"[NPC-DIAG] No DB row found for newly inserted NpcID={newNpc.NpcID}.");
                            }
                        }
                        catch (Exception exDiag)
                        {
                            Console.WriteLine($"[NPC-DIAG][ERROR] Diagnostic read failed: {exDiag.Message}");
                        }
                    }
                }
                catch { /* best-effort only; do not block creation */ }

                // Ensure world list contains it
                if (!_world.Npcs.Any(n => n.NpcID == newNpc.NpcID))
                {
                    _world.Npcs.Add(newNpc);
                    Console.WriteLine($"[WORLD][TRACE] NPC created and added: NpcID={newNpc.NpcID} Name='{newNpc.Name}' RoomID={room.RoomID}");
                }
                else
                {
                    Console.WriteLine($"[WORLD][TRACE] NPC already in world list: NpcID={newNpc.NpcID}");
                }

                //// If template specifies equipment, instantiate items and assign to NPC  //No longer instatiating items automatically - use drops instead.
                //try
                //{
                //    var equipTemplates = newNpc.Template?.EquipmentTemplateIds;
                //    if (equipTemplates != null && equipTemplates.Count > 0 && _world.ItemFactory != null)
                //    {
                //        foreach (var equipTemplateId in equipTemplates)
                //        {
                //            try
                //            {
                //                var item = _world.ItemFactory.CreateItem(equipTemplateId, "NPC", newNpc.NpcID, newNpc.CurrentRoom?.RoomID ?? 0);
                //                MudPersistence.ItemPersistence.SaveItem(_db, item);
                //                newNpc.Inventory.Add(item);
                //            }
                //            catch (Exception iex)
                //            {
                //                Console.WriteLine($"[NPC] Failed to create/equip item '{equipTemplateId}' for NPC {newNpc.Name}: {iex.Message}");
                //            }
                //        }
                //    }
                //}
                //catch (Exception exEquip)
                //{
                //    Console.WriteLine($"[NPC] Equipment assignment error for NPC {newNpc.Name}: {exEquip.Message}");
                //}

                // Broadcast updated room so clients immediately see the NPC
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                        _world));
                }

                // send NPC actions to log
                foreach (var n in room.Npcs)
                {
                    if (!string.IsNullOrWhiteSpace(n.LastAction))
                    {
                        foreach (var recipient in room.Players)
                            MessageHandler.SendSystem(recipient, n.LastAction);
                    }
                }

                return MessageHandler.System($"NPC '{newNpc.Name}' created (ID {newId}).");
            }
            catch (KeyNotFoundException)
            {
                return MessageHandler.System($"NPC template not found: {templateId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPC] Failed to create NPC from template {templateId}: {ex}");
                return MessageHandler.System($"Failed to create NPC: {ex.Message}");
            }
        } //end HandleCreateNpc

        // Public: start the EditNpc admin flow (delegates interactive steps to HandlePendingAction)
        // Moves the initial routing from CommandHandler into AdminService so all admin edit UX lives here.
        public Message? HandleEditNpc(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            // Normalize payload after 'editnpc'
            string payload = (command ?? "").Trim();
            if (payload.StartsWith("editnpc", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring("editnpc".Length).Trim();

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // If no argument provided, enter pending flow so admin can type the npc name/template
            if (string.IsNullOrWhiteSpace(payload))
            {
                player.PendingAction = new PendingAction
                {
                    Command = "EditNpc",
                    Step = "SpecifyTarget"
                };
                return MessageHandler.System("Enter NPC name or template to edit (or type 'EndEdit' to cancel):");
            }

            var targetExpr = payload.Trim();
            if (string.IsNullOrWhiteSpace(targetExpr)) return MessageHandler.System("Usage: editnpc <npcname>");

            // Resolve matches in current room (reuse NpcTargeting to keep behavior consistent)
            List<Npc> matches;
            try
            {
                matches = NpcTargeting.FindMatches(room, targetExpr);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][EditNpc] Match lookup failed: {ex.Message}");
                return MessageHandler.System("Failed to resolve NPC matches due to a server error.");
            }

            if (matches == null || matches.Count == 0)
            {
                return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
            }

            if (matches.Count > 1)
            {
                var list = string.Join("\n", matches.Select((m, idx) => $"{idx + 1}) {m.DisplayName}"));
                // Store matches so pending handler can resolve selection
                player.PendingAction = new PendingAction
                {
                    Command = "EditNpc",
                    Step = "SelectTarget",
                    Data = new Dictionary<string, object> { { "Matches", matches } }
                };
                return MessageHandler.System($"Multiple NPCs match '{targetExpr}'. Choose one to edit:\n{list}\nType the number of the NPC to edit, or 'EndEdit' to cancel.");
            }

            // Single match -> present edit menu and set pending to SelectField
            var chosen = matches[0];
            var dict = new Dictionary<string, object> { { "NpcId", chosen.NpcID } };
            player.PendingAction = new PendingAction
            {
                Command = "EditNpc",
                Step = "SelectField",
                Data = dict
            };

            // Add AdminEdit interrupt on chosen NPC so movement is suppressed while admin edits it.
            try
            {
                try { chosen.AddInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name, note: "AdminEdit", ttl: null); } catch { }
            }
            catch { /* best-effort */ }

            string summary = $"Editing NPC: Template='{chosen.TemplateID}', Name='{chosen.Name}', Flags=[{string.Join(", ", chosen.Flags ?? new List<string>())}], AiBehaviors=[{(chosen.Template?.AiBehaviorConfigs != null ? string.Join(", ", chosen.Template.AiBehaviorConfigs.Select(b => b.Behavior)) : string.Join(", ", chosen.AiBehaviors ?? new List<string>()))}]";
            var menu = summary + "\n\nChoose a field to edit:\n1) NPC Name\n2) NPC Flags\n3) NPC AiBehaviors\n4) Visual Trait\n5) Buffs/Debuffs\n6) Hunger\n7) WanderAreas\n8) Toggle NoAttack flag\nType the option number or name, or 'EndEdit' to finish.";
            return MessageHandler.System(menu);
        }

        public Message? AddItem(Player player, string itemTemplateId, int quantity = 1)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (_world.ItemFactory == null)
                return MessageHandler.System("Item factory not available.");

            if (string.IsNullOrWhiteSpace(itemTemplateId))
                return MessageHandler.System("Usage: additem <item> [quantity] OR additem <item> to <player|container>");

            // Allow admin to specify target: "itemName to targetName"
            string payload = itemTemplateId.Trim();
            string itemNamePart = payload;
            string targetPart = "";
            int idxTo = payload.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (idxTo >= 0)
            {
                itemNamePart = payload.Substring(0, idxTo).Trim();
                targetPart = payload.Substring(idxTo + " to ".Length).Trim();
            }

            try
            {
                // If no explicit target, preserve existing behavior but disallow liquids in room.
                if (string.IsNullOrWhiteSpace(targetPart))
                {
                    // instantiate from template so we can inspect flags (but do not yet persist)
                    var preview = _world.ItemFactory.CreateItem(itemNamePart, "Room", room.RoomID, room.RoomID, Math.Max(1, quantity));
                    bool isLiquid = (preview.Flags != null && preview.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                     || (!string.IsNullOrEmpty(preview.FlagsJson) && preview.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isLiquid)
                    {
                        return MessageHandler.System("You cannot spawn liquids directly into a room. Use: additem <item> to <player> or additem <item> to <container>");
                    }

                    // fall back to existing room-add flow (re-use InventoryManager)
                    return InventoryManager.AddItemToRoom(_world, _db, player, itemNamePart, quantity);
                }

                // Resolve target: prefer player match, then container match (player's inventory then room)
                // 1) Try find a player by name (exact, starts-with, case-insensitive)
                Player? targetPlayer = null;
                try
                {
                    var candidates = _world.Players
                        .Where(p => p != null && p.IsAuthenticated)
                        .ToList();

                    // exact
                    targetPlayer = candidates.FirstOrDefault(p => string.Equals(p.Name, targetPart, StringComparison.OrdinalIgnoreCase));
                    // starts-with
                    if (targetPlayer == null)
                        targetPlayer = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name.StartsWith(targetPart, StringComparison.OrdinalIgnoreCase));
                    // token contains
                    if (targetPlayer == null)
                        targetPlayer = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name.IndexOf(targetPart, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch { targetPlayer = null; }

                // 2) If not player, try to resolve a container (parser-first then legacy fallback)
                Item? targetContainer = null;
                if (targetPlayer == null)
                {
                    try
                    {
                        var optsCont = new ItemParserOptions { IncludeDescriptorTokens = false, IncludeMaterialTokens = false, AllowStartsWith = true };
                        var pres = ItemParser.ResolveForAny(player, targetPart, optsCont);
                        if (pres?.Winner != null) targetContainer = pres.Winner;
                    }
                    catch { /* best-effort */ }

                    if (targetContainer == null)
                    {
                        try { targetContainer = InventoryManager.FindContainer(player, targetPart); } catch { targetContainer = null; }
                    }
                }

                if (targetPlayer == null && targetContainer == null)
                {
                    return MessageHandler.System($"No player or container matching '{targetPart}' found.");
                }

                // Create item with correct owner fields and persist
                if (targetPlayer != null)
                {
                    // create item owned by the target player
                    var newItem = _world.ItemFactory.CreateItem(itemNamePart, "Player", targetPlayer.DbPlayerID != 0 ? targetPlayer.DbPlayerID : 0,
                                                                targetPlayer.CurrentRoom?.RoomID ?? room.RoomID, Math.Max(1, quantity));
                    newItem.OwnerType = "Player";
                    newItem.OwnerID = targetPlayer.DbPlayerID != 0 ? targetPlayer.DbPlayerID : 0;
                    newItem.RoomID = targetPlayer.CurrentRoom?.RoomID ?? room.RoomID;
                    newItem.OwnerAreaID = targetPlayer.CurrentRoom?.AreaID ?? room.AreaID;

                    // disallow placing liquids into a room-owned player when target player is null (shouldn't happen) - already covered
                    // persist
                    ItemPersistence.SaveItem(_db, newItem);

                    // Add to in-memory inventory for target player
                    try
                    {
                        targetPlayer.Inventory.Add(newItem);
                    }
                    catch
                    {
                        // best-effort; even if in-memory add fails, DB contains the item.
                    }

                    // notify target player and room occupants
                    try { MessageHandler.EnqueuePlayerUpdate(targetPlayer); } catch { }
                    try
                    {
                        foreach (var p in (targetPlayer.CurrentRoom ?? room).Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(targetPlayer.CurrentRoom ?? room,
                                (targetPlayer.CurrentRoom ?? room).Npcs.Select(n => n.DisplayName).ToList(),
                                (targetPlayer.CurrentRoom ?? room).Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                    catch { }

                    return MessageHandler.System($"Added {newItem.Name} to {targetPlayer.Name}.");
                }
                else // container target
                {
                    if (targetContainer == null) return MessageHandler.System("Target container resolution failed.");

                    // Ensure container can accept liquids / items (respect Accept flag)
                    var accepts = InventoryManager.ParseAcceptList(targetContainer);
                    if (!accepts.Contains("All", StringComparer.OrdinalIgnoreCase))
                    {
                        // instantiate preview to get category
                        var preview = _world.ItemFactory.CreateItem(itemNamePart, "Item", targetContainer.ItemID, targetContainer.RoomID, Math.Max(1, quantity));
                        if (!accepts.Contains(preview.ItemCategory ?? "", StringComparer.OrdinalIgnoreCase))
                        {
                            return MessageHandler.System($"Container '{targetContainer.Name}' does not accept items of category '{preview.ItemCategory}'.");
                        }
                    }

                    // create item owned by the container (OwnerType='Item', OwnerID = container.ItemID)
                    var contItem = _world.ItemFactory.CreateItem(itemNamePart, "Item", targetContainer.ItemID, targetContainer.RoomID, Math.Max(1, quantity));
                    contItem.OwnerType = "Item";
                    contItem.OwnerID = targetContainer.ItemID;
                    contItem.RoomID = targetContainer.RoomID;
                    contItem.OwnerAreaID = targetContainer.OwnerAreaID;

                    // Persist into DB
                    ItemPersistence.SaveItem(_db, contItem);

                    // No in-memory container contents list is maintained here (client will re-query via inspect).
                    // Notify owner and room occupants so UI updates
                    try
                    {
                        // If container is carried by a player, notify that player
                        var ownerPlayer = (player.Inventory ?? new List<Item>()).Any(i => i.ItemID == targetContainer.ItemID)
                                          ? player
                                          : targetContainer.RoomID == room.RoomID ? player : null;

                        // Broadcast room update
                        foreach (var p in (player.CurrentRoom ?? room).Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(player.CurrentRoom ?? room,
                                (player.CurrentRoom ?? room).Npcs.Select(n => n.DisplayName).ToList(),
                                (player.CurrentRoom ?? room).Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }

                        if (ownerPlayer != null) MessageHandler.EnqueuePlayerUpdate(ownerPlayer);
                    }
                    catch { /* best-effort */ }

                    return MessageHandler.System($"Added {contItem.Name} into {targetContainer.Name}.");
                }
            }
            catch (KeyNotFoundException)
            {
                return MessageHandler.System($"Item template not found: {itemNamePart}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AddItem][ERROR] AddItem failed: {ex}");
                return MessageHandler.System($"Failed to add item: {ex.Message}");
            }
        } // end AddItem

        public Message? RemoveItem(Player player, string identifier)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var target = room.Inventory.FirstOrDefault(i => i.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                                                   || i.TemplateID.Equals(identifier, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                return MessageHandler.System($"No item '{identifier}' found in the room.");

            target.OwnerType = "ROOM";
            target.OwnerID = 0;
            MudPersistence.ItemPersistence.SaveItem(_db, target);

            room.Inventory.Remove(target);
            room.IsDirty = true;
            _world.SaveDirtyRooms(_db);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }

            foreach (var n in room.Npcs)
            {
                if (!string.IsNullOrWhiteSpace(n.LastAction))
                {
                    foreach (var recipient in room.Players)
                        MessageHandler.SendSystem(recipient, n.LastAction);
                }
            }

            return MessageHandler.System($"Item '{target.Name}' removed from the room.");
        }

        public Message? ListItems(Player player)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (room.Inventory == null || room.Inventory.Count == 0)
                return MessageHandler.System("This room contains no items.");

            var lines = room.Inventory.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})");
            return MessageHandler.System("Items in room:\n" + string.Join("\n", lines));
        }

        // NEW: List items owned by an NPC
        public Message? ListItemsForNpc(Player player, string npcIdExpr)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            if (!int.TryParse(npcIdExpr.Trim(), out int npcId) || npcId <= 0)
                return MessageHandler.System("Usage: listitems <npcid> — npcid must be a positive integer");

            var npc = _world.Npcs?.FirstOrDefault(n => n != null && n.NpcID == npcId);
            if (npc == null)
                return MessageHandler.System($"NPC with NpcID {npcId} not found.");

            if (npc.Inventory == null || npc.Inventory.Count == 0)
                return MessageHandler.System($"NPC '{npc.Name}' (NpcID={npcId}) has no items.");

            var lines = new List<string> { $"=== Items owned by '{npc.Name}' (NpcID={npcId}) ===" };

            foreach (var item in npc.Inventory.OrderBy(i => i.Name))
            {
                int qty = Math.Max(1, item.Quantity);
                string display = qty > 1 ? $"{item.Name} x{qty}" : item.Name;
                lines.Add($"  {display} (Template: {item.TemplateID})");
            }

            lines.Add($"Total: {npc.Inventory.Count} item type(s)");
            lines.Add("================================================");

            return MessageHandler.System(string.Join("\n", lines));
        }

        public Message? DebugItems(Player player)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID, OwnerAreaID FROM Item ORDER BY ItemID DESC";
                using var rdr = cmd.ExecuteReader();

                int n = 0;
                while (rdr.Read())
                {
                    n++;
                    var id = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                    var tid = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                    var name = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                    var otype = rdr.IsDBNull(3) ? "" : rdr.GetString(3);
                    var oid = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                    var rid = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                    var oarea = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);

                    Console.WriteLine($"[DEBUGITEMS] id={id} tid='{tid}' name='{name}' otype='{otype}' oid={oid} rid={rid} oarea={oarea}");
                }

                return MessageHandler.System($"DebugItems: printed {n} row(s) to server log.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUGITEMS][ERROR] {ex}");
                return MessageHandler.System($"DebugItems failed: {ex.Message}");
            }
        }

        // Add this method to the AdminService class, after the HandleNpcCounts method:

        /// <summary>
        /// Admin command to display current game time in human-readable format.
        /// Syntax: time

        public Message? HandleTime(Player player)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null || _world.GameTime == null) return MessageHandler.System("Game time unavailable.");

            try
            {
                var gc = _world.GameTime;

                // Month names array (matches GameClock internal array)
                string[] monthNames = new[]
                {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

                string monthName = (gc.Month >= 1 && gc.Month <= 12) ? monthNames[gc.Month - 1] : $"Month{gc.Month}";
                string period = gc.GetPeriodDescriptor();
                string periodRange = GameClock.FormatPeriodHourRange(period);

                var lines = new List<string>
        {
            "=== Current Game Time ===",
            $"Year: {gc.Year}",
            $"Month: {gc.Month} ({monthName})",
            $"Day: {gc.Day}",
            $"Hour: {gc.Hour}",
            $"Minute: {gc.Minute}",
            $"Second: {gc.Second}",
            $"Time Period: {period}",
            "========================"
        };
                if (!string.IsNullOrWhiteSpace(periodRange))
                    lines.Add(periodRange);

                lines.Add("========================");

                return MessageHandler.System(string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][TIME] Failed: {ex.Message}");
                return MessageHandler.System("Failed to retrieve time due to a server error.");
            }
        }

        /// <summary>
        /// Admin command to list all available admin commands.
        /// Syntax: adminhelp
        /// </summary>
        public Message? HandleAdminHelp(Player player)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var commands = new List<string>
    {
        "time - Display current time",
            "timechange - Adjust game time",
            "npccounts - Show NPC counts",
            "debugrooms - List all rooms",
            "debugitems - Print all items",
            "debugareas - List all areas",
            "debugnpcs - List NPCs in area",
            "kill <npc> - Remove NPCs",
            "createnpc - Spawn an NPC",
            "editnpc - Edit an NPC",
            "createarea - Create new area",
            "createroom - Create new room",
            "editarea - Edit current area",
            "editroom - Edit current room",
            "editexit - Edit exit properties",
            "additem - Add item to room",
            "removeitem - Remove item",
            "listitems - List items in room",
            "listitems [npcid] - List Npc items",
            "anchor - Prevents item pickup",
            "teleport - Move to room",
            "togglevis - Toggle invisibility",
            "shutdown - Shutdown server"
    };

            var header = "=== Admin Commands ===";
            var footer = "======================";

            return MessageHandler.System($"{header}\n{string.Join("\n", commands)}\n{footer}");
        }

        // NEW: admin timechange handler
        // Syntax: timechange [int] [unit]
        // Examples: timechange 1 hour   timechange -3 hour   timechange 4 day
        public Message? HandleTimeChange(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");

            // Normalize payload after 'timechange'
            var payload = command ?? "";
            if (payload.StartsWith("timechange", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring("timechange".Length).Trim();

            if (string.IsNullOrWhiteSpace(payload))
                return MessageHandler.System("Usage: timechange <n> <unit>  (unit = second|minute|hour|day). Examples: timechange 1 hour  timechange -3 hour");

            var toks = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 2)
                return MessageHandler.System("Usage: timechange <n> <unit>. Examples: timechange 1 hour  timechange -3 hour");

            // Parse amount (allow + or -)
            if (!int.TryParse(toks[0], out var amt))
            {
                // allow formats like '+1' or '-1' (int.TryParse handles +/-) - fail otherwise
                return MessageHandler.System("Invalid amount. Provide an integer, e.g. 1 or -3.");
            }

            string unitTok = toks[1].Trim().ToLowerInvariant();
            // accept plural forms and common abbreviations
            string NormalizeUnit(string u)
            {
                if (string.IsNullOrWhiteSpace(u)) return "";
                u = u.Trim().ToLowerInvariant();
                if (u.EndsWith("s")) u = u.Substring(0, u.Length - 1); // crude singularize
                if (u == "sec") return "second";
                if (u == "min") return "minute";
                if (u == "hr") return "hour";
                if (u == "h") return "hour";
                if (u == "d") return "day";
                return u;
            }

            unitTok = NormalizeUnit(unitTok);

            long multiplierSeconds = unitTok switch
            {
                "second" => 1L,
                "minute" => 60L,
                "hour" => 60L * 60L,
                "day" => 24L * 60L * 60L,
                _ => 0L
            };

            if (multiplierSeconds == 0L)
                return MessageHandler.System("Unknown unit. Supported units: second(s), minute(s), hour(s), day(s).");

            long deltaSeconds = (long)amt * multiplierSeconds;

            try
            {
                // Compute new GameClock by converting current clock to total seconds, adding delta, then reconstructing.
                // Use same calendar rules as GameClock: 60s/min,60min/hr,24hr/day,30day/month,12month/year
                const long SecondsPerMinute = 60L;
                const long MinutesPerHour = 60L;
                const long HoursPerDay = 24L;
                const long DaysPerMonth = 30L;
                const long MonthsPerYear = 12L;

                var gc = _world.GameTime ?? new GameClock();
                long curYear = gc.Year;
                int curMonth = gc.Month;
                int curDay = gc.Day;
                int curHour = gc.Hour;
                int curMinute = gc.Minute;
                int curSecond = gc.Second;

                // compute total seconds since epoch (Year=1, Month=1, Day=1, Hour=0,Min=0,Sec=0)
                long totalDays = (long)(curYear - 1) * MonthsPerYear * DaysPerMonth
                                 + (long)(curMonth - 1) * DaysPerMonth
                                 + (long)(curDay - 1);
                long totalHours = totalDays * HoursPerDay + curHour;
                long totalMinutes = totalHours * MinutesPerHour + curMinute;
                long totalSeconds = totalMinutes * SecondsPerMinute + curSecond;

                long newTotalSeconds = totalSeconds + deltaSeconds;
                if (newTotalSeconds < 0) newTotalSeconds = 0; // clamp to epoch start

                // decompose back
                long seconds = newTotalSeconds;
                int newSecond = (int)(seconds % SecondsPerMinute);
                seconds /= SecondsPerMinute;
                int newMinute = (int)(seconds % MinutesPerHour);
                seconds /= MinutesPerHour;
                int newHour = (int)(seconds % HoursPerDay);
                seconds /= HoursPerDay;
                long daysSinceEpoch = seconds % (MonthsPerYear * DaysPerMonth);
                long fullMonths = seconds / DaysPerMonth;
                int newDay = (int)((daysSinceEpoch % DaysPerMonth) + 1);
                int newMonth = (int)((fullMonths % MonthsPerYear) + 1);
                long newYear = (seconds / (DaysPerMonth * MonthsPerYear)) + 1;

                // Apply new GameClock
                _world.GameTime = new GameClock(newYear, newMonth, newDay, newHour, newMinute, newSecond);

                // Skip NPC hunger processing for this admin-initiated time change
                // (prevents starvation during testing/setup time jumps)
                try
                {
                    MudCore.AI.NpcAI.ProcessHunger(_world, _db, isAdminTimeChange: true);
                }
                catch { /* best-effort */ }

                // Broadcast admin message to all players
                string notif = $"[ADMIN] Time changed by {amt} {unitTok}{(Math.Abs(amt) == 1 ? "" : "s")} -> {_world.GameTime.GetHeaderString()}";
                try
                {
                    foreach (var p in _world.Players ?? Enumerable.Empty<Player>())
                    {
                        try { MessageHandler.SendSystem(p, notif); } catch { /* best-effort */ }
                    }
                }
                catch { /* best-effort */ }

                return MessageHandler.System($"Time updated. {_world.GameTime.GetHeaderString()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][TIMECHANGE] Failed: {ex}");
                return MessageHandler.System("Failed to change time due to server error.");
            }
        }

        public Message? DebugRooms(Player player, string args = "")
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World not available.");

            try
            {
                IEnumerable<Room> rooms = _world.Rooms;

                // If an area id was supplied, try parse and filter
                if (!string.IsNullOrWhiteSpace(args))
                {
                    if (int.TryParse(args.Trim(), out var areaId))
                    {
                        rooms = rooms.Where(r => r.AreaID == areaId);
                    }
                    else
                    {
                        return MessageHandler.System("Usage: debugrooms [areaid] — areaid must be an integer.");
                    }
                }

                var list = rooms
                    .OrderBy(r => r.RoomID)
                    .Select(r => $"{r.RoomID}: {r.Name}")
                    .ToList();

                if (list.Count == 0)
                    return MessageHandler.System(string.IsNullOrWhiteSpace(args)
                        ? "No rooms found."
                        : $"No rooms found for AreaID = {args.Trim()}.");

                // Limit output length to avoid very large payloads; admin can refine by areaId
                const int maxLines = 500;
                if (list.Count > maxLines)
                {
                    var head = list.Take(maxLines).ToList();
                    head.Add($"... {list.Count - maxLines} more (specify an areaid to narrow results).");
                    return MessageHandler.System(string.Join("\n", head));
                }

                return MessageHandler.System(string.Join("\n", list));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][DEBUGROOMS] Failed: {ex.Message}");
                return MessageHandler.System("Failed to retrieve rooms due to a server error.");
            }
        }

        // NEW: admin Kill command handler (supports pending flow and immediate execution)
        // Usage:
        //   kill <npcname>                         -> removes one matching NPC in current room
        //   kill all <npcname> in room             -> removes all matching NPCs in current room
        //   kill all <npcname> in area             -> removes all matching NPCs in the current area
        // If called with just 'kill' this will set a pending action so admin can enter target next.
        public Message? HandleKill(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");
            if (player.CurrentRoom == null) return MessageHandler.System("You are not inside a valid room.");

            // Normalize input after 'kill'
            var payload = command ?? "";
            if (payload.StartsWith("kill", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring(4).Trim();

            // If no payload provided, enter pending flow (like CreateRoom)
            if (string.IsNullOrWhiteSpace(payload))
            {
                player.PendingAction = new PendingAction
                {
                    Command = "Kill",
                    Step = "SpecifyTarget"
                };
                return MessageHandler.System("Enter target to kill (examples: 'goblin' or 'all goblin in area' or 'all goblin in room'):");
            }

            // Parse pattern: optional leading "all", optional trailing "in room"/"in area"
            bool all = false;
            string scope = "room"; // "room" or "area"
            string rest = payload;

            if (rest.StartsWith("all ", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
                rest = rest.Substring(4).Trim();
            }

            // detect " in <scope>" suffix
            var idxIn = rest.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (idxIn >= 0)
            {
                var scopeTok = rest.Substring(idxIn + 4).Trim().ToLowerInvariant();
                rest = rest.Substring(0, idxIn).Trim();
                if (scopeTok.StartsWith("area")) scope = "area";
                else scope = "room";
            }

            var npcToken = rest.Trim();
            if (string.IsNullOrWhiteSpace(npcToken))
                return MessageHandler.System("Usage: kill [npcname] OR kill all [npcname] in [room|area]");

            try
            {
                // Determine target rooms
                List<Room> targetRooms;
                if (scope == "area")
                {
                    var curAreaId = player.CurrentRoom.AreaID;
                    targetRooms = _world.Rooms.Where(r => r.AreaID == curAreaId).ToList();
                }
                else
                {
                    targetRooms = new List<Room> { player.CurrentRoom };
                }

                var removedCount = 0;
                var affectedRooms = new HashSet<int>();

                foreach (var rm in targetRooms)
                {
                    if (rm.Npcs == null || rm.Npcs.Count == 0) continue;

                    // Find matches according to name/template heuristics
                    var matches = rm.Npcs.Where(n =>
                        (!string.IsNullOrWhiteSpace(n.Name) && string.Equals(n.Name, npcToken, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(n.TemplateID) && string.Equals(n.TemplateID, npcToken, StringComparison.OrdinalIgnoreCase)) ||
                        (n.Template != null && !string.IsNullOrWhiteSpace(n.Template.Id) && string.Equals(n.Template.Id, npcToken, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(n.Name) && n.Name.StartsWith(npcToken, StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (matches.Count == 0) continue;

                    // If not 'all', remove only a single match (prefer first)
                    var toRemove = all ? matches.ToList() : new List<Npc> { matches[0] };

                    foreach (var npc in toRemove)
                    {
                        try
                        {
                            // Remove NPC-owned items (best-effort)
                            try
                            {
                                if (npc.Inventory != null)
                                {
                                    foreach (var it in npc.Inventory.ToList())
                                    {
                                        try
                                        {
                                            if (it.ItemID != 0)
                                                MudPersistence.ItemPersistence.DeleteItem(_db, it.ItemID);
                                        }
                                        catch (Exception iex)
                                        {
                                            Console.WriteLine($"[KILL][WARN] Failed to delete item {it.ItemID} for NPC {npc.NpcID}: {iex.Message}");
                                        }
                                    }
                                }
                            }
                            catch { /* best-effort */ }

                            // Delete NPC DB row (best-effort)
                            try
                            {
                                if (npc.NpcID != 0)
                                    MudPersistence.NpcPersistence.DeleteNpc(_db, npc.NpcID);
                            }
                            catch (Exception ne)
                            {
                                Console.WriteLine($"[KILL][WARN] Failed to delete NPC row {npc.NpcID}: {ne.Message}");
                            }

                            // Remove from room and world lists
                            rm.Npcs.RemoveAll(n => n.NpcID == npc.NpcID);
                            _world.Npcs.RemoveAll(n => n.NpcID == npc.NpcID);

                            removedCount++;
                            affectedRooms.Add(rm.RoomID);

                            Console.WriteLine($"[KILL] Admin removed NPC '{npc.Name}' (NpcID={npc.NpcID}) from Room {rm.RoomID}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[KILL][ERROR] Failed removing NPC {npc?.NpcID}: {ex.Message}");
                        }

                        // If not removing 'all', break after first
                        if (!all) break;
                    }
                }

                // Persist any dirty room state (best-effort)
                try
                {
                    // Mark affected rooms dirty so SaveDirtyRooms persists related state if needed
                    foreach (var rid in affectedRooms)
                    {
                        var r = _world.Rooms.FirstOrDefault(rr => rr.RoomID == rid);
                        if (r != null) r.IsDirty = true;
                    }
                    _world.SaveDirtyRooms(_db);
                }
                catch { /* best-effort */ }

                // Notify players in affected rooms
                try
                {
                    foreach (var rid in affectedRooms)
                    {
                        var r = _world.Rooms.FirstOrDefault(rr => rr.RoomID == rid);
                        if (r == null) continue;
                        foreach (var p in r.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                r,
                                r.Npcs.Select(n => n.DisplayName).ToList(),
                                r.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                }
                catch { /* best-effort */ }

                if (removedCount == 0)
                {
                    return MessageHandler.System($"No matching NPCs found for '{npcToken}' in the specified scope ({scope}).");
                }

                return MessageHandler.System($"Removed {removedCount} NPC(s) matching '{npcToken}' in {scope}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][KILL] Failed: {ex.Message}");
                return MessageHandler.System("Kill command failed due to a server error.");
            }
        }

        // Typed pending data for EditRoom -> AI generation flow.
        // Keeps strongly-typed fields instead of using an unstructured Dictionary.
        private class EditRoomPendingData
        {
            public int RoomID { get; set; } = 0;
            public string[] Keywords { get; set; } = Array.Empty<string>();
            public string DraftDescription { get; set; } = "";
            public string DraftDistant { get; set; } = "";
        } // end EditRoomPendingData

        private static bool IsValidDirection(string dir)
        {
            string[] valid = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "UP", "DOWN" };
            return valid.Any(v => v.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeDirection(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return string.Empty;
            var s = d.Trim().ToUpperInvariant();
            return s switch
            {
                "N" or "NORTH" => "N",
                "S" or "SOUTH" => "S",
                "E" or "EAST" => "E",
                "W" or "WEST" => "W",
                "NE" or "NORTHEAST" => "NE",
                "NW" or "NORTHWEST" => "NW",
                "SE" or "SOUTHEAST" => "SE",
                "SW" or "SOUTHWEST" => "SW",
                "U" or "UP" => "UP",
                "D" or "DOWN" => "DOWN",
                _ => s
            };
        }

        public Message? HandleToggleVisible(Player player)
        {
            // 1. Toggle state
            player.IsInvisibleToMortals = !player.IsInvisibleToMortals;

            // 2. Persist immediately
            try
            {
                if (_db != null)
                    PlayerPersistence.SavePlayer(_db, player);
            }
            catch { /* log error */ }

            // 3. Send confirmation to admin
            string adminMsg = player.IsInvisibleToMortals
                ? "You are now invisible to Mortals"
                : "You are now visible to Mortals";
            player.PendingMessageQueue.Add(MessageHandler.System(adminMsg));

            // 4. Broadcast dramatic message to room
            if (player.CurrentRoom != null)
            {
                string broadcastMsg = player.IsInvisibleToMortals
                    ? $"{player.Name} vanishes into the air!"
                    : $"{player.Name} appears in a flash of lightning!";

                foreach (var p in player.CurrentRoom.Players.Where(p => p != player))
                {
                    p.PendingMessageQueue.Add(MessageHandler.System(broadcastMsg));
                }

                // 5. Force immediate room refresh for all players
                BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
            }

            return null;
        }

        public Message? HandleToggleFly(Player player)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            // 1. Toggle IsFlying flag
            if (player.Flags == null) player.Flags = new List<string>();

            bool currentlyFlying = player.Flags.Any(f => string.Equals(f, "IsFlying", StringComparison.OrdinalIgnoreCase));

            if (currentlyFlying)
            {
                // Remove flag
                player.Flags.RemoveAll(f => string.Equals(f, "IsFlying", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // Add flag
                player.Flags.Add("IsFlying");
            }

            // 2. Persist immediately
            try
            {
                if (_db != null)
                    PlayerPersistence.SavePlayer(_db, player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][ToggleFly] Failed to persist for {player.Name}: {ex.Message}");
            }

            // 3. Send confirmation to admin
            string adminMsg = currentlyFlying
                ? "You gently descend to the ground."
                : "You lift off the ground and begin hovering!";
            player.PendingMessageQueue.Add(MessageHandler.System(adminMsg));

            // 4. Broadcast dramatic message to room
            if (player.CurrentRoom != null)
            {
                string broadcastMsg = currentlyFlying
                    ? $"{player.Name} gently descends to the ground."
                    : $"{player.Name} lifts off the ground and begins hovering in the air!";

                foreach (var p in player.CurrentRoom.Players.Where(p => p != player))
                {
                    p.PendingMessageQueue.Add(MessageHandler.System(broadcastMsg));
                }

                // 5. Force immediate room refresh for all players
                BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
            }

            return null;
        }

        public Message? HandleTeleport(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            // Parse command payload after "teleport"
            var payload = command.Trim();
            if (payload.StartsWith("teleport", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring(8).Trim();

            if (string.IsNullOrWhiteSpace(payload))
                return MessageHandler.System("Usage: teleport <roomid> OR teleport <player> [to <roomid>]");

            // Detect "to <roomid>" clause
            int idxTo = payload.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            string targetExpr = idxTo >= 0 ? payload.Substring(0, idxTo).Trim() : payload.Trim();
            string roomExpr = idxTo >= 0 ? payload.Substring(idxTo + 4).Trim() : "";

            // Case 1: "teleport <roomid>" - admin self-teleport
            if (string.IsNullOrWhiteSpace(roomExpr) && int.TryParse(targetExpr, out int selfRoomId))
            {
                return ExecuteAdminSelfTeleport(player, selfRoomId);
            }

            // Case 2: "teleport <player> [to <roomid>]" - teleport another player
            Player? targetPlayer = ResolvePlayer(targetExpr);
            if (targetPlayer == null)
                return MessageHandler.System($"Player '{targetExpr}' not found.");

            // Determine destination room
            Room? destRoom;
            if (string.IsNullOrWhiteSpace(roomExpr))
            {
                // "teleport player1" → bring to admin's location
                destRoom = player.CurrentRoom;
            }
            else
            {
                // "teleport player1 to 34"
                if (!int.TryParse(roomExpr, out int targetRoomId))
                    return MessageHandler.System($"Invalid room ID '{roomExpr}'.");
                destRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == targetRoomId);
            }

            if (destRoom == null)
                return MessageHandler.System("Destination room not found.");

            return ExecutePlayerTeleport(player, targetPlayer, destRoom);
        }

        // Helper: Resolve player by name (exact, starts-with, contains)
        private Player? ResolvePlayer(string name)
        {
            var candidates = _world.Players.Where(p => p != null && p.IsAuthenticated).ToList();

            // Exact match
            var exact = candidates.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Starts-with
            var starts = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) &&
                                                         p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (starts != null) return starts;

            // Contains
            return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) &&
                                                   p.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Execute admin self-teleport
        private Message? ExecuteAdminSelfTeleport(Player admin, int targetRoomId)
        {
            var originRoom = admin.CurrentRoom;
            var destRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == targetRoomId);

            if (destRoom == null)
                return MessageHandler.System($"Room {targetRoomId} not found.");

            if (originRoom == destRoom)
                return MessageHandler.System("You are already in that room.");

            // Broadcast departure (only if visible)
            if (originRoom != null && !admin.IsInvisibleToMortals)
            {
                foreach (var p in originRoom.Players.Where(p => p != admin))
                {
                    MessageHandler.SendSystem(p, $"{admin.Name} disappears in a flash, leaving only a wisp of smoke!");
                }
            }

            // Update admin's location
            try
            {
                if (originRoom != null)
                    originRoom.Players.Remove(admin);

                admin.CurrentRoom = destRoom;
                destRoom.Players.Add(admin);

                // Persist
                PlayerPersistence.SavePlayer(_db, admin);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][TELEPORT] Failed to teleport admin: {ex.Message}");
                return MessageHandler.System("Teleport failed due to server error.");
            }

            // Broadcast arrival (only if visible)
            if (!admin.IsInvisibleToMortals)
            {
                foreach (var p in destRoom.Players.Where(p => p != admin))
                {
                    MessageHandler.SendSystem(p, $"In a thunderous flash {admin.Name} appears!");
                }
            }

            // Notify admin
            admin.PendingMessageQueue.Add(MessageHandler.System($"You teleported to {destRoom.Name}."));

            // Send room updates to all affected players
            if (originRoom != null)
                BroadcastRoomUpdates(originRoom, _world, _db);
            BroadcastRoomUpdates(destRoom, _world, _db);

            return null;
        }

        // Execute player teleport (admin teleporting another player)
        private Message? ExecutePlayerTeleport(Player admin, Player target, Room destRoom)
        {
            var originRoom = target.CurrentRoom;

            if (originRoom == destRoom)
                return MessageHandler.System($"{target.Name} is already in that room.");

            // Broadcast departure to origin room
            if (originRoom != null)
            {
                foreach (var p in originRoom.Players.Where(p => p != target))
                {
                    MessageHandler.SendSystem(p, $"{target.Name} disappears, leaving no trace!");
                }
            }

            // Notify target player (BEFORE moving so they see this first)
            MessageHandler.SendSystem(target, "You are torn asunder, space and time flash before you! You are then yourself again!");

            // Update player's location
            try
            {
                if (originRoom != null)
                    originRoom.Players.Remove(target);

                target.CurrentRoom = destRoom;
                destRoom.Players.Add(target);

                // Persist
                PlayerPersistence.SavePlayer(_db, target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][TELEPORT] Failed to teleport player: {ex.Message}");
                return MessageHandler.System("Teleport failed due to server error.");
            }

            // Broadcast arrival to destination room (AFTER player is moved)
            foreach (var p in destRoom.Players.Where(p => p != target))
            {
                MessageHandler.SendSystem(p, $"In a thunderous flash {target.Name} appears, quite confused!");
            }

            // Notify admin
            admin.PendingMessageQueue.Add(MessageHandler.System($"Teleported {target.Name} to {destRoom.Name}."));

            // Send room updates to all affected players
            if (originRoom != null)
                BroadcastRoomUpdates(originRoom, _world, _db);
            BroadcastRoomUpdates(destRoom, _world, _db);

            return null;
        }

        public Message GetNpcStats(string args, Player admin)
        {
            if (!int.TryParse(args.Trim(), out int npcId))
                return MessageHandler.System("Usage: npcstats <npcId>");

            var npc = _world.Npcs?.FirstOrDefault(n => n != null && n.NpcID == npcId);
            if (npc == null)
                return MessageHandler.System($"No NPC with ID {npcId} found in world.");

            var lines = new List<string>
            {
                $"=== NPC Stats: {npc.DisplayName} (ID: {npc.NpcID}) ===",
                $"Template:        {npc.TemplateID ?? "unknown"}",
                $"Room:            {npc.CurrentRoom?.Name ?? "unknown"} (RoomID: {npc.CurrentRoomID})",
                $"Area:            {npc.CurrentAreaID}",
                $"Alignment:       {npc.Alignment}",
                $"Level:           {npc.Level ?? 0}",
                $"Health:          {npc.Health}/{npc.HealthMax}",
                $"Size (SizeEnum): {npc.SizeEnum ?? 4}",
                $"Mass:            {npc.Mass ?? 0:F1} kg",
                $"Eff Mass:        {npc.EffMass ?? 0:F1} kg",
                $"CombatSkill:     {npc.CombatSkill ?? 0:F2}",
                $"EffCombatSkill:  {npc.EffCombatSkill ?? 0:F2}",
                $"BaseDmgDice:     {npc.BaseDmgDice:F2}",
                $"EffDmgDice:      {npc.EffDmgDice:F2}",
                $"BaseArmorClass:  {npc.BaseArmorClass:F2}",
                $"EffArmorClass:   {npc.EffArmorClass:F2}",
                $"IsFlying:        {npc.IsFlying}",
                $"IsHumanoid:      {npc.IsHumanoid}",
                $"IsPersistent:    {npc.IsPersistent}",
                $"IsBeingLed:      {npc.IsBeingLed}",
                $"IsHitched:       {npc.IsHitched}",
                $"Hunger:          {npc.Hunger}",
                $"WanderChance:    {npc.WanderChance:F2}",
                $"Buffs:           {(npc.Buffs?.Count > 0 ? string.Join(", ", npc.Buffs) : "(none)")}",
                $"Debuffs:         {(npc.Debuffs?.Count > 0 ? string.Join(", ", npc.Debuffs) : "(none)")}",
                $"Flags:           {(npc.Flags?.Count > 0 ? string.Join(", ", npc.Flags) : "(none)")}",
                $"TimedEffects:    {(npc.TimedEffectSeconds?.Count > 0 ? string.Join(", ", npc.TimedEffectSeconds.Select(kv => $"{kv.Key}:{kv.Value}s")) : "(none)")}",
                $"Inventory:       {npc.Inventory?.Count ?? 0} item(s)",
            };

            // Show full detail for each inventory item
            if (npc.Inventory != null && npc.Inventory.Count > 0)
            {
                lines.Add("--- Inventory Detail ---");
                foreach (var item in npc.Inventory)
                {
                    if (item == null) { lines.Add("  [null item]"); continue; }
                    lines.Add($"  [{item.ItemID}] {item.Name} | Cat:{item.ItemCategory} | DmgMult:{item.DmgDiceMult:F2} | ACBonus:{item.BaseACBonus:F2} | Cond:{item.Condition:F1} | Flags:{item.FlagsJson} | Material:{item.Material}");
                }
            }

            lines.Add("=====================================");

            return MessageHandler.System(string.Join("\n", lines));
        }

        public Message? HandleShutdown(Player player, string command, Action onShutdown)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            // Parse command payload after "shutdown"
            var payload = command.Trim();
            if (payload.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring(8).Trim();

            if (string.IsNullOrWhiteSpace(payload))
                return MessageHandler.System("Usage: shutdown <inminutes> <onewordreason> <downtimemins> OR shutdown abort");

            // Check for abort command
            if (payload.Equals("abort", StringComparison.OrdinalIgnoreCase))
            {
                if (ShutdownManager.AbortShutdown())
                {
                    return MessageHandler.System("Shutdown aborted.");
                }
                else
                {
                    return MessageHandler.System("No shutdown is currently pending.");
                }
            }

            // Parse: <minutes> <reason> <downtime>
            var tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
                return MessageHandler.System("Usage: shutdown <minutes> <reason> <downtime>");

            if (!int.TryParse(tokens[0], out int minutes) || minutes < 0)
                return MessageHandler.System("Invalid minutes. Provide a non-negative integer.");

            string reason = tokens[1].Trim();
            if (string.IsNullOrWhiteSpace(reason))
                return MessageHandler.System("Reason cannot be empty.");

            if (!int.TryParse(tokens[2], out int downtime) || downtime < 0)
                return MessageHandler.System("Invalid downtime. Provide a non-negative integer.");

            // Check if shutdown already pending
            if (ShutdownManager.IsShutdownPending)
            {
                return MessageHandler.System("A shutdown is already pending. Use 'shutdown abort' to cancel it first.");
            }

            // Schedule shutdown
            bool success = ShutdownManager.ScheduleShutdown(minutes, reason, downtime, _world, onShutdown);

            if (success)
            {
                if (minutes == 0)
                {
                    return MessageHandler.System("Immediate shutdown initiated.");
                }
                else
                {
                    return MessageHandler.System($"Shutdown scheduled in {minutes} minute{(minutes != 1 ? "s" : "")}. Reason: {reason}, Downtime: {downtime} minutes.");
                }
            }
            else
            {
                return MessageHandler.System("Failed to schedule shutdown (already pending).");
            }
        }

        public Message? HandleNpcCounts(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");

            // Parse optional scope tokens from command: "npccounts", optionally followed by "room <id>" or "area <id>"
            string payload = (command ?? "").Trim();
            if (payload.StartsWith("npccounts", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring("npccounts".Length).Trim();

            int? roomFilter = null;
            int? areaFilter = null;

            try
            {
                var toks = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < toks.Length; i++)
                {
                    var t = toks[i].Trim().ToLowerInvariant();
                    if (t == "room" && i + 1 < toks.Length && int.TryParse(toks[i + 1], out var rid))
                    {
                        roomFilter = rid;
                        i++;
                        continue;
                    }
                    if (t == "area" && i + 1 < toks.Length && int.TryParse(toks[i + 1], out var aid))
                    {
                        areaFilter = aid;
                        i++;
                        continue;
                    }
                }
            }
            catch { /* best-effort parsing */ }

            IEnumerable<Npc> candidates = _world.Npcs ?? Enumerable.Empty<Npc>();

            if (roomFilter.HasValue)
            {
                candidates = candidates.Where(n => n != null && n.CurrentRoom != null && n.CurrentRoom.RoomID == roomFilter.Value);
            }
            else if (areaFilter.HasValue)
            {
                candidates = candidates.Where(n => n != null && (n.CurrentAreaID == areaFilter.Value || (n.CurrentRoom != null && n.CurrentRoom.AreaID == areaFilter.Value)));
            }

            var grouped = candidates
                .GroupBy(n => (n.Template?.Id ?? n.TemplateID ?? n.Name ?? "Unknown"), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var sample = g.FirstOrDefault();
                    string displayName = sample?.Template?.Name ?? sample?.Name ?? g.Key;
                    return new { TemplateId = g.Key, DisplayName = displayName, Count = g.Count() };
                })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.DisplayName)
                .ToList();

            if (grouped.Count == 0)
            {
                string scopeText = roomFilter.HasValue ? $"room {roomFilter.Value}" : (areaFilter.HasValue ? $"area {areaFilter.Value}" : "world");
                return MessageHandler.System($"No NPCs found in {scopeText}.");
            }

            var lines = new List<string>();
            int total = grouped.Sum(g => g.Count);
            string scopeLabel = roomFilter.HasValue ? $"room {roomFilter.Value}" : (areaFilter.HasValue ? $"area {areaFilter.Value}" : "world");
            lines.Add($"NPC counts for {scopeLabel} — total {total}:");

            foreach (var g in grouped)
            {
                lines.Add($"{g.Count}x {g.DisplayName} ({g.TemplateId})");
            }

            // Limit output to reasonable size (safety)
            const int maxLines = 500;
            if (lines.Count > maxLines)
            {
                var head = lines.Take(maxLines).ToList();
                head.Add($"... {lines.Count - maxLines} more (narrow scope with 'room <id>' or 'area <id>').");
                return MessageHandler.System(string.Join("\n", head));
            }

            return MessageHandler.System(string.Join("\n", lines));
        }

        /// <summary>
        /// Admin command: debugareas
        /// Lists all areas with their ID, name, climate, and PvP status.
        /// </summary>
        public Message? HandleDebugAreas(Player player)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");

            var areas = _world.Areas ?? Enumerable.Empty<Area>();

            if (areas.Count() == 0)
                return MessageHandler.System("No areas found.");

            var lines = new List<string> { "=== Areas ===" };

            foreach (var area in areas.OrderBy(a => a.AreaID))
            {
                string pvpStatus = area.IsPvPAllowed ? "PvP" : "NoPvP";
                string climate = string.IsNullOrWhiteSpace(area.Climate) ? "(none)" : area.Climate;
                lines.Add($"[{area.AreaID}] {area.Name} | Climate: {climate} | {pvpStatus}");
            }

            lines.Add("===============");
            return MessageHandler.System(string.Join("\n", lines));
        }

        /// <summary>
        /// Admin command: debugnpcs [areaid]
        /// Lists all NPCs in the specified area (or current area if not specified).
        /// Shows NpcID, Name, Template, Current Room, and RoomID.
        /// </summary>
        public Message? HandleDebugNpcs(Player player, string payload)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");

            int? targetAreaId = null;

            // Parse optional areaid argument
            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (int.TryParse(payload.Trim(), out int areaId))
                {
                    targetAreaId = areaId;
                }
                else
                {
                    return MessageHandler.System("Usage: debugnpcs [areaid] — areaid must be an integer");
                }
            }
            else
            {
                // No argument provided; use current area
                if (player.CurrentRoom != null)
                {
                    targetAreaId = player.CurrentRoom.AreaID;
                }
                else
                {
                    return MessageHandler.System("You are not in a valid room.");
                }
            }

            // Filter NPCs by area
            var npcs = (_world.Npcs ?? Enumerable.Empty<Npc>())
                .Where(n => n != null &&
                           (n.CurrentAreaID == targetAreaId ||
                            (n.CurrentRoom != null && n.CurrentRoom.AreaID == targetAreaId)))
                .OrderBy(n => n.NpcID)
                .ToList();

            if (npcs.Count == 0)
            {
                return MessageHandler.System($"No NPCs found in area {targetAreaId}.");
            }

            var lines = new List<string> { $"=== NPCs in Area {targetAreaId} ===" };

            foreach (var npc in npcs)
            {
                string roomName = npc.CurrentRoom?.Name ?? "unknown";
                string template = npc.TemplateID ?? npc.Template?.Id ?? "unknown";
                lines.Add($"[{npc.NpcID}] {npc.Name,-20} | Template: {template,-30} | Room: {roomName,-40} (RoomID: {npc.CurrentRoomID})");
            }

            lines.Add("====================================");
            lines.Add($"Total: {npcs.Count} NPC(s)");

            // Limit output length to avoid excessive payload
            const int maxLines = 500;
            if (lines.Count > maxLines)
            {
                var head = lines.Take(maxLines).ToList();
                head.Add($"... {lines.Count - maxLines} more lines (output limited).");
                return MessageHandler.System(string.Join("\n", head));
            }

            return MessageHandler.System(string.Join("\n", lines));
        }

        public Message? HandleCreateSky(Player player)
        {
            if (!player.IsAdmin)
                return MessageHandler.System("Permission denied. Admins only.");

            if (player.CurrentRoom == null)
                return MessageHandler.System("You must be in a room to create a sky.");

            var room = player.CurrentRoom;
            var area = _world.Areas?.FirstOrDefault(a => a?.AreaID == room.AreaID);
            if (area == null)
                return MessageHandler.System("Area not found.");

            // Validation 1: Room cannot be Indoor
            if (room.Flags != null && room.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase)))
                return MessageHandler.System("Can't create sky here as Room is Indoor!");

            // Validation 2: Area cannot be Indoor
            if (!string.IsNullOrWhiteSpace(area.Climate) && string.Equals(area.Climate, "Indoor", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System("Can't create sky here as Area is Indoor!");

            // Validation 3: Area must not already have a sky room
            if (area.SkyRoomID.HasValue)
                return MessageHandler.System($"This area already has a sky room (RoomID={area.SkyRoomID.Value}).");

            // Validation 4: Area must have at least one outdoor room for landing
            var outdoorRooms = _world.Rooms?.Where(r =>
                r != null &&
                r.AreaID == area.AreaID &&
                (r.Flags == null || !r.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase)))
            ).ToList() ?? new List<Room>();

            if (outdoorRooms.Count == 0)
                return MessageHandler.System("Cannot create sky - area has no outdoor rooms for landing!");

            try
            {
                // Create sky room - use the same pattern as HandleCreateRoom
                var skyRoom = new Room($"Sky above {area.Name}", $"You soar high above {area.Name}. Clouds drift past as you hover effortlessly. The land stretches out far below.\n\nType 'look down' to see where you can land, or 'land' to descend.")
                {
                    AreaID = area.AreaID,
                    Flags = new List<string> { "IsSkyRoom", "Outdoor" }
                };
                skyRoom.Keywords.AddRange(new[] { "sky", "clouds", "air" });

                // Single atomic DB write using DbWriteQueue - create room in DB and get assigned ID
                int newRoomID = DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                {
                    // Use World.CreateRoomInDB to persist the room (same as CreateRoom does)
                    int roomId = _world.CreateRoomInDB(skyRoom, wConn, wTx);

                    // CRITICAL FIX: Update the Flags column directly after room creation
                    // (CreateRoomInDB doesn't persist Flags by default)
                    using (var flagCmd = wConn.CreateCommand())
                    {
                        flagCmd.Transaction = wTx;
                        flagCmd.CommandText = "UPDATE Room SET Flags = @flags WHERE RoomID = @rid";
                        flagCmd.Parameters.AddWithValue("@flags", JsonSerializer.Serialize(skyRoom.Flags));
                        flagCmd.Parameters.AddWithValue("@rid", roomId);
                        flagCmd.ExecuteNonQuery();
                    }

                    // Update area with SkyRoomID in same transaction
                    using (var cmd = wConn.CreateCommand())
                    {
                        cmd.Transaction = wTx;
                        cmd.CommandText = "UPDATE Area SET SkyRoomID = @skyid WHERE AreaID = @aid";
                        cmd.Parameters.AddWithValue("@skyid", roomId);
                        cmd.Parameters.AddWithValue("@aid", area.AreaID);
                        cmd.ExecuteNonQuery();
                    }

                    return roomId;
                }, timeout: TimeSpan.FromSeconds(5));

                // Update in-memory state (after DB success)
                area.SkyRoomID = newRoomID;
                skyRoom.RoomID = newRoomID;
                _world.Rooms.Add(skyRoom);

                Console.WriteLine($"[ADMIN] Sky room created for {area.Name} (RoomID={newRoomID})");
                return MessageHandler.System($"Sky room created for {area.Name} (RoomID={newRoomID}).\nUse 'linksky' command to connect to adjacent area skies.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN] Failed to create sky room: {ex.Message}");
                return MessageHandler.System("Failed to create sky room due to a server error.");
            }
        }//END HandleCreateSky

        public Message? HandleLinkSky(Player player, string command)
        {
            if (!player.IsAdmin)
                return MessageHandler.System("Permission denied. Admins only.");

            // Usage: linksky <direction> <targetAreaID>
            // Example: linksky north 2
            // This creates a bidirectional link: current area's sky <-> target area's sky

            var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
                return MessageHandler.System("Usage: linksky <direction> <targetAreaID>\nExample: linksky north 2");

            var direction = NormalizeDirection(tokens[1].Trim().ToUpperInvariant());
            if (string.IsNullOrWhiteSpace(direction) || !IsValidDirection(direction))
            {
                return MessageHandler.System("Invalid direction. Valid: N, S, E, W, NE, NW, SE, SW");
            }

            if (!int.TryParse(tokens[2], out int targetAreaID))
                return MessageHandler.System("Invalid target area ID. Must be a number.");

            var currentRoom = player.CurrentRoom;
            if (currentRoom == null)
                return MessageHandler.System("You must be in a room.");

            var currentArea = _world.Areas?.FirstOrDefault(a => a?.AreaID == currentRoom.AreaID);
            if (currentArea == null)
                return MessageHandler.System("Current area not found.");

            if (!currentArea.SkyRoomID.HasValue)
                return MessageHandler.System("Current area has no sky room. Use 'createsky' first.");

            var targetArea = _world.Areas?.FirstOrDefault(a => a?.AreaID == targetAreaID);
            if (targetArea == null)
                return MessageHandler.System($"Target area {targetAreaID} not found.");

            if (!targetArea.SkyRoomID.HasValue)
                return MessageHandler.System($"Target area '{targetArea.Name}' has no sky room. An admin must create one first.");

            var currentSkyRoomID = currentArea.SkyRoomID.Value;
            var targetSkyRoomID = targetArea.SkyRoomID.Value;

            // Determine opposite direction for bidirectional link
            var oppositeDir = OppositeDirection(direction);

            try
            {
                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    // Load or initialize SkyExits dictionaries
                    Dictionary<string, int> currentSkyExits;
                    Dictionary<string, int> targetSkyExits;

                    if (currentArea.SkyExits == null)
                        currentSkyExits = new Dictionary<string, int>();
                    else
                        currentSkyExits = new Dictionary<string, int>(currentArea.SkyExits);

                    if (targetArea.SkyExits == null)
                        targetSkyExits = new Dictionary<string, int>();
                    else
                        targetSkyExits = new Dictionary<string, int>(targetArea.SkyExits);

                    // 2) Add/update links
                    currentSkyExits[direction] = targetSkyRoomID;
                    targetSkyExits[oppositeDir] = currentSkyRoomID;

                    // 3) Persist to DB
                    using (var cmd = wConn.CreateCommand())
                    {
                        cmd.Transaction = wTx;
                        cmd.CommandText = "UPDATE Area SET SkyExits = @exits WHERE AreaID = @aid";
                        cmd.Parameters.AddWithValue("@exits", System.Text.Json.JsonSerializer.Serialize(currentSkyExits));
                        cmd.Parameters.AddWithValue("@aid", currentArea.AreaID);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = wConn.CreateCommand())
                    {
                        cmd.Transaction = wTx;
                        cmd.CommandText = "UPDATE Area SET SkyExits = @exits WHERE AreaID = @aid";
                        cmd.Parameters.AddWithValue("@exits", System.Text.Json.JsonSerializer.Serialize(targetSkyExits));
                        cmd.Parameters.AddWithValue("@aid", targetArea.AreaID);
                        cmd.ExecuteNonQuery();
                    }

                    return null;
                }, timeout: TimeSpan.FromSeconds(5));

                // Update in-memory state
                currentArea.SkyExits = currentArea.SkyExits ?? new Dictionary<string, int>();
                currentArea.SkyExits[direction] = targetSkyRoomID;

                targetArea.SkyExits = targetArea.SkyExits ?? new Dictionary<string, int>();
                targetArea.SkyExits[oppositeDir] = currentSkyRoomID;

                Console.WriteLine($"[ADMIN] Sky link created: {currentArea.Name} ({direction}) <-> {targetArea.Name} ({oppositeDir})");
                return MessageHandler.System($"Sky link created:\n{currentArea.Name} sky ({direction}) <-> {targetArea.Name} sky ({oppositeDir})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN] LinkSky failed: {ex.Message}");
                return MessageHandler.System("Failed to create sky link due to server error.");
            }
        }

        private static string OppositeDirection(string dir) => dir.ToUpper() switch
        {
            "N" => "S",
            "S" => "N",
            "E" => "W",
            "W" => "E",
            "NE" => "SW",
            "SW" => "NE",
            "NW" => "SE",
            "SE" => "NW",
            "UP" => "DOWN",
            "DOWN" => "UP",
            _ => ""
        };

        /// <summary>
        /// Helper to broadcast room updates to all occupants (for admin edits).
        /// </summary>
        private void BroadcastRoomUpdates(Room room, World.World world, SqliteConnection db)
        {
            if (room == null) return;
            try
            {
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                        room.Players.Where(pl => pl != p && !pl.IsInvisibleToMortals).Select(pl => pl.Name).ToList(),
                        world, db));
                }
            }
            catch { /* best-effort */ }
        }
    }
    
}

