// RoomPersistence.cs
using Microsoft.Data.Sqlite;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Linq; // added
using System.Text.Json;
using System.Data;

namespace MudPersistence
{
    public static class RoomPersistence
    {
        static bool setVerboseServerLogs = false; // Set to true to enable detailed logging

        private static void EnsureOpen(SqliteConnection conn)
        {
            if (conn.State != ConnectionState.Open)
                conn.Open();
        }

        
public static void SaveRoom(SqliteConnection conn, Room room)
        {
            EnsureOpen(conn);

            if (setVerboseServerLogs) Console.WriteLine($"[RoomPersistence][TRACE] SaveRoom START RoomID={room.RoomID}, AreaID={room.AreaID}, IsSpawner={room.IsSpawner}, SpawnType='{room.SpawnType}', Templates=[{string.Join(',', room.SpawnTemplateList ?? new List<string>())}]");

            var cmd = new SqliteCommand(@"
                INSERT INTO Room 
                    (RoomID, AreaID, Name, Description, IsSpawner, SpawnType, SpawnTemplateList, SpawnChance, SpawnDelayMinutes, LastSpawnTime, SpawnMaxRoom, SpawnMessage, DistantDescription, Flags, KeyWords, IsBuildZone)
                VALUES 
                    (@rid, @aid, @name, @desc, @isSpawner, @spawnType, @spawnList, @spawnChance, @spawnDelay, @lastSpawn, @spawnMax, @spawnMsg, @distant, @roomFlags, @roomKeyWords, @isBuildZone)
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
                    SpawnMessage = @spawnMsg,
                    DistantDescription = @distant,
                    Flags = @roomFlags,
                    KeyWords = @roomKeyWords,
                    IsBuildZone = @isBuildZone;
            ", conn);

            cmd.Parameters.AddWithValue("@rid", room.RoomID);
            cmd.Parameters.AddWithValue("@aid", room.AreaID);
            cmd.Parameters.AddWithValue("@name", room.Name ?? "");
            cmd.Parameters.AddWithValue("@desc", room.Description ?? "");
            cmd.Parameters.AddWithValue("@isSpawner", room.IsSpawner ? 1 : 0);
            cmd.Parameters.AddWithValue("@spawnType", room.SpawnType ?? "");
            cmd.Parameters.AddWithValue("@spawnList", JsonSerializer.Serialize(room.SpawnTemplateList ?? new List<string>()));

            int currentDbChance = -1;
            try
            {
                EnsureOpen(conn);
                using var read = new SqliteCommand("SELECT SpawnChance FROM Room WHERE RoomID=@rid", conn);
                read.Parameters.AddWithValue("@rid", room.RoomID);
                var obj = read.ExecuteScalar();
                currentDbChance = obj is long l ? (int)l : Convert.ToInt32(obj);
            }
            catch { /* ignore */ }

            if (room.SpawnChance == 0 && currentDbChance > 0)
            {
                if (setVerboseServerLogs) { Console.WriteLine($"[RoomPersistence][WARN] Preserving DB SpawnChance={currentDbChance} for RoomID={room.RoomID} (in-memory was 0)."); }
                cmd.Parameters.AddWithValue("@spawnChance", currentDbChance);
            }
            else
            {
                cmd.Parameters.AddWithValue("@spawnChance", room.SpawnChance);
            }

            cmd.Parameters.AddWithValue("@spawnDelay", room.SpawnDelayMinutes);
            cmd.Parameters.AddWithValue("@spawnMax", room.SpawnMaxRoom);
            cmd.Parameters.AddWithValue("@spawnMsg", room.SpawnMessage ?? "");

            // NEW: DistantDescription persisted (nullable -> empty string fallback)
            cmd.Parameters.AddWithValue("@distant", room.DistantDescription ?? "");

            // NEW: Flags and KeyWords persisted as JSON text
            cmd.Parameters.AddWithValue("@roomFlags", JsonSerializer.Serialize(room.Flags ?? new List<string>()));
            cmd.Parameters.AddWithValue("@roomKeyWords", JsonSerializer.Serialize(room.KeyWords ?? new List<string>()));

            // NEW: IsBuildZone stored as string "true"/"false" (column is TEXT per DB change request)
            cmd.Parameters.AddWithValue("@isBuildZone", room.IsBuildZone ? "true" : "false");

            var last = room.LastSpawnTime == DateTime.MinValue ? DateTime.UtcNow : room.LastSpawnTime;
            cmd.Parameters.AddWithValue("@lastSpawn", last.ToString("o"));

            EnsureOpen(conn);
            var rows = cmd.ExecuteNonQuery();
            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoom UPSERT rows={rows}, LastSpawnTime={last:o}");
            }

            SaveRoomExits(conn, room);
            SaveRoomKeywords(conn, room);
            SaveRoomInventory(conn, room);

            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoom END RoomID={room.ID}");
            }
        }

