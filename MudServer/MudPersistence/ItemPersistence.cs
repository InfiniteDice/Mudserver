using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;

namespace MudPersistence
{
    public static partial class ItemPersistence
    {
        public static void SaveItem(SqliteConnection conn, Item item)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (item == null) throw new ArgumentNullException(nameof(item));

            using var tran = conn.BeginTransaction();
            try
            {
                if (item.ItemID == 0)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT INTO Item
                            (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags)
                        VALUES
                            (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags);
                        SELECT last_insert_rowid();
                    ";
                    insertCmd.Parameters.AddWithValue("@template", item.TemplateID ?? "");
                    insertCmd.Parameters.AddWithValue("@name", item.Name ?? "");
                    insertCmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
                    insertCmd.Parameters.AddWithValue("@oid", item.OwnerID); // int
                    insertCmd.Parameters.AddWithValue("@rid", item.RoomID);
                    insertCmd.Parameters.AddWithValue("@value", item.Value);
                    insertCmd.Parameters.AddWithValue("@weight", item.Weight);
                    insertCmd.Parameters.AddWithValue("@wear", item.WearLoc ?? "");
                    insertCmd.Parameters.AddWithValue("@hand", item.HandLoc);
                    insertCmd.Parameters.AddWithValue("@category", item.ItemCategory ?? "");
                    insertCmd.Parameters.AddWithValue("@short", item.ShortDesc ?? "");
                    insertCmd.Parameters.AddWithValue("@long", item.LongDesc ?? "");
                    insertCmd.Parameters.AddWithValue("@creator", item.Creator ?? "");
                    insertCmd.Parameters.AddWithValue("@cond", item.Condition);
                    insertCmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");

                    // execute and handle possible null safely (fixes CS8605 / CS8601)
                    var scalarObj = insertCmd.ExecuteScalar();
                    if (scalarObj != null)
                    {
                        if (scalarObj is long l) item.ItemID = (int)l;
                        else if (scalarObj is int i) item.ItemID = i;
                        else if (long.TryParse(scalarObj.ToString(), out var parsed)) item.ItemID = (int)parsed;
                    }
                }
                else
                {
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = @"
                        UPDATE Item SET
                            TemplateID = @template,
                            Name = @name,
                            OwnerType = @otype,
                            OwnerID = @oid,
                            RoomID = @rid,
                            Value = @value,
                            Weight = @weight,
                            WearLoc = @wear,
                            HandLoc = @hand,
                            ItemCategory = @category,
                            ShortDesc = @short,
                            LongDesc = @long,
                            Creator = @creator,
                            Condition = @cond,
                            Flags = @flags
                        WHERE ItemID = @id;
                    ";
                    updateCmd.Parameters.AddWithValue("@template", item.TemplateID ?? "");
                    updateCmd.Parameters.AddWithValue("@name", item.Name ?? "");
                    updateCmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
                    updateCmd.Parameters.AddWithValue("@oid", item.OwnerID);
                    updateCmd.Parameters.AddWithValue("@rid", item.RoomID);
                    updateCmd.Parameters.AddWithValue("@value", item.Value);
                    updateCmd.Parameters.AddWithValue("@weight", item.Weight);
                    updateCmd.Parameters.AddWithValue("@wear", item.WearLoc ?? "");
                    updateCmd.Parameters.AddWithValue("@hand", item.HandLoc);
                    updateCmd.Parameters.AddWithValue("@category", item.ItemCategory ?? "");
                    updateCmd.Parameters.AddWithValue("@short", item.ShortDesc ?? "");
                    updateCmd.Parameters.AddWithValue("@long", item.LongDesc ?? "");
                    updateCmd.Parameters.AddWithValue("@creator", item.Creator ?? "");
                    updateCmd.Parameters.AddWithValue("@cond", item.Condition);
                    updateCmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");
                    updateCmd.Parameters.AddWithValue("@id", item.ItemID);

                    var rows = updateCmd.ExecuteNonQuery();

                    if (rows == 0)
                    {
                        using var insertCmd = conn.CreateCommand();
                        insertCmd.CommandText = @"
                            INSERT INTO Item
                                (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags)
                            VALUES
                                (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags);
                            SELECT last_insert_rowid();
                        ";
                        insertCmd.Parameters.AddWithValue("@template", item.TemplateID ?? "");
                        insertCmd.Parameters.AddWithValue("@name", item.Name ?? "");
                        insertCmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
                        insertCmd.Parameters.AddWithValue("@oid", item.OwnerID);
                        insertCmd.Parameters.AddWithValue("@rid", item.RoomID);
                        insertCmd.Parameters.AddWithValue("@value", item.Value);
                        insertCmd.Parameters.AddWithValue("@weight", item.Weight);
                        insertCmd.Parameters.AddWithValue("@wear", item.WearLoc ?? "");
                        insertCmd.Parameters.AddWithValue("@hand", item.HandLoc);
                        insertCmd.Parameters.AddWithValue("@category", item.ItemCategory ?? "");
                        insertCmd.Parameters.AddWithValue("@short", item.ShortDesc ?? "");
                        insertCmd.Parameters.AddWithValue("@long", item.LongDesc ?? "");
                        insertCmd.Parameters.AddWithValue("@creator", item.Creator ?? "");
                        insertCmd.Parameters.AddWithValue("@cond", item.Condition);
                        insertCmd.Parameters.AddWithValue("@flags", item.FlagsJson ?? "[]");

                        var scalarObj = insertCmd.ExecuteScalar();
                        if (scalarObj != null)
                        {
                            if (scalarObj is long l) item.ItemID = (int)l;
                            else if (scalarObj is int i) item.ItemID = i;
                            else if (long.TryParse(scalarObj.ToString(), out var parsed)) item.ItemID = (int)parsed;
                        }
                    }
                }

