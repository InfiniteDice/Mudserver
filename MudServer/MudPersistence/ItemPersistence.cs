using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;

namespace MudPersistence
{
    public static class ItemPersistence
    {
        // -------------------
        // Save a single item instance
        // - Inserts new item when ItemID == 0 and sets item.ItemID
        // - Updates existing item when ItemID != 0
        // -------------------
        public static void SaveItem(SqliteConnection conn, Item item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (item.ItemID == 0)
            {
                // Insert new row, return generated id
                var insertCmd = new SqliteCommand(@"
                    INSERT INTO Item (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight,
                                      WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags)
                    VALUES (@tid, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags);
                    SELECT last_insert_rowid();
                ", conn);

                insertCmd.Parameters.AddWithValue("@tid", item.TemplateID ?? "");
                insertCmd.Parameters.AddWithValue("@name", item.Name ?? "");
                insertCmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
                insertCmd.Parameters.AddWithValue("@oid", item.OwnerID); // integer OwnerID
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

                long newId = (long)insertCmd.ExecuteScalar();
                item.ItemID = (int)newId;
                return;
            }

            // Update existing (or upsert)
            var cmd = new SqliteCommand(@"
                INSERT INTO Item (ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight,
                                  WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags)
                VALUES (@id, @tid, @name, @otype, @oid, @rid, @value, @weight, 
                        @wear, @hand, @category, @short, @long, @creator, @cond, @flags)
                ON CONFLICT(ItemID) DO UPDATE SET
                    TemplateID = @tid,
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
                    Flags = @flags;
            ", conn);

            cmd.Parameters.AddWithValue("@id", item.ItemID);
            cmd.Parameters.AddWithValue("@tid", item.TemplateID ?? "");
            cmd.Parameters.AddWithValue("@name", item.Name ?? "");
            cmd.Parameters.AddWithValue("@otype", item.OwnerType ?? "");
            cmd.Parameters.AddWithValue("@oid", item.OwnerID); // integer OwnerID
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

            cmd.ExecuteNonQuery();
        }

        // -------------------
        // Load items by owner (Player, NPC, Room, Container)
        // -------------------
        public static List<Item> LoadItemsByOwner(SqliteConnection conn, string ownerType, int ownerId)
        {
            var items = new List<Item>();

            var cmd = new SqliteCommand("SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags FROM Item WHERE OwnerType = @otype AND OwnerID = @oid", conn);
            cmd.Parameters.AddWithValue("@otype", ownerType);
            cmd.Parameters.AddWithValue("@oid", ownerId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = new Item
                {
                    ItemID = reader.GetInt32(0),
                    TemplateID = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    OwnerType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    OwnerID = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    RoomID = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    Value = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    Weight = reader.IsDBNull(7) ? 0.0 : reader.GetDouble(7),
                    WearLoc = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    HandLoc = reader.IsDBNull(9) ? 1 : reader.GetInt32(9),
                    ItemCategory = reader.IsDBNull(10) ? "" : reader.GetString(10),
                    ShortDesc = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    LongDesc = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    Creator = reader.IsDBNull(13) ? null : reader.GetString(13),
                    Condition = reader.IsDBNull(14) ? 100 : reader.GetInt32(14),
                    FlagsJson = reader.IsDBNull(15) ? "[]" : reader.GetString(15)
                };

                items.Add(item);
            }

            return items;
        }
    }
}
