using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Data;

namespace MudPersistence
{
    public static partial class ItemPersistence
    {
        static bool setVerboseServerLogs = false;
        private static readonly object DbLock = new();
        private static bool? hasBaseAcBonus;

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

        public static void SaveItem(SqliteConnection conn, Item item, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (string.Equals(item.OwnerType, "ROOM", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "Room";
            if (string.Equals(item.OwnerType, "PLAYER", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "Player";
            if (string.Equals(item.OwnerType, "NPC", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "NPC";

            // Only synchronize FlagsJson from Flags when Flags is non-null AND differs
            if (item.Flags != null)
            {
                var serialized = JsonSerializer.Serialize(item.Flags);
                if (!string.Equals(item.FlagsJson ?? "[]", serialized, StringComparison.Ordinal))
                {
                    item.FlagsJson = serialized;
                }
            }

            if (!string.Equals(item.OwnerType, "Room", StringComparison.OrdinalIgnoreCase))
            {
                item.OwnerAreaID = 0;
            }

            try
            {
                lock (DbLock)
                {
                    EnsureOpen(conn);

                    if (!hasBaseAcBonus.HasValue)
                        hasBaseAcBonus = TryHasColumn(conn, "BaseACBonus");

                    if (setVerboseServerLogs)
                        Console.WriteLine($"[ItemPersistence][TRACE] SaveItem START: ConnState={conn.State}, ItemID={item.ItemID}, TemplateID='{item.TemplateID}', Name='{item.Name}', OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}, FlagsJson='{item.FlagsJson}'");

                    if (item.ItemID <= 0)
                    {
                        using var cmd = new SqliteCommand(
                            @"INSERT INTO Item (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags, OwnerAreaID"
                            + (hasBaseAcBonus == true ? ", BaseACBonus" : string.Empty) + @")
                              VALUES (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags, @ownerarea"
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
                        cmd.Parameters.AddWithValue("@cond", item.Condition);
                        cmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");
                        cmd.Parameters.AddWithValue("@ownerarea", item.OwnerAreaID);
                        if (hasBaseAcBonus == true) cmd.Parameters.AddWithValue("@baseac", item.BaseACBonus);

                        if (setVerboseServerLogs)
                        {
                            Console.WriteLine("[ItemPersistence][TRACE] FLAGS param (INSERT): " + (item.FlagsJson ?? "null"));
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
                                Flags = @flags"
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
                        cmd.Parameters.AddWithValue("@cond", item.Condition);
                        cmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");
                        if (hasBaseAcBonus == true) cmd.Parameters.AddWithValue("@baseac", item.BaseACBonus);
                        cmd.Parameters.AddWithValue("@id", item.ItemID);

                        if (setVerboseServerLogs)
                        {
                            Console.WriteLine("[ItemPersistence][TRACE] FLAGS param (UPDATE): " + (item.FlagsJson ?? "null"));
                            Console.WriteLine($"[ItemPersistence][TRACE] UPDATE Exec: ConnState={conn.State}, ItemID={item.ItemID}");
                        }

                        cmd.ExecuteNonQuery();
                    }

                    if (setVerboseServerLogs) Console.WriteLine($"[ItemPersistence][TRACE] SaveItem END: ConnState={conn.State}, ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}");
                }
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
        }

        public static void DeleteItem(SqliteConnection conn, int itemId)
        {
            if (itemId <= 0) return;
            lock (DbLock)
            {
                EnsureOpen(conn);
                using var cmd = new SqliteCommand("DELETE FROM Item WHERE ItemID = @id", conn);
                cmd.Parameters.AddWithValue("@id", itemId);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool EnsureHasBaseAc(SqliteConnection conn)
        {
            if (!hasBaseAcBonus.HasValue) hasBaseAcBonus = TryHasColumn(conn, "BaseACBonus");
            return hasBaseAcBonus == true;
        }

        public static List<Item> LoadItemsByOwner(SqliteConnection conn, string ownerType, int ownerId)
        {
            var list = new List<Item>();
            if (conn == null) return list;

            lock (DbLock)
            {
                EnsureOpen(conn);
                var normalized = NormalizeOwnerType(ownerType);
                bool includeBaseAc = EnsureHasBaseAc(conn);

                using var cmd = new SqliteCommand(@"
                    SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID,
                           Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc,
                           LongDesc, Creator, Condition, Flags"
                           + (includeBaseAc ? ", BaseACBonus" : ""), conn);

                cmd.CommandText += " FROM Item WHERE OwnerType = @otype AND OwnerID = @oid;";
                cmd.Parameters.AddWithValue("@otype", normalized);
                cmd.Parameters.AddWithValue("@oid", ownerId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var it = new Item
                    {
                        ItemID      = !reader.IsDBNull(0)  ? reader.GetInt32(0)  : 0,
                        TemplateID  = !reader.IsDBNull(1)  ? reader.GetString(1) : "",
                        Name        = !reader.IsDBNull(2)  ? reader.GetString(2) : "",
                        OwnerType   = !reader.IsDBNull(3)  ? reader.GetString(3) : "",
                        OwnerID     = !reader.IsDBNull(4)  ? reader.GetInt32(4)  : 0,
                        RoomID      = !reader.IsDBNull(5)  ? reader.GetInt32(5)  : 0,
                        Value       = !reader.IsDBNull(6)  ? reader.GetInt32(6)  : 0,
                        Weight      = !reader.IsDBNull(7)  ? reader.GetDouble(7) : 0.0,
                        WearLoc     = !reader.IsDBNull(8)  ? reader.GetString(8) : "",
                        HandLoc     = !reader.IsDBNull(9)  ? reader.GetInt32(9)  : 0,
                        ItemCategory= !reader.IsDBNull(10) ? reader.GetString(10): "",
                        ShortDesc   = !reader.IsDBNull(11) ? reader.GetString(11): "",
                        LongDesc    = !reader.IsDBNull(12) ? reader.GetString(12): "",
                        Creator     = !reader.IsDBNull(13) ? reader.GetString(13): "",
                        Condition   = !reader.IsDBNull(14) ? reader.GetInt32(14) : 100,
                        FlagsJson   = !reader.IsDBNull(15) ? reader.GetString(15): "[]"
                    };

                    if (includeBaseAc)
                    {
                        int baseAcOrd = 16; // after Flags
                        if (!reader.IsDBNull(baseAcOrd))
                            it.BaseACBonus = reader.GetDouble(baseAcOrd);
                    }

                    try
                    {
                        it.Flags = JsonSerializer.Deserialize<List<string>>(it.FlagsJson ?? "[]") ?? new List<string>();
                    }
                    catch { it.Flags = new List<string>(); }

                    it.OwnerType = NormalizeOwnerType(it.OwnerType);
                    list.Add(it);
                }
            }
            return list;
        }

        private static bool TryHasColumn(SqliteConnection conn, string column)
        {
            EnsureOpen(conn);
            using var c = conn.CreateCommand();
            c.CommandText = "PRAGMA table_info(Item);";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