                tran.Commit();
            }
            catch (SqliteException sx)
            {
                // If ON CONFLICT errors appear, log the schema and rethrow for easier diagnosis
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ItemPersistence] SQLite error saving item: {sx.Message}");
                try
                {
                    using var infoCmd = conn.CreateCommand();
                    infoCmd.CommandText = "PRAGMA table_info('Item');";
                    using var rdr = infoCmd.ExecuteReader();
                    Console.WriteLine("[ItemPersistence] Item table columns:");
                    while (rdr.Read())
                        Console.WriteLine($"  - {rdr.GetString(1)} ({rdr.GetString(2)}) pk={rdr.GetInt32(5)}");
                }
                catch { /* swallow */ }
                Console.ResetColor();
                throw;
            }
            catch
            {
                try { tran.Rollback(); } catch { }
                throw;
            }
        }

        // New: load items by owner (used by WorldLoader / NPC loader)
        public static List<Item> LoadItemsByOwner(SqliteConnection conn, string ownerType, int ownerId)
        {
            var list = new List<Item>();
            if (conn == null) return list;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags
                FROM Item
                WHERE OwnerType = @otype AND OwnerID = @oid";
            cmd.Parameters.AddWithValue("@otype", ownerType);
            cmd.Parameters.AddWithValue("@oid", ownerId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var it = new Item();

                it.ItemID = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                it.TemplateID = !reader.IsDBNull(1) ? reader.GetString(1) : "";
                it.Name = !reader.IsDBNull(2) ? reader.GetString(2) : "";
                it.OwnerType = !reader.IsDBNull(3) ? reader.GetString(3) : "";
                it.OwnerID = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                it.RoomID = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                it.Value = !reader.IsDBNull(6) ? reader.GetInt32(6) : 0;
                it.Weight = !reader.IsDBNull(7) ? reader.GetDouble(7) : 0.0;
                it.WearLoc = !reader.IsDBNull(8) ? reader.GetString(8) : "";
                it.HandLoc = !reader.IsDBNull(9) ? reader.GetInt32(9) : 0;
                it.ItemCategory = !reader.IsDBNull(10) ? reader.GetString(10) : "";
                it.ShortDesc = !reader.IsDBNull(11) ? reader.GetString(11) : "";
                it.LongDesc = !reader.IsDBNull(12) ? reader.GetString(12) : "";
                it.Creator = !reader.IsDBNull(13) ? reader.GetString(13) : "";
                it.Condition = !reader.IsDBNull(14) ? reader.GetInt32(14) : 100;
                it.FlagsJson = !reader.IsDBNull(15) ? reader.GetString(15) : "[]";

                list.Add(it);
            }

            return list;
        }
    }
}
