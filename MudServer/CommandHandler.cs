using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using Microsoft.Data.Sqlite;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MudServer
{
    public class CommandHandler
    {
        private readonly World _world;
        private readonly SqliteConnection _dbConnection;
        private readonly PlayerAuthentication _auth;

        public CommandHandler(World world, SqliteConnection dbConnection)
        {
            _world = world;
            _dbConnection = dbConnection;
            _auth = new PlayerAuthentication(world, dbConnection);
        }

        public Message ProcessCommand(Player player, string rawCommand)
        {
            var pendingMsg = CheckPendingAction(player, rawCommand);
            if (pendingMsg != null) return pendingMsg;

            string command = ExtractCommandText(rawCommand).Trim();

            // Handle 'get' or 'take' command
            if (command.StartsWith("get ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("take ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2);
                if (parts.Length < 2) return MessageHandler.System("Usage: get <item>");
                string itemName = parts[1].Trim();
                return GetItem(player, itemName);
            }

            // Handle 'drop' command
            if (command.StartsWith("drop ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2);
                if (parts.Length < 2) return MessageHandler.System("Usage: drop <item>");
                string itemName = parts[1].Trim();
                return DropItem(player, itemName);
            }

            if (IsValidDirection(command))
                return Move(player, command);

            // Handle 'say' command
            if (command.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                string text = command.Substring(4);
                Say(player, text);
                return null;
            }

            // Handle 'yell' command
            if (command.StartsWith("yell ", StringComparison.OrdinalIgnoreCase))
            {
                string text = command.Substring(5);
                Yell(player, text);
                return null;
            }

            // Handle room inventory commands
            if (command.StartsWith("additem ", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin)
                    return MessageHandler.System("Permission denied. Admins only.");
                string item = command.Substring(8).Trim();
                return AddItem(player, item);
            }

            if (command.StartsWith("removeitem ", StringComparison.OrdinalIgnoreCase))
            {
                string item = command.Substring(11).Trim();
                return RemoveItem(player, item);
            }

            if (command.Equals("listitems", StringComparison.OrdinalIgnoreCase))
            {
                return ListItems(player);
            }

            switch (command.Split(' ')[0].ToLower())
            {
                case "look": return Look(player);
                case "move": return Move(player, command);

                case "attack":
                    return Combat.Attack(player, _world);   // Logic moved to Combat.cs

                case string s when s.StartsWith("createarea", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return HandleCreateArea(player, command);

                case string s when s.StartsWith("createroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return HandleCreateRoom(player, command);

                case string s when s.StartsWith("editroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return HandleEditRoom(player);

                // Handle 'createnpc' admin command (create NPC instance from a JSON template)
                case string s when s.StartsWith("createnpc", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    var parts = command.Split(' ', 2);
                    if (parts.Length < 2) return MessageHandler.System("Usage: createnpc <templateId>");
                    string templateId = parts[1].Trim();
                    return HandleCreateNpc(player, templateId);

                default:
                    return MessageHandler.System($"Unknown command: {command}");
            }
        }

        private void Say(Player player, string text)
        {
            if (player.CurrentRoom == null)
            {
                player.PendingMessageQueue.Add(MessageHandler.System("You are lost in the void."));
                return;
            }

            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                player.PendingMessageQueue.Add(MessageHandler.System("You must say something."));
                return;
            }

            string messageText = $"{player.Name} says: {text}";

            foreach (var p in player.CurrentRoom.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.System(messageText));
            }
        }

        private void Yell(Player player, string text)
        {
            if (player.CurrentRoom == null)
            {
                player.PendingMessageQueue.Add(MessageHandler.System("You are lost in the void."));
                return;
            }

            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                player.PendingMessageQueue.Add(MessageHandler.System("You must yell something."));
                return;
            }

            string messageText = $"{player.Name} yells: {text}";

            // Broadcast to players in current room
            foreach (var p in player.CurrentRoom.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.System(messageText));
            }

            // Broadcast to players in connected rooms
            foreach (var exit in player.CurrentRoom.Exits)
            {
                var targetRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                if (targetRoom == null) continue;

                foreach (var p in targetRoom.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.System($"From {player.CurrentRoom.Name}: {messageText}"));
                }
            }
        }

        private string ExtractCommandText(string rawCommand)
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(rawCommand);

                if (json.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() == "command" &&
                    json.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString();
                }
            }
            catch { }

            return rawCommand;
        }

        private Message Look(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are lost in the void.");

            var npcs = room.Npcs
                .Select(n => string.IsNullOrEmpty(n.LastAction) ? n.Name : $"{n.Name} {n.LastAction}")
                .ToList();

            var players = room.Players
                .Where(p => p != player)
                .Select(p => p.Name)
                .ToList();

            return MessageHandler.Room(room, npcs, players, _world);
        }

        private Message Move(Player player, string rawCommand)
        {
            string dir;
            var parts = rawCommand.Trim().Split(' ', 2);

            if (parts[0].Equals("move", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 2)
                    return MessageHandler.System("Usage: move <direction>");
                dir = parts[1].Trim().ToUpper();
            }
            else
            {
                dir = rawCommand.Trim().ToUpper();
            }

            return Movement.AttemptMove(player, dir, _world);
        }

        // -------------------------------
        // GET / TAKE
        // -------------------------------
        private Message GetItem(Player player, string requested)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Use InventoryManager to find matches
            var matches = InventoryManager.FindRoomMatches(room, requested);

            if (matches.Count == 0)
                return MessageHandler.System($"No '{requested}' found in this room.");

            if (matches.Count == 1)
            {
                // Single match - transfer immediately using the InventoryManager
                return InventoryManager.TransferToPlayer(_world, _dbConnection, player, matches[0]);
            }

            // Multiple matches: check if they are identical (same TemplateID)
            bool allSameTemplate = matches.All(i => i.TemplateID == matches[0].TemplateID);

            if (allSameTemplate)
            {
                // Ask how many to pick up
                player.PendingAction = new PendingAction
                {
                    Command = "GetItem",
                    Step = "ChooseQuantity",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"There are {matches.Count} of '{matches[0].Name}'. How many do you want to get?");
            }
            else
            {
                // Different kinds - ask which one (show indices)
                var listText = string.Join("\n", matches.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})"));
                player.PendingAction = new PendingAction
                {
                    Command = "GetItem",
                    Step = "SelectIndex",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"Multiple items match '{requested}'. Choose which one:\n{listText}\nType the number of the item to take.");
            }
        }

        // -------------------------------
        // DROP
        // -------------------------------
        private Message DropItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Find matches in player's inventory
            var matches = player.Inventory
                .Where(i => i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                         || i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                // try partial
                matches = player.Inventory
                    .Where(i => i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (matches.Count == 0)
                return MessageHandler.System($"You are not carrying '{requested}'.");

            if (matches.Count == 1)
            {
                return InventoryManager.TransferToRoom(_world, _dbConnection, player, matches[0]);
            }

            bool allSameTemplate = matches.All(i => i.TemplateID == matches[0].TemplateID);

            if (allSameTemplate)
            {
                player.PendingAction = new PendingAction
                {
                    Command = "DropItem",
                    Step = "ChooseQuantity",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"You have {matches.Count} of '{matches[0].Name}'. How many do you want to drop?");
            }
            else
            {
                var listText = string.Join("\n", matches.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})"));
                player.PendingAction = new PendingAction
                {
                    Command = "DropItem",
                    Step = "SelectIndex",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"Multiple carried items match '{requested}'. Choose which one to drop:\n{listText}\nType the number of the item to drop.");
            }
        }

        // Called when we have a single item to transfer to player
        private Message TransferItemToPlayer(Player player, MudCore.Entities.Item item)
        {
            return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item);
        }

        // Called when player drops a single item into current room
        private Message TransferItemToRoom(Player player, MudCore.Entities.Item item)
        {
            return InventoryManager.TransferToRoom(_world, _dbConnection, player, item);
        }

        // -------------------------------
        // Pending Actions (extended)
        // -------------------------------
        private Message CheckPendingAction(Player player, string rawCommand)
        {
            if (player.PendingAction == null) return null;

            // Delegate auth flow to PlayerAuthentication
            var authHandled = _auth.HandleAuthPending(player, rawCommand);
            if (authHandled != null) return authHandled;

            // The auth handler may have completed the flow and cleared PendingAction (e.g. successful login).
            // Stop processing further pending-action logic if PendingAction is now null.
            if (player.PendingAction == null) return null;

            string input = ExtractCommandText(rawCommand).Trim();
            var pa = player.PendingAction;

            // existing pending action handling (GetItem, DropItem, EditRoom, CreateArea, CreateRoom, etc.)
            if (pa.Command == "GetItem")
            {
                var room = player.CurrentRoom;
                var data = pa.Data as GetPendingData;

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to take.");

                    var chosen = data.Matches[idx - 1];
                    player.PendingAction = null;
                    return TransferItemToPlayer(player, chosen);
                }

                if (pa.Step == "ChooseQuantity")
                {
                    if (!int.TryParse(input, out int quantity) || quantity < 1)
                        return MessageHandler.System("Please enter a positive number.");

                    int available = data.Matches.Count;
                    if (quantity > available)
                        return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                    // Transfer 'quantity' items (they are identical instances)
                    var toTransfer = data.Matches.Take(quantity).ToList();
                    player.PendingAction = null;

                    foreach (var it in toTransfer)
                    {
                        // reuse TransferItemToPlayer which persists
                        TransferItemToPlayer(player, it);
                    }

                    return MessageHandler.System($"You picked up {quantity} x {toTransfer.First().Name}.");
                }
            }

            // Handle DropItem multi-step interaction (mirror GetItem)
            if (pa.Command == "DropItem")
            {
                var room = player.CurrentRoom;
                var data = pa.Data as GetPendingData;

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to drop.");

                    var chosen = data.Matches[idx - 1];
                    player.PendingAction = null;
                    return TransferItemToRoom(player, chosen);
                }

                if (pa.Step == "ChooseQuantity")
                {
                    if (!int.TryParse(input, out int quantity) || quantity < 1)
                        return MessageHandler.System("Please enter a positive number.");

                    int available = data.Matches.Count;
                    if (quantity > available)
                        return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                    var toTransfer = data.Matches.Take(quantity).ToList();
                    player.PendingAction = null;

                    foreach (var it in toTransfer)
                    {
                        TransferItemToRoom(player, it);
                    }

                    return MessageHandler.System($"You dropped {quantity} x {toTransfer.First().Name}.");
                }
            }

            // EDIT ROOM
            if (pa.Command == "EditRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null;
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

                    return MessageHandler.System("Invalid option. Type Name, Description, 1, 2, or EndEdit.");
                }

                if (pa.Step == "EditName")
                {
                    room.Name = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null;
                    return MessageHandler.System($"Room name updated to '{room.Name}'.");
                }

                if (pa.Step == "EditDescription")
                {
                    room.Description = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null;
                    return MessageHandler.System("Room description updated.");
                }
            }

            // CREATE AREA
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;

                var newArea = new Area(input, _world.Areas.Count + 1);
                var newRoom = new Room($"{input} - Undefined Room", "Starting room for the area")
                {
                    AreaID = newArea.AreaID
                };

                int dbID = _world.CreateRoomInDB(newRoom, _dbConnection);
                if (dbID == 0)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Failed to create room in database.");
                }

                newArea.Rooms.Add(newRoom);
                _world.Areas.Add(newArea);

                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null;

                return MessageHandler.Room(player.CurrentRoom,
                                           player.CurrentRoom.Npcs.Select(n => string.IsNullOrEmpty(n.LastAction) ? n.Name : $"{n.Name} {n.LastAction}").ToList(),
                                           player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                                           _world); // items now included
            }

            // CREATE ROOM
            if (pa.Command == "CreateRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;

                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea == null)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Current room is not in an area.");
                }

                var newRoom = new Room(input, "Newly created room")
                {
                    AreaID = currentArea.AreaID
                };

                int dbID = _world.CreateRoomInDB(newRoom, _dbConnection);
                if (dbID == 0)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Failed to create room in database.");
                }

                currentArea.Rooms.Add(newRoom);

                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null;

                return MessageHandler.Room(player.CurrentRoom,
                                           player.CurrentRoom.Npcs.Select(n => string.IsNullOrEmpty(n.LastAction) ? n.Name : $"{n.Name} {n.LastAction}").ToList(),
                                           player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                                           _world); // items now included
            }

            return null;
        }

        // Helper class for GetItem / DropItem pending data
        private class GetPendingData
        {
            public List<MudCore.Entities.Item> Matches { get; set; }
        }

        // -------------------------------
        // Admin Commands
        // -------------------------------
        private Message HandleCreateArea(Player player, string command)
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

        private Message HandleCreateRoom(Player player, string command)
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

        private Message HandleEditRoom(Player player)
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
                "Type the option name/number, or type EndEdit to finish."
            );
        }

        private Message HandleCreateNpc(Player player, string templateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            try
            {
                // Create in-memory NPC via NpcFactory
                var npc = _world.NpcFactory.CreateNpc(templateId, room);

                // Persist the NPC and assign NpcID
                int newId = MudPersistence.NpcPersistence.InsertNpc(_dbConnection, npc);
                npc.NpcID = newId;

                // Ensure world list contains it
                if (!_world.Npcs.Contains(npc)) _world.Npcs.Add(npc);

                // If template specifies equipment, instantiate items and assign to NPC
                try
                {
                    var equipTemplates = npc.Template?.EquipmentTemplateIds;
                    if (equipTemplates != null && equipTemplates.Count > 0 && _world.ItemFactory != null)
                    {
                        foreach (var equipTemplateId in equipTemplates)
                        {
                            try
                            {
                                // Create item owned by NPC (OwnerType "NPC", OwnerID = npc.NpcID), RoomID set to current room
                                var item = _world.ItemFactory.CreateItem(equipTemplateId, "NPC", npc.NpcID, npc.CurrentRoom?.RoomID ?? 0);
                                // Persist item (assigns ItemID)
                                MudPersistence.ItemPersistence.SaveItem(_dbConnection, item);
                                // Add to NPC inventory
                                npc.Inventory.Add(item);
                            }
                            catch (Exception iex)
                            {
                                Console.WriteLine($"[NPC] Failed to create/equip item '{equipTemplateId}' for NPC {npc.Name}: {iex.Message}");
                            }
                        }
                    }
                }
                catch (Exception exEquip)
                {
                    Console.WriteLine($"[NPC] Equipment assignment error for NPC {npc.Name}: {exEquip.Message}");
                }

                // Broadcast updated room so clients immediately see the NPC (and any equipment effect)
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(room,
                        room.Npcs.Select(n => string.IsNullOrEmpty(n.LastAction) ? n.Name : $"{n.Name} {n.LastAction}").ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                        _world));
                }

                return MessageHandler.System($"NPC '{npc.Name}' created (ID {newId}).");
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

        private bool IsValidDirection(string dir)
        {
            string[] valid = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "UP", "DOWN" };
            return valid.Any(v => v.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }

        private string OppositeDirection(string dir) => dir.ToUpper() switch
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

        // -------------------------------
        // Inventory command methods
        // -------------------------------
        private Message AddItem(Player player, string itemName)
        {
            return InventoryManager.AddItemToRoom(_world, _dbConnection, player, itemName);
        }

        private Message RemoveItem(Player player, string itemName)
        {
            return InventoryManager.RemoveItemFromRoom(_dbConnection, player, itemName);
        }

        private Message ListItems(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            var items = InventoryManager.LoadRoomInventory(_dbConnection, room.RoomID);
            room.Inventory = items;

            if (items.Count == 0)
                return MessageHandler.System("The room contains no items.");

            string itemList = string.Join(", ", items.Select(i => i.Name));
            return MessageHandler.System($"Items in the room: {itemList}");
        }
    }
}