        public static void SaveRoomExits(SqliteConnection conn, Room room)
        {
            EnsureOpen(conn);
            if (room.Exits == null) return;
            foreach (var exit in room.Exits)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE RoomExit
                    SET ToRoomID = @toRid, IsHidden = @hidden, DiscoverKeyword = @keyword, DoorId = @doorId
                    WHERE RoomID = @rid AND Direction = @dir;";
                updateCmd.Parameters.AddWithValue("@rid", room.ID);
                updateCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                updateCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                updateCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                updateCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                // DoorId is nullable in DB; write NULL when not present.
                if (exit.DoorId.HasValue)
                    updateCmd.Parameters.AddWithValue("@doorId", exit.DoorId.Value);
                else
                    updateCmd.Parameters.AddWithValue("@doorId", DBNull.Value);

                EnsureOpen(conn);
                var rows = updateCmd.ExecuteNonQuery();

                if (rows == 0)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, @hidden, @keyword, @doorId);";
                    insertCmd.Parameters.AddWithValue("@rid", room.ID);
                    insertCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                    insertCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                    insertCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                    if (exit.DoorId.HasValue)
                        insertCmd.Parameters.AddWithValue("@doorId", exit.DoorId.Value);
                    else
                        insertCmd.Parameters.AddWithValue("@doorId", DBNull.Value);
                    EnsureOpen(conn);
                    insertCmd.ExecuteNonQuery();
                    if (setVerboseServerLogs) Console.WriteLine($"[RoomPersistence][TRACE] Inserted Exit RoomID={room.ID} Dir='{exit.Direction}' ToRoomID={exit.ToRoomID} DoorId={exit.DoorId}");
                }
                else
                {
                    if (setVerboseServerLogs) Console.WriteLine($"[RoomPersistence][TRACE] Updated Exit RoomID={room.ID} Dir='{exit.Direction}' ToRoomID={exit.ToRoomID} DoorId={exit.DoorId}");
                }
            }
        }

        public static void SaveRoomKeywords(SqliteConnection conn, Room room)
        {
            EnsureOpen(conn);
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

            if (setVerboseServerLogs) Console.WriteLine($"[RoomPersistence][TRACE] Keywords refreshed for RoomID={room.ID}, Count={room.Keywords.Count}");
        }

        public static void SaveRoomInventory(SqliteConnection conn, Room room)
        {
            EnsureOpen(conn);
            if (room.Inventory == null) return;

            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoomInventory START RoomID={room.ID}, ItemsInMemory={room.Inventory.Count}");
            }

            if (room.Inventory.Count == 0)
            {
                if (setVerboseServerLogs)
                {
                    Console.WriteLine($"[RoomPersistence][TRACE] Room {room.ID} has empty inventory, skipping DB delete.");
                }
                return;
            }

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

                if (string.IsNullOrWhiteSpace(item.TemplateID) && item.ItemID != 0)
                {
                    Console.WriteLine($"[RoomPersistence][WARN] Skipping update for ItemID={item.ItemID} due to empty TemplateID; preventing overwrite. Room={room.ID}");
                    keptIds.Add(item.ItemID);
                    continue;
                }

                if (setVerboseServerLogs)
                {
                    Console.WriteLine($"[RoomPersistence][TRACE] Pre-SaveItem ItemID={item.ItemID}, TemplateID='{item.TemplateID}', Name='{item.Name}', OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}");
                }

                EnsureOpen(conn);
                ItemPersistence.SaveItem(conn, item);

                if (setVerboseServerLogs)
                {
                    Console.WriteLine($"[RoomPersistence][TRACE] Post-SaveItem ItemID={item.ItemID}");
                }

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
                    EnsureOpen(conn);
                    var delRows = delCmd.ExecuteNonQuery();

                    if (setVerboseServerLogs)
                    {
                        Console.WriteLine($"[RoomPersistence][TRACE] Removed {toDelete.Count} stale item(s) from Room {room.ID}, DB rows affected={delRows}.");
                    }
                }
            }

            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoomInventory END RoomID={room.ID}");
            }
        }
    }
}
