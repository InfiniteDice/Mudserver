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
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null) return;

            // Prefer executing this as an atomic writer job when the DbWriteQueue is available.
            // The writer will provide a dedicated connection and transaction so we can persist
            // the Room row plus its related exits/keywords/inventory atomically.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    // Perform upsert of Room and related persistence inside the same writer transaction.
                    SaveRoomInternal(wConn, room, wTx);
                    SaveRoomExitsInternal(wConn, room, wTx);        // already implemented to accept tx
                    SaveRoomKeywordsInternal(wConn, room, wTx);     // internal variant below
                    SaveRoomInventoryInternal(wConn, room, wTx);    // internal variant below (uses ItemPersistence with tx)
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct save on provided connection.
            }
            catch
            {
                // best-effort: on any enqueue-related error, fallback to direct save to preserve prior behavior
            }

            // Fallback path: perform same steps using a local transaction on the provided connection.
            using var tx = conn.BeginTransaction();
            try
            {
                SaveRoomInternal(conn, room, tx);
                SaveRoomExitsInternal(conn, room, tx);
                SaveRoomKeywordsInternal(conn, room, tx);
                SaveRoomInventoryInternal(conn, room, tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                Console.WriteLine($"[RoomPersistence][ERROR] SaveRoom transaction failed for RoomID={room?.RoomID}: {ex.Message}");
                throw;
            }
        }

        // Internal helper: perform the Room upsert using caller-supplied connection + transaction.
        // This mirrors the original SaveRoom upsert but uses the provided tx (no local commit).
        private static void SaveRoomInternal(SqliteConnection conn, Room room, SqliteTransaction tx)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            EnsureOpen(conn);

            if (setVerboseServerLogs) Console.WriteLine($"[RoomPersistence][TRACE] SaveRoomInternal START RoomID={room.RoomID}, AreaID={room.AreaID}, IsSpawner={room.IsSpawner}, SpawnType='{room.SpawnType}', Templates=[{string.Join(',', room.SpawnTemplateList ?? new List<string>())}]");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
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
            ";

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
                using var read = conn.CreateCommand();
                read.Transaction = tx;
                read.CommandText = "SELECT SpawnChance FROM Room WHERE RoomID=@rid";
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

            // DistantDescription persisted (nullable -> empty string fallback)
            cmd.Parameters.AddWithValue("@distant", room.DistantDescription ?? "");

            // Flags and KeyWords persisted as JSON text
            cmd.Parameters.AddWithValue("@roomFlags", JsonSerializer.Serialize(room.Flags ?? new List<string>()));
            cmd.Parameters.AddWithValue("@roomKeyWords", JsonSerializer.Serialize(room.KeyWords ?? new List<string>()));

            // IsBuildZone stored as string "true"/"false" (column is TEXT per DB change request)
            cmd.Parameters.AddWithValue("@isBuildZone", room.IsBuildZone ? "true" : "false");

            var last = room.LastSpawnTime == DateTime.MinValue ? DateTime.UtcNow : room.LastSpawnTime;
            cmd.Parameters.AddWithValue("@lastSpawn", last.ToString("o"));

            var rows = cmd.ExecuteNonQuery();
            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoomInternal UPSERT rows={rows}, LastSpawnTime={last:o}");
            }
        }

        // Internal transactional variant of SaveRoomKeywords (works inside provided tx).
        private static void SaveRoomKeywordsInternal(SqliteConnection conn, Room room, SqliteTransaction tx)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null) return;
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            EnsureOpen(conn);

            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM RoomKeyword WHERE RoomID = @rid";
            deleteCmd.Parameters.AddWithValue("@rid", room.ID);
            deleteCmd.ExecuteNonQuery();

            foreach (var kw in room.Keywords)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO RoomKeyword (RoomID, Keyword) VALUES (@rid, @kw)";
                cmd.Parameters.AddWithValue("@rid", room.ID);
                cmd.Parameters.AddWithValue("@kw", kw);
                cmd.ExecuteNonQuery();
            }

            if (setVerboseServerLogs) Console.WriteLine($"[RoomPersistence][TRACE] Keywords refreshed for RoomID={room.ID}, Count={room.Keywords.Count}");
        }

        // Internal transactional variant of SaveRoomInventory (calls ItemPersistence with transaction to avoid nested enqueueing).
        private static void SaveRoomInventoryInternal(SqliteConnection conn, Room room, SqliteTransaction tx)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null || room.Inventory == null) return;
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            EnsureOpen(conn);

            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoomInventoryInternal START RoomID={room.ID}, ItemsInMemory={room.Inventory.Count}");
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
                listCmd.Transaction = tx;
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

                // Use ItemPersistence with the provided transaction so the writer thread does not re-enqueue.
                ItemPersistence.SaveItem(conn, item, tx);

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
                    delCmd.Transaction = tx;
                    delCmd.CommandText = sql;
                    delCmd.Parameters.AddWithValue("@otype", "Room");
                    delCmd.Parameters.AddWithValue("@oid", room.ID);
                    var delRows = delCmd.ExecuteNonQuery();

                    if (setVerboseServerLogs)
                    {
                        Console.WriteLine($"[RoomPersistence][TRACE] Removed {toDelete.Count} stale item(s) from Room {room.ID}, DB rows affected={delRows}.");
                    }
                }
            }

            if (setVerboseServerLogs)
            {
                Console.WriteLine($"[RoomPersistence][TRACE] SaveRoomInventoryInternal END RoomID={room.ID}");
            }
        }

        public static void SaveRoomExits(SqliteConnection conn, Room room)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null || room.Exits == null) return;

            // If the writer queue is running, route the whole SaveRoomExits operation through it
            // so that writes are serialized on the dedicated writer connection.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    // SaveRoomExitsInternal expects an active transaction managed by the caller (writer queue already provides one)
                    SaveRoomExitsInternal(wConn, room, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct save on provided connection.
            }
            catch
            {
                // best-effort: on any enqueue-related error, fallback to direct save to preserve prior behavior
            }

            // Fallback: perform the same logic directly using a local transaction on the provided connection.
            using var tx = conn.BeginTransaction();
            try
            {
                SaveRoomExitsInternal(conn, room, tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                Console.WriteLine($"[RoomPersistence][ERROR] SaveRoomExits failed for RoomID={room.RoomID}: {ex.Message}");
                throw;
            }
        }

        // Internal implementation that requires an active transaction. This is invoked either by the writer
        // worker (which supplies its transaction) or by the fallback path above (which creates a tx).
        private static void SaveRoomExitsInternal(SqliteConnection conn, Room room, SqliteTransaction tx)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null || room.Exits == null) return;
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            EnsureOpen(conn);

            foreach (var exit in room.Exits)
            {
                // DoorId must always be persisted. DB schema now uses non-nullable integer default 0
                // where 0 means "no door". Convert nullable in-memory value -> 0 when null.
                int doorVal = exit.DoorId ?? 0;
                string doorDisplay = exit.DoorId.HasValue ? exit.DoorId.Value.ToString() : "0";

                if (setVerboseServerLogs)
                    Console.WriteLine($"[RoomPersistence][TRACE] Persisting Exit (in-memory) RoomID={room.RoomID} Dir='{exit.Direction}' ToRoomID={exit.ToRoomID} DoorId={doorDisplay}");

                int rows = 0;

                // 1) Try update by RoomID + Direction (case-insensitive)
                using (var updateByDirCmd = conn.CreateCommand())
                {
                    updateByDirCmd.Transaction = tx;
                    updateByDirCmd.CommandText = @"
                UPDATE RoomExit
                SET ToRoomID = @toRid,
                    IsHidden = @hidden,
                    DiscoverKeyword = @keyword,
                    DoorId = @doorId
                WHERE RoomID = @rid AND Direction = @dir COLLATE NOCASE;";
                    updateByDirCmd.Parameters.AddWithValue("@rid", room.RoomID);
                    updateByDirCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                    updateByDirCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                    updateByDirCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                    updateByDirCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                    updateByDirCmd.Parameters.AddWithValue("@doorId", doorVal);

                    rows = updateByDirCmd.ExecuteNonQuery();

                    if (setVerboseServerLogs)
                        Console.WriteLine($"[RoomPersistence][TRACE] UpdateByDir affected={rows} RoomID={room.RoomID} Dir='{exit.Direction}' DoorId={doorDisplay}");
                }

                // 2) Fallback: match by RoomID + ToRoomID and align Direction
                if (rows == 0)
                {
                    using var updateByToCmd = conn.CreateCommand();
                    updateByToCmd.Transaction = tx;
                    updateByToCmd.CommandText = @"
                UPDATE RoomExit
                SET Direction = @dir,
                    IsHidden = @hidden,
                    DiscoverKeyword = @keyword,
                    DoorId = @doorId
                WHERE RoomID = @rid AND ToRoomID = @toRid;";
                    updateByToCmd.Parameters.AddWithValue("@rid", room.RoomID);
                    updateByToCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                    updateByToCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                    updateByToCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                    updateByToCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                    updateByToCmd.Parameters.AddWithValue("@doorId", doorVal);

                    rows = updateByToCmd.ExecuteNonQuery();

                    if (setVerboseServerLogs)
                        Console.WriteLine($"[RoomPersistence][TRACE] UpdateByToRoom affected={rows} RoomID={room.RoomID} ToRoomID={exit.ToRoomID} Dir='{exit.Direction}' DoorId={doorDisplay}");
                }

                // 3) Insert if no row matched
                if (rows == 0)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"
                INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                VALUES (@rid, @dir, @toRid, @hidden, @keyword, @doorId);";
                    insertCmd.Parameters.AddWithValue("@rid", room.RoomID);
                    insertCmd.Parameters.AddWithValue("@dir", exit.Direction ?? "");
                    insertCmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                    insertCmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");
                    insertCmd.Parameters.AddWithValue("@doorId", doorVal);

                    insertCmd.ExecuteNonQuery();

                    if (setVerboseServerLogs)
                        Console.WriteLine($"[RoomPersistence][TRACE] Inserted Exit RoomID={room.RoomID} Dir='{exit.Direction}' ToRoomID={exit.ToRoomID} DoorId={doorDisplay}");
                }
                else
                {
                    if (setVerboseServerLogs)
                        Console.WriteLine($"[RoomPersistence][TRACE] Updated Exit RoomID={room.RoomID} Dir='{exit.Direction}' ToRoomID={exit.ToRoomID} DoorId={doorDisplay}");
                }
            }
        } // end SaveRoomExitsInternal

        public static void SaveRoomKeywords(SqliteConnection conn, Room room)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null) return;

            // Prefer executing via the single-writer queue so keywords updates participate
            // in the same transaction when called from SaveRoom or other writer jobs.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    SaveRoomKeywordsInternal(wConn, room, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct save on provided connection.
            }
            catch
            {
                // best-effort: on any enqueue-related error, fallback to direct save to preserve prior behavior
            }

            // Fallback: use a local transaction so keywords are written atomically.
            using var tx = conn.BeginTransaction();
            try
            {
                SaveRoomKeywordsInternal(conn, room, tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                Console.WriteLine($"[RoomPersistence][ERROR] SaveRoomKeywords failed for RoomID={room.ID}: {ex.Message}");
                throw;
            }
        }

        public static void SaveRoomInventory(SqliteConnection conn, Room room)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (room == null || room.Inventory == null) return;

            // Prefer executing inventory persistence on the single-writer so the whole room save
            // (items created/updated/deleted) can be atomic with other room changes.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    SaveRoomInventoryInternal(wConn, room, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(10)); // inventory may take a bit longer
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct save on provided connection.
            }
            catch
            {
                // best-effort: on any enqueue-related error, fallback to direct save to preserve prior behavior
            }

            // Fallback: perform the same logic inside a local transaction to keep deletes/inserts atomic.
            using var tx = conn.BeginTransaction();
            try
            {
                SaveRoomInventoryInternal(conn, room, tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                Console.WriteLine($"[RoomPersistence][ERROR] SaveRoomInventory failed for RoomID={room.ID}: {ex.Message}");
                throw;
            }
        }
    }
}
