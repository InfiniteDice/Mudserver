using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Data;
using System.Linq;

namespace MudPersistence
{
    public static partial class ItemPersistence
    {
        static bool setVerboseServerLogs = false;
        private static readonly object DbLock = new();
        private static bool? hasBaseAcBonus;
        private static bool? hasQuantity; // NEW: detect Quantity column presence
        private static bool? hasDescriptor; // NEW: detect Descriptor column presence

        private static void EnsureOpen(SqliteConnection conn)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (conn.State != ConnectionState.Open)
            {
                if (setVerboseServerLogs) Console.WriteLine("[ItemPersistence][TRACE] Connection not open. Opening now...");
                conn.Open();
            }
        }

        private static string NormalizeOwnerType(string? ownerType)
        {
            if (string.IsNullOrWhiteSpace(ownerType)) return string.Empty;
            var s = ownerType.Trim();
            return s.ToLowerInvariant() switch
            {
                "room" or "rooms" or "room " => "Room",
                "player" or "players" => "Player",
                "npc" or "npcs" => "NPC",
                _ => s
            };
        }

        
        // Remove orphaned items owned by non-existent NPCs.
        // - dryRun: when true, returns the count only and performs no deletes.
        // - batchSize: number of rows to delete per writer transaction loop (keeps transactions small).
        // Returns number of rows deleted (or would be deleted when dryRun=true).
        public static int CleanOrphanNpcItems(SqliteConnection conn, bool dryRun = true, int batchSize = 500)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            // Normalize expected OwnerType token used in DB
            const string ownerTypeToken = "NPC";

            try
            {
                EnsureOpen(conn);

                // Quick dry-run: count orphans without changing DB (use primary connection, no writer required)
                if (dryRun)
                {
                    using var countCmd = conn.CreateCommand();
                    countCmd.CommandText = $@"
                        SELECT COUNT(*) FROM Item
                        WHERE OwnerType = @otype
                          AND OwnerID NOT IN (SELECT NpcID FROM Npc);";
                    countCmd.Parameters.AddWithValue("@otype", ownerTypeToken);
                    var obj = countCmd.ExecuteScalar();
                    int count = 0;
                    try { count = Convert.ToInt32(obj); } catch { count = 0; }
                    if (setVerboseServerLogs) Console.WriteLine($"[ItemPersistence][CLEAN] Dry-run orphaned NPC items: {count}");
                    return count;
                }

                // Real run: perform deletions in batches inside the writer queue for serialized safe writes.
                int totalDeleted = 0;

                // Use writer queue to ensure we hold a writer transaction for the delete loop.
                totalDeleted = MudServer.DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                {
                    // Ensure connection/tx are usable inside the writer
                    if (wConn == null) return 0;

                    int deletedThisRun = 0;
                    while (true)
                    {
                        // Select a batch of orphan ItemIDs
                        var ids = new List<int>();
                        using (var sel = wConn.CreateCommand())
                        {
                            sel.Transaction = wTx;
                            sel.CommandText = $@"
                                SELECT ItemID FROM Item
                                WHERE OwnerType = @otype
                                  AND OwnerID NOT IN (SELECT NpcID FROM Npc)
                                LIMIT @limit;";
                            sel.Parameters.AddWithValue("@otype", ownerTypeToken);
                            sel.Parameters.AddWithValue("@limit", batchSize);

                            using var rdr = sel.ExecuteReader();
                            while (rdr.Read())
                            {
                                if (!rdr.IsDBNull(0))
                                    ids.Add(rdr.GetInt32(0));
                            }
                        }

                        if (ids.Count == 0)
                        {
                            // nothing left to delete
                            break;
                        }

                        // Delete the batch by ItemID
                        using (var del = wConn.CreateCommand())
                        {
                            del.Transaction = wTx;
                            // build parameterized IN clause to avoid SQL injection and to keep it explicit
                            var paramNames = new List<string>();
                            for (int i = 0; i < ids.Count; i++)
                            {
                                string pn = $"@id{i}";
                                paramNames.Add(pn);
                                del.Parameters.AddWithValue(pn, ids[i]);
                            }
                            del.CommandText = $"DELETE FROM Item WHERE ItemID IN ({string.Join(",", paramNames)});";
                            int affected = del.ExecuteNonQuery();
                            deletedThisRun += affected;
                        }

                        // Loop to pick next batch
                    }

                    return deletedThisRun;
                }, timeout: TimeSpan.FromSeconds(30));

