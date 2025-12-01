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

        public Message? ProcessCommand(Player player, string rawCommand)
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

            // Handle 'say' command (delegated to Chat for centralization)
            if (command.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                string text = command.Substring(4);
                Chat.Say(player, text);
                return null;
            }

            // Handle 'yell' command - delegated to Chat.Yell for centralized chat behavior
            if (command.StartsWith("yell ", StringComparison.OrdinalIgnoreCase))
            {
                string text = command.Substring(5);
                Chat.Yell(player, text, _world);
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
                case "look":
                    {
                        var lookParts = command.Split(' ', 2);
                        if (lookParts.Length == 1)
                            return Look(player); // existing room look

                        var targetExpr = lookParts[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are lost in the void.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0)
                            return MessageHandler.System($"No NPC matching '{targetExpr}' found here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        var npc = matches[0];
                        var desc = string.IsNullOrWhiteSpace(npc.Description) ? "No description." : npc.Description;

                        // Append health segment; current in <span class='hp-current'> for client styling.
                        var healthSegment = $" (<span class='hp-current'>{npc.Health}</span>/{npc.HealthMax})";

                        return MessageHandler.System($"{npc.DisplayName}: {desc}{healthSegment}");
                    }
                case "move": return Move(player, command);

                case "attack":
                    {
                        var attackParts = command.Split(' ', 2);
                        if (attackParts.Length < 2) return MessageHandler.System("Usage: attack <target>");
                        var targetExpr = attackParts[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0)
                            return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple targets: {list}");
                        }

                        return Combat.Attack(player, _world, matches[0]); // overload accepting target
                    }

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

                // Handle 'inventory' command - show detailed carried items in modal
                case string s when s.Equals("inventory", StringComparison.OrdinalIgnoreCase) ||
                                  s.Equals("inv", StringComparison.OrdinalIgnoreCase):
                    // Map player's Item objects to MessageHandler.InventoryItem DTOs
                    var invItems = player.Inventory?.Select(i => new MudNetworking.InventoryItem
                    {
                        Name = i.Name ?? "",
                        Weight = i.Weight,
                        Condition = i.Condition,
                        ItemCategory = i.ItemCategory ?? "",
                        ShortDesc = i.ShortDesc ?? ""
                    }).ToList() ?? new List<MudNetworking.InventoryItem>();

                    return MudNetworking.MessageHandler.Inventory(invItems);

                default:
                    return MessageHandler.System($"Unknown command: {command}");
            }
        }

        private Message? Look(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are lost in the void.");

            // Show only NPC names in room payload; action text is delivered as system messages.
            var npcs = room.Npcs
                .Select(n => n.DisplayName)
                .ToList();

            var players = room.Players
                .Where(p => p != player)
                .Select(p => p.Name)
                .ToList();

            return MessageHandler.Room(room, npcs, players, _world);
        }

        private Message? Move(Player player, string rawCommand)
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
        private Message? GetItem(Player player, string requested)
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
        private Message? DropItem(Player player, string requested)
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
        private Message? TransferItemToPlayer(Player player, MudCore.Entities.Item item)
        {
            return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item);
        }

        // Called when player drops a single item into current room
        private Message? TransferItemToRoom(Player player, MudCore.Entities.Item item)
        {
            return InventoryManager.TransferToRoom(_world, _dbConnection, player, item);
        }

        // -------------------------------
        // Pending Actions (extended)
        // -------------------------------
        private Message? CheckPendingAction(Player player, string rawCommand)
        {
            if (player.PendingAction == null) return null;

            // Delegate auth flow to PlayerAuthentication
            var authHandled = _auth.HandleAuthPending(player, rawCommand);
            if (authHandled != null) return authHandled;

            // The auth handler may have completed the flow and cleared PendingAction (e.g. successful login).
            // Stop processing further pending-action logic if PendingAction is now null.
            if (player.PendingAction == null) return null;

            string input = ExtractCommandText(rawCommand).Trim();
            var pa = player.PendingAction!;

            // existing pending action handling (GetItem, DropItem, EditRoom, CreateArea, CreateRoom, etc.)
            if (pa.Command == "GetItem")
            {
                var room = player.CurrentRoom;
                var data = pa.Data as GetPendingData;
                if (data == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to take.");

                    var chosen = data.Matches[idx - 1];
                    player.PendingAction = null!;
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
                    player.PendingAction = null!;

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
                if (data == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to drop.");

                    var chosen = data.Matches[idx - 1];
                    player.PendingAction = null!;
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
                    player.PendingAction = null!;

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

                    return MessageHandler.System("Invalid option. Type Name, Description, 1, 2, or EndEdit.");
                }

                if (pa.Step == "EditName")
                {
                    room.Name = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null!;
                    return MessageHandler.System($"Room name updated to '{room.Name}'.");
                }

                if (pa.Step == "EditDescription")
                {
                    room.Description = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room description updated.");
                }
            }

            // CREATE AREA
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
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
                    player.PendingAction = null!;
                    return MessageHandler.System("Failed to create room in database.");
                }

                newArea.Rooms.Add(newRoom);
                _world.Areas.Add(newArea);

                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null!;

                // 4) Replace CreateArea/CreateRoom return Room calls to use names only (no LastAction formatting)
                return MessageHandler.Room(
                    player.CurrentRoom,
                    player.CurrentRoom.Npcs.Select(n => n.DisplayName).ToList(),
                    player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                    _world);
            }

            // CREATE ROOM
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

                int dbID = _world.CreateRoomInDB(newRoom, _dbConnection);
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
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null!;

                // 4) Replace CreateArea/CreateRoom return Room calls to use names only (no LastAction formatting)
                return MessageHandler.Room(
                    player.CurrentRoom,
                    player.CurrentRoom.Npcs.Select(n => n.DisplayName).ToList(),
                    player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                    _world);
            }

            return null;
        }

        // Helper class for GetItem / DropItem pending data
        private class GetPendingData
        {
            public List<MudCore.Entities.Item> Matches { get; set; } = new List<MudCore.Entities.Item>();
        }

        // -------------------------------
        // Admin Commands
        // -------------------------------
        private Message? HandleCreateArea(Player player, string command)
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

        private Message? HandleCreateRoom(Player player, string command)
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

        private Message? HandleEditRoom(Player player)
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

        private Message? HandleCreateNpc(Player player, string templateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            try
            {
                // Create in-memory NPC via NpcFactory
                var newNpc = _world.NpcFactory.CreateNpc(templateId, room);

                // Persist the NPC and assign NpcID
                int newId = MudPersistence.NpcPersistence.InsertNpc(_dbConnection, newNpc);
                newNpc.NpcID = newId;

                // Ensure world list contains it
                if (!_world.Npcs.Contains(newNpc)) _world.Npcs.Add(newNpc);

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
                                // Create item owned by NPC (OwnerType "NPC", OwnerID = newNpc.NpcID), RoomID set to current room
                                var item = _world.ItemFactory.CreateItem(equipTemplateId, "NPC", newNpc.NpcID, newNpc.CurrentRoom?.RoomID ?? 0);
                                // Persist item (assigns ItemID)
                                MudPersistence.ItemPersistence.SaveItem(_dbConnection, item);
                                // Add to NPC inventory
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

                // Broadcast updated room so clients immediately see the NPC (and any equipment effect)
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => n.DisplayName).ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                        _world));
                }

                // send NPC actions to log (separate system messages, no name+action in badges)
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

        private bool IsValidDirection(string dir)
        {
            string[] valid = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "UP", "DOWN" };
            return valid.Any(v => v.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractCommandText(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand)) return string.Empty;

            var s = rawCommand.Trim();

            // If client sent a JSON payload (e.g. {"type":"command","text":"s"}), extract the text field.
            if (s.StartsWith("{") || s.StartsWith("[") || s.StartsWith("\""))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.String)
                        return root.GetString() ?? string.Empty;

                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("text", out var prop) && prop.ValueKind == JsonValueKind.String)
                            return prop.GetString() ?? string.Empty;

                        if (root.TryGetProperty("command", out prop) && prop.ValueKind == JsonValueKind.String)
                            return prop.GetString() ?? string.Empty;

                        // nested payloads: data.text
                        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                            data.TryGetProperty("text", out var dataText) && dataText.ValueKind == JsonValueKind.String)
                        {
                            return dataText.GetString() ?? string.Empty;
                        }
                    }
                }
                catch
                {
                    // fall through to return trimmed raw string if JSON parsing fails
                }
            }

            return s;
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

        // Admin helpers: add/remove/list items in current room
        private Message? AddItem(Player player, string itemTemplateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (_world.ItemFactory == null)
                return MessageHandler.System("Item factory not available.");

            try
            {
                var item = _world.ItemFactory.CreateItem(itemTemplateId, "ROOM", 0, room.RoomID);
                MudPersistence.ItemPersistence.SaveItem(_dbConnection, item);
                room.Inventory.Add(item);
                room.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                // Broadcast updated room
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

                return MessageHandler.System($"Item '{item.Name}' added to the room.");
            }
            catch (Exception ex)
            {
                return MessageHandler.System($"Failed to add item: {ex.Message}");
            }
        }

        private Message? RemoveItem(Player player, string identifier)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // allow admin only removal
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var target = room.Inventory.FirstOrDefault(i => i.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                                                   || i.TemplateID.Equals(identifier, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                return MessageHandler.System($"No item '{identifier}' found in the room.");

            // Remove from DB and room
            target.OwnerType = "ROOM";
            target.OwnerID = 0;
            MudPersistence.ItemPersistence.SaveItem(_dbConnection, target);

            room.Inventory.Remove(target);
            room.IsDirty = true;
            _world.SaveDirtyRooms(_dbConnection);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
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

        private Message? ListItems(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (room.Inventory == null || room.Inventory.Count == 0)
                return MessageHandler.System("This room contains no items.");

            var lines = room.Inventory.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})");
            return MessageHandler.System("Items in room:\n" + string.Join("\n", lines));
        }
    }
}
