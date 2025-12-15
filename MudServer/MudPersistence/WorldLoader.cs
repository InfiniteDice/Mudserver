using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using System.Data;

namespace MudPersistence
{
    public static class WorldLoader
    {
        private static void EnsureOpen(SqliteConnection conn)
        {
            if (conn.State != ConnectionState.Open)
                conn.Open();
        }

        public static World LoadFromDatabase(SqliteConnection conn)
        {
            EnsureOpen(conn);
            World world = LoadWorld(conn);

            // Initialize template manager and factory here
            try
            {
                // Candidate paths (bin and project root)
                var baseDir = AppContext.BaseDirectory;
                var candidateDirs = new[]
                {
                    Path.Combine(baseDir, "Data", "NpcTemplates"),
                    Path.Combine(baseDir, "..", "..", "..", "Data", "NpcTemplates"),
                    Path.Combine(baseDir, "..", "Data", "NpcTemplates")
                };

                string npcTemplateDir = candidateDirs.FirstOrDefault(Directory.Exists) ?? candidateDirs.Last();
                Console.WriteLine($"[TEMPLATE] Loading NPC templates from: {npcTemplateDir}");

                var tmplMgr = new MudCore.Templates.NpcTemplateManager();
                int loaded = 0;

                if (Directory.Exists(npcTemplateDir))
                {
                    foreach (var file in Directory.EnumerateFiles(npcTemplateDir, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var json = File.ReadAllText(file);
                            // Enable enum string deserialization
                            var opts = new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            };
                            opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

                            var tmpl = System.Text.Json.JsonSerializer.Deserialize<MudCore.Templates.NpcTemplate>(json, opts);
                            if (tmpl != null && !string.IsNullOrWhiteSpace(tmpl.Id))
                            {
                                tmplMgr.Register(tmpl);
                                loaded++;
                            }
                            else
                            {
                                Console.WriteLine($"[TEMPLATE][WARN] Skipped invalid NPC template file: {Path.GetFileName(file)}");
                            }
                        }
                        catch (Exception exFile)
                        {
                            Console.WriteLine($"[TEMPLATE][ERROR] Failed to load '{Path.GetFileName(file)}': {exFile.Message}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"[TEMPLATE][ERROR] Directory does not exist: {npcTemplateDir}");
                }

                world.InitializeTemplates(tmplMgr);
                Console.WriteLine($"[TEMPLATE] Loaded {loaded} NPC template(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TEMPLATE][ERROR] Failed to initialize NPC templates: {ex.Message}");
            }

            world.Areas = LoadAreas(conn, world.WorldID);

            foreach (var area in world.Areas)
            {
                area.Rooms = LoadRooms(conn, area.AreaID);

                // Load RoomExits, Keywords, Inventory for each room
                foreach (var room in area.Rooms)
                {
                    room.Exits = LoadRoomExits(conn, room.RoomID);
                    room.Keywords = LoadRoomKeywords(conn, room.RoomID);

                    // IMPORTANT: load full item records for room inventory to avoid partial object overwrites.
                    room.Inventory = ItemPersistence.LoadItemsByOwner(conn, "Room", room.RoomID);

                    // NEW: area linkage validation
                    foreach (var it in room.Inventory)
                    {
                        if (it.OwnerAreaID > 0 && room.AreaID > 0 && it.OwnerAreaID != room.AreaID)
                        {
                            Console.WriteLine($"[WorldLoader][WARN] ItemID={it.ItemID} area mismatch: Item.OwnerAreaID={it.OwnerAreaID}, Room.AreaID={room.AreaID}. (TemplateID='{it.TemplateID}', Name='{it.Name}')");
                            // Non-destructive: keep DB value; optionally align in-memory for consistency
                            it.OwnerAreaID = room.AreaID;
                        }
                    }
                }

                world.Rooms.AddRange(area.Rooms);
            }

            // Load persisted NPC instances and associate them with rooms + load their items
            try
            {
                EnsureOpen(conn);
                var loadedNpcs = NpcPersistence.LoadAllNpcs(conn, world);

                // Add to world list so World.Tick can iterate them
                if (loadedNpcs != null && loadedNpcs.Count > 0)
                {
                    foreach (var npc in loadedNpcs)
                    {
                        // Load NPC-owned items
                        try
                        {
                            var items = ItemPersistence.LoadItemsByOwner(conn, "NPC", npc.NpcID);
                            if (items != null && items.Count > 0)
                                npc.Inventory = items;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[NPC] Failed to load items for NPC {npc.NpcID}: {ex.Message}");
                        }

                        if (!world.Npcs.Any(n => n.NpcID == npc.NpcID))
                            world.Npcs.Add(npc);
                    }

                    Console.WriteLine($"[WORLD][TRACE] Loaded NPCs into world list: Count={world.Npcs.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPC] Failed to load NPCs: {ex.Message}");
            }

            // Optional: assert templates present after world load for diagnostics
            if (world.TemplateManager == null)
                Console.WriteLine("[WORLD][WARN] TemplateManager is null; NPC template-based features will fail until initialized.");

            return world;
        }

        private static World LoadWorld(SqliteConnection conn)
        {
            EnsureOpen(conn);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT WorldID, Name, DateTime FROM World LIMIT 1";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new Exception("No world found in database.");

            var world = new World
            {
                WorldID = reader.GetInt32(0),
                Name = reader.GetString(1)
            };

            // DateTime is optional; parse into GameClock if present.
            try
            {
                if (!reader.IsDBNull(2))
                {
                    var dtText = reader.GetString(2);
                    world.GameTime = GameClock.Parse(dtText);
                }
                else
                {
                    world.GameTime = new GameClock();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorldLoader] Failed to parse World.DateTime: {ex.Message}. Using default clock.");
                world.GameTime = new GameClock();
            }

            return world;
        }

        private static List<Area> LoadAreas(SqliteConnection conn, int worldID)
        {
            EnsureOpen(conn);
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
            EnsureOpen(conn);
            var rooms = new List<Room>();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
    SELECT RoomID, Name, Description, IsSpawner, SpawnType, SpawnTemplateList, SpawnChance, SpawnDelayMinutes, LastSpawnTime, SpawnMaxRoom, SpawnMessage, DistantDescription
    FROM Room
    WHERE AreaID = @aid";
            cmd.Parameters.AddWithValue("@aid", areaID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var roomId = reader.GetInt32(0);
                var roomName = reader.GetString(1);
                var roomDesc = reader.IsDBNull(2) ? "" : reader.GetString(2);

                var room = new Room(roomName, roomDesc, areaID: areaID, roomID: roomId);

                // Spawn fields (safe parsing with defaults)
                room.IsSpawner = !reader.IsDBNull(3) && reader.GetInt32(3) != 0;
                room.SpawnType = reader.IsDBNull(4) ? "" : reader.GetString(4);

                var spawnListJson = reader.IsDBNull(5) ? "[]" : reader.GetString(5);
                try
                {
                    room.SpawnTemplateList = JsonSerializer.Deserialize<List<string>>(spawnListJson) ?? new List<string>();
                }
                catch
                {
                    room.SpawnTemplateList = new List<string>();
                }

                room.SpawnChance = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                room.SpawnDelayMinutes = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);

                // LastSpawnTime: parse ISO string if present
                if (!reader.IsDBNull(8))
                {
                    var s = reader.GetString(8);
                    if (DateTime.TryParse(s, out var dt))
                        room.LastSpawnTime = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    else
                        room.LastSpawnTime = DateTime.MinValue;
                }
                else
                {
                    room.LastSpawnTime = DateTime.MinValue;
                }

                room.SpawnMaxRoom = reader.IsDBNull(9) ? 1 : reader.GetInt32(9);
                room.SpawnMessage = reader.IsDBNull(10) ? "" : reader.GetString(10);

                // NEW: load DistantDescription (nullable)
                room.DistantDescription = reader.IsDBNull(11) ? "" : reader.GetString(11);

                // existing post-processing: load exits/keywords/inventory happens elsewhere in loader
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

        //private static List<Item> LoadRoomInventory(SqliteConnection conn, int roomID)
        //{
        //    var inventory = new List<Item>();
        //    var cmd = conn.CreateCommand();
        //    cmd.CommandText = "SELECT ItemID, Name FROM Item WHERE OwnerType = @otype AND OwnerID = @oid";
        //    cmd.Parameters.AddWithValue("@otype", "Room");
        //    cmd.Parameters.AddWithValue("@oid", roomID); // integer OwnerID
        //
        //    using var reader = cmd.ExecuteReader();
        //    while (reader.Read())
        //    {
        //        inventory.Add(new Item
        //        {
        //            ItemID = reader.GetInt32(0),
        //            Name = reader.GetString(1),
        //            RoomID = roomID
        //        });
        //    }
        //
        //    return inventory;
        //}
    }
}
