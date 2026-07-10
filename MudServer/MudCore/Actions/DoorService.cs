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
                                room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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

                // NPC path: refuse to open key-locked doors (NPC must wait)
                if (IsDoorLockedByKey(doorId, db))
                {
                    Console.WriteLine($"[DOOR] OpenDoorById: DoorId={doorId} is key-locked; NPC cannot open.");
                    return false;
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

                // Check for key-lock before opening (mirrors KeyLocks.EnsureAccessible)
                var lockedKeyId = GetDoorLockedKeyId(exit.DoorId.Value, _db);
                if (lockedKeyId.HasValue)
                {
                    var hasKey = player.Inventory?.Any(i => i != null && i.ItemID == lockedKeyId.Value) ?? false;
                    if (!hasKey)
                        return MessageHandler.System($"The {exit.DoorType} to the {exit.Direction} is locked. You need the correct key.");
                }

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
                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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
                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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

        // ----------------------------------------------------------------
        // Door lock helpers - mirrors KeyLocks container-lock pattern
        // ----------------------------------------------------------------

        // Flag prefix used to bind a key instance to a door (matches KeyLocks.LockedByKeyPrefix)
        private const string DoorLockedByKeyPrefix = "LockedByKey:";

        /// <summary>
        /// Returns the ItemID of the bound key if the door's Flags column contains a LockedByKey:<ID> entry,
        /// otherwise null. Reads directly from Doors table; safe to call on any connection state.
        /// </summary>
        public static int? GetDoorLockedKeyId(int doorId, SqliteConnection conn)
        {
            if (conn == null) return null;
            try
            {
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                cmd.Parameters.AddWithValue("@did", doorId);
                var obj = cmd.ExecuteScalar();
                if (obj == null || obj is DBNull) return null;
                var flagsJson = obj.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(flagsJson)) return null;

                var flags = System.Text.Json.JsonSerializer.Deserialize<List<string>>(flagsJson) ?? new List<string>();
                foreach (var f in flags)
                {
                    if (!string.IsNullOrWhiteSpace(f) && f.StartsWith(DoorLockedByKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = f.Substring(DoorLockedByKeyPrefix.Length);
                        if (int.TryParse(rest, out var id)) return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DOOR] GetDoorLockedKeyId failed for DoorId={doorId}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Returns true when the door has a LockedByKey:<ID> flag, i.e. is key-locked.
        /// </summary>
        public static bool IsDoorLockedByKey(int doorId, SqliteConnection conn)
            => GetDoorLockedKeyId(doorId, conn).HasValue;

        /// <summary>
        /// Returns true if the player is the owner of the area that contains the given room.
        /// Ownership is checked case-insensitively against Area.Owner.
        /// </summary>
        private static bool IsAreaOwnerForRoom(Player player, Room room, World.World world)
        {
            if (player == null || room == null || world == null) return false;
            var area = world.Areas?.FirstOrDefault(a => a != null && a.AreaID == room.AreaID);
            if (area == null) return false;
            return string.Equals(area.Owner, player.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Updates the in-memory DoorFlags cache on both sides of a door so that subsequent
        /// in-memory checks (EnsureExitPassableForNpc, Pathfinder) see the current flags without
        /// needing a DB round-trip.
        /// </summary>
        private void UpdateDoorFlagsCache(Room.RoomExit exit, Room room, List<string> newFlags)
        {
            try
            {
                exit.DoorFlags = new List<string>(newFlags);

                // update mate exit
                var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                if (mateRoom != null)
                {
                    string opp = OppositeDirection(Movement.NormalizeDirection(exit.Direction));
                    var mateExit = mateRoom.Exits.FirstOrDefault(e =>
                        e.ToRoomID == room.RoomID &&
                        string.Equals(Movement.NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                        ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                    if (mateExit != null)
                        mateExit.DoorFlags = new List<string>(newFlags);
                }
            }
            catch { /* best-effort */ }
        }

        // ----------------------------------------------------------------
        // LockDoor - owner places a key lock on a door exit
        // Syntax: lockdoor <direction>  (then prompted for key label)
        // ----------------------------------------------------------------

        // Pending data for the key-label prompt step (mirrors KeyLocks.LockPendingData)
        public class DoorLockPendingData
        {
            public int DoorId { get; set; }
            public Room.RoomExit Exit { get; set; } = null!;
            public Room Room { get; set; } = null!;
            public Item Key { get; set; } = null!;
        }

        /// <summary>
        /// Phase 1 of LockDoor: validate area ownership, door existence, and key possession.
        /// Returns a DoorLockPendingData to store on the player's PendingAction so CommandHandler
        /// can prompt for a label, then call LockDoorCommit.
        /// On error, returns null and sets errorMessage.
        /// </summary>
        public bool TryBeginLockDoor(Player player, string dir, out DoorLockPendingData? pending, out string errorMessage)
        {
            pending = null;
            errorMessage = "";

            if (player == null) { errorMessage = "Invalid player."; return false; }
            var room = player.CurrentRoom;
            if (room == null) { errorMessage = "You are not in a valid room."; return false; }

            // Diagnostic: log ownership check details so server console shows why it failed if needed
            var area = _world.Areas?.FirstOrDefault(a => a != null && a.AreaID == room.AreaID);
            Console.WriteLine($"[LOCKDOOR] Player={player.Name} Room={room.RoomID} AreaID={room.AreaID} AreaOwner={(area?.Owner ?? "null")}");

            if (!IsAreaOwnerForRoom(player, room, _world))
            {
                errorMessage = $"You do not own this area (owner: {area?.Owner ?? "unknown"}) and cannot lock its doors.";
                return false;
            }

            var targetDir = Movement.NormalizeDirection(dir);
            var exit = room.Exits.FirstOrDefault(e => Movement.NormalizeDirection(e.Direction) == targetDir);
            if (exit == null)
            {
                // Report what exits the room actually has so the player knows valid directions
                var validExits = room.Exits.Count > 0
                    ? string.Join(", ", room.Exits.Select(e => e.Direction))
                    : "none";
                errorMessage = $"You don't see an exit to the {dir}. Available exits: {validExits}";
                return false;
            }
            if (!exit.DoorId.HasValue) { errorMessage = $"There is no door on the {exit.Direction} exit (it is an open passage)."; return false; }

            // Already locked?
            if (IsDoorLockedByKey(exit.DoorId.Value, _db))
            {
                errorMessage = $"The {exit.DoorType} to the {dir} is already locked.";
                return false;
            }

            // Player must hold a Key item
            var key = player.Inventory?.FirstOrDefault(i =>
                i != null && string.Equals(i.ItemCategory ?? "", "Key", StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                errorMessage = "You need to be carrying a key item to lock this door.";
                return false;
            }

            pending = new DoorLockPendingData
            {
                DoorId = exit.DoorId.Value,
                Exit = exit,
                Room = room,
                Key = key
            };
            return true;
        }

        /// <summary>
        /// Phase 2 of LockDoor: apply the label, write LockedByKey:<KeyItemID> to Doors.Flags,
        /// label the key, and update in-memory caches. Mirrors KeyLocks.LockContainer.
        /// </summary>
        public Message LockDoorCommit(Player player, DoorLockPendingData pending, string label)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (pending == null) return MessageHandler.System("Invalid lock data.");
            if (string.IsNullOrWhiteSpace(label)) return MessageHandler.System("Key label cannot be empty.");

            var key = pending.Key;
            if (key == null) return MessageHandler.System("Invalid key.");

            // Re-verify door still unlocked (race guard)
            if (IsDoorLockedByKey(pending.DoorId, _db))
                return MessageHandler.System($"The {pending.Exit.DoorType} is already locked.");

            SqliteTransaction? tx = null;
            try
            {
                if (_db.State != System.Data.ConnectionState.Open) _db.Open();
                tx = _db.BeginTransaction();

                // Read current Doors.Flags
                List<string> flags;
                using (var readCmd = _db.CreateCommand())
                {
                    readCmd.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                    readCmd.Parameters.AddWithValue("@did", pending.DoorId);
                    var obj = readCmd.ExecuteScalar();
                    flags = (obj != null && obj is not DBNull)
                        ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(obj.ToString()!) ?? new List<string>()
                        : new List<string>();
                }

                // Remove any stale LockedByKey entries and add the new binding
                flags.RemoveAll(f => f != null && f.StartsWith(DoorLockedByKeyPrefix, StringComparison.OrdinalIgnoreCase));
                flags.Add($"{DoorLockedByKeyPrefix}{key.ItemID}");
                var newFlagsJson = System.Text.Json.JsonSerializer.Serialize(flags);

                // Persist Doors.Flags
                using (var updCmd = _db.CreateCommand())
                {
                    updCmd.CommandText = "UPDATE Doors SET Flags = @flags WHERE DoorId = @did;";
                    updCmd.Parameters.AddWithValue("@flags", newFlagsJson);
                    updCmd.Parameters.AddWithValue("@did", pending.DoorId);
                    updCmd.ExecuteNonQuery();
                }

                // Label the key (mirrors KeyLocks.LockContainer naming logic)
                var trimmed = label.Trim();
                key.Name = trimmed.EndsWith("key", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed} Key";
                MudPersistence.ItemPersistence.SaveItem(_db, key, tx);

                tx.Commit();

                // Update in-memory DoorFlags cache on both sides
                UpdateDoorFlagsCache(pending.Exit, pending.Room, flags);

                // Broadcast updated room to all occupants on both sides of the door
                try
                {
                    Console.WriteLine($"[DOOR-LOCK-BROADCAST] Sending room refresh to {pending.Room.Players.Count} player(s) in room {pending.Room.RoomID}");
                    foreach (var p in pending.Room.Players)
                    {
                        p.PendingMessageQueue.Add(MessageHandler.Room(
                            pending.Room,
                            pending.Room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                            pending.Room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                            _world, _db));
                    }
                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == pending.Exit.ToRoomID);
                    if (mateRoom != null)
                    {
                        foreach (var p in mateRoom.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                mateRoom,
                                mateRoom.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                mateRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                }
                catch { /* best-effort */ }

                Console.WriteLine($"[DOOR] DoorId={pending.DoorId} locked by player={player.Name} KeyItemID={key.ItemID}");
                return MessageHandler.System($"You lock the {pending.Exit.DoorType} to the {pending.Exit.Direction} with '{key.Name}'.");
            }
            catch (Exception ex)
            {
                try { tx?.Rollback(); } catch { /* best-effort */ }
                Console.WriteLine($"[DOOR] LockDoorCommit failed DoorId={pending.DoorId}: {ex}");
                return MessageHandler.System("Failed to lock the door due to a server error.");
            }
            finally
            {
                try { tx?.Dispose(); } catch { /* best-effort */ }
            }
        } // end LockDoorCommit

        // ----------------------------------------------------------------
        // UnlockDoor - player with the bound key removes the lock
        // Syntax: unlockdoor <direction> with <keyname>
        // ----------------------------------------------------------------

        /// <summary>
        /// Remove the LockedByKey binding from a door exit. Player must carry the bound key.
        /// Reverts key name to "Key" if it matches the generated label pattern (mirrors KeyLocks.UnlockContainer).
        /// </summary>
        public Message UnlockDoor(Player player, string dir, Item key)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (key == null) return MessageHandler.System("Invalid key.");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            var targetDir = Movement.NormalizeDirection(dir);
            var exit = room.Exits.FirstOrDefault(e => Movement.NormalizeDirection(e.Direction) == targetDir);
            if (exit == null) return MessageHandler.System($"You don't see an exit to the {dir}.");
            if (!exit.DoorId.HasValue) return MessageHandler.System($"There is no door to the {dir}.");

            var lockedKeyId = GetDoorLockedKeyId(exit.DoorId.Value, _db);
            if (!lockedKeyId.HasValue)
                return MessageHandler.System($"The {exit.DoorType} to the {dir} is not locked.");

            if (lockedKeyId.Value != key.ItemID)
                return MessageHandler.System("That key does not match this lock.");

            SqliteTransaction? tx = null;
            try
            {
                if (_db.State != System.Data.ConnectionState.Open) _db.Open();
                tx = _db.BeginTransaction();

                // Read flags, remove LockedByKey entry
                List<string> flags;
                using (var readCmd = _db.CreateCommand())
                {
                    readCmd.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                    readCmd.Parameters.AddWithValue("@did", exit.DoorId.Value);
                    var obj = readCmd.ExecuteScalar();
                    flags = (obj != null && obj is not DBNull)
                        ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(obj.ToString()!) ?? new List<string>()
                        : new List<string>();
                }
                flags.RemoveAll(f => f != null && f.StartsWith(DoorLockedByKeyPrefix, StringComparison.OrdinalIgnoreCase));
                var newFlagsJson = System.Text.Json.JsonSerializer.Serialize(flags);

                using (var updCmd = _db.CreateCommand())
                {
                    updCmd.CommandText = "UPDATE Doors SET Flags = @flags WHERE DoorId = @did;";
                    updCmd.Parameters.AddWithValue("@flags", newFlagsJson);
                    updCmd.Parameters.AddWithValue("@did", exit.DoorId.Value);
                    updCmd.ExecuteNonQuery();
                }

                // Revert key name if it was generated as "<label> Key"
                if (key.Name != null && key.Name.EndsWith(" Key", StringComparison.OrdinalIgnoreCase))
                {
                    key.Name = "Key";
                    MudPersistence.ItemPersistence.SaveItem(_db, key, tx);
                }

                tx.Commit();

                // Update in-memory DoorFlags cache on both sides
                UpdateDoorFlagsCache(exit, room, flags);

                // Broadcast updated room to all occupants on both sides of the door
                try
                {
                    foreach (var p in room.Players)
                    {
                        p.PendingMessageQueue.Add(MessageHandler.Room(
                            room,
                            room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                            room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                            _world, _db));
                    }
                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                    if (mateRoom != null)
                    {
                        foreach (var p in mateRoom.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                mateRoom,
                                mateRoom.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                mateRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                }
                catch { /* best-effort */ }

                Console.WriteLine($"[DOOR] DoorId={exit.DoorId.Value} unlocked by player={player.Name}");
                return MessageHandler.System($"You unlock the {exit.DoorType} to the {dir}.");
            }
            catch (Exception ex)
            {
                try { tx?.Rollback(); } catch { /* best-effort */ }
                Console.WriteLine($"[DOOR] UnlockDoor failed DoorId={exit.DoorId}: {ex}");
                return MessageHandler.System("Failed to unlock the door due to a server error.");
            }
            finally
            {
                try { tx?.Dispose(); } catch { /* best-effort */ }
            }
        } // end UnlockDoor
    }
}