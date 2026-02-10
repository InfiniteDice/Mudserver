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
    public class AdminService
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

        // HandleEditRoomPending: processes removed as redundant.

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
                        room.Npcs.Select(n => n.DisplayName).ToList(),
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
            var menu = summary + "\n\nChoose a field to edit:\n1) NPC Name\n2) NPC Flags\n3) NPC AiBehaviors\n4) Visual Trait\n5) Buffs/Debuffs\n6) Hunger\n7) WanderAreas\nType the option number or name, or 'EndEdit' to finish.";
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
                    room.Npcs.Select(n => n.DisplayName).ToList(),
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

        // Handles admin-related pending actions: EditRoom, CreateArea, CreateRoom.
        // Returns non-null Message if handled and the pending action consumed.
        public Message? HandlePendingAction(Player player, PendingAction pa, string input)
        {
            if (pa == null) return null;

            // --- EditExit pending action ---
            if (pa.Command == "EditExit")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;
                if (room == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("You are not inside a valid room.");
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Edit exit cancelled.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("HasDoor", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditHasDoor";
                        return MessageHandler.System("Set HasDoor: type 't' for true or 'f' for false.");
                    }

                    // New option: create an exit to an existing room
                    if (input == "2" || input.Equals("CreateExitToRoom", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ask admin for the direction to create (allow any valid direction)
                        pa.Step = "CreateExitToRoom_Dir";
                        return MessageHandler.System("Enter exit direction to create (N, S, E, W, NE, NW, SE, SW, UP, DOWN):");
                    }

                    // NEW: Option 3 - EditDoor (only when exit has a door)
                    if (input == "3" || input.Equals("EditDoor", StringComparison.OrdinalIgnoreCase))
                    {
                        // Find the exit by normalized direction
                        var exit = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(pa.Direction), StringComparison.OrdinalIgnoreCase));
                        if (exit == null)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System($"No exit found for direction '{pa.Direction}'.");
                        }

                        if (!exit.DoorId.HasValue)
                        {
                            return MessageHandler.System($"Exit '{exit.Direction}' does not have a door. Set HasDoor to true first (option 1).");
                        }

                        // Store DoorId in pa.Data for subsequent steps
                        var dict = new Dictionary<string, object>
                    {
                        { "DoorId", exit.DoorId.Value },
                        { "Direction", pa.Direction }
                    };
                        pa.Data = dict;
                        pa.Step = "EditDoorMenu";

                        // Load current door metadata from DB
                        string currentType = "Door";
                        List<string> currentFlags = new List<string>();
                        try
                        {
                            using var q = _db.CreateCommand();
                            q.CommandText = "SELECT DoorType, Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                            q.Parameters.AddWithValue("@did", exit.DoorId.Value);
                            using var rdr = q.ExecuteReader();
                            if (rdr.Read())
                            {
                                if (!rdr.IsDBNull(0))
                                {
                                    var dt = rdr.GetString(0);
                                    if (!string.IsNullOrWhiteSpace(dt))
                                        currentType = dt;
                                }
                                if (!rdr.IsDBNull(1))
                                {
                                    var fj = rdr.GetString(1);
                                    if (!string.IsNullOrWhiteSpace(fj))
                                    {
                                        try
                                        {
                                            var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                            if (parsed != null) currentFlags = parsed;
                                        }
                                        catch { /* fallback to empty */ }
                                    }
                                }
                            }
                        }
                        catch (Exception exLoad)
                        {
                            Console.WriteLine($"[ADMIN][EditDoor] Failed to load door metadata: {exLoad.Message}");
                        }

                        var flagsDisplay = currentFlags.Count == 0 ? "(none)" : string.Join(", ", currentFlags);
                        return MessageHandler.System(
                            $"Editing door for exit '{exit.Direction}' (DoorId={exit.DoorId.Value})\n" +
                            $"Current DoorType: {currentType}\n" +
                            $"Current Flags: {flagsDisplay}\n\n" +
                            "Options:\n" +
                            "1) Set DoorType\n" +
                            "2) Add Flag\n" +
                            "3) Remove Flag\n" +
                            "4) Done\n" +
                            "Type the option number or 'EndEdit' to cancel."
                        );
                    }


                    return MessageHandler.System("Invalid option. Type '1' or 'HasDoor' or '2' or 'CreateExitToRoom' or '3' or 'EditExit', or 'EndEdit'.");
                }

                // --- CreateExitToRoom: Step 1 = direction ---
                if (pa.Step == "CreateExitToRoom_Dir")
                {
                    var dirInput = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(dirInput))
                        return MessageHandler.System("Enter a direction (N, S, E, W, NE, NW, SE, SW, UP, DOWN).");

                    if (!IsValidDirection(dirInput))
                        return MessageHandler.System("Invalid direction. Use one of: N, S, E, W, NE, NW, SE, SW, UP, DOWN.");

                    // store new direction normalized in pa.Data
                    var dict = pa.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
                    dict["NewDir"] = NormalizeDirection(dirInput);
                    pa.Data = dict;

                    pa.Step = "CreateExitToRoom_RoomId";
                    // Added abort hint so admin can back out if they don't remember the RoomID.
                    return MessageHandler.System("Enter RoomID (integer) to link this exit to (or type 'EndEdit' to cancel):");
                }

                // --- CreateExitToRoom: Step 2 = room id ---
                if (pa.Step == "CreateExitToRoom_RoomId")
                {
                    var dict = pa.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
                    if (!dict.TryGetValue("NewDir", out var ndObj) || ndObj == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: missing direction. Restart edit and try again.");
                    }

                    string newDir = ndObj.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(newDir))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: invalid direction stored. Restart edit and try again.");
                    }

                    if (!int.TryParse((input ?? "").Trim(), out var targetRoomId))
                        return MessageHandler.System("Invalid RoomID. Enter an integer RoomID.");

                    // Validate target room exists
                    var targetRoom = _world.Rooms.FirstOrDefault(r => r != null && r.RoomID == targetRoomId);
                    if (targetRoom == null)
                        return MessageHandler.System($"RoomID {targetRoomId} not found.");

                    // Perform DB upsert for both sides (src -> target, and reverse target -> src) within writer
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            // Helper: upsert by RoomID+ToRoomID (preserve metadata)
                            void UpsertExit(int rRoomId, string dir, int toRoomId)
                            {
                                using var upd = conn.CreateCommand();
                                upd.Transaction = tx;
                                upd.CommandText = @"
                                    UPDATE RoomExit
                                    SET Direction = @dir,
                                        IsHidden = @hidden,
                                        DiscoverKeyword = @keyword,
                                        DoorId = @doorId
                                    WHERE RoomID = @rid AND ToRoomID = @toRid;";
                                upd.Parameters.AddWithValue("@rid", rRoomId);
                                upd.Parameters.AddWithValue("@dir", dir ?? "");
                                upd.Parameters.AddWithValue("@toRid", toRoomId);
                                upd.Parameters.AddWithValue("@hidden", 0);
                                upd.Parameters.AddWithValue("@keyword", "");
                                upd.Parameters.AddWithValue("@doorId", 0);

                                var affected = upd.ExecuteNonQuery();
                                if (affected == 0)
                                {
                                    using var ins = conn.CreateCommand();
                                    ins.Transaction = tx;
                                    ins.CommandText = @"
                                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                                    ins.Parameters.AddWithValue("@rid", rRoomId);
                                    ins.Parameters.AddWithValue("@dir", dir ?? "");
                                    ins.Parameters.AddWithValue("@toRid", toRoomId);
                                    ins.ExecuteNonQuery();
                                }
                            }

                            // Upsert target->src first (reverse), using opposite direction
                            var opp = OppositeDirection(newDir);
                            UpsertExit(targetRoomId, opp, room.RoomID);

                            // Upsert src->target using provided newDir
                            UpsertExit(room.RoomID, newDir, targetRoomId);

                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(8));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditExit][CreateExitToRoom] DB write failed: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to create exit due to a server error.");
                    }

                    // Update in-memory exits (avoid duplicates)
                    try
                    {
                        // Add src -> target
                        if (!room.Exits.Any(x => x.ToRoomID == targetRoomId && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(newDir), StringComparison.OrdinalIgnoreCase)))
                        {
                            room.Exits.Add(new Room.RoomExit(newDir, targetRoomId));
                            room.IsDirty = true;
                        }

                        // Add reverse exit in target room
                        var reverseDir = OppositeDirection(newDir);
                        if (!targetRoom.Exits.Any(x => x.ToRoomID == room.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(reverseDir), StringComparison.OrdinalIgnoreCase)))
                        {
                            targetRoom.Exits.Add(new Room.RoomExit(reverseDir, room.RoomID));
                            targetRoom.IsDirty = true;
                        }

                        // Persist both rooms' exits to DB via RoomPersistence (best-effort)
                        try
                        {
                            RoomPersistence.SaveRoomExits(_db, targetRoom);
                        }
                        catch { /* best-effort */ }
                        try
                        {
                            RoomPersistence.SaveRoomExits(_db, room);
                        }
                        catch { /* best-effort */ }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditExit][CreateExitToRoom] In-memory update failed: {ex.Message}");
                    }

                    // Broadcast updated room(s)
                    try
                    {
                        foreach (var p in room.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                room,
                                room.Npcs.Select(n => n.DisplayName).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }

                        foreach (var p in targetRoom.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                targetRoom,
                                targetRoom.Npcs.Select(n => n.DisplayName).ToList(),
                                targetRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Created exit '{newDir}' linking Room {room.RoomID} -> Room {targetRoomId} (reverse '{OppositeDirection(newDir)}' also created).");
                }

                // --- EditHasDoor preserved unchanged (existing logic) ---
                if (pa.Step == "EditHasDoor")
                {
                    var v = input?.Trim().ToLowerInvariant();
                    if (v != "t" && v != "f" && v != "true" && v != "false")
                        return MessageHandler.System("Invalid value. Type 't' (true) or 'f' (false).");

                    bool newVal = (v == "t" || v == "true");

                    // Find the exit by normalized direction
                    var exit = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(pa.Direction), StringComparison.OrdinalIgnoreCase));
                    if (exit == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System($"No exit found for direction '{pa.Direction}'.");
                    }

                    try
                    {
                        if (newVal)
                        {
                            if (!exit.DoorId.HasValue)
                            {
                                int newDoorId = DbWriteQueue.Instance.RunWriteAndWait<int>((conn, tx) =>
                                {
                                    using var ins = conn.CreateCommand();
                                    ins.Transaction = tx;
                                    ins.CommandText = "INSERT INTO Doors (IsOpen, DoorType, Flags) VALUES (0, 'Door', '[\"Close:30\"]');";
                                    ins.ExecuteNonQuery();

                                    using var last = conn.CreateCommand();
                                    last.Transaction = tx;
                                    last.CommandText = "SELECT last_insert_rowid();";
                                    var vobj = last.ExecuteScalar();
                                    var createdId = Convert.ToInt32(vobj);

                                    void UpsertExitInternal(int rRoomId, string dir, int toRoomId, int doorIdVal, bool isHidden, string? keyword)
                                    {
                                        using var upd = conn.CreateCommand();
                                        upd.Transaction = tx;
                                        upd.CommandText = @"
                                    UPDATE RoomExit
                                    SET Direction = @dir,
                                        IsHidden = @hidden,
                                        DiscoverKeyword = @keyword,
                                        DoorId = @doorId
                                    WHERE RoomID = @rid AND ToRoomID = @toRid;";
                                        upd.Parameters.AddWithValue("@rid", rRoomId);
                                        upd.Parameters.AddWithValue("@dir", dir ?? "");
                                        upd.Parameters.AddWithValue("@toRid", toRoomId);
                                        upd.Parameters.AddWithValue("@hidden", isHidden ? 1 : 0);
                                        upd.Parameters.AddWithValue("@keyword", keyword ?? "");
                                        upd.Parameters.AddWithValue("@doorId", doorIdVal);

                                        var affected = upd.ExecuteNonQuery();
                                        if (affected == 0)
                                        {
                                            using var insCmd = conn.CreateCommand();
                                            insCmd.Transaction = tx;
                                            insCmd.CommandText = @"
                                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                                        VALUES (@rid, @dir, @toRid, @hidden, @keyword, @doorId);";
                                            insCmd.Parameters.AddWithValue("@rid", rRoomId);
                                            insCmd.Parameters.AddWithValue("@dir", dir ?? "");
                                            insCmd.Parameters.AddWithValue("@toRid", toRoomId);
                                            insCmd.Parameters.AddWithValue("@hidden", isHidden ? 1 : 0);
                                            insCmd.Parameters.AddWithValue("@keyword", keyword ?? "");
                                            insCmd.Parameters.AddWithValue("@doorId", doorIdVal);
                                            insCmd.ExecuteNonQuery();
                                        }
                                    }

                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && mateRoom != null)
                                    {
                                        UpsertExitInternal(mateRoom.RoomID, mateExit.Direction, room.RoomID, createdId, mateExit.IsHidden, mateExit.DiscoverKeyword);
                                    }

                                    UpsertExitInternal(room.RoomID, exit.Direction, exit.ToRoomID, createdId, exit.IsHidden, exit.DiscoverKeyword);

                                    return createdId;
                                }, timeout: TimeSpan.FromSeconds(5));

                                exit.DoorId = newDoorId;
                                exit.DoorIsOpen = false;
                                room.IsDirty = true;

                                try
                                {
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && !mateExit.DoorId.HasValue)
                                    {
                                        mateExit.DoorId = newDoorId;
                                        mateExit.DoorIsOpen = false;
                                        mateRoom!.IsDirty = true;
                                    }
                                }
                                catch { /* best-effort in-memory sync */ }
                            }
                        }
                        else
                        {
                            int? oldDoor = exit.DoorId;
                            exit.DoorId = null;

                            if (oldDoor.HasValue)
                            {
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                                {
                                    try
                                    {
                                        var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                        Room.RoomExit? mateExit = null;
                                        if (mateRoom != null && mateRoom.Exits != null)
                                        {
                                            string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                            mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                                e.ToRoomID == room.RoomID &&
                                                string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                                ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                        }

                                        if (mateRoom != null && mateExit != null)
                                        {
                                            using var cmd = conn.CreateCommand();
                                            cmd.Transaction = tx;
                                            cmd.CommandText = @"
                                        UPDATE RoomExit
                                        SET DoorId = 0
                                        WHERE RoomID = @mrid AND ToRoomID = @toRid AND DoorId = @door;";
                                            cmd.Parameters.AddWithValue("@mrid", mateRoom.RoomID);
                                            cmd.Parameters.AddWithValue("@toRid", room.RoomID);
                                            cmd.Parameters.AddWithValue("@door", oldDoor.Value);
                                            cmd.ExecuteNonQuery();
                                        }

                                        using var cmd2 = conn.CreateCommand();
                                        cmd2.Transaction = tx;
                                        cmd2.CommandText = @"
                                    UPDATE RoomExit
                                    SET DoorId = 0
                                    WHERE RoomID = @rid AND ToRoomID = @toRid AND DoorId = @door;";
                                        cmd2.Parameters.AddWithValue("@rid", room.RoomID);
                                        cmd2.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                                        cmd2.Parameters.AddWithValue("@door", oldDoor.Value);
                                        cmd2.ExecuteNonQuery();
                                    }
                                    catch (Exception ex)
                                    {
                                        throw new Exception("Failed to clear DoorId in DB: " + ex.Message, ex);
                                    }
                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(5));

                                try
                                {
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && mateExit.DoorId.HasValue && mateExit.DoorId.Value == oldDoor.Value)
                                    {
                                        mateExit.DoorId = null;
                                        mateExit.DoorIsOpen = null;
                                        mateRoom!.IsDirty = true;
                                    }
                                }
                                catch { /* best-effort */ }
                            }
                        }

                        room.IsDirty = true;
                        _world.SaveDirtyRooms(_db);

                        try
                        {
                            var mateRoomForSave = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                            if (mateRoomForSave != null)
                                RoomPersistence.SaveRoomExits(_db, mateRoomForSave);

                            RoomPersistence.SaveRoomExits(_db, room);
                        }
                        catch (Exception exSave)
                        {
                            Console.WriteLine($"[ADMIN][EditExit] SaveRoomExits failed: {exSave.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EDITEXIT] Failed saving exit change: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save exit changes due to a server error.");
                    }

                    try
                    {
                        foreach (var p in room.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                room,
                                room.Npcs.Select(n => n.DisplayName).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }

                        try
                        {
                            var mateRoomNotify = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                            if (mateRoomNotify != null)
                            {
                                foreach (var p in mateRoomNotify.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        mateRoomNotify,
                                        mateRoomNotify.Npcs.Select(n => n.DisplayName).ToList(),
                                        mateRoomNotify.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world, _db));
                                }
                            }
                        }
                        catch { /* best-effort */ }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Exit '{exit.Direction}' HasDoor set to {(exit.DoorId.HasValue ? "true" : "false")}.");
                }

                // NEW: EditDoorMenu step
                if (pa.Step == "EditDoorMenu")
                {
                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("4", StringComparison.OrdinalIgnoreCase) || input.Equals("Done", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Door editing complete.");
                    }

                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found. Restart edit.");
                    }

                    if (input.Equals("1", StringComparison.OrdinalIgnoreCase) || input.Equals("Set DoorType", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "SetDoorType";
                        return MessageHandler.System("Enter new DoorType (e.g., Door, Gate, Trapdoor, Portcullis):");
                    }

                    if (input.Equals("2", StringComparison.OrdinalIgnoreCase) || input.Equals("Add Flag", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "AddDoorFlag";
                        return MessageHandler.System("Enter flag to add (e.g., 'Close:30' for 30-second auto-close, 'Lockable' to enable locking):");
                    }

                    if (input.Equals("3", StringComparison.OrdinalIgnoreCase) || input.Equals("Remove Flag", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "RemoveDoorFlag";
                        // Load current flags and display numbered list
                        List<string> flags = new List<string>();
                        try
                        {
                            using var q = _db.CreateCommand();
                            q.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                            q.Parameters.AddWithValue("@did", doorId);
                            var obj = q.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                var fj = obj.ToString();
                                if (!string.IsNullOrWhiteSpace(fj))
                                {
                                    var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                    if (parsed != null) flags = parsed;
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        if (flags.Count == 0)
                            return MessageHandler.System("No flags to remove. Type 'back' to return to menu.");

                        var list = string.Join("\n", flags.Select((f, i) => $"{i + 1}) {f}"));
                        return MessageHandler.System($"Current flags:\n{list}\n\nType the flag number to remove, or 'back':");
                    }

                    return MessageHandler.System("Invalid option. Type 1, 2, 3, 4, or 'EndEdit'.");
                }

                // NEW: SetDoorType step
                if (pa.Step == "SetDoorType")
                {
                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found.");
                    }

                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    var newType = input.Trim();
                    if (string.IsNullOrWhiteSpace(newType) || newType.Length > 50)
                        return MessageHandler.System("Invalid DoorType. Enter a reasonable name (max 50 chars).");

                    // Persist to DB via DbWriteQueue
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Doors SET DoorType = @dtype WHERE DoorId = @did;";
                            cmd.Parameters.AddWithValue("@dtype", newType);
                            cmd.Parameters.AddWithValue("@did", doorId);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditDoor] Failed to update DoorType: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save DoorType due to a server error.");
                    }

                    // Update in-memory cache on both exits
                    try
                    {
                        var currentRoom = player.CurrentRoom;
                        if (currentRoom != null && dict.TryGetValue("Direction", out var dirObj))
                        {
                            var dir = dirObj.ToString();
                            var exit = currentRoom.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                            if (exit != null && exit.DoorId == doorId)
                                exit.DoorType = newType;

                            // Update mate exit
                            var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit?.ToRoomID);
                            if (mateRoom != null && exit != null)
                            {
                                var opp = OppositeDirection(NormalizeDirection(dir));
                                var mateExit = mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == currentRoom.RoomID && string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase));
                                if (mateExit != null && mateExit.DoorId == doorId)
                                    mateExit.DoorType = newType;
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // Broadcast room updates
                    try
                    {
                        BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"DoorType updated to '{newType}'.");
                }

                // NEW: AddDoorFlag step
                if (pa.Step == "AddDoorFlag")
                {
                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found.");
                    }

                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    var newFlag = input.Trim();
                    if (string.IsNullOrWhiteSpace(newFlag))
                        return MessageHandler.System("Flag cannot be empty. Enter a flag or 'EndEdit'.");

                    // Load current flags, remove any existing Close:* flags if adding a new Close:N
                    List<string> flags = new List<string>();
                    try
                    {
                        using var q = _db.CreateCommand();
                        q.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                        q.Parameters.AddWithValue("@did", doorId);
                        var obj = q.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                        {
                            var fj = obj.ToString();
                            if (!string.IsNullOrWhiteSpace(fj))
                            {
                                var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                if (parsed != null) flags = parsed;
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // If adding Close:N, remove existing Close:* flags (only one allowed)
                    if (newFlag.StartsWith("Close:", StringComparison.OrdinalIgnoreCase))
                    {
                        flags.RemoveAll(f => f.StartsWith("Close:", StringComparison.OrdinalIgnoreCase));
                    }

                    // Add new flag (avoid duplicates)
                    if (!flags.Contains(newFlag, StringComparer.OrdinalIgnoreCase))
                        flags.Add(newFlag);

                    // Persist via DbWriteQueue
                    try
                    {
                        var flagsJson = JsonSerializer.Serialize(flags);
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Doors SET Flags = @flags WHERE DoorId = @did;";
                            cmd.Parameters.AddWithValue("@flags", flagsJson);
                            cmd.Parameters.AddWithValue("@did", doorId);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditDoor] Failed to add flag: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save flag due to a server error.");
                    }

                    // Update in-memory cache on both exits
                    try
                    {
                        var currentRoom = player.CurrentRoom;
                        if (currentRoom != null && dict.TryGetValue("Direction", out var dirObj))
                        {
                            var dir = dirObj.ToString();
                            var exit = currentRoom.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                            if (exit != null && exit.DoorId == doorId)
                                exit.DoorFlags = new List<string>(flags);

                            // Update mate exit
                            var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit?.ToRoomID);
                            if (mateRoom != null && exit != null)
                            {
                                var opp = OppositeDirection(NormalizeDirection(dir));
                                var mateExit = mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == currentRoom.RoomID && string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase));
                                if (mateExit != null && mateExit.DoorId == doorId)
                                    mateExit.DoorFlags = new List<string>(flags);
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // Broadcast room updates
                    try
                    {
                        BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Flag '{newFlag}' added.");
                }

                // NEW: RemoveDoorFlag step
                if (pa.Step == "RemoveDoorFlag")
                {
                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found.");
                    }

                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase) || input.Equals("back", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    if (!int.TryParse(input.Trim(), out var idx) || idx < 1)
                        return MessageHandler.System("Invalid index. Enter a flag number or 'back'.");

                    // Load current flags
                    List<string> flags = new List<string>();
                    try
                    {
                        using var q = _db.CreateCommand();
                        q.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                        q.Parameters.AddWithValue("@did", doorId);
                        var obj = q.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                        {
                            var fj = obj.ToString();
                            if (!string.IsNullOrWhiteSpace(fj))
                            {
                                var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                if (parsed != null) flags = parsed;
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    if (idx > flags.Count)
                        return MessageHandler.System($"Invalid index. Enter 1..{flags.Count} or 'back'.");

                    var removed = flags[idx - 1];
                    flags.RemoveAt(idx - 1);

                    // Persist via DbWriteQueue
                    try
                    {
                        var flagsJson = JsonSerializer.Serialize(flags);
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Doors SET Flags = @flags WHERE DoorId = @did;";
                            cmd.Parameters.AddWithValue("@flags", flagsJson);
                            cmd.Parameters.AddWithValue("@did", doorId);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditDoor] Failed to remove flag: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save flags due to a server error.");
                    }

                    // Update in-memory cache on both exits
                    try
                    {
                        var currentRoom = player.CurrentRoom;
                        if (currentRoom != null && dict.TryGetValue("Direction", out var dirObj))
                        {
                            var dir = dirObj.ToString();
                            var exit = currentRoom.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                            if (exit != null && exit.DoorId == doorId)
                                exit.DoorFlags = new List<string>(flags);

                            // Update mate exit
                            var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit?.ToRoomID);
                            if (mateRoom != null && exit != null)
                            {
                                var opp = OppositeDirection(NormalizeDirection(dir));
                                var mateExit = mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == currentRoom.RoomID && string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase));
                                if (mateExit != null && mateExit.DoorId == doorId)
                                    mateExit.DoorFlags = new List<string>(flags);
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // Broadcast room updates
                    try
                    {
                        BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Flag '{removed}' removed.");
                }



                return null;
            }//End EditExit

            // --- Kill pending action ---
            if (pa.Command == "Kill")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                if (pa.Step == "SpecifyTarget")
                {
                    // input contains the target string the admin typed after the prompt
                    player.PendingAction = null!; // consume pending
                    return HandleKill(player, "kill " + (input ?? ""));
                }

                return null;
            }

            // --- EditArea pending action ---
            if (pa.Command == "EditArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Area editing ended.");
                }

                // Resolve area from pa.Data
                var area = _world.Areas?.FirstOrDefault(a => a?.AreaID == player.CurrentRoom?.AreaID);
                if (area == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Area not found.");
                }

                if (pa.Step == "SelectField")
                {
                    var choice = (input ?? "").Trim();
                    if (choice.Equals("1") || choice.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System($"Current name is '{area.Name}'. Enter new name (or 'EndEdit'):");
                    }
                    if (choice.Equals("2") || choice.Equals("IsPvPAllowed", StringComparison.OrdinalIgnoreCase) || choice.Equals("PvP", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditPvP";
                        return MessageHandler.System($"Current IsPvPAllowed is '{area.IsPvPAllowed}'. Enter 'true' or 'false' (or 'EndEdit'):");
                    }
                    if (choice.Equals("3") || choice.Equals("Climate", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditClimate";
                        return MessageHandler.System($"Current Climate is '{area.Climate}'. Enter new climate (e.g., Temperate, Tropical, Arid, Polar, Swampland, Highland, Indoor) or 'EndEdit':");
                    }
                    if (choice.Equals("4") || choice.Equals("Owner", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditOwner";
                        return MessageHandler.System($"Current Owner is '{area.Owner}'. Enter new owner (or 'EndEdit'):");
                    }
                    if (choice.Equals("5") || choice.Equals("MaxNpcs", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditMaxNpcs";
                        return MessageHandler.System($"Current MaxNpcs is '{area.MaxNpcs}'. Enter new max NPCs (or 'EndEdit'):");
                    }

                    return MessageHandler.System("Invalid option. Choose 1..5 or type the field name, or 'EndEdit'.");
                }

                if (pa.Step == "EditName")
                {
                    var newName = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newName))
                        return MessageHandler.System("Name cannot be empty. Enter a valid name or 'EndEdit'.");

                    area.Name = newName;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET Name = @name WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@name", newName);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist Name for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save area name due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Area name updated to '{area.Name}'.");
                }

                if (pa.Step == "EditPvP")
                {
                    var val = (input ?? "").Trim().ToLowerInvariant();
                    if (val != "true" && val != "false" && val != "t" && val != "f")
                        return MessageHandler.System("Invalid value. Enter 'true' or 'false'.");

                    bool newVal = val == "true" || val == "t";
                    area.IsPvPAllowed = newVal;

                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET IsPvPAllowed = @pvp WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@pvp", newVal ? 1 : 0);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist IsPvPAllowed for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save PvP setting due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"IsPvPAllowed updated to '{area.IsPvPAllowed}'.");
                }

                if (pa.Step == "EditClimate")
                {
                    var newClimate = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newClimate))
                        return MessageHandler.System("Climate cannot be empty. Enter a valid climate or 'EndEdit'.");

                    area.Climate = newClimate;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET Climate = @climate WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@climate", newClimate);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist Climate for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save climate due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Climate updated to '{area.Climate}'.");
                }

                if (pa.Step == "EditOwner")
                {
                    var newOwner = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newOwner))
                        return MessageHandler.System("Owner cannot be empty. Enter a valid owner or 'EndEdit'.");

                    area.Owner = newOwner;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET Owner = @owner WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@owner", newOwner);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist Owner for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save owner due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Owner updated to '{area.Owner}'.");
                }

                if (pa.Step == "EditMaxNpcs")
                {
                    if (!int.TryParse((input ?? "").Trim(), out int newMax) || newMax < 1)
                        return MessageHandler.System("MaxNpcs must be an integer >= 1.");

                    area.MaxNpcs = newMax;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET MaxNpcs = @max WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@max", newMax);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist MaxNpcs for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save MaxNpcs due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"MaxNpcs updated to {area.MaxNpcs}.");
                }

                return null;
            } // end EditArea pending action

            // --- EditNpc pending action ---
            if (pa.Command == "EditNpc")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                // helper to resolve npc by id stored in pa.Data
                Npc ResolveNpcFromPa()
                {
                    try
                    {
                        if (pa.Data is Dictionary<string, object> dict && dict.TryGetValue("NpcId", out var nidObj) && int.TryParse(nidObj.ToString(), out var nid))
                        {
                            return _world.Npcs.FirstOrDefault(n => n != null && n.NpcID == nid);
                        }
                    }
                    catch { }
                    return null!;
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    // If we were editing a specific npc, remove the admin edit interrupt (owner-scoped)
                    try
                    {
                        var maybe = ResolveNpcFromPa();
                        if (maybe != null) try { maybe.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System("NPC editing cancelled.");
                }

                // Step: selecting target from a prior presented list
                if (pa.Step == "SelectTarget")
                {
                    if (!int.TryParse(input, out var idx) || idx < 1)
                        return MessageHandler.System("Invalid selection. Type the number of the NPC to edit or 'EndEdit' to cancel.");

                    if (!(pa.Data is Dictionary<string, object> data) || !data.TryGetValue("Matches", out var matchesObj) || !(matchesObj is List<Npc> matches) || matches.Count == 0)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Pending selection list no longer available. Restart edit.");
                    }

                    if (idx > matches.Count)
                        return MessageHandler.System($"Invalid selection. Enter a number between 1 and {matches.Count}.");

                    var chosen = matches[idx - 1];
                    if (chosen == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Selected NPC no longer available.");
                    }

                    // Store selected npc id and present edit menu
                    var dict = new Dictionary<string, object> { { "NpcId", chosen.NpcID } };
                    pa.Data = dict;
                    pa.Step = "SelectField";

                    // Add AdminEdit interrupt on chosen NPC so movement is suppressed while admin edits it.
                    try { chosen.AddInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name, note: "AdminEdit", ttl: null); } catch { }

                    string summary = $"Editing NPC: Template='{chosen.TemplateID}', Name='{chosen.Name}', Flags=[{string.Join(", ", chosen.Flags ?? new List<string>())}], AiBehaviors=[{(chosen.Template?.AiBehaviorConfigs != null ? string.Join(", ", chosen.Template.AiBehaviorConfigs.Select(b => b.Behavior)) : string.Join(", ", chosen.AiBehaviors ?? new List<string>()))}]";
                    var menu = summary + "\n\nChoose a field to edit:\n1) NPC Name\n2) NPC Flags\n3) NPC AiBehaviors\n4) Visual Trait\n5) Buffs/Debuffs\n6) Hunger\n7) WanderAreas\nType the option number or name, or 'EndEdit' to finish.";
                    return MessageHandler.System(menu);
                }

                // Step: choose which field to edit
                if (pa.Step == "SelectField")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("NPC not found. It may have been removed.");
                    }

                    var choice = (input ?? "").Trim();
                    if (choice.Equals("1") || choice.Equals("NPC Name", StringComparison.OrdinalIgnoreCase) || choice.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System($"Current name is '{npc.Name}'. Enter new name (or 'EndEdit'):");
                    }
                    if (choice.Equals("2") || choice.Equals("NPC Flags", StringComparison.OrdinalIgnoreCase) || choice.Equals("Flags", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditFlags";
                        // present flags numbered
                        var flags = npc.Flags ?? new List<string>();
                        if (flags.Count == 0)
                            return MessageHandler.System("NPC has no flags. Type 'add <flag>' to add one, or 'EndEdit' to cancel.");
                        var list = string.Join("\n", flags.Select((f, i) => $"{i + 1}) {f}"));
                        return MessageHandler.System($"Current NPC Flags:\n{list}\n\nCommands: 'add <flag>' or 'remove <n>' or 'edit <n> <newflag>' or 'EndEdit'.");
                    }
                    if (choice.Equals("3") || choice.Equals("NPC AiBehaviors", StringComparison.OrdinalIgnoreCase) || choice.Equals("AiBehaviors", StringComparison.OrdinalIgnoreCase) || choice.Equals("AI", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditAi";
                        var behaviors = npc.Template?.AiBehaviorConfigs != null && npc.Template.AiBehaviorConfigs.Count > 0
                            ? npc.Template.AiBehaviorConfigs.Select(b => b.Behavior).ToList()
                            : (npc.AiBehaviors ?? new List<string>());
                        if (behaviors == null || behaviors.Count == 0)
                            return MessageHandler.System("NPC has no AI behaviors. Type 'add <behavior>' to add one, or 'EndEdit' to cancel.");
                        var list = string.Join("\n", behaviors.Select((b, i) => $"{i + 1}) {b}"));
                        return MessageHandler.System($"Current AI Behaviors:\n{list}\n\nCommands: 'add <behavior>' or 'remove <n>' or 'edit <n> <newbehavior>' or 'EndEdit'.");
                    }
                    if (choice.Equals("4") || choice.Equals("Visual Trait", StringComparison.OrdinalIgnoreCase) || choice.Equals("VisualTrait", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditVisual";
                        var current = string.IsNullOrWhiteSpace(npc.VisualTrait) ? "(none)" : npc.VisualTrait;
                        return MessageHandler.System($"Current visual trait is: {current}\nEnter new visual trait (or 'clear' to remove, 'EndEdit' to cancel):");
                    }
                    if (choice.Equals("5") || choice.Equals("Buffs", StringComparison.OrdinalIgnoreCase) || choice.Equals("Debuffs", StringComparison.OrdinalIgnoreCase) || choice.Equals("Buffs/Debuffs", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditEffects";
                        var buffs = npc.Buffs ?? new List<string>();
                        var debuffs = npc.Debuffs ?? new List<string>();
                        var all = new List<string>();
                        all.AddRange(buffs.Select(b => $"Buff: {b}"));
                        all.AddRange(debuffs.Select(d => $"Debuff: {d}"));
                        var text = all.Count == 0 ? "(none)" : string.Join("\n", all.Select((s, i) => $"{i + 1}) {s}"));
                        return MessageHandler.System($"Current Buffs/Debuffs:\n{text}\n\nCommands: 'add buff <token>', 'add debuff <token>', 'remove buff <token>', 'remove debuff <token>' or 'EndEdit'.");
                    }
                    if (choice.Equals("6") || choice.Equals("Hunger", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditHunger";
                        return MessageHandler.System($"Current Hunger is {npc.Hunger}. Enter new hunger value (0-10) or 'EndEdit':");
                    }
                    if (choice.Equals("7") || choice.Equals("WanderAreas", StringComparison.OrdinalIgnoreCase) || choice.Equals("Wander", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current wander areas and transition to WanderAreas step
                        var areas = npc.WanderAreas ?? new List<int>();
                        string list = areas.Count == 0 ? "(none)" : string.Join(" ", areas.Select(a => $"[{a}]"));
                        pa.Step = "WanderAreas";
                        // Ensure NpcId remains present in pa.Data
                        try
                        {
                            if (!(pa.Data is Dictionary<string, object> dict)) pa.Data = new Dictionary<string, object> { { "NpcId", npc.NpcID } };
                            else dict["NpcId"] = npc.NpcID;
                        }
                        catch { pa.Data = new Dictionary<string, object> { { "NpcId", npc.NpcID } }; }

                        return MessageHandler.System($"Current wander areas are: {list}\nWould you like to Add, or Remove an AreaID? Type: 'Add [AreaID]' or 'Remove [AreaID]', or 'EndEdit' to cancel.");
                    }

                    return MessageHandler.System("Invalid option. Choose 1..7 or type the field name, or 'EndEdit'.");
                }

                // Step: Edit Name
                if (pa.Step == "EditName")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    var newName = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newName)) return MessageHandler.System("Name cannot be empty. Enter a valid name or 'EndEdit'.");

                    npc.Name = newName;
                    try
                    {
                        NpcPersistence.UpdateNpc(_db, npc);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditNpc] Failed to persist name for NpcID={npc.NpcID}: {ex.Message}");
                        return MessageHandler.System("Failed to save NPC name due to a server error.");
                    }

                    // Remove admin-edit interrupt now that admin completed editing
                    try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                    // Notify room occupants
                    try
                    {
                        var room = npc.CurrentRoom;
                        if (room != null)
                        {
                            foreach (var p in room.Players)
                                p.PendingMessageQueue.Add(MessageHandler.Room(room, room.Npcs.Select(n => n.DisplayName).ToList(), room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), _world));
                        }
                    }
                    catch { }

                    player.PendingAction = null!;
                    return MessageHandler.System($"NPC name updated to '{npc.Name}'.");
                }

                // Step: Edit Flags
                if (pa.Step == "EditFlags")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    // add <flag>
                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var flag = cmd.Substring(4).Trim();
                        if (string.IsNullOrWhiteSpace(flag)) return MessageHandler.System("Usage: add <flag>");
                        if (npc.Flags == null) npc.Flags = new List<string>();
                        if (!npc.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)))
                            npc.Flags.Add(flag);

                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Added flag '{flag}' to NPC.");
                    }

                    // remove <n> or remove <flag>
                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = cmd.Substring(7).Trim();
                        if (int.TryParse(arg, out var idx))
                        {
                            var flags = npc.Flags ?? new List<string>();
                            if (idx < 1 || idx > flags.Count) return MessageHandler.System($"Invalid index. Enter 1..{flags.Count} or 'remove <flag>'.");
                            var removed = flags[idx - 1];
                            flags.RemoveAt(idx - 1);
                            npc.Flags = flags;
                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed flag '{removed}'.");
                        }
                        else
                        {
                            var flags = npc.Flags ?? new List<string>();
                            var found = flags.FirstOrDefault(f => string.Equals(f, arg, StringComparison.OrdinalIgnoreCase));
                            if (found == null) return MessageHandler.System($"Flag '{arg}' not found on NPC.");
                            flags.RemoveAll(f => string.Equals(f, arg, StringComparison.OrdinalIgnoreCase));
                            npc.Flags = flags;
                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed flag '{arg}'.");
                        }
                    }

                    // edit <n> <newflag>
                    if (cmd.StartsWith("edit ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = cmd.Substring(5).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: edit <n> <newflag>");
                        if (!int.TryParse(parts[0], out var idx)) return MessageHandler.System("Usage: edit <n> <newflag>");
                        var flags = npc.Flags ?? new List<string>();
                        if (idx < 1 || idx > flags.Count) return MessageHandler.System($"Invalid index. Enter 1..{flags.Count}.");
                        flags[idx - 1] = parts[1].Trim();
                        npc.Flags = flags;
                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags edit for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Updated flag {idx} to '{parts[1].Trim()}'.");
                    }

                    return MessageHandler.System("Invalid flags command. Use 'add <flag>', 'remove <n>' or 'remove <flag>' or 'edit <n> <newflag>' or 'EndEdit'.");
                }

                // Step: Edit AI behaviors
                if (pa.Step == "EditAi")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    // Behaviors list - support editing template-based AiBehaviorConfigs only as simple string fallback
                    var behaviors = npc.Template?.AiBehaviorConfigs != null && npc.Template.AiBehaviorConfigs.Count > 0
                        ? npc.Template.AiBehaviorConfigs.Select(b => b.Behavior).ToList()
                        : (npc.AiBehaviors ?? new List<string>());

                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var b = cmd.Substring(4).Trim();
                        if (string.IsNullOrWhiteSpace(b)) return MessageHandler.System("Usage: add <behavior>");
                        if (npc.AiBehaviors == null) npc.AiBehaviors = new List<string>();
                        npc.AiBehaviors.Add(b);
                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Added AI behavior '{b}'.");
                    }

                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = cmd.Substring(7).Trim();
                        if (int.TryParse(arg, out var idx))
                        {
                            var list = npc.AiBehaviors ?? new List<string>();
                            if (idx < 1 || idx > list.Count) return MessageHandler.System($"Invalid index. Enter 1..{list.Count}.");
                            var removed = list[idx - 1];
                            list.RemoveAt(idx - 1);
                            npc.AiBehaviors = list;
                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed AI behavior '{removed}'.");
                        }
                        else
                        {
                            var list = npc.AiBehaviors ?? new List<string>();
                            if (!list.RemoveAll(s => string.Equals(s, arg, StringComparison.OrdinalIgnoreCase)).Equals(0))
                            {
                                try { NpcPersistence.UpdateNpc(_db, npc); }
                                catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                                // Remove admin-edit interrupt now that admin completed editing
                                try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                                player.PendingAction = null!;
                                return MessageHandler.System($"Removed AI behavior '{arg}'.");
                            }
                            return MessageHandler.System($"Behavior '{arg}' not found.");
                        }
                    }

                    if (cmd.StartsWith("edit ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = cmd.Substring(5).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: edit <n> <newbehavior>");
                        if (!int.TryParse(parts[0], out var idx)) return MessageHandler.System("Usage: edit <n> <newbehavior>");
                        var list = npc.AiBehaviors ?? new List<string>();
                        if (idx < 1 || idx > list.Count) return MessageHandler.System($"Invalid index. Enter 1..{list.Count}.");
                        list[idx - 1] = parts[1].Trim();
                        npc.AiBehaviors = list;
                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai edit for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Updated AI behavior {idx} to '{parts[1].Trim()}'.");
                    }

                    return MessageHandler.System("Invalid AI command. Use 'add <behavior>', 'remove <n>', 'edit <n> <newbehavior>' or 'EndEdit'.");
                }

                // Step: Edit Visual Trait
                if (pa.Step == "EditVisual")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    if (string.Equals(cmd, "clear", StringComparison.OrdinalIgnoreCase))
                        npc.VisualTrait = "";
                    else
                        npc.VisualTrait = cmd;

                    try { NpcPersistence.UpdateNpc(_db, npc); }
                    catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist visual trait for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save visual trait."); }

                    // Remove admin-edit interrupt now that admin completed editing
                    try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                    // Notify room occupants
                    try
                    {
                        var room = npc.CurrentRoom;
                        if (room != null)
                        {
                            foreach (var p in room.Players)
                                p.PendingMessageQueue.Add(MessageHandler.Room(room, room.Npcs.Select(n => n.DisplayName).ToList(), room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), _world));
                        }
                    }
                    catch { }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Visual trait updated to '{npc.VisualTrait}'.");
                }

                // Step: Edit Buffs/Debuffs
                if (pa.Step == "EditEffects")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    // add buff <token> / add debuff <token>
                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = cmd.Substring(4).Trim();
                        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: add buff <token> or add debuff <token>");
                        var which = parts[0].Trim().ToLowerInvariant();
                        var token = parts[1].Trim();
                        if (which == "buff")
                        {
                            if (npc.Buffs == null) npc.Buffs = new List<string>();
                            npc.Buffs.Add(token);
                        }
                        else if (which == "debuff")
                        {
                            if (npc.Debuffs == null) npc.Debuffs = new List<string>();
                            npc.Debuffs.Add(token);
                        }
                        else return MessageHandler.System("Specify 'buff' or 'debuff'. Usage: add buff <token>");

                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist effects add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC effects."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Added {which} '{token}' to NPC.");
                    }

                    // remove buff <token> or remove debuff <token>
                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = cmd.Substring(7).Trim();
                        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: remove buff <token> or remove debuff <token>");
                        var which = parts[0].Trim().ToLowerInvariant();
                        var token = parts[1].Trim();
                        if (which == "buff")
                        {
                            npc.Buffs?.RemoveAll(b => string.Equals(b, token, StringComparison.OrdinalIgnoreCase));
                        }
                        else if (which == "debuff")
                        {
                            npc.Debuffs?.RemoveAll(d => string.Equals(d, token, StringComparison.OrdinalIgnoreCase));
                        }
                        else return MessageHandler.System("Specify 'buff' or 'debuff'. Usage: remove buff <token>");

                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist effects remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC effects."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Removed {which} '{token}' from NPC.");
                    }

                    return MessageHandler.System("Invalid effects command. Use 'add buff <token>', 'add debuff <token>', 'remove buff <token>', 'remove debuff <token>' or 'EndEdit'.");
                }

                // Step: Edit Hunger
                if (pa.Step == "EditHunger")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    if (!int.TryParse(cmd, out var val) || val < 0 || val > 10)
                        return MessageHandler.System("Invalid hunger. Enter an integer between 0 and 10.");

                    npc.Hunger = val;
                    try { NpcPersistence.UpdateNpc(_db, npc); }
                    catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist hunger for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC hunger."); }

                    // Remove admin-edit interrupt now that admin completed editing
                    try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                    player.PendingAction = null!;
                    return MessageHandler.System($"NPC Hunger set to {npc.Hunger}.");
                }

                // Step: WanderAreas - accept "Add <AreaID>" or "Remove <AreaID>"
                if (pa.Step == "WanderAreas")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }

                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase) || string.Equals(cmd, "cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return MessageHandler.System("Usage: Add <AreaID> or Remove <AreaID>");

                    var verb = parts[0].Trim().ToLowerInvariant();
                    if (!int.TryParse(parts[1], out int areaId) || areaId <= 0)
                        return MessageHandler.System("Invalid AreaID. Provide a positive integer.");

                    try
                    {
                        if (npc.WanderAreas == null) npc.WanderAreas = new List<int>();

                        if (verb == "add")
                        {
                            if (!npc.WanderAreas.Contains(areaId))
                                npc.WanderAreas.Add(areaId);
                            else
                                return MessageHandler.System($"AreaID {areaId} is already present in NPC wander areas.");

                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist WanderAreas add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC wander areas."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            var list = string.Join(" ", npc.WanderAreas.Select(a => $"[{a}]"));
                            return MessageHandler.System($"Added AreaID {areaId}. Current wander areas: {list}");
                        }
                        else if (verb == "remove" || verb == "rm")
                        {
                            if (!npc.WanderAreas.Contains(areaId))
                                return MessageHandler.System($"AreaID {areaId} is not present in NPC wander areas.");

                            npc.WanderAreas.RemoveAll(a => a == areaId);

                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist WanderAreas remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC wander areas."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            var list = npc.WanderAreas.Count == 0 ? "(none)" : string.Join(" ", npc.WanderAreas.Select(a => $"[{a}]"));
                            return MessageHandler.System($"Removed AreaID {areaId}. Current wander areas: {list}");
                        }
                        else
                        {
                            return MessageHandler.System("Unknown action. Use 'Add <AreaID>' or 'Remove <AreaID>'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        player.PendingAction = null!;
                        Console.WriteLine($"[ADMIN][EditNpc] Error processing WanderAreas edit: {ex.Message}");
                        return MessageHandler.System("Failed to update wander areas due to a server error.");
                    }
                }

                return null;
            } // end EditNpc pending action

            // EditRoom pending action
            if (pa.Command == "EditRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Room editing ended.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System("Enter new name for this room:");
                    }
                    if (input == "2" || input.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current description to make editing easier, then prompt for new value.
                        pa.Step = "EditDescription";
                        var current = string.IsNullOrWhiteSpace(room?.Description) ? "(no current description)" : room.Description;
                        return MessageHandler.System($"Current description is:\n{current}\n\nEnter new description for this room:");
                    }
                    // NEW: allow editing distant description
                    if (input == "3" || input.Equals("DistantDescription", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current distant description to make editing easier, then prompt for new value.
                        pa.Step = "EditDistantDescription";
                        var currentD = string.IsNullOrWhiteSpace(room?.DistantDescription) ? "(no current distant description)" : room.DistantDescription;
                        return MessageHandler.System($"Current distant description is:\n{currentD}\n\nEnter new distant description for this room:");
                    }

                    // NEW: LLM generation option
                    if (input == "4" || input.Equals("GenerateDescription", StringComparison.OrdinalIgnoreCase) ||
                        input.Equals("Generate Description", StringComparison.OrdinalIgnoreCase) ||
                        input.Equals("Generate Description from Prompt", StringComparison.OrdinalIgnoreCase))
                    {
                        // Transition to collecting operator keywords for AI generation
                        pa.Step = "CollectKeywords";
                        // Store room id for reference in pa.Data
                        pa.Data = new Dictionary<string, object> { { "RoomID", room?.RoomID ?? 0 } };
                        return MessageHandler.System("Enter comma-separated keywords to guide the AI (or type 'none' to skip). Type 'EndEdit' to cancel.");
                    }

                    // NEW: SetRoomFlag option (accept '5' or 'SetRoomFlag')
                    if (input == "5" || input.Equals("SetRoomFlag", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "SetRoomFlag";
                        return MessageHandler.System("Enter the flag string you would like to set for this room:");
                    }

                    return MessageHandler.System("Invalid option. Type Name, Description, DistantDescription, GenerateDescription, SetRoomFlag, 1, 2, 3, 4, 5, or EndEdit.");
                }

                if (pa.Step == "EditName")
                {
                    room.Name = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System($"Room name updated to '{room.Name}'.");
                }

                if (pa.Step == "EditDescription")
                {
                    room.Description = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room description updated.");
                }

                /// handle generate-from-prompt flow - NEW: CollectKeywords -> GenerateDescriptionPreview -> GenerateDescriptionConfirm
                if (pa.Step == "CollectKeywords")
                {
                    // input contains comma-separated keywords (or 'none')
                    var keywordsRaw = (input ?? "").Trim();
                    string[] keywordArray;
                    if (string.Equals(keywordsRaw, "none", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(keywordsRaw))
                        keywordArray = new string[0];
                    else
                    {
                        keywordArray = keywordsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(12).ToArray();
                    }

                    // save into pa.Data dictionary
                    try
                    {
                        if (!(pa.Data is Dictionary<string, object> dict)) dict = new Dictionary<string, object>();
                        dict["Keywords"] = keywordArray;
                        pa.Data = dict;
                    }
                    catch { pa.Data = new Dictionary<string, object> { { "Keywords", keywordArray } }; }

                    // Build structured payload for LLM
                    var lmUrl = Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://127.0.0.1:8000/";
                    var lmModel = Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "default";
                    var systemPrompt = Environment.GetEnvironmentVariable("LMSTUDIO_SYSTEM_PROMPT") ??
                        "You are a concise immersive worldbuilding assistant for a text MUD. Produce exactly one JSON object with keys 'Description' and 'DistantDescription'. Do not include any extra text. Use neutral, evocative language; no markup; avoid profanity. Description should be 60-800 characters. DistantDescription should be one or two brief sentences.";

                    try
                    {
                        // Assemble exits info
                        var exitsList = new List<string>();
                        try
                        {
                            if (room != null && room.Exits != null)
                            {
                                foreach (var ex in room.Exits)
                                {
                                    // toRoomName best-effort
                                    var toRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == ex.ToRoomID);
                                    var toName = toRoom?.Name ?? "";
                                    var dirWord = Movement.DirectionDisplayName(ex.Direction);
                                    var label = ex.DiscoverKeyword ?? "";
                                    // e.g. north: the gate -> Gatehouse
                                    var piece = $"{dirWord}:{(string.IsNullOrWhiteSpace(label) ? ex.Direction : label)}->{toName}";
                                    exitsList.Add(piece);
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        var userBuilder = new System.Text.StringBuilder();
                        userBuilder.AppendLine($"Title: {room?.Name ?? "Unknown"}");
                        userBuilder.AppendLine($"Exits: {string.Join(", ", exitsList)}");
                        if (keywordArray.Length > 0) userBuilder.AppendLine($"Keywords: {string.Join(", ", keywordArray)}");
                        userBuilder.AppendLine($"ExistingDescription: {room?.Description ?? ""}");
                        userBuilder.AppendLine();
                        userBuilder.AppendLine("Produce a JSON object exactly like: {\"Description\":\"<full text>\",\"DistantDescription\":\"<short text>\"} with Description and DistantDescription only.");

                        using var lmClient = new MudServer.Integrations.LmStudioClient(lmUrl);
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));

                        var messages = new List<(string role, string content)>
        {
            ("system", systemPrompt),
            ("user", userBuilder.ToString())
        };

                        using var respDoc = lmClient.CreateChatCompletionAsync(lmModel, messages, temperature: 0.7, ct: cts.Token)
                                                   .GetAwaiter().GetResult();

                        string generated = "";

                        try
                        {
                            var root = respDoc.RootElement;

                            // chat/completions: choices[0].message.content (preferred)
                            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
                            {
                                var first = choices[0];
                                if (first.TryGetProperty("message", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.Object && msg.TryGetProperty("content", out var content))
                                {
                                    generated = content.GetString() ?? "";
                                }
                                else if (first.TryGetProperty("text", out var textProp))
                                {
                                    generated = textProp.GetString() ?? "";
                                }
                            }

                            // fallback: responses style
                            if (string.IsNullOrWhiteSpace(generated) && root.TryGetProperty("output_text", out var outText) && outText.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                generated = outText.GetString() ?? "";
                            }

                            if (string.IsNullOrWhiteSpace(generated) && root.TryGetProperty("output", out var outNode))
                            {
                                if (outNode.ValueKind == System.Text.Json.JsonValueKind.String)
                                    generated = outNode.GetString() ?? "";
                                else if (outNode.ValueKind == System.Text.Json.JsonValueKind.Array && outNode.GetArrayLength() > 0)
                                    generated = string.Join("\n", outNode.EnumerateArray().Select(e => e.GetString() ?? ""));
                            }
                        }
                        catch (Exception exParse)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse LM response container: {exParse.Message}");
                            generated = "";
                        }

                        generated = (generated ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(generated))
                        {
                            return MessageHandler.System("LLM returned empty description. Try different keywords or try again later.");
                        }

                        // Attempt to extract JSON object from generated string
                        string jsonText = generated;
                        try
                        {
                            // If output contains surrounding text, find first { and last } and parse substring
                            int firstIdx = jsonText.IndexOf('{');
                            int lastIdx = jsonText.LastIndexOf('}');
                            if (firstIdx >= 0 && lastIdx > firstIdx)
                                jsonText = jsonText.Substring(firstIdx, lastIdx - firstIdx + 1);

                            using var jd = JsonDocument.Parse(jsonText);
                            var root = jd.RootElement;

                            string desc = "";
                            string distant = "";

                            if (root.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String)
                                desc = d.GetString() ?? "";
                            if (root.TryGetProperty("DistantDescription", out var dd) && dd.ValueKind == JsonValueKind.String)
                                distant = dd.GetString() ?? "";

                            if (string.IsNullOrWhiteSpace(desc))
                            {
                                return MessageHandler.System("LLM returned JSON but Description was empty. Try different keywords.");
                            }

                            // store draft in pa.Data
                            if (!(pa.Data is Dictionary<string, object> dct)) dct = new Dictionary<string, object>();
                            dct["DraftDescription"] = desc.Trim();
                            dct["DraftDistant"] = distant.Trim();
                            dct["Keywords"] = keywordArray;
                            pa.Data = dct;
                            pa.Step = "GenerateDescriptionConfirm";

                            // Present preview and prompt for confirmation
                            var preview = $"Preview - Description:\n{desc}\n\nDistantDescription:\n{distant}\n\nType 'confirm' to save, 'edit' to enter new keywords, or 'cancel'.";
                            return MessageHandler.System(preview);
                        }
                        catch (Exception exJson)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse JSON from LM output: {exJson.Message}");
                            Console.WriteLine($"[LLM][WARN] Raw LM output: {generated}");
                            return MessageHandler.System("LLM returned output that could not be parsed as JSON. Try different keywords or try again later.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LLM] Generation failed: {ex.Message}");
                        return MessageHandler.System($"LLM generation failed: {ex.Message}");
                    }
                }

                if (pa.Step == "GenerateDescriptionConfirm")
                {
                    // Expecting confirm / edit / cancel
                    var dct = pa.Data as Dictionary<string, object>;
                    string draftDesc = dct != null && dct.TryGetValue("DraftDescription", out var ddObj) ? (ddObj as string ?? "") : "";
                    string draftDistant = dct != null && dct.TryGetValue("DraftDistant", out var drObj) ? (drObj as string ?? "") : "";

                    var cmd = (input ?? "").Trim().ToLowerInvariant();
                    if (cmd == "confirm")
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(draftDesc))
                            {
                                room.Description = draftDesc;
                                room.DistantDescription = draftDistant;
                                room.IsDirty = true;
                                _world.SaveDirtyRooms(_db);

                                // Broadcast updated room payloads to occupants
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => n.DisplayName).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world));
                                }

                                player.PendingAction = null!;
                                return MessageHandler.System("Generated description saved and broadcast to room.");
                            }
                            else
                            {
                                player.PendingAction = null!;
                                return MessageHandler.System("No draft description available to save.");
                            }
                        }
                        catch (Exception exSave)
                        {
                            Console.WriteLine($"[LLM][ERROR] Failed to save generated description: {exSave.Message}");
                            player.PendingAction = null!;
                            return MessageHandler.System("Failed to save generated description due to a server error.");
                        }
                    }
                    else if (cmd == "edit")
                    {
                        pa.Step = "CollectKeywords";
                        return MessageHandler.System("Enter comma-separated keywords to guide the AI (or 'none' to skip). Type 'EndEdit' to cancel.");
                    }
                    else if (cmd == "cancel" || cmd == "endedit")
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Generation cancelled.");
                    }
                    else
                    {
                        return MessageHandler.System("Invalid option. Type 'confirm' to save, 'edit' to re-enter keywords, or 'cancel'.");
                    }
                }

                // NEW: persist DistantDescription edits
                if (pa.Step == "EditDistantDescription")
                {
                    room.DistantDescription = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room distant description updated.");
                }

                // NEW: SetRoomFlag step (handles input after selecting option 5)
                if (pa.Step == "SetRoomFlag")
                {
                    var flag = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(flag))
                    {
                        return MessageHandler.System("Enter the flag string you would like to set for this room:");
                    }

                    try
                    {
                        // Ensure flags list exists, avoid duplicates (case-insensitive)
                        try
                        {
                            if (room.Flags == null) room.Flags = new List<string>();
                        }
                        catch { /* best-effort */ }

                        bool already = false;
                        try
                        {
                            already = room.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));
                        }
                        catch { already = false; }

                        if (!already)
                        {
                            try
                            {
                                room.Flags.Add(flag);
                            }
                            catch
                            {
                                // fallback: reassign new list
                                var tmp = new List<string>(room.Flags ?? new List<string>()) { flag };
                                room.Flags = tmp;
                            }
                        }

                        // Update FlagsJson if present on Room (best-effort)
                        try
                        {
                            var fj = JsonSerializer.Serialize(room.Flags ?? new List<string>());
                            var prop = room.GetType().GetProperty("FlagsJson");
                            if (prop != null && prop.CanWrite)
                                prop.SetValue(room, fj);
                        }
                        catch { /* best-effort */ }

                        room.IsDirty = true;
                        _world.SaveDirtyRooms(_db);

                        // Broadcast updated room to occupants so clients refresh immediately
                        try
                        {
                            foreach (var p in room.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    room,
                                    room.Npcs.Select(n => n.DisplayName).ToList(),
                                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }
                        catch { /* best-effort */ }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Flag '{flag}' added to room.");
                    }
                    catch (Exception ex)
                    {
                        player.PendingAction = null!;
                        Console.WriteLine($"[ADMIN][SetRoomFlag] Failed to set flag: {ex.Message}");
                        return MessageHandler.System("Failed to set room flag due to a server error.");
                    }
                }

                return null;
            }//End EditRoom

            // CreateArea pending action
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var newArea = new Area(input, 0);

                try
                {
                    // capture current room id and normalized directions for the writer
                    int srcRoomId = player.CurrentRoom?.RoomID ?? 0;
                    string dirNorm = dir;
                    string oppDir = OppositeDirection(dirNorm);

                    // Perform both Area insert and starting Room creation inside single-writer so they are atomic and serialized.
                    var result = MudServer.DbWriteQueue.Instance.RunWriteAndWait<(int areaId, int roomId)>((wConn, wTx) =>
                    {
                        // Insert Area row
                        using var ins = wConn.CreateCommand();
                        ins.Transaction = wTx;
                        ins.CommandText = @"
                    INSERT INTO Area (Name, Description, WorldID, Theme, LevelRangeMin, LevelRangeMax, IsPvPAllowed, Owner)
                    VALUES (@name, @desc, @wid, @theme, @min, @max, @pvp, @owner);
                    SELECT last_insert_rowid();";
                        ins.Parameters.AddWithValue("@name", newArea.Name ?? "");
                        ins.Parameters.AddWithValue("@desc", newArea.Description ?? "");
                        ins.Parameters.AddWithValue("@wid", _world.WorldID);
                        ins.Parameters.AddWithValue("@theme", newArea.Theme ?? "Generic");
                        ins.Parameters.AddWithValue("@min", newArea.LevelRangeMin);
                        ins.Parameters.AddWithValue("@max", newArea.LevelRangeMax);
                        ins.Parameters.AddWithValue("@pvp", newArea.IsPvPAllowed ? 1 : 0);
                        ins.Parameters.AddWithValue("@owner", newArea.Owner ?? "System");

                        long newAreaIdLong = (long)ins.ExecuteScalar();
                        int newAreaId = (int)newAreaIdLong;
                        newArea.AreaID = newAreaId;

                        // Create a starting room for the area using the writer connection/tx (atomic)
                        var newRoom = new Room($"{input} - Undefined Room", "Starting room for the area")
                        {
                            AreaID = newArea.AreaID
                        };

                        int roomDbId = _world.CreateRoomInDB(newRoom, wConn, wTx);

                        // Persist two RoomExit rows linking srcRoom <-> newRoom so reverse exit exists in DB.
                        // Insert src -> new
                        using (var e1 = wConn.CreateCommand())
                        {
                            e1.Transaction = wTx;
                            e1.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e1.Parameters.AddWithValue("@rid", srcRoomId);
                            e1.Parameters.AddWithValue("@dir", dirNorm ?? "");
                            e1.Parameters.AddWithValue("@toRid", roomDbId);
                            e1.ExecuteNonQuery();
                        }

                        // Insert new -> src (reverse)
                        using (var e2 = wConn.CreateCommand())
                        {
                            e2.Transaction = wTx;
                            e2.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e2.Parameters.AddWithValue("@rid", roomDbId);
                            e2.Parameters.AddWithValue("@dir", oppDir ?? "");
                            e2.Parameters.AddWithValue("@toRid", srcRoomId);
                            e2.ExecuteNonQuery();
                        }

                        return (newAreaId, roomDbId);
                    }, timeout: TimeSpan.FromSeconds(10));

                    // Update in-memory world with new area and room
                    newArea.AreaID = result.areaId;

                    // Try to find the room instance that CreateRoomInDB likely added (writer may have added to _world.Rooms).
                    Room createdRoomInstance = _world.Rooms.FirstOrDefault(r => r.RoomID == result.roomId);
                    if (createdRoomInstance == null)
                    {
                        // If not present, create a local instance and add to world's Rooms.
                        createdRoomInstance = new Room($"{input} - Undefined Room", "Starting room for the area") { AreaID = newArea.AreaID, RoomID = result.roomId };
                        _world.Rooms.Add(createdRoomInstance);
                    }
                    else
                    {
                        // Ensure AreaID is set correctly
                        createdRoomInstance.AreaID = newArea.AreaID;
                    }

                    newArea.Rooms.Add(createdRoomInstance);
                    _world.Areas.Add(newArea);

                    var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                    //CODE REMOVED - 2025-12-21 12:00:00 // 'currentArea.Rooms.Add(createdRoomInstance);' // Rationale: Do not add the new-area room into the previous area's Rooms collection — this caused the client header to resolve the wrong area via Rooms.Contains(...) leading to a flicker.
                    // ensure we didn't accidentally duplicate the room into the wrong area's Rooms list

                    // Link exits in-memory using the authoritative room instance from world.Rooms
                    // ensure we don't duplicate exits
                    if (!player.CurrentRoom.Exits.Any(x => x.ToRoomID == createdRoomInstance.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase)))
                        player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, createdRoomInstance.RoomID));

                    if (!createdRoomInstance.Exits.Any(x => x.ToRoomID == player.CurrentRoom.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(OppositeDirection(dir)), StringComparison.OrdinalIgnoreCase)))
                        createdRoomInstance.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                    player.CurrentRoom.IsDirty = true;
                    createdRoomInstance.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    player.PendingAction = null!;
                    return MessageHandler.System("Area created.");
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEAREA][ERROR] Failed to insert Area '{input}': {ex.Message}");
                    return MessageHandler.System($"Error creating area {input}: {ex.Message}");
                }
            }

            // CreateRoom pending action
            if (pa.Command == "CreateRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Current room is not in an area.");
                }

                var newRoom = new Room(input, "Newly created room")
                {
                    AreaID = currentArea.AreaID
                };

                try
                {
                    int srcRoomId = player.CurrentRoom?.RoomID ?? 0;
                    string dirNorm = dir;
                    string oppDir = OppositeDirection(dirNorm);

                    // Create the room using writer queue so the DB insert and related writes are serialized.
                    int createdRoomId = MudServer.DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                    {
                        int id = _world.CreateRoomInDB(newRoom, wConn, wTx);

                        // Persist exits linking both sides inside writer tx
                        using (var e1 = wConn.CreateCommand())
                        {
                            e1.Transaction = wTx;
                            e1.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e1.Parameters.AddWithValue("@rid", srcRoomId);
                            e1.Parameters.AddWithValue("@dir", dirNorm ?? "");
                            e1.Parameters.AddWithValue("@toRid", id);
                            e1.ExecuteNonQuery();
                        }

                        using (var e2 = wConn.CreateCommand())
                        {
                            e2.Transaction = wTx;
                            e2.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e2.Parameters.AddWithValue("@rid", id);
                            e2.Parameters.AddWithValue("@dir", oppDir ?? "");
                            e2.Parameters.AddWithValue("@toRid", srcRoomId);
                            e2.ExecuteNonQuery();
                        }

                        return id;
                    }, timeout: TimeSpan.FromSeconds(10));

                    if (createdRoomId == 0)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to create room in database.");
                    }

                    // Prefer to locate the authoritative room instance that may have been added by CreateRoomInDB.
                    Room createdRoomInstance = _world.Rooms.FirstOrDefault(r => r.RoomID == createdRoomId);
                    if (createdRoomInstance == null)
                    {
                        createdRoomInstance = new Room(input, "Newly created room") { AreaID = currentArea.AreaID, RoomID = createdRoomId };
                        _world.Rooms.Add(createdRoomInstance);
                    }
                    else
                    {
                        // ensure name/area set
                        createdRoomInstance.Name = input;
                        createdRoomInstance.AreaID = currentArea.AreaID;
                    }

                    currentArea.Rooms.Add(createdRoomInstance);

                    // Link exits in-memory (avoid duplicates)
                    if (!player.CurrentRoom.Exits.Any(x => x.ToRoomID == createdRoomInstance.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase)))
                        player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, createdRoomInstance.RoomID));

                    if (!createdRoomInstance.Exits.Any(x => x.ToRoomID == player.CurrentRoom.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(oppDir), StringComparison.OrdinalIgnoreCase)))
                        createdRoomInstance.Exits.Add(new Room.RoomExit(oppDir, player.CurrentRoom.RoomID));

                    player.CurrentRoom.IsDirty = true;
                    createdRoomInstance.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    // Broadcast updated room views so clients immediately see the new exit
                    try
                    {
                        var srcRoom = player.CurrentRoom;
                        if (srcRoom != null)
                        {
                            foreach (var p in srcRoom.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    srcRoom,
                                    srcRoom.Npcs.Select(n => n.DisplayName).ToList(),
                                    srcRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }

                        if (createdRoomInstance != null)
                        {
                            foreach (var p in createdRoomInstance.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    createdRoomInstance,
                                    createdRoomInstance.Npcs.Select(n => n.DisplayName).ToList(),
                                    createdRoomInstance.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System("Room created.");
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEROOM][ERROR] Failed to create room '{input}': {ex.Message}");
                    return MessageHandler.System("Failed to create room due to a server error.");
                }
            }

            return null;
        } // end HandlePendingAction

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

        // Add after HandleTeleport method (around line 1400)

        public Message? HandleShutdown(Player player, string command, Action onShutdown)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            // Parse command payload after "shutdown"
            var payload = command.Trim();
            if (payload.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring(8).Trim();

            if (string.IsNullOrWhiteSpace(payload))
                return MessageHandler.System("Usage: shutdown <minutes> <reason> <downtime> OR shutdown abort");

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
                        room.Npcs.Select(n => n.DisplayName).ToList(),
                        room.Players.Where(pl => pl != p && !pl.IsInvisibleToMortals).Select(pl => pl.Name).ToList(),
                        world, db));
                }
            }
            catch { /* best-effort */ }
        }
    }
    
}

