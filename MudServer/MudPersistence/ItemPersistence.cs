using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MudPersistence
{
    /// <summary>
    /// Item persistence helpers (insert / update / load by owner).
    /// NOTE: This class was missing NormalizeOwnerType and LoadItemsByOwner after recent edits;
    /// they are restored here to fix compilation errors referenced in WorldLoader.
    /// </summary>
    public static partial class ItemPersistence
    {
        // Canonicalize OwnerType values ("Room", "Player", "NPC")
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

        // Save or update an item, ensuring OwnerType casing and integer OwnerID/RoomID are persisted correctly.
        // Add verbose logging inside SaveItem to trace parameters, SQL, and results
        public static void SaveItem(SqliteConnection conn, Item item)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (item == null) throw new ArgumentNullException(nameof(item));

            // Normalize OwnerType casing — canonical values: "Room","Player","NPC"
            if (string.Equals(item.OwnerType, "ROOM", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "Room";
            if (string.Equals(item.OwnerType, "PLAYER", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "Player";
            if (string.Equals(item.OwnerType, "NPC", StringComparison.OrdinalIgnoreCase)) item.OwnerType = "NPC";

            // Ensure FlagsJson aligns with Flags list if provided
            if (item.Flags != null)
                item.FlagsJson = JsonSerializer.Serialize(item.Flags);

            // If not Room, OwnerAreaID should be 0; otherwise keep value
            // Only force OwnerAreaID=0 if not Room; otherwise leave as provided
            if (!string.Equals(item.OwnerType, "Room", StringComparison.OrdinalIgnoreCase))
            {
                item.OwnerAreaID = 0;
            }
            // else: keep item.OwnerAreaID as set by caller (InventoryManager/CommandHandler/RoomSpawner)

            try
            {
                Console.WriteLine($"[ItemPersistence][TRACE] SaveItem START: ItemID={item.ItemID}, TemplateID='{item.TemplateID}', Name='{item.Name}', OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}, FlagsJson='{item.FlagsJson}'");

                if (item.ItemID <= 0)
                {
                    // DEPRECATED: legacy INSERT without OwnerAreaID (left for reference; do not use)
                    // using var cmdLegacy = new SqliteCommand(
                    //     @"INSERT INTO Item (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags)
                    //       VALUES (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags);
                    //       SELECT last_insert_rowid();", conn);

                    using var cmd = new SqliteCommand(
                        @"INSERT INTO Item (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags, OwnerAreaID)
                          VALUES (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags, @ownerarea);
                          SELECT last_insert_rowid();", conn);

                    cmd.Parameters.AddWithValue("@template", item.TemplateID ?? "");
                    cmd.Parameters.AddWithValue("@name", item.Name ?? "");
                    cmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "Room");
                    cmd.Parameters.AddWithValue("@oid", item.OwnerID);   // INTEGER
                    cmd.Parameters.AddWithValue("@rid", item.RoomID);    // INTEGER
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

                    Console.WriteLine("[ItemPersistence][TRACE] INSERT parameters: " +
                        $"template='{item.TemplateID}', name='{item.Name}', otype='{item.OwnerType}', oid={item.OwnerID}, rid={item.RoomID}, ownerarea={item.OwnerAreaID}, value={item.Value}, weight={item.Weight}, wear='{item.WearLoc}', hand={item.HandLoc}, category='{item.ItemCategory}', short='{item.ShortDesc}', long='{item.LongDesc}', creator='{item.Creator}', cond={item.Condition}, flags='{item.FlagsJson}'");

                    long newId = (long)cmd.ExecuteScalar();
                    item.ItemID = (int)newId;

                    Console.WriteLine($"[ItemPersistence][TRACE] INSERT result: ItemID={item.ItemID}");
                }
                else
                {
                    // DEPRECATED: legacy UPDATE without OwnerAreaID (left for reference; do not use)
                    // using var cmdLegacy = new SqliteCommand(
                    //     @"UPDATE Item SET
                    //         TemplateID=@template,
                    //         Name=@name,
                    //         OwnerType=@otype,
                    //         OwnerID=@oid,
                    //         RoomID=@rid,
                    //         Value=@value,
                    //         Weight=@weight,
                    //         WearLoc=@wear,
                    //         HandLoc=@hand,
                    //         ItemCategory=@category,
                    //         ShortDesc=@short,
                    //         LongDesc=@long,
                    //         Creator=@creator,
                    //         Condition=@cond,
                    //         Flags=@flags
                    //       WHERE ItemID = @id", conn);

                    using var cmd = new SqliteCommand(
                        @"UPDATE Item SET
                    TemplateID=@template,
                    Name=@name,
                    OwnerType=@otype,
                    OwnerID=@oid,
                    RoomID=@rid,
                    Value=@value,
                    Weight=@weight,
                    WearLoc=@wear,
                    HandLoc=@hand,
                    ItemCategory=@category,
                    ShortDesc=@short,
                    LongDesc=@long,
                    Creator=@creator,
                    Condition=@cond,
                    Flags=@flags,
                    OwnerAreaID=@ownerarea
                  WHERE ItemID = @id", conn);

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
                    cmd.Parameters.AddWithValue("@id", item.ItemID);

                    Console.WriteLine("[ItemPersistence][TRACE] UPDATE parameters: " +
                        $"id={item.ItemID}, template='{item.TemplateID}', name='{item.Name}', otype='{item.OwnerType}', oid={item.OwnerID}, rid={item.RoomID}, ownerarea={item.OwnerAreaID}, value={item.Value}, weight={item.Weight}, wear='{item.WearLoc}', hand={item.HandLoc}, category='{item.ItemCategory}', short='{item.ShortDesc}', long='{item.LongDesc}', creator='{item.Creator}', cond={item.Condition}, flags='{item.FlagsJson}'");

                    var rows = cmd.ExecuteNonQuery();
                    Console.WriteLine($"[ItemPersistence][TRACE] UPDATE affected rows: {rows}");
                }

                Console.WriteLine($"[ItemPersistence][TRACE] SaveItem END: ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}");
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
            using var cmd = new SqliteCommand("DELETE FROM Item WHERE ItemID = @id", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Load all items owned by (OwnerType, OwnerID).
        /// Matches the columns currently persisted by SaveItem.
        /// </summary>
        public static List<Item> LoadItemsByOwner(SqliteConnection conn, string ownerType, int ownerId)
        {
            var list = new List<Item>();
            if (conn == null) return list;

            var normalized = NormalizeOwnerType(ownerType);

            using var cmd = new SqliteCommand(@"
                SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID,
                       Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc,
                       LongDesc, Creator, Condition, Flags
                FROM Item
                WHERE OwnerType = @otype AND OwnerID = @oid;", conn);

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

                // Hydrate Flags list from FlagsJson
                try
                {
                    it.Flags = JsonSerializer.Deserialize<List<string>>(it.FlagsJson ?? "[]") ?? new List<string>();
                }
                catch
                {
                    it.Flags = new List<string>();
                }

                // Normalize OwnerType casing
                it.OwnerType = NormalizeOwnerType(it.OwnerType);

                list.Add(it);
            }

            return list;
        }
    }
}
