using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;

namespace MudServer
{
    // Encapsulates admin command logic previously in CommandHandler.
    // Construct with the same runtime dependencies and call from CommandHandler.
    public class AdminService
    {
        private readonly World _world;
        private readonly SqliteConnection _db;

        public AdminService(World world, SqliteConnection dbConnection)
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
                    "gold"   => ("coin_gold", Currency.CopperPerGold),
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


        public Message? HandleCreateArea(Player player, string command)
        {
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateArea <Direction>");

            string dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            if (player.CurrentRoom.Exits.Any(e =>
                    e.Direction.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            {
                return MessageHandler.System($"Cannot create area. An exit already exists in direction {dir}.");
            }

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
                "Type the option name/number, or type EndEdit to finish."
            );
        }

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

                // If template specifies equipment, instantiate items and assign to NPC
                try
                {
                    var equipTemplates = newNpc.Template?.EquipmentTemplateIds;
                    if (equipTemplates != null && equipTemplates.Count > 0 && _world.ItemFactory != null)
                    {
                        foreach (var equipTemplateId in equipTemplates)
                        {
                            try
                            {
                                var item = _world.ItemFactory.CreateItem(equipTemplateId, "NPC", newNpc.NpcID, newNpc.CurrentRoom?.RoomID ?? 0);
                                MudPersistence.ItemPersistence.SaveItem(_db, item);
                                newNpc.Inventory.Add(item);
                            }
                            catch (Exception iex)
                            {
                                Console.WriteLine($"[NPC] Failed to create/equip item '{equipTemplateId}' for NPC {newNpc.Name}: {iex.Message}");
                            }
                        }
                    }
                }
                catch (Exception exEquip)
                {
                    Console.WriteLine($"[NPC] Equipment assignment error for NPC {newNpc.Name}: {exEquip.Message}");
                }

                // Broadcast updated room so clients immediately see the NPC
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => n.DisplayName).ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
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
        }

        public Message? AddItem(Player player, string itemTemplateId, int quantity = 1)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (_world.ItemFactory == null)
                return MessageHandler.System("Item factory not available.");

            try
            {
                return InventoryManager.AddItemToRoom(_world, _db, player, itemTemplateId, quantity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AddItem][ERROR] AddItem failed: {ex}");
                return MessageHandler.System($"Failed to add item: {ex.Message}");
            }
        }

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
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (room.Inventory == null || room.Inventory.Count == 0)
                return MessageHandler.System("This room contains no items.");

            var lines = room.Inventory.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})");
            return MessageHandler.System("Items in room:\n" + string.Join("\n", lines));
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

        // Handles admin-related pending actions: EditRoom, CreateArea, CreateRoom.
        // Returns non-null Message if handled and the pending action consumed.
        public Message? HandlePendingAction(Player player, PendingAction pa, string input)
        {
            if (pa == null) return null;

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
                        pa.Step = "EditDescription";
                        return MessageHandler.System("Enter new description for this room:");
                    }
                    // NEW: allow editing distant description
                    if (input == "3" || input.Equals("DistantDescription", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditDistantDescription";
                        return MessageHandler.System("Enter new distant description for this room:");
                    }

                    return MessageHandler.System("Invalid option. Type Name, Description, DistantDescription, 1, 2, 3, or EndEdit.");
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

                // NEW: persist DistantDescription edits
                if (pa.Step == "EditDistantDescription")
                {
                    room.DistantDescription = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room distant description updated.");
                }

                return null;
            }

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
                    using var ins = _db.CreateCommand();
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

                    long newAreaId = (long)ins.ExecuteScalar();
                    newArea.AreaID = (int)newAreaId;
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEAREA][ERROR] Failed to insert Area '{input}': {ex.Message}");
                    return MessageHandler.System($"Error creating area {input}: {ex.Message}");
                }

                var newRoom = new Room($"{input} - Undefined Room", "Starting room for the area")
                {
                    AreaID = newArea.AreaID
                };

                int dbID = _world.CreateRoomInDB(newRoom, _db);
                if (dbID == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Failed to create room in database.");
                }

                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea != null)
                    currentArea.Rooms.Add(newRoom);
                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_db);

                player.PendingAction = null!;
                return MessageHandler.System("Area created.");
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

                int dbID = _world.CreateRoomInDB(newRoom, _db);
                if (dbID == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Failed to create room in database.");
                }

                currentArea.Rooms.Add(newRoom);
                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_db);

                player.PendingAction = null!;
                return MessageHandler.System("Room created.");
            }

            return null;
        }

        private static bool IsValidDirection(string dir)
        {
            string[] valid = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "UP", "DOWN" };
            return valid.Any(v => v.Equals(dir, StringComparison.OrdinalIgnoreCase));
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
    }
}