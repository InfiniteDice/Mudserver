using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudCore.Entities;
using MudNetworking;
using MudPersistence;

namespace MudServer.MudCore.Actions
{
    internal class DoorService
    {
        private readonly World _world;
        private readonly SqliteConnection _db;

        public DoorService(World world, SqliteConnection db)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        private static string NormalizeDirection(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return string.Empty;
            var s = d.Trim().ToUpper();

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

            var targetDir = NormalizeDirection(dir);
            var exit = room.Exits.FirstOrDefault(e => NormalizeDirection(e.Direction) == targetDir);

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
                    string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                    var mateExit = mateRoom.Exits.FirstOrDefault(e =>
                        e.ToRoomID == room.RoomID &&
                        string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
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

            var targetDir = NormalizeDirection(dir);
            var exit = room.Exits.FirstOrDefault(e => NormalizeDirection(e.Direction) == targetDir);

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
                    string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                    var mateExit = mateRoom.Exits.FirstOrDefault(e =>
                        e.ToRoomID == room.RoomID &&
                        string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
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