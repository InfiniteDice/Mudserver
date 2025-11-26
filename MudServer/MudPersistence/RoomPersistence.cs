// RoomPersistence.cs
using Microsoft.Data.Sqlite;
using MudCore.World;
using System;
using System.Collections.Generic;

namespace MudPersistence
{
    public static class RoomPersistence
    {
        // -------------------
        // Save a single room
        // -------------------
        public static void SaveRoom(SqliteConnection conn, Room room)
        {
            var cmd = new SqliteCommand(@"
                INSERT INTO Room (RoomID, AreaID, Name, Description)
                VALUES (@rid, @aid, @name, @desc)
                ON CONFLICT(RoomID) DO UPDATE SET
                    Name = @name,
                    Description = @desc,
                    AreaID = @aid;
            ", conn);

            cmd.Parameters.AddWithValue("@rid", room.ID);
            cmd.Parameters.AddWithValue("@aid", room.AreaID);
            cmd.Parameters.AddWithValue("@name", room.Name);
            cmd.Parameters.AddWithValue("@desc", room.Description ?? "");

            cmd.ExecuteNonQuery();

            // Save associated data
            SaveRoomExits(conn, room);
            SaveRoomKeywords(conn, room);
            SaveRoomInventory(conn, room);
        }

        // -------------------
        // Save room exits
        // -------------------
        public static void SaveRoomExits(SqliteConnection conn, Room room)
        {
            if (room.Exits == null) return;

            foreach (var exit in room.Exits)
            {
                var cmd = new SqliteCommand(@"
                    INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword)
                    VALUES (@rid, @dir, @toRid, @hidden, @keyword)
                    ON CONFLICT(RoomID, Direction) DO UPDATE SET
                        ToRoomID = @toRid,
                        IsHidden = @hidden,
                        DiscoverKeyword = @keyword;
                ", conn);

                cmd.Parameters.AddWithValue("@rid", room.ID);
                cmd.Parameters.AddWithValue("@dir", exit.Direction);
                cmd.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                cmd.Parameters.AddWithValue("@hidden", exit.IsHidden ? 1 : 0);
                cmd.Parameters.AddWithValue("@keyword", exit.DiscoverKeyword ?? "");

                cmd.ExecuteNonQuery();
            }
        }

        // -------------------
        // Save room keywords
        // -------------------
        public static void SaveRoomKeywords(SqliteConnection conn, Room room)
        {
            if (room.Keywords == null) return;

            // Delete old keywords first
            var deleteCmd = new SqliteCommand("DELETE FROM RoomKeyword WHERE RoomID = @rid", conn);
            deleteCmd.Parameters.AddWithValue("@rid", room.ID);
            deleteCmd.ExecuteNonQuery();

            // Insert new keywords
            foreach (var kw in room.Keywords)
            {
                var cmd = new SqliteCommand("INSERT INTO RoomKeyword (RoomID, Keyword) VALUES (@rid, @kw)", conn);
                cmd.Parameters.AddWithValue("@rid", room.ID);
                cmd.Parameters.AddWithValue("@kw", kw);
                cmd.ExecuteNonQuery();
            }
        }

        // -------------------
        // Save room inventory (use Item.OwnerType/OwnerID)
        // -------------------
        public static void SaveRoomInventory(SqliteConnection conn, Room room)
        {
            if (room.Inventory == null) return;

            // Remove existing item associations for this room
            var deleteCmd = new SqliteCommand("DELETE FROM Item WHERE OwnerType = @otype AND OwnerID = @oid", conn);
            deleteCmd.Parameters.AddWithValue("@otype", "Room");
            deleteCmd.Parameters.AddWithValue("@oid", room.ID.ToString());
            deleteCmd.ExecuteNonQuery();

            // Insert current inventory into Item table
            foreach (var item in room.Inventory)
            {
                var cmd = new SqliteCommand(@"
                    INSERT INTO Item (TemplateID, Name, OwnerType, OwnerID, RoomID, Value, Weight, WearLoc, HandLoc, ItemCategory, ShortDesc, LongDesc, Creator, Condition, Flags)
                    VALUES (@template, @name, @otype, @oid, @rid, @value, @weight, @wear, @hand, @category, @short, @long, @creator, @cond, @flags)
                    ON CONFLICT(ItemID) DO UPDATE SET
                        OwnerType = @otype,
                        OwnerID = @oid,
                        RoomID = @rid;
                ", conn);

                // If Inventory holds Item objects or plain names, handle both
                string name = item?.Name ?? item?.ToString() ?? "";
                cmd.Parameters.AddWithValue("@template", (item?.TemplateID) ?? "");
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@otype", "Room");
                cmd.Parameters.AddWithValue("@oid", room.ID.ToString());
                cmd.Parameters.AddWithValue("@rid", room.ID);
                cmd.Parameters.AddWithValue("@value", item?.Value ?? 0);
                cmd.Parameters.AddWithValue("@weight", item?.Weight ?? 0.0);
                cmd.Parameters.AddWithValue("@wear", item?.WearLoc ?? "");
                cmd.Parameters.AddWithValue("@hand", item?.HandLoc ?? 1);
                cmd.Parameters.AddWithValue("@category", item?.ItemCategory ?? "");
                cmd.Parameters.AddWithValue("@short", item?.ShortDesc ?? "");
                cmd.Parameters.AddWithValue("@long", item?.LongDesc ?? "");
                cmd.Parameters.AddWithValue("@creator", item?.Creator ?? "");
                cmd.Parameters.AddWithValue("@cond", item?.Condition ?? 100);
                cmd.Parameters.AddWithValue("@flags", item?.FlagsJson ?? "[]");

                cmd.ExecuteNonQuery();
            }
        }
    }
}
