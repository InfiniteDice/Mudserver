// RoomPersistence.cs
using Microsoft.Data.Sqlite;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Linq; // added
using System.Text.Json;

namespace MudPersistence
{
    public static class RoomPersistence
    {
        public static void SaveRoom(SqliteConnection conn, Room room)
        {
            var cmd = new SqliteCommand(@"
                INSERT INTO Room 
                    (RoomID, AreaID, Name, Description, IsSpawner, SpawnType, SpawnTemplateList, SpawnChance, SpawnDelayMinutes, LastSpawnTime, SpawnMaxRoom, SpawnMessage)
                VALUES 
                    (@rid, @aid, @name, @desc, @isSpawner, @spawnType, @spawnList, @spawnChance, @spawnDelay, @lastSpawn, @spawnMax, @spawnMsg)
                ON CONFLICT(RoomID) DO UPDATE SET
                    Name = @name,
                    Description = @desc,
                    AreaID = @aid,
                    IsSpawner = @isSpawner,
                    SpawnType = @spawnType,
                    SpawnTemplateList = @spawnList,
                    SpawnChance = @spawnChance,
                    SpawnDelayMinutes = @spawnDelay,
                    LastSpawnTime = @lastSpawn,
                    SpawnMaxRoom = @spawnMax,
                    SpawnMessage = @spawnMsg;
            ", conn);

            cmd.Parameters.AddWithValue("@rid", room.RoomID);
            cmd.Parameters.AddWithValue("@aid", room.AreaID);
            cmd.Parameters.AddWithValue("@name", room.Name ?? "");
            cmd.Parameters.AddWithValue("@desc", room.Description ?? "");
            cmd.Parameters.AddWithValue("@isSpawner", room.IsSpawner ? 1 : 0);
            cmd.Parameters.AddWithValue("@spawnType", room.SpawnType ?? "");
            cmd.Parameters.AddWithValue("@spawnList", JsonSerializer.Serialize(room.SpawnTemplateList ?? new List<string>()));
            cmd.Parameters.AddWithValue("@spawnChance", room.SpawnChance);
            cmd.Parameters.AddWithValue("@spawnDelay", room.SpawnDelayMinutes);
            cmd.Parameters.AddWithValue("@spawnMax", room.SpawnMaxRoom);
            cmd.Parameters.AddWithValue("@spawnMsg", room.SpawnMessage ?? "");

            var last = room.LastSpawnTime == DateTime.MinValue ? DateTime.UtcNow : room.LastSpawnTime;
            cmd.Parameters.AddWithValue("@lastSpawn", last.ToString("o"));

            cmd.ExecuteNonQuery();

            SaveRoomExits(conn, room);
            SaveRoomKeywords(conn, room);
            SaveRoomInventory(conn, room);
        }

        public static void SaveRoomExits(SqliteConnection conn, Room room)
        {
            if (room.Exits == null) return;
            foreach (var exit in room.Exits)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE RoomExit
                    SET ToRoomID = @toRid, IsHidden = @hidden, DiscoverKeyword = @keyword
                    WHERE RoomID = @rid AND Direction = @dir;";
                updateCmd.Parameters.AddWithValue("@rid", room.ID);
                updateCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                updateCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                updateCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                updateCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                var rows = updateCmd.ExecuteNonQuery();

                if (rows == 0)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword)
                        VALUES (@rid, @dir, @toRid, @hidden, @keyword);";
                    insertCmd.Parameters.AddWithValue("@rid", room.ID);
                    insertCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                    insertCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                    insertCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                    insertCmd.ExecuteNonQuery();
                }
            }
        }

        public static void SaveRoomKeywords(SqliteConnection conn, Room room)
        {
            if (room.Keywords == null) return;

            using var deleteCmd = new SqliteCommand("DELETE FROM RoomKeyword WHERE RoomID = @rid", conn);
            deleteCmd.Parameters.AddWithValue("@rid", room.ID);
            deleteCmd.ExecuteNonQuery();

            foreach (var kw in room.Keywords)
            {
                using var cmd = new SqliteCommand("INSERT INTO RoomKeyword (RoomID, Keyword) VALUES (@rid, @kw)", conn);
                cmd.Parameters.AddWithValue("@rid", room.ID);
                cmd.Parameters.AddWithValue("@kw", kw);
                cmd.ExecuteNonQuery();
            }
        }

        public static void SaveRoomInventory(SqliteConnection conn, Room room)
        {
            if (room.Inventory == null) return;

            var existingIds = new HashSet<int>();
            using (var listCmd = conn.CreateCommand())
            {
                listCmd.CommandText = "SELECT ItemID FROM Item WHERE OwnerType = @otype AND OwnerID = @oid";
                listCmd.Parameters.AddWithValue("@otype", "Room");
                listCmd.Parameters.AddWithValue("@oid", room.ID);
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        existingIds.Add(reader.GetInt32(0));
                }
            }

            var keptIds = new HashSet<int>();
            foreach (var item in room.Inventory)
            {
                if (item == null) continue;

                item.OwnerType = "Room";
                item.OwnerID = room.ID;
                item.RoomID = room.ID;

                // Guard: if this runtime item is partial (missing TemplateID) but refers to an existing DB row, skip to avoid wiping fields.
                if (string.IsNullOrWhiteSpace(item.TemplateID) && item.ItemID != 0)
                {
                    Console.WriteLine($"[RoomPersistence][WARN] Skipping update for ItemID={item.ItemID} due to empty TemplateID; preventing overwrite. Room={room.ID}");
                    keptIds.Add(item.ItemID);
                    continue;
                }

                ItemPersistence.SaveItem(conn, item);

                if (item.ItemID == 0)
                {
                    Console.WriteLine($"[RoomPersistence][WARN] Item still has ItemID=0 after save (TemplateID='{item.TemplateID}', Name='{item.Name}', Room={room.ID}).");
                    continue;
                }

                keptIds.Add(item.ItemID);
            }

            if (existingIds.Count > 0)
            {
                var toDelete = existingIds.Where(id => !keptIds.Contains(id)).ToList();
                if (toDelete.Count > 0)
                {
                    var sql = $"DELETE FROM Item WHERE OwnerType = @otype AND OwnerID = @oid AND ItemID IN ({string.Join(',', toDelete)})";
                    using var delCmd = conn.CreateCommand();
                    delCmd.CommandText = sql;
                    delCmd.Parameters.AddWithValue("@otype", "Room");
                    delCmd.Parameters.AddWithValue("@oid", room.ID);
                    delCmd.ExecuteNonQuery();
                    Console.WriteLine($"[RoomPersistence] Removed {toDelete.Count} stale item(s) from Room {room.ID}.");
                }
            }
        }
    }
}
