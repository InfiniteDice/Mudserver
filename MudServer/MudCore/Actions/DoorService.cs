using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudCore.Entities;
using MudNetworking;
using MudPersistence;

namespace MudCore.Actions
{
    internal class DoorService
    {
        private readonly World.World _world;
        private readonly SqliteConnection _db;

        // NEW: Track doors that need auto-closing
        // Key: DoorId, Value: DateTime when door should auto-close
        private static readonly Dictionary<int, DateTime> _autoCloseDoors = new Dictionary<int, DateTime>();
        private static readonly object _autoCloseLock = new object();

        // NEW: Called by GameLoop every tick to auto-close doors whose timers expired
        public static void TickAutoCloseDoors(World.World world, SqliteConnection db)
        {
            if (world == null || db == null) return;

            List<int> toClose = new List<int>();
            DateTime now = DateTime.UtcNow;

            // Find doors whose timers have expired
            lock (_autoCloseLock)
            {
                foreach (var kvp in _autoCloseDoors)
                {
                    if (now >= kvp.Value)
                        toClose.Add(kvp.Key);
                }

                // Remove from tracking
                foreach (var doorId in toClose)
                    _autoCloseDoors.Remove(doorId);
            }

            // Close each expired door
            foreach (var doorId in toClose)
            {
                try
                {
                    // Update DB
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "UPDATE Doors SET IsOpen = 0 WHERE DoorId = @did;";
                    cmd.Parameters.AddWithValue("@did", doorId);
                    cmd.ExecuteNonQuery();

                    // Update in-memory cache for all exits with this DoorId
                    foreach (var room in world.Rooms)
                    {
                        foreach (var exit in room.Exits.Where(e => e.DoorId == doorId))
                        {
                            exit.DoorIsOpen = false;
                        }
                    }

                    // Broadcast to affected rooms
                    var affectedRooms = world.Rooms
                        .Where(r => r.Exits.Any(e => e.DoorId == doorId))
                        .ToList();

                    foreach (var room in affectedRooms)
                    {
                        foreach (var p in room.Players)
                        {
                            MessageHandler.SendSystem(p, "A door closes automatically.");
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                room,
                                room.Npcs.Select(n => n.DisplayName).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                world, db));
                        }
                    }

                    Console.WriteLine($"[DOOR] Auto-closed DoorId={doorId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DOOR] Auto-close failed for DoorId={doorId}: {ex.Message}");
                }
            }
        }//end TickAutoCloseDoors

       
        /// <summary>
        /// Opens a door by DoorId and schedules auto-close if the door has a Close:[seconds] flag.
        /// This is logic used by NPCs (during pathfinding/movement).
        /// <param name="world">World instance for updating in-memory cache</param>
        /// <returns>True if door was opened successfully</returns>
        public static bool OpenDoorById(int doorId, SqliteConnection db, World.World world = null)
        {
            if (db == null || doorId <= 0) return false;

            try
            {
                // Check if door is already open
                using (var checkCmd = db.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT IsOpen FROM Doors WHERE DoorId = @did";
                    checkCmd.Parameters.AddWithValue("@did", doorId);
                    var checkVal = checkCmd.ExecuteScalar();
                    if (checkVal != null && checkVal != DBNull.Value)
                    {
                        var isOpen = Convert.ToInt32(checkVal) != 0;
                        if (isOpen) return true; // Already open
                    }
                }

                // Update door to open state
                using var u = db.CreateCommand();
                u.CommandText = "UPDATE Doors SET IsOpen = 1 WHERE DoorId = @did";
                u.Parameters.AddWithValue("@did", doorId);
                int affected = u.ExecuteNonQuery();

                if (affected == 0) return false; // Door doesn't exist

                // Check for Close:N flag and schedule auto-close
                try
                {
                    using var flagCmd = db.CreateCommand();
                    flagCmd.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                    flagCmd.Parameters.AddWithValue("@did", doorId);
                    var flagsObj = flagCmd.ExecuteScalar();

                    if (flagsObj != null && flagsObj != DBNull.Value)
                    {
                        var flagsJson = flagsObj.ToString();
                        if (!string.IsNullOrWhiteSpace(flagsJson))
                        {
                            var flags = System.Text.Json.JsonSerializer.Deserialize<List<string>>(flagsJson);
                            var closeFlag = flags?.FirstOrDefault(f => f.StartsWith("Close:", StringComparison.OrdinalIgnoreCase));

                            if (!string.IsNullOrWhiteSpace(closeFlag))
                            {
                                var parts = closeFlag.Split(':');
                                if (parts.Length == 2 && int.TryParse(parts[1], out int seconds) && seconds > 0)
                                {
                                    lock (_autoCloseLock)
                                    {
                                        _autoCloseDoors[doorId] = DateTime.UtcNow.AddSeconds(seconds);
                                    }
                                    Console.WriteLine($"[DOOR] Scheduled auto-close for DoorId={doorId} in {seconds} seconds");
                                }
                            }
                        }
                    }
                }
                catch (Exception exFlag)
                {
                    Console.WriteLine($"[DOOR] Failed to read auto-close flag for DoorId={doorId}: {exFlag.Message}");
                }

                // Update in-memory cache for all exits with this DoorId
                if (world != null)
                {
                    try
                    {
                        foreach (var room in world.Rooms)
                        {
                            foreach (var exit in room.Exits.Where(e => e.DoorId == doorId))
                            {
                                exit.DoorIsOpen = true;
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DOOR] Failed to open door DoorId={doorId}: {ex.Message}");
                return false;
            }
        }

        public DoorService(World.World world, SqliteConnection db)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        

        // Added helper: OppositeDirection is defined locally so DoorService can compute mate exit direction.
        // This mirrors the mapping used elsewhere (Movement, MessageHandler).
        private static string OppositeDirection(string dir) => dir?.ToUpperInvariant() switch
        {
            "N" or "NORTH" => "S",
            "S" or "SOUTH" => "N",
            "E" or "EAST" => "W",
            "W" or "WEST" => "E",
            "NE" or "NORTHEAST" => "SW",
            "SW" or "SOUTHWEST" => "NE",
            "NW" or "NORTHWEST" => "SE",
            "SE" or "SOUTHEAST" => "NW",
            "U" or "UP" => "DOWN",
            "D" or "DOWN" => "UP",
            _ => string.Empty
        };


        public Message OpenDoor(Player player, string dir)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            var targetDir = Movement.NormalizeDirection(dir);
            var exit = room.Exits.FirstOrDefault(e => Movement.NormalizeDirection(e.Direction) == targetDir);

            if (exit == null)
                return MessageHandler.System($"You don't see an exit in that direction.");

            if (!exit.DoorId.HasValue)
                return MessageHandler.System($"There is no door to the {exit.Direction}.");

            // Query door state from Doors table
            try
            {
                using var q = _db.CreateCommand();
                q.CommandText = "SELECT IsOpen FROM Doors WHERE DoorId = @did";
                q.Parameters.AddWithValue("@did", exit.DoorId.Value);
                var val = q.ExecuteScalar();
                if (val == null || val is DBNull)
                {
                    // Missing door row -- treat as no-door
                    return MessageHandler.System($"There is no door to the {exit.Direction}.");
                }

                var isOpen = Convert.ToInt32(val) != 0;
                if (isOpen)
                    return MessageHandler.System($"The door to the {exit.Direction} is already open.");

                // Update door row
                using var u = _db.CreateCommand();
                u.CommandText = "UPDATE Doors SET IsOpen = 1 WHERE DoorId = @did";
                u.Parameters.AddWithValue("@did", exit.DoorId.Value);
                u.ExecuteNonQuery();

                // NEW: Check for Close:N flag and schedule auto-close
                try
                {
                    using var flagCmd = _db.CreateCommand();
                    flagCmd.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                    flagCmd.Parameters.AddWithValue("@did", exit.DoorId.Value);
                    var flagsObj = flagCmd.ExecuteScalar();

                    if (flagsObj != null && flagsObj != DBNull.Value)
                    {
                        var flagsJson = flagsObj.ToString();
                        if (!string.IsNullOrWhiteSpace(flagsJson))
                        {
                            var flags = System.Text.Json.JsonSerializer.Deserialize<List<string>>(flagsJson);
                            var closeFlag = flags?.FirstOrDefault(f => f.StartsWith("Close:", StringComparison.OrdinalIgnoreCase));

                            if (!string.IsNullOrWhiteSpace(closeFlag))
                            {
                                var parts = closeFlag.Split(':');
                                if (parts.Length == 2 && int.TryParse(parts[1], out int seconds) && seconds > 0)
                                {
                                    lock (_autoCloseLock)
                                    {
                                        _autoCloseDoors[exit.DoorId.Value] = DateTime.UtcNow.AddSeconds(seconds);
                                    }
                                    Console.WriteLine($"[DOOR] Scheduled auto-close for DoorId={exit.DoorId.Value} in {seconds} seconds");
                                }
                            }
                        }
                    }
                }
                catch (Exception exFlag)
                {
                    Console.WriteLine($"[DOOR] Failed to read auto-close flag: {exFlag.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DOOR] Failed to open door: {ex.Message}");
                return MessageHandler.System("Failed to open the door due to a server error.");
            }

            // Update in-memory cache on both sides so renders without DB still see the change immediately.
            try
            {
                // set local exit cached state
                exit.DoorIsOpen = true;

                // find mate exit (if any) in the target room and set its cache too
                var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                if (mateRoom != null)
                {
                    // prefer opposite-direction match, fallback to any exit back to this room
                    string opp = OppositeDirection(Movement.NormalizeDirection(exit.Direction));
                    var mateExit = mateRoom.Exits.FirstOrDefault(e =>
                        e.ToRoomID == room.RoomID &&
                        string.Equals(Movement.NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                        ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);

                    if (mateExit != null)
                        mateExit.DoorIsOpen = true;
                }
            }
            catch { /* best-effort: cache update shouldn't block user action */ }

            // Broadcast updated room to occupants (pass DB so MessageHandler can also consult DB if needed)
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

                // Also notify mate room players if present so they see change immediately
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

            return MessageHandler.System($"You open the door to the {exit.Direction}.");
        }

        public Message CloseDoor(Player player, string dir)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            var targetDir = Movement.NormalizeDirection(dir);
            var exit = room.Exits.FirstOrDefault(e => Movement.NormalizeDirection(e.Direction) == targetDir);

            if (exit == null)
                return MessageHandler.System($"You don't see an exit in that direction.");

            if (!exit.DoorId.HasValue)
                return MessageHandler.System($"There is no door to the {exit.Direction}.");

            // Query door state from Doors table
            try
            {
                using var q = _db.CreateCommand();
                q.CommandText = "SELECT IsOpen FROM Doors WHERE DoorId = @did";
                q.Parameters.AddWithValue("@did", exit.DoorId.Value);
                var val = q.ExecuteScalar();
                if (val == null || val is DBNull)
                {
                    // Missing door row -- treat as no-door
                    return MessageHandler.System($"There is no door to the {exit.Direction}.");
                }

                var isOpen = Convert.ToInt32(val) != 0;
                if (!isOpen)
                    return MessageHandler.System($"The door to the {exit.Direction} is already closed.");

                // Update door row
                using var u = _db.CreateCommand();
                u.CommandText = "UPDATE Doors SET IsOpen = 0 WHERE DoorId = @did";
                u.Parameters.AddWithValue("@did", exit.DoorId.Value);
                u.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DOOR] Failed to close door: {ex.Message}");
                return MessageHandler.System("Failed to close the door due to a server error.");
            }

            // Update in-memory cache on both sides so renders without DB still see the change immediately.
            try
            {
                exit.DoorIsOpen = false;

                var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                if (mateRoom != null)
                {
                    string opp = OppositeDirection(Movement.NormalizeDirection(exit.Direction));
                    var mateExit = mateRoom.Exits.FirstOrDefault(e =>
                        e.ToRoomID == room.RoomID &&
                        string.Equals(Movement.NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                        ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);

                    if (mateExit != null)
                        mateExit.DoorIsOpen = false;
                }
            }
            catch { /* best-effort */ }

            // Broadcast updated room to occupants
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

            return MessageHandler.System($"You close the door to the {exit.Direction}.");
        }
        // REPLACE TO: public Message CloseDoor(Player player, string dir)
    }
}