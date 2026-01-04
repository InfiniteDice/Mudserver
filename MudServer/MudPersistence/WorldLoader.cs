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
                    // NOTE: changed to search subdirectories so templates organized into subfolders are discovered.
                    foreach (var file in Directory.EnumerateFiles(npcTemplateDir, "*.json", SearchOption.AllDirectories))
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

                            // Primary attempt: deserialize into NpcTemplate
                            MudCore.Templates.NpcTemplate? tmpl = null;
                            try
                            {
                                tmpl = System.Text.Json.JsonSerializer.Deserialize<MudCore.Templates.NpcTemplate>(json, opts);
                            }
                            catch
                            {
                                tmpl = null;
                            }

                            // If deserialization failed or required fields missing, attempt to extract Id and Name from JSON.
                            // IMPORTANT: Enforce that a template must include a human-friendly "Name" in JSON.
                            if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.Id) || string.IsNullOrWhiteSpace(tmpl.Name))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    var root = doc.RootElement;
                                    if (root.ValueKind == JsonValueKind.Object)
                                    {
                                        // If Id present, try to read minimal id to validate file
                                        string id = "";
                                        if (root.TryGetProperty("Id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                                            id = idNode.GetString() ?? "";

                                        string name = "";
                                        if (root.TryGetProperty("Name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String)
                                            name = nameNode.GetString() ?? "";

                                        // If we couldn't deserialize into full shape, but JSON contains both Id and Name,
                                        // create a minimal template using those values. If Name is missing, skip the file.
                                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                                        {
                                            if (tmpl == null)
                                                tmpl = new MudCore.Templates.NpcTemplate { Id = id, Name = name };
                                            else
                                            {
                                                // ensure required identity fields are present
                                                tmpl.Id = string.IsNullOrWhiteSpace(tmpl.Id) ? id : tmpl.Id;
                                                tmpl.Name = string.IsNullOrWhiteSpace(tmpl.Name) ? name : tmpl.Name;
                                            }

                                            // If AiBehaviors is an array of objects, deserialize into structured configs
                                            if (root.TryGetProperty("AiBehaviors", out var abNode) && abNode.ValueKind == JsonValueKind.Array)
                                            {
                                                bool elementsAreObjects = abNode.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object);
                                                if (elementsAreObjects)
                                                {
                                                    try
                                                    {
                                                        var configs = JsonSerializer.Deserialize<List<MudCore.Templates.NpcTemplate.AiBehaviorConfig>>(abNode.GetRawText(), opts);
                                                        if (configs != null)
                                                        {
                                                            tmpl.AiBehaviorConfigs = configs;
                                                        }
                                                    }
                                                    catch (Exception exc)
                                                    {
                                                        Console.WriteLine($"[NpcTemplateManager] Failed to parse structured AiBehaviors in {Path.GetFileName(file)}: {exc.Message}");
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // Enforce NO FALLBACKS for display Name: skip templates missing Name.
                                            Console.WriteLine($"[TEMPLATE][WARN] Skipped NPC template (missing Name or Id): {Path.GetFileName(file)}");
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[TEMPLATE][WARN] Skipped invalid NPC template file (root not object): {Path.GetFileName(file)}");
                                        continue;
                                    }
                                }
                                catch
                                {
                                    Console.WriteLine($"[TEMPLATE][WARN] Skipped invalid NPC template file: {Path.GetFileName(file)}");
                                    continue;
                                }
                            }
                            else
                            {
                                // tmpl deserialized successfully — still check for structured AiBehaviors and populate AiBehaviorConfigs
                                try
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    var root = doc.RootElement;
                                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("AiBehaviors", out var abNode) && abNode.ValueKind == JsonValueKind.Array)
                                    {
                                        bool elementsAreObjects = abNode.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object);
                                        if (elementsAreObjects)
                                        {
                                            try
                                            {
                                                var configs = JsonSerializer.Deserialize<List<MudCore.Templates.NpcTemplate.AiBehaviorConfig>>(abNode.GetRawText(), opts);
                                                if (configs != null)
                                                    tmpl.AiBehaviorConfigs = configs;
                                            }
                                            catch (Exception exc)
                                            {
                                                Console.WriteLine($"[NpcTemplateManager] Failed to parse structured AiBehaviors in {Path.GetFileName(file)}: {exc.Message}");
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                    // ignore post-parse errors; proceed with tmpl as deserialized
                                }
                            }

                            // Enforce: templates must have both Id and Name. Do not register otherwise.
                            if (tmpl != null && !string.IsNullOrWhiteSpace(tmpl.Id) && !string.IsNullOrWhiteSpace(tmpl.Name))
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
        }//End LoadFromDatabase

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
            cmd.CommandText = "SELECT AreaID, Name, Description, Theme, LevelRangeMin, LevelRangeMax, IsPvPAllowed, Owner, Climate FROM Area WHERE WorldID = @wid";
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

                // NEW: load Climate (TEXT) - default to "Temperate"
                try
                {
                    area.Climate = reader.IsDBNull(8) ? "Temperate" : reader.GetString(8);
                    if (string.IsNullOrWhiteSpace(area.Climate))
                        area.Climate = "Temperate";
                }
                catch
                {
                    area.Climate = "Temperate";
                }

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
SELECT RoomID, Name, Description, IsSpawner, SpawnType, SpawnTemplateList, SpawnChance, SpawnDelayMinutes, LastSpawnTime, SpawnMaxRoom, SpawnMessage, DistantDescription, Flags, KeyWords, IsBuildZone
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

                // Robustness: some databases or manual edits may store a comma-separated string
                // (e.g. "goblin,goblin_assassin") instead of a JSON array. Detect and normalize.
                try
                {
                    if (room.SpawnTemplateList == null)
                        room.SpawnTemplateList = new List<string>();

                    if (room.SpawnTemplateList.Count == 1 && !string.IsNullOrWhiteSpace(room.SpawnTemplateList[0]))
                    {
                        var single = room.SpawnTemplateList[0].Trim();

                        // If the stored value looks like a JSON array string but failed deserialization,
                        // or is a comma-separated list (no surrounding '['), split on commas.
                        if ((single.Contains(',') && !single.StartsWith("[") && !single.EndsWith("]")) ||
                            (single.StartsWith("\"") && single.EndsWith("\"") && single.Contains(',')))
                        {
                            var parts = single.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                              .Select(s => s.Trim().Trim().Trim('"'))
                                              .Where(s => s.Length > 0)
                                              .ToList();
                            if (parts.Count > 0)
                                room.SpawnTemplateList = parts;
                        }
                    }
                }
                catch { /* best-effort normalization, do not fail load */ }

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

                // NEW: load Flags JSON
                try
                {
                    var flagsJson = reader.IsDBNull(12) ? "[]" : reader.GetString(12);
                    room.Flags = JsonSerializer.Deserialize<List<string>>(flagsJson) ?? new List<string>();
                }
                catch
                {
                    room.Flags = new List<string>();
                }

                // NEW: load KeyWords JSON
                try
                {
                    var kwJson = reader.IsDBNull(13) ? "[]" : reader.GetString(13);
                    room.KeyWords = JsonSerializer.Deserialize<List<string>>(kwJson) ?? new List<string>();
                }
                catch
                {
                    room.KeyWords = new List<string>();
                }

                // NEW: load IsBuildZone (stored as TEXT "true"/"false"); default false
                try
                {
                    if (!reader.IsDBNull(14))
                    {
                        var b = reader.GetString(14);
                        room.IsBuildZone = string.Equals(b, "true", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        room.IsBuildZone = false;
                    }
                }
                catch
                {
                    room.IsBuildZone = false;
                }

                // existing post-processing: load exits/keywords/inventory happens elsewhere in loader
                rooms.Add(room);
            }

            return rooms;
        }

        private static List<Room.RoomExit> LoadRoomExits(SqliteConnection conn, int roomID)
        {
            var exits = new List<Room.RoomExit>();
            var cmd = conn.CreateCommand();
            // Load DoorId (nullable) instead of deprecated HasDoor/DoorState columns
            cmd.CommandText = "SELECT Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId FROM RoomExit WHERE RoomID = @rid";
            cmd.Parameters.AddWithValue("@rid", roomID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var direction = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var toRoom = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                var isHidden = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
                var keyword = reader.IsDBNull(3) ? null : reader.GetString(3);

                int? doorId = null;
                try
                {
                    if (!reader.IsDBNull(4))
                        doorId = reader.GetInt32(4);
                }
                catch
                {
                    doorId = null;
                }

                var exit = new Room.RoomExit(
                    direction,
                    toRoom,
                    isHidden,
                    keyword,
                    doorId
                );

                // Initialize in-memory cached door state from authoritative Doors table when available.
                // If the DoorId references no row in Doors, clear the DoorId so renderers/clients do not show a ghost door.
                if (doorId.HasValue)
                {
                    try
                    {
                        EnsureOpen(conn);
                        using var dq = conn.CreateCommand();
                        dq.CommandText = "SELECT IsOpen FROM Doors WHERE DoorId = @did";
                        dq.Parameters.AddWithValue("@did", doorId.Value);
                        var obj = dq.ExecuteScalar();
                        if (obj != null && !(obj is DBNull))
                        {
                            exit.DoorIsOpen = Convert.ToInt32(obj) != 0;
                        }
                        else
                        {
                            // Authoritative Doors row missing -> clear the stale reference.
                            // Previously we left DoorIsOpen=null which left DoorId populated and caused the client to render a door.
                            //Console.WriteLine($"[WorldLoader] Warning: RoomExit (RoomID={roomID}, Dir='{direction}') references missing DoorId={doorId.Value}; clearing reference.");
                            exit.DoorId = null;
                            exit.DoorIsOpen = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        // best-effort: on error, clear the reference so we don't display a phantom door
                        Console.WriteLine($"[WorldLoader] Error resolving DoorId {doorId.Value} for Room {roomID}, Dir '{direction}': {ex.Message}. Clearing reference.");
                        exit.DoorId = null;
                        exit.DoorIsOpen = null;
                    }
                }

                exits.Add(exit);
            }

            return exits;
        } // end LoadRoomExits

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


    }
}
