using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;

namespace MudPersistence
{
    /// <summary>
    /// Item persistence helpers (insert / update / load by owner).
    /// NOTE: This class was missing NormalizeOwnerType and LoadItemsByOwner after recent edits;
    /// they are restored here to fix compilation errors referenced in WorldLoader.
    /// </summary>
    public static partial class ItemPersistence
    {
        // Restored: normalizes OwnerType strings to canonical casing used throughout the DB.
        private static string NormalizeOwnerType(string? ownerType)
        {
            if (string.IsNullOrWhiteSpace(ownerType))
                return string.Empty;

            var s = ownerType.Trim();
            return s.ToLowerInvariant() switch
            {
                "room" or "rooms" or "room " => "Room",
                "npc" or "npcs" => "NPC",
                "player" or "players" => "Player",
                "owner" => "Player",
                _ => s
            };
        }

        /// <summary>
        /// Load all items owned by (OwnerType, OwnerID).
        /// Used by WorldLoader to hydrate full room and NPC inventories.
        /// </summary>
        public static List<Item> LoadItemsByOwner(SqliteConnection conn, string ownerType, int ownerId)
        {
            var list = new List<Item>();
            if (conn == null) return list;

            var normalized = NormalizeOwnerType(ownerType);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID,
                       Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc,
                       LongDesc, Creator, Condition, Flags, BaseACBonus, OwnerAreaID
                FROM Item
                WHERE OwnerType = @otype AND OwnerID = @oid";
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
                    FlagsJson   = !reader.IsDBNull(15) ? reader.GetString(15): "[]",
                    BaseACBonus = !reader.IsDBNull(16) ? reader.GetDouble(16) : 0.0,
                    OwnerAreaID = !reader.IsDBNull(17) ? reader.GetInt32(17) : 0
                };
                list.Add(it);
            }

            return list;
        }

        public static void SaveItem(SqliteConnection conn, Item item)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (item == null) throw new ArgumentNullException(nameof(item));

            // Uses restored NormalizeOwnerType
            item.OwnerType = NormalizeOwnerType(item.OwnerType);

            // Ensure OwnerAreaID is coherent: Room -> AreaID, else 0
            if (string.Equals(item.OwnerType, "Room", StringComparison.OrdinalIgnoreCase))
            {
                // OwnerAreaID should already be set by callers using room.AreaID; keep as-is.
                item.OwnerAreaID = item.OwnerAreaID;
            }
            else
            {
                item.OwnerAreaID = 0;
            }

            using var tran = conn.BeginTransaction();
            try
            {
                if (item.ItemID == 0)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT INTO Item
                            (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight,
                             WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator,
                             Condition, Flags, BaseACBonus, OwnerAreaID)
                        VALUES
                            (@template, @name, @otype, @oid, @rid, @value, @weight,
                             @wear, @hand, @category, @short, @long, @creator,
                             @cond, @flags, @baseac, @ownerarea);
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
                    insertCmd.Parameters.AddWithValue("@baseac", item.BaseACBonus);
                    insertCmd.Parameters.AddWithValue("@ownerarea", item.OwnerAreaID);

                    var scalarObj = insertCmd.ExecuteScalar();
                    if (scalarObj != null)
                    {
                        if (scalarObj is long l) item.ItemID = (int)l;
                        else if (scalarObj is int i) item.ItemID = i;
                        else if (long.TryParse(scalarObj.ToString(), out var parsed)) item.ItemID = (int)parsed;
                    }

                    Console.WriteLine($"[ItemPersistence] Inserted item (TemplateID='{item.TemplateID}', Name='{item.Name}', ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}).");
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
                            Flags = @flags,
                            BaseACBonus = @baseac,
                            OwnerAreaID = @ownerarea
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
                    updateCmd.Parameters.AddWithValue("@baseac", item.BaseACBonus);
                    updateCmd.Parameters.AddWithValue("@id", item.ItemID);
                    updateCmd.Parameters.AddWithValue("@ownerarea", item.OwnerAreaID);

                    var rows = updateCmd.ExecuteNonQuery();
                    if (rows == 0)
                    {
                        using var existsCmd = conn.CreateCommand();
                        existsCmd.CommandText = "SELECT 1 FROM Item WHERE ItemID = @id";
                        existsCmd.Parameters.AddWithValue("@id", item.ItemID);
                        var exists = existsCmd.ExecuteScalar() != null;

                        if (!exists)
                        {
                            if (!string.IsNullOrWhiteSpace(item.TemplateID) && !string.IsNullOrWhiteSpace(item.Name))
                            {
                                using var insertCmd = conn.CreateCommand();
                                insertCmd.CommandText = @"
                                    INSERT INTO Item
                                        (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight,
                                         WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator,
                                         Condition, Flags, BaseACBonus)
                                    VALUES
                                        (@template, @name, @otype, @oid, @rid, @value, @weight,
                                         @wear, @hand, @category, @short, @long, @creator,
                                         @cond, @flags, @baseac);
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

                                Console.WriteLine($"[ItemPersistence] Upsert fallback inserted item (TemplateID='{item.TemplateID}', ItemID={item.ItemID}).");
                            }
                            else
                            {
                                Console.WriteLine($"[ItemPersistence][WARN] Skipping fallback insert for ItemID={item.ItemID} due to missing TemplateID/Name.");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[ItemPersistence][WARN] UPDATE affected 0 rows for existing ItemID={item.ItemID}; skipping duplicate INSERT.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[ItemPersistence] Updated item ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}.");
                    }
                }

                tran.Commit();
            }
            catch (SqliteException sx)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ItemPersistence] SQLite error saving item: {sx.Message}");
                Console.ResetColor();
                try { tran.Rollback(); } catch { }
                throw;
            }
            catch
            {
                try { tran.Rollback(); } catch { }
                throw;
            }
        }
    }
}
