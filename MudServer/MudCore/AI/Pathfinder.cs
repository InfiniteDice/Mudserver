using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using MudCore.World;

namespace MudServer.MudCore.AI
{
    /// <summary>
    /// Simple BFS pathfinder that is door-aware.
    /// Returns an ordered list of steps (Exit, ToRoom) from source -> destination.
    /// If no passable path exists (all routes blocked by locked doors) returns null.
    /// Best-effort: if DB lookup for door flags fails, assume the door is NOT locked.
    /// </summary>
    public static class Pathfinder
    {
        /// <summary>
        /// Find a shortest path (fewest steps) from fromRoomId to toRoomId.
        /// Skips exits whose DoorId resolves to a locked door (Doors.Flags contains "locked").
        /// If conn is null the method will conservatively assume doors are not locked.
        /// Returns null when no passable path exists.
        /// </summary>
        public static List<(Room.RoomExit Exit, Room ToRoom)>? FindPath(World world, int fromRoomId, int toRoomId, SqliteConnection? conn = null)
        {
            if (world == null) return null;
            if (fromRoomId == toRoomId) return new List<(Room.RoomExit, Room)>(); // already there: zero-step path

            var start = world.Rooms.FirstOrDefault(r => r.RoomID == fromRoomId);
            var goal = world.Rooms.FirstOrDefault(r => r.RoomID == toRoomId);
            if (start == null || goal == null) return null;

            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            // predecessor: roomId -> (prevRoomId, exitUsed)
            var prev = new Dictionary<int, (int prevRoomId, Room.RoomExit exitUsed)>();

            queue.Enqueue(start.RoomID);
            visited.Add(start.RoomID);

            while (queue.Count > 0)
            {
                var curId = queue.Dequeue();
                var curRoom = world.Rooms.FirstOrDefault(r => r.RoomID == curId);
                if (curRoom == null) continue;

                // enumerate neighbors
                foreach (var ex in curRoom.Exits ?? Enumerable.Empty<Room.RoomExit>())
                {
                    if (ex == null) continue;
                    int nbrId = ex.ToRoomID;
                    if (visited.Contains(nbrId)) continue;

                    // If exit has a door, check whether it's locked. If locked -> skip this neighbor.
                    bool blockedByLockedDoor = false;
                    try
                    {
                        if (ex.DoorId.HasValue)
                        {
                            var did = ex.DoorId.Value;
                            if (IsDoorLocked(did, conn))
                                blockedByLockedDoor = true;
                        }
                    }
                    catch (Exception exChk)
                    {
                        // Best-effort: on DB error assume unlocked (conservative for progress)
                        Console.WriteLine($"[Pathfinder] Door lock check failed for DoorId={ex.DoorId}: {exChk.Message}");
                        blockedByLockedDoor = false;
                    }

                    if (blockedByLockedDoor) continue;

                    // Accept neighbor
                    visited.Add(nbrId);
                    prev[nbrId] = (curId, ex);

                    if (nbrId == goal.RoomID)
                    {
                        // reconstruct path: from start -> goal
                        var path = new List<(Room.RoomExit Exit, Room ToRoom)>();
                        int walk = goal.RoomID;
                        while (walk != start.RoomID)
                        {
                            if (!prev.TryGetValue(walk, out var p)) break;
                            var exitUsed = p.exitUsed;
                            var toRoom = world.Rooms.FirstOrDefault(r => r.RoomID == walk);
                            path.Add((exitUsed, toRoom!));
                            walk = p.prevRoomId;
                        }
                        path.Reverse();
                        return path;
                    }

                    queue.Enqueue(nbrId);
                }
            }

            // No passable path found (likely blocked-by-locked-doors)
            return null;
        }

        /// <summary>
        /// Best-effort door locked determination.
        /// Returns true when the Doors.Flags column contains "locked" (case-insensitive).
        /// If no row exists or DB error occurs, returns false (treat as unlocked).
        /// </summary>
        private static bool IsDoorLocked(int doorId, SqliteConnection? conn)
        {
            if (conn == null) return false; // assume unlocked when no DB available

            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                    conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                cmd.Parameters.AddWithValue("@did", doorId);
                var obj = cmd.ExecuteScalar();
                if (obj == null || obj is DBNull) return false;
                var flags = obj.ToString() ?? "";
                if (flags.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pathfinder] Failed to read Doors.Flags for DoorId={doorId}: {ex.Message}");
                // on error assume unlocked so we don't incorrectly block NPCs
                return false;
            }

            return false;
        }
    }
}