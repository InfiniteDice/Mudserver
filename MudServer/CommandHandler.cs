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

        public CommandHandler(World world, SqliteConnection dbConnection)
        {
            _world = world;
            _dbConnection = dbConnection;
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

            string input = ExtractCommandText(rawCommand).Trim();
            var pa = player.PendingAction;

            // -----------------------
            // AUTH / SIGNUP FLOW
            // -----------------------
            if (pa.Command == "Auth")
            {
                // ChooseOption -> Login / Signup / ForgotPassword
                if (pa.Step == "ChooseOption")
                {
                    if (input == "1")
                    {
                        pa.Step = "LoginName";
                        return MessageHandler.System("Login selected. Enter player name:");
                    }
                    if (input == "2")
                    {
                        pa.Step = "SignupName";
                        return MessageHandler.System("Signup selected. Enter desired player name:");
                    }
                    if (input == "3")
                    {
                        // not implemented
                        player.PendingAction = null;
                        return MessageHandler.System("Password recovery not implemented yet. Returning to main menu.");
                    }

                    return MessageHandler.System("Invalid option. Type 1 for Login, 2 for Signup, or 3 for Forgot Password.");
                }

                // LOGIN: ask name then password
                if (pa.Step == "LoginName")
                {
                    pa.Step = "LoginPassword";
                    pa.Data = new AuthData { Name = input };
                    return MessageHandler.System("Enter your password:");
                }

                if (pa.Step == "LoginPassword")
                {
                    var data = pa.Data as AuthData;
                    string name = data?.Name ?? "";
                    string password = input;

                    try
                    {
                        using var cmd = new SqliteCommand("SELECT PlayerID, Password, LastRoomID, IsAdmin FROM Player WHERE Name = @name", _dbConnection);
                        cmd.Parameters.AddWithValue("@name", name);
                        using var reader = cmd.ExecuteReader();
                        if (!reader.Read())
                        {
                            player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                            return MessageHandler.System("Username not found. Returning to main menu.");
                        }

                        int dbId = reader.GetInt32(0);
                        string storedPassword = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        int lastRoomId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                        bool isAdmin = !reader.IsDBNull(3) && reader.GetInt32(3) != 0;

                        if (storedPassword != password)
                        {
                            player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                            return MessageHandler.System("Incorrect password. Returning to main menu.");
                        }

                        // Success: mark authenticated and load player state
                        player.IsAuthenticated = true;
                        player.Name = name;
                        player.PlayerID = name;
                        player.DbPlayerID = dbId;
                        player.IsAdmin = isAdmin;

                        // Determine start room
                        Room startRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == lastRoomId) ?? _world.CurrentRoom;
                        player.CurrentRoom = startRoom ?? _world.CurrentRoom;

                        // Load carried items (use numeric DbPlayerID)
                        try
                        {
                            if (player.DbPlayerID != 0)
                            {
                                var carried = ItemPersistence.LoadItemsByOwner(_dbConnection, "Player", player.DbPlayerID);
                                if (carried != null && carried.Count > 0)
                                {
                                    player.Inventory = carried;
                                    Console.WriteLine($"[ITEM] Loaded {carried.Count} carried item(s) for DBPlayerID={player.DbPlayerID} at login.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ITEM] Failed to load carried items for DBPlayerID={player.DbPlayerID}: {ex.Message}");
                        }

                        player.PendingAction = null;

                        // Send welcome messages and room
                        player.PendingMessageQueue.Add(MessageHandler.System($"Welcome back, {player.Name}!"));
                        var carriedNames = player.Inventory.Select(i => i.Name).ToList();
                        player.PendingMessageQueue.Add(MessageHandler.PlayerUpdate(player.Name, player.Health, player.MaxHealth, carriedNames));
                        player.PendingMessageQueue.Add(MessageHandler.Room(player.CurrentRoom,
                            player.CurrentRoom.Npcs.Select(n => string.IsNullOrEmpty(n.LastAction) ? n.Name : $"{n.Name} {n.LastAction}").ToList(),
                            player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                            _world));
                        return null;
                    }
                    catch (Exception ex)
                    {
                        player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                        return MessageHandler.System($"Authentication failed (DB error): {ex.Message}");
                    }
                }

                // SIGNUP: Name -> Password -> Email -> create
                if (pa.Step == "SignupName")
                {
                    string name = input;
                    try
                    {
                        using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Player WHERE Name = @name", _dbConnection);
                        checkCmd.Parameters.AddWithValue("@name", name);
                        long count = (long)checkCmd.ExecuteScalar();
                        if (count > 0)
                        {
                            player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                            return MessageHandler.System("That username is already taken. Returning to main menu.");
                        }
                    }
                    catch (Exception ex)
                    {
                        player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                        return MessageHandler.System($"Signup failed (DB error): {ex.Message}");
                    }

                    pa.Step = "SignupPassword";
                    pa.Data = new AuthData { Name = name };
                    return MessageHandler.System("Enter a password for the new account:");
                }

                if (pa.Step == "SignupPassword")
                {
                    var data = pa.Data as AuthData;
                    data.Password = input;
                    pa.Step = "SignupEmail";
                    pa.Data = data;
                    return MessageHandler.System("Enter your email address (for password recovery):");
                }

                if (pa.Step == "SignupEmail")
                {
                    var data = pa.Data as AuthData;
                    string name = data?.Name ?? "";
                    string password = data?.Password ?? "";
                    string email = input;

                    try
                    {
                        // Check email uniqueness
                        using var checkEmail = new SqliteCommand("SELECT COUNT(*) FROM Player WHERE Email = @mail", _dbConnection);
                        checkEmail.Parameters.AddWithValue("@mail", email);
                        long emailCount = (long)checkEmail.ExecuteScalar();
                        if (emailCount > 0)
                        {
                            player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                            return MessageHandler.System("Email already registered. Returning to main menu.");
                        }

                        // Insert new player row
                        using var insert = new SqliteCommand("INSERT INTO Player (Name, Password, Email, LastRoomID) VALUES (@name, @pw, @mail, @rid); SELECT last_insert_rowid();", _dbConnection);
                        insert.Parameters.AddWithValue("@name", name);
                        insert.Parameters.AddWithValue("@pw", password);
                        insert.Parameters.AddWithValue("@mail", email);
                        int startRoomId = _world.CurrentRoom?.RoomID ?? 0;
                        insert.Parameters.AddWithValue("@rid", startRoomId);
                        long newId = (long)insert.ExecuteScalar();

                        // Authenticate and place player
                        player.IsAuthenticated = true;
                        player.Name = name;
                        player.PlayerID = name;
                        player.DbPlayerID = (int)newId;
                        player.CurrentRoom = _world.CurrentRoom ?? _world.Rooms.FirstOrDefault();

                        player.PendingAction = null;

                        // No carried items yet
                        player.PendingMessageQueue.Add(MessageHandler.System($"Account created. Welcome, {player.Name}!"));
                        player.PendingMessageQueue.Add(MessageHandler.PlayerUpdate(player.Name, player.Health, player.MaxHealth, new List<string>()));
                        player.PendingMessageQueue.Add(MessageHandler.Room(player.CurrentRoom,
                            player.CurrentRoom.Npcs.Select(n => string.IsNullOrEmpty(n.LastAction) ? n.Name : $"{n.Name} {n.LastAction}").ToList(),
                            player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                            _world));

                        return null;
                    }
                    catch (Exception ex)
                    {
                        player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                        return MessageHandler.System($"Signup failed (DB error): {ex.Message}");
                    }
                }

                // fallback - reset to main menu
                player.PendingAction = new PendingAction { Command = "Auth", Step = "ChooseOption" };
                return MessageHandler.System("Auth flow interrupted. Choose 1) Login 2) Signup 3) Forgot Password.");
            }

            // -----------------------
            // existing pending action handling (GetItem, DropItem, EditRoom, etc.
            // -----------------------
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

        // Helper class for auth pending data (moved inside CommandHandler)
        private class AuthData
        {
            public string Name { get; set; }
            public string Password { get; set; }
            public string Email { get; set; }
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
