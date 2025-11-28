using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;

namespace MudPersistence
{
    public static class WorldLoader
    {
        public static World LoadFromDatabase(SqliteConnection conn)
        {
            World world = LoadWorld(conn);
            world.Areas = LoadAreas(conn, world.WorldID);

            foreach (var area in world.Areas)
            {
                area.Rooms = LoadRooms(conn, area.AreaID);

                // Load RoomExits, Keywords, Inventory for each room
                foreach (var room in area.Rooms)
                {
                    room.Exits = LoadRoomExits(conn, room.RoomID);
                    room.Keywords = LoadRoomKeywords(conn, room.RoomID);
                    room.Inventory = LoadRoomInventory(conn, room.RoomID); // now List<Item>
                }

                // Add all area rooms to world's master room list
                world.Rooms.AddRange(area.Rooms);
            }

            // Load persisted NPC instances and associate them with rooms + load their items
            try
            {
                var loadedNpcs = NpcPersistence.LoadAllNpcs(conn, world);

                foreach (var npc in loadedNpcs)
                {
                    try
                    {
                        // Load items that belong to this NPC (OwnerType = "NPC", OwnerID = npc.NpcID)
                        var items = ItemPersistence.LoadItemsByOwner(conn, "NPC", npc.NpcID);
                        if (items != null && items.Count > 0)
                            npc.Inventory = items;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NPC] Failed to load items for NPC {npc.NpcID}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPC] Failed to load NPCs: {ex.Message}");
            }

            return world;
        }

        private static World LoadWorld(SqliteConnection conn)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT WorldID, Name FROM World LIMIT 1";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new Exception("No world found in database.");

            return new World
            {
                WorldID = reader.GetInt32(0),
                Name = reader.GetString(1)
            };
        }

        private static List<Area> LoadAreas(SqliteConnection conn, int worldID)
        {
            var areas = new List<Area>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT AreaID, Name, Description, Theme, LevelRangeMin, LevelRangeMax, IsPvPAllowed, Owner FROM Area WHERE WorldID = @wid";
            cmd.Parameters.AddWithValue("@wid", worldID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var area = new Area(reader.GetString(1), reader.GetInt32(0))
                {
                    Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Theme = reader.IsDBNull(3) ? "Generic" : reader.GetString(3),
                    LevelRangeMin = reader.IsDBNull(4) ? 1 : reader.GetInt32(4),
                    LevelRangeMax = reader.IsDBNull(5) ? 100 : reader.GetInt32(5),
                    IsPvPAllowed = !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
                    Owner = reader.IsDBNull(7) ? "System" : reader.GetString(7)
                };
                areas.Add(area);
            }

            return areas;
        }

        private static List<Room> LoadRooms(SqliteConnection conn, int areaID)
        {
            var rooms = new List<Room>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT RoomID, Name, Description FROM Room WHERE AreaID = @aid";
            cmd.Parameters.AddWithValue("@aid", areaID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var room = new Room(
                    reader.GetString(1),                          // Name
                    reader.IsDBNull(2) ? "" : reader.GetString(2), // Description
                    areaID,                                        // Correct order: AreaID first
                    reader.GetInt32(0),                            // RoomID second
                    null                                           // Keywords loaded separately
                );

                rooms.Add(room);
            }

            return rooms;
        }

        private static List<Room.RoomExit> LoadRoomExits(SqliteConnection conn, int roomID)
        {
            var exits = new List<Room.RoomExit>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Direction, ToRoomID, IsHidden, DiscoverKeyword FROM RoomExit WHERE RoomID = @rid";
            cmd.Parameters.AddWithValue("@rid", roomID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                exits.Add(new Room.RoomExit(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    !reader.IsDBNull(2) && reader.GetInt32(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3)
                ));
            }

            return exits;
        }

        private static List<string> LoadRoomKeywords(SqliteConnection conn, int roomID)
        {
            var keywords = new List<string>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Keyword FROM RoomKeyword WHERE RoomID = @rid";
            cmd.Parameters.AddWithValue("@rid", roomID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                keywords.Add(reader.GetString(0));
            }

            return keywords;
        }

        private static List<Item> LoadRoomInventory(SqliteConnection conn, int roomID)
        {
            var inventory = new List<Item>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ItemID, Name FROM Item WHERE OwnerType = @otype AND OwnerID = @oid";
            cmd.Parameters.AddWithValue("@otype", "Room");
            cmd.Parameters.AddWithValue("@oid", roomID); // integer OwnerID

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                inventory.Add(new Item
                {
                    ItemID = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    RoomID = roomID
                });
            }

            return inventory;
        }
    }
}