                if (setVerboseServerLogs)
                    Console.WriteLine($"[ItemPersistence][CLEAN] Deleted {totalDeleted} orphaned NPC-owned items.");

                return totalDeleted;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemPersistence][CLEAN][ERROR] Failed cleaning orphan NPC items: {ex.Message}");
                return 0;
            }
        }


        public static void SaveItem(SqliteConnection conn, Item item, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (item == null) throw new ArgumentNullException(nameof(item));

            // If a transaction was not supplied, and the DbWriteQueue is running, route the write
            // through the single-writer queue to avoid concurrent-writer races. If the queue isn't
            // started, fall back to direct save on the provided connection.
            try
            {
                // Attempt to enqueue only when caller didn't supply a transaction (caller intends a standalone write).
                if (tx == null)
                {
                    try
                    {
                        // Use a conservative timeout for interactive flows; callers that require longer can call SaveItemInternal directly.
                        MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            // call internal implementation that accepts the external transaction
                            SaveItemInternal(wConn, item, wTx);
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        // DbWriteQueue not started — fall through to direct save using provided connection.
                    }
                }
            }
            catch
            {
                // best-effort: on any enqueue-related error, continue with direct save to preserve prior behavior
            }

            // If we reach here, perform the save directly on provided connection/tx context
            SaveItemInternal(conn, item, tx);
        }

        // Internal worker implementation of SaveItem that accepts an external transaction.
        // This contains the original SaveItem logic but is callable from the writer queue without
        // starting nested transactions.
        private static void SaveItemInternal(SqliteConnection conn, Item item, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (string.Equals(item.OwnerType, "ROOM", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "Room";
            if (string.Equals(item.OwnerType, "PLAYER", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "Player";
            if (string.Equals(item.OwnerType, "NPC", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "NPC";

            // Normalize and deterministically serialize FlagsJson when Flags is present.
            // Sorting ensures stable JSON so merge queries that compare Flags text behave predictably.
            if (item.Flags != null)
            {
                try
                {
                    var flagsList = item.Flags ?? new List<string>();
                    var sorted = flagsList.OrderBy(s => s ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                    var serialized = JsonSerializer.Serialize(sorted);
                    if (!string.Equals(item.FlagsJson ?? "[]", serialized, StringComparison.Ordinal))
                    {
                        item.FlagsJson = serialized;
                    }
                }
                catch
                {
                    // If flag normalization fails, fall back to existing FlagsJson as-is.
                }
            }

            if (!string.Equals(item.OwnerType, "Room", StringComparison.OrdinalIgnoreCase))
            {
                item.OwnerAreaID = 0;
            }

            // NOTE:
            // - Writer serialization is provided by MudServer.DbWriteQueue for the normal write path.
            // - SaveItemInternal accepts an optional SqliteTransaction (tx). When tx is supplied the caller
            //   is responsible for serializing and owning the transaction (for example the writer queue).
            // - We intentionally removed ad-hoc lock(DbLock) here to avoid blocking concurrent readers and
            //   to rely on the centralized writer for serialization of writes.
            try
            {
                EnsureOpen(conn);

                // Best-effort: ensure a descriptor is assigned for items created via factories or inline before persistence.
                // Use the global qualifier so this code remains unambiguous even when compiled under other namespaces.
                try
                {
                    global::MudCore.World.ItemDescriptorManager.AssignDescriptorIfApplicable(item);
                }
                catch
                {
                    // Non-fatal: descriptor assignment must not prevent saves.
                }

                // When a transaction is supplied, propagate it into schema checks so PRAGMA/SELECT uses the same transaction.
                if (!hasBaseAcBonus.HasValue)
                    hasBaseAcBonus = TryHasColumn(conn, "BaseACBonus", tx);

                if (!hasQuantity.HasValue)
                    hasQuantity = TryHasColumn(conn, "Quantity", tx);

                // Do a quick existence check for Material column and Descriptor column (do not cache material to avoid schema drift on long-running servers)
                bool hasMaterial = TryHasColumn(conn, "Material", tx);

                if (!hasDescriptor.HasValue)
                    hasDescriptor = TryHasColumn(conn, "Descriptor", tx);

                if (setVerboseServerLogs)
                    Console.WriteLine($"[ItemPersistence][TRACE] SaveItem START: ConnState={conn.State}, ItemID={item.ItemID}, TemplateID='{item.TemplateID}', Name='{item.Name}', OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}, FlagsJson='{item.FlagsJson}', DmgDiceMult={item.DmgDiceMult}, Quantity={item.Quantity}, Descriptor='{item.Descriptor}'");

                // MERGE PATH...
                if (item.ItemID <= 0 && hasQuantity == true)
                {
                    bool isStackable = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(item.FlagsJson) && item.FlagsJson.IndexOf("\"Stackable\"", StringComparison.OrdinalIgnoreCase) >= 0)
                            isStackable = true;
                    }
                    catch { isStackable = false; }

                    if (isStackable && item.Quantity >= 1 && !string.IsNullOrEmpty(item.TemplateID))
                    {
                        using var findCmd = new SqliteCommand(
                            @"SELECT ItemID, Quantity FROM Item
          WHERE TemplateID = @template
            AND OwnerType = @otype
            AND OwnerID = @oid
            AND RoomID = @rid
            AND Flags = @flags"
                            + (hasMaterial ? " AND Material = @material" : string.Empty)
                            + (hasDescriptor == true ? " AND Descriptor = @descriptor" : string.Empty)
                            + @" ORDER BY ItemID LIMIT 1;", conn);

                        if (tx != null) findCmd.Transaction = tx;

                        findCmd.Parameters.AddWithValue("@template", item.TemplateID);
                        findCmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
                        findCmd.Parameters.AddWithValue("@oid", item.OwnerID);
                        findCmd.Parameters.AddWithValue("@rid", item.RoomID);
                        findCmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");
                        if (hasMaterial) findCmd.Parameters.AddWithValue("@material", item.Material ?? "");
                        if (hasDescriptor == true) findCmd.Parameters.AddWithValue("@descriptor", item.Descriptor ?? "");

                        using var rdr = findCmd.ExecuteReader();
                        if (rdr.Read())
                        {
                            int existingId = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                            int existingQty = rdr.IsDBNull(1) ? 1 : rdr.GetInt32(1);
                            rdr.Close();

                            int addQty = Math.Max(1, item.Quantity);
                            int newQty = existingQty + addQty;

                            using var upd = new SqliteCommand("UPDATE Item SET Quantity = @newQty WHERE ItemID = @id", conn);
                            if (tx != null) upd.Transaction = tx;
                            upd.Parameters.AddWithValue("@newQty", newQty);
                            upd.Parameters.AddWithValue("@id", existingId);
                            upd.ExecuteNonQuery();

                            item.ItemID = existingId;
                            item.Quantity = newQty;

                            if (setVerboseServerLogs) Console.WriteLine($"[ItemPersistence][TRACE] MERGED stackable item: Template='{item.TemplateID}' into ItemID={existingId}, NewQty={newQty}");

                            return; // merged, nothing else to do
                        }
                    }
                }

                if (item.ItemID <= 0)
                {
                    using var cmd = new SqliteCommand(
                        @"INSERT INTO Item (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags"
                        + (hasMaterial ? ", Material" : string.Empty)
                        + (hasDescriptor == true ? ", Descriptor" : string.Empty)
                        + (hasQuantity == true ? ", Quantity" : string.Empty)
                        + ", DmgDiceMult, OwnerAreaID"
                        + (hasBaseAcBonus == true ? ", BaseACBonus" : string.Empty) + @")
      VALUES (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags"
                        + (hasMaterial ? ", @material" : string.Empty)
                        + (hasDescriptor == true ? ", @descriptor" : string.Empty)
                        + (hasQuantity == true ? ", @quantity" : string.Empty)
                        + ", @dmgmult, @ownerarea"
                        + (hasBaseAcBonus == true ? ", @baseac" : string.Empty) + @");
      SELECT last_insert_rowid();", conn);

                    if (tx != null) cmd.Transaction = tx;

                    cmd.Parameters.AddWithValue("@template", item.TemplateID ?? "");
                    cmd.Parameters.AddWithValue("@name", item.Name ?? "");
                    cmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "Room");
                    cmd.Parameters.AddWithValue("@oid", item.OwnerID);
                    cmd.Parameters.AddWithValue("@rid", item.RoomID);
                    cmd.Parameters.AddWithValue("@value", item.Value);
                    cmd.Parameters.AddWithValue("@weight", item.Weight);
                    cmd.Parameters.AddWithValue("@wear", item.WearLoc ?? "");
                    cmd.Parameters.AddWithValue("@hand", item.HandLoc);
                    cmd.Parameters.AddWithValue("@category", item.ItemCategory ?? "");
                    cmd.Parameters.AddWithValue("@short", item.ShortDesc ?? "");
                    cmd.Parameters.AddWithValue("@long", item.LongDesc ?? "");
                    cmd.Parameters.AddWithValue("@creator", item.Creator ?? "");
                    cmd.Parameters.AddWithValue("@cond", Math.Round(item.Condition, 2)); // Store as decimal
                    cmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");
                    if (hasMaterial) cmd.Parameters.AddWithValue("@material", item.Material ?? "");
                    if (hasDescriptor == true) cmd.Parameters.AddWithValue("@descriptor", item.Descriptor ?? "");
                    if (hasQuantity == true) cmd.Parameters.AddWithValue("@quantity", item.Quantity);
                    cmd.Parameters.AddWithValue("@dmgmult", item.DmgDiceMult);
                    cmd.Parameters.AddWithValue("@ownerarea", item.OwnerAreaID);
                    if (hasBaseAcBonus == true) cmd.Parameters.AddWithValue("@baseac", item.BaseACBonus);

                    if (setVerboseServerLogs)
                    {
                        Console.WriteLine("[ItemPersistence][TRACE] FLAGS param (INSERT): " + (item.FlagsJson ?? "null"));
                        Console.WriteLine("[ItemPersistence][TRACE] DMGMULT param (INSERT): " + item.DmgDiceMult);
                        if (hasQuantity == true) Console.WriteLine($"[ItemPersistence][TRACE] QUANTITY param (INSERT): {item.Quantity}");
                        if (hasMaterial) Console.WriteLine($"[ItemPersistence][TRACE] MATERIAL param (INSERT): {item.Material}");
                        if (hasDescriptor == true) Console.WriteLine($"[ItemPersistence][TRACE] DESCRIPTOR param (INSERT): {item.Descriptor}");
                        Console.WriteLine($"[ItemPersistence][TRACE] INSERT Exec: ConnState={conn.State}");
                    }

                    long newId = (long)cmd.ExecuteScalar();
                    item.ItemID = (int)newId;

                    if (setVerboseServerLogs) Console.WriteLine($"[ItemPersistence][TRACE] INSERT result: ItemID={item.ItemID}");
                }
                else
                {
                    using var cmd = new SqliteCommand(
                        @"UPDATE Item SET
            Name = @name,
            OwnerType = @otype,
            OwnerID = @oid,
            RoomID = @rid,
            OwnerAreaID = @ownerarea,
            Value = @value,
            Weight = @weight,
            WearLoc = @wear,
            HandLoc = @hand,
            ItemCategory = @category,
            ShortDesc = @short,
            LongDesc = @long,
            Creator = @creator,
            Condition = @cond,
            Flags = @flags,"
                        + (hasMaterial ? " Material = @material," : string.Empty)
                        + (hasDescriptor == true ? " Descriptor = @descriptor," : string.Empty)
                        + (hasQuantity == true ? " Quantity = @quantity," : string.Empty) + @"
            DmgDiceMult = @dmgmult"
                        + (hasBaseAcBonus == true ? ", BaseACBonus = @baseac" : string.Empty) + @"
        WHERE ItemID = @id;", conn);

                    if (tx != null) cmd.Transaction = tx;

                    cmd.Parameters.AddWithValue("@name", item.Name ?? "");
                    cmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
                    cmd.Parameters.AddWithValue("@oid", item.OwnerID);
                    cmd.Parameters.AddWithValue("@rid", item.RoomID);
                    cmd.Parameters.AddWithValue("@ownerarea", item.OwnerAreaID);
                    cmd.Parameters.AddWithValue("@value", item.Value);
                    cmd.Parameters.AddWithValue("@weight", item.Weight);
                    cmd.Parameters.AddWithValue("@wear", item.WearLoc ?? "");
                    cmd.Parameters.AddWithValue("@hand", item.HandLoc);
                    cmd.Parameters.AddWithValue("@category", item.ItemCategory ?? "");
                    cmd.Parameters.AddWithValue("@short", item.ShortDesc ?? "");
                    cmd.Parameters.AddWithValue("@long", item.LongDesc ?? "");
                    cmd.Parameters.AddWithValue("@creator", item.Creator ?? "");
                    cmd.Parameters.AddWithValue("@cond", Math.Round(item.Condition, 2)); // Store as decimal
                    cmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");
                    if (hasMaterial) cmd.Parameters.AddWithValue("@material", item.Material ?? "");
                    if (hasDescriptor == true) cmd.Parameters.AddWithValue("@descriptor", item.Descriptor ?? "");
                    if (hasQuantity == true) cmd.Parameters.AddWithValue("@quantity", item.Quantity);
                    cmd.Parameters.AddWithValue("@dmgmult", item.DmgDiceMult);
                    if (hasBaseAcBonus == true) cmd.Parameters.AddWithValue("@baseac", item.BaseACBonus);
                    cmd.Parameters.AddWithValue("@id", item.ItemID);

                    if (setVerboseServerLogs)
                    {
                        Console.WriteLine("[ItemPersistence][TRACE] FLAGS param (UPDATE): " + (item.FlagsJson ?? "null"));
                        Console.WriteLine("[ItemPersistence][TRACE] DMGMULT param (UPDATE): " + item.DmgDiceMult);
                        if (hasQuantity == true) Console.WriteLine($"[ItemPersistence][TRACE] QUANTITY param (UPDATE): {item.Quantity}");
                        if (hasMaterial) Console.WriteLine($"[ItemPersistence][TRACE] MATERIAL param (UPDATE): {item.Material}");
                        if (hasDescriptor == true) Console.WriteLine($"[ItemPersistence][TRACE] DESCRIPTOR param (UPDATE): {item.Descriptor}");
                        Console.WriteLine($"[ItemPersistence][TRACE] UPDATE Exec: ConnState={conn.State}, ItemID={item.ItemID}");
                    }

                    cmd.ExecuteNonQuery();
                }

                if (setVerboseServerLogs) Console.WriteLine($"[ItemPersistence][TRACE] SaveItem END: ConnState={conn.State}, ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}");
            }
            catch (SqliteException sx)
            {
                Console.WriteLine($"[ItemPersistence][ERROR] SQLite error saving item: {sx.ErrorCode} {sx.SqliteErrorCode} {sx.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemPersistence][ERROR] General error saving item: {ex}");
                throw;
            }
        } // end SaveItemInternal

        public static void DeleteItem(SqliteConnection conn, int itemId)
        {
            if (itemId <= 0) return;

            // If writer queue is available, perform delete through the single-writer to avoid concurrency issues.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    DeleteItemInternal(wConn, itemId, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall back to direct delete on provided connection.
            }
            catch
            {
                // On error enqueueing, fall back to direct delete to preserve previous behavior.
            }

            // Direct delete path (legacy)
            DeleteItemInternal(conn, itemId, null);
        }

        public static void DeleteItem(SqliteConnection conn, int itemId, SqliteTransaction? tx = null)
        {
            if (itemId <= 0) return;

            // If a transaction is supplied, caller is already in a writer context:
            // perform the delete using the provided transaction (no enqueue).
            if (tx != null)
            {
                try
                {
                    DeleteItemInternal(conn ?? throw new ArgumentNullException(nameof(conn)), itemId, tx);
                }
                catch
                {
                    // best-effort: swallow to preserve previous behavior semantics
                    throw;
                }
                return;
            }

            // No transaction supplied: try to route through the single-writer queue (preferred).
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    DeleteItemInternal(wConn, itemId, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct delete using provided connection.
            }
            catch
            {
                // On any enqueue error, fall back to direct delete to preserve prior behavior.
            }

            // Direct delete path (legacy)
            DeleteItemInternal(conn, itemId, null);
        }

        // Internal delete implementation that accepts an external transaction.
        // Used by the queue worker and by direct callers.
        private static void DeleteItemInternal(SqliteConnection conn, int itemId, SqliteTransaction? tx = null)
        {
            if (itemId <= 0) return;

            // NOTE: rely on DbWriteQueue for writer serialization. Do not use ad-hoc global locks here.
            EnsureOpen(conn);
            using var cmd = new SqliteCommand("DELETE FROM Item WHERE ItemID = @id", conn);
            if (tx != null) cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.ExecuteNonQuery();
        } // end DeleteItemInternal

        private static bool EnsureHasBaseAc(SqliteConnection conn, SqliteTransaction? tx = null)
        {
            if (!hasBaseAcBonus.HasValue) hasBaseAcBonus = TryHasColumn(conn, "BaseACBonus", tx);
            return hasBaseAcBonus == true;
        } // end EnsureHasBaseAc

        public static List<Item> LoadItemsByOwner(SqliteConnection conn, string ownerType, int ownerId, SqliteTransaction? tx = null)
        {
            var list = new List<Item>();
            if (conn == null) return list;

            EnsureOpen(conn);
            var normalized = NormalizeOwnerType(ownerType);
            bool includeBaseAc = EnsureHasBaseAc(conn, tx);
            bool includeQuantity = false;
            if (!hasQuantity.HasValue) hasQuantity = TryHasColumn(conn, "Quantity", tx);
            includeQuantity = hasQuantity == true;

            // detect Material and Descriptor column on the fly (do not cache here)
            bool includeMaterial = TryHasColumn(conn, "Material", tx);
            bool includeDescriptorLocal = TryHasColumn(conn, "Descriptor", tx);

            using var cmd = new SqliteCommand(@"
            SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID,
                   Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc,
                   LongDesc, Creator, Condition, Flags"
                            + (includeMaterial ? ", Material" : string.Empty)
                            + (includeDescriptorLocal ? ", Descriptor" : string.Empty)
                            + (includeQuantity ? ", Quantity" : string.Empty)
                            + ", DmgDiceMult"
                            + (includeBaseAc ? ", BaseACBonus" : ""), conn);

            if (tx != null) cmd.Transaction = tx;

            cmd.CommandText += " FROM Item WHERE OwnerType = @otype AND OwnerID = @oid;";
            cmd.Parameters.AddWithValue("@otype", normalized);
            cmd.Parameters.AddWithValue("@oid", ownerId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var it = new Item
                {
                    ItemID = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0,
                    TemplateID = !reader.IsDBNull(1) ? reader.GetString(1) : "",
                    Name = !reader.IsDBNull(2) ? reader.GetString(2) : "",
                    OwnerType = !reader.IsDBNull(3) ? reader.GetString(3) : "",
                    OwnerID = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0,
                    RoomID = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0,
                    Value = !reader.IsDBNull(6) ? reader.GetInt32(6) : 0,
                    Weight = !reader.IsDBNull(7) ? reader.GetDouble(7) : 0.0,
                    WearLoc = !reader.IsDBNull(8) ? reader.GetString(8) : "",
                    HandLoc = !reader.IsDBNull(9) ? reader.GetInt32(9) : 0,
                    ItemCategory = !reader.IsDBNull(10) ? reader.GetString(10) : "",
                    ShortDesc = !reader.IsDBNull(11) ? reader.GetString(11) : "",
                    LongDesc = !reader.IsDBNull(12) ? reader.GetString(12) : "",
                    Creator = !reader.IsDBNull(13) ? reader.GetString(13) : "",
                    Condition = !reader.IsDBNull(14) ? reader.GetFloat(14) : 100,
                    FlagsJson = !reader.IsDBNull(15) ? reader.GetString(15) : "[]"
                };

                // Determine ordinals for optional fields in a robust way
                int nextOrd = 16; // first ordinal after Flags
                int materialOrd = -1;
                int descriptorOrd = -1;
                int qtyOrd = -1;
                int dmgOrd = -1;
                int baseAcOrd = -1;

                if (includeMaterial)
                {
                    materialOrd = nextOrd++;
                }

                if (includeDescriptorLocal)
                {
                    descriptorOrd = nextOrd++;
                }

                if (includeQuantity)
                {
                    qtyOrd = nextOrd++;
                }

                // DmgDiceMult is always present after optional columns
                dmgOrd = nextOrd++;
                if (includeBaseAc)
                    baseAcOrd = nextOrd++;

                // Quantity (if present)
                try
                {
                    if (qtyOrd != -1 && !reader.IsDBNull(qtyOrd))
                        it.Quantity = reader.GetInt32(qtyOrd);
                    else
                        it.Quantity = 1;
                }
                catch { it.Quantity = 1; }

                // DmgDiceMult
                try
                {
                    if (!reader.IsDBNull(dmgOrd))
                        it.DmgDiceMult = reader.GetDouble(dmgOrd);
                    else
                        it.DmgDiceMult = 1.0;
                }
                catch
                {
                    it.DmgDiceMult = 1.0;
                }

                if (includeBaseAc)
                {
                    if (!reader.IsDBNull(baseAcOrd))
                        it.BaseACBonus = reader.GetDouble(baseAcOrd);
                }

                // Material (if present)
                try
                {
                    if (materialOrd != -1 && !reader.IsDBNull(materialOrd))
                        it.Material = reader.GetString(materialOrd);
                    else
                        it.Material = null;
                }
                catch { it.Material = null; }

                // Descriptor (if present)
                try
                {
                    if (descriptorOrd != -1 && !reader.IsDBNull(descriptorOrd))
                        it.Descriptor = reader.GetString(descriptorOrd) ?? "";
                    else
                        it.Descriptor = "";
                }
                catch { it.Descriptor = ""; }

                try
                {
                    it.Flags = JsonSerializer.Deserialize<List<string>>(it.FlagsJson ?? "[]") ?? new List<string>();
                }
                catch { it.Flags = new List<string>(); }

                it.OwnerType = NormalizeOwnerType(it.OwnerType);
                list.Add(it);
            }

            return list;
        } // end LoadItemsByOwner

     
        private static bool TryHasColumn(SqliteConnection conn, string column, SqliteTransaction? tx = null)
        {
            EnsureOpen(conn);
            using var c = conn.CreateCommand();
            if (tx != null) c.Transaction = tx;
            c.CommandText = "PRAGMA table_info(Item);";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        } // end TryHasColumn
    }
}