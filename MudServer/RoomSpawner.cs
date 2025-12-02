using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudPersistence;
using MudNetworking;

namespace MudServer
{
    /// <summary>
    /// Periodically spawns NPCs or Items into rooms marked as spawners.
    /// Requirements (room side):
    ///   IsSpawner = true
    ///   SpawnType = "Npc" or "Item"
    ///   SpawnTemplateList = list of template IDs (for the chosen type)
    ///   SpawnChance (0..100) and SpawnDelayMinutes control timing
    ///   SpawnMaxRoom limits simultaneous instances of those templates
    /// </summary>
    public static class RoomSpawner
    {
        private static readonly Random _rng = new Random();

        public static void ProcessSpawns(World world, SqliteConnection conn)
        {
            if (world == null || conn == null) return;

            var now = DateTime.UtcNow;

            foreach (var room in world.Rooms)
            {
                try
                {
                    if (!room.IsSpawner) continue;
                    if (room.SpawnTemplateList == null || room.SpawnTemplateList.Count == 0) continue;

                    // Elapsed time since last spawn attempt
                    double elapsedMinutes = room.LastSpawnTime == DateTime.MinValue
                        ? double.PositiveInfinity
                        : (now - room.LastSpawnTime).TotalMinutes;

                    // Respect delay before attempting again
                    if (room.SpawnDelayMinutes > 0 && elapsedMinutes < room.SpawnDelayMinutes)
                        continue;

                    Console.WriteLine($"[Spawner] Room {room.RoomID} check: Type={room.SpawnType}, Chance={room.SpawnChance}, DelayMin={room.SpawnDelayMinutes}, ElapsedMin={elapsedMinutes:F1}, Templates=[{string.Join(',', room.SpawnTemplateList)}]");

                    // Roll chance (0..99). If roll >= SpawnChance, skip this cycle.
                    if (_rng.Next(100) >= Math.Clamp(room.SpawnChance, 0, 100))
                        continue;

                    if (room.SpawnType?.Equals("Item", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Count items whose TemplateID matches any of the configured spawn templates.
                        int currentCount = room.Inventory.Count(i => room.SpawnTemplateList.Contains(i.TemplateID));

                        if (currentCount >= Math.Max(1, room.SpawnMaxRoom))
                        {
                            // Room already at or above limit – update timestamp & persist room state.
                            room.LastSpawnTime = now;
                            RoomPersistence.SaveRoom(conn, room);
                            continue;
                        }
                    }
                    else if (room.SpawnType?.Equals("Npc", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        int currentCount = room.Npcs.Count(n => room.SpawnTemplateList.Contains(n.TemplateID));
                        if (currentCount >= Math.Max(1, room.SpawnMaxRoom))
                        {
                            room.LastSpawnTime = now;
                            RoomPersistence.SaveRoom(conn, room);
                            continue;
                        }
                    }
                    else
                    {
                        // Unknown type (defensive). Mark time so it doesn't retrigger rapidly.
                        room.LastSpawnTime = now;
                        RoomPersistence.SaveRoom(conn, room);
                        continue;
                    }

                    // Pick a template from the list uniformly
                    var templateId = room.SpawnTemplateList[_rng.Next(room.SpawnTemplateList.Count)];
                    Console.WriteLine($"[Spawner] Room {room.RoomID} attempting spawn of '{templateId}' as {room.SpawnType}");

                    if (room.SpawnType.Equals("Npc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (world.NpcFactory == null)
                        {
                            Console.WriteLine("[Spawner][WARN] NpcFactory missing - cannot spawn NPC.");
                        }
                        else
                        {
                            var npc = world.NpcFactory.CreateNpc(templateId, room);
                            npc.CurrentAreaID = room.AreaID;
                            // Persist NPC
                            int newId = NpcPersistence.InsertNpc(conn, npc);
                            npc.NpcID = newId;
                            if (!world.Npcs.Contains(npc)) world.Npcs.Add(npc);

                            // Broadcast updated room + spawn message
                            foreach (var p in room.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    room,
                                    room.Npcs.Select(n => n.DisplayName).ToList(),
                                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    world));

                                var text = string.IsNullOrEmpty(room.SpawnMessage)
                                    ? $"{npc.DisplayName} appears."
                                    : room.SpawnMessage;
                                MessageHandler.SendSystem(p, text);
                            }
                        }
                    }
                    else // Item branch
                    {
                        if (world.ItemFactory == null)
                        {
                            Console.WriteLine("[Spawner][WARN] ItemFactory missing - cannot spawn Item.");
                        }
                        else
                        {
                            try
                            {
                                // Create fully populated runtime item
                                var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID);
                                item.OwnerAreaID = room.AreaID; // already present; retain
                                ItemPersistence.SaveItem(conn, item);

                                if (item.ItemID == 0)
                                {
                                    Console.WriteLine($"[Spawner][ERROR] ItemID still 0 after insert (TemplateID='{templateId}', Name='{item.Name}'). Skipping add.");
                                }
                                else
                                {
                                    // Add to in-memory inventory
                                    room.Inventory.Add(item);
                                    room.IsDirty = true;

                                    // Optional quick verification query (count items for this room)
                                    using var verifyCmd = conn.CreateCommand();
                                    verifyCmd.CommandText = "SELECT COUNT(*) FROM Item WHERE OwnerType='Room' AND OwnerID=@rid";
                                    verifyCmd.Parameters.AddWithValue("@rid", room.RoomID);
                                    var countObj = verifyCmd.ExecuteScalar();
                                    long count = countObj is long l ? l : Convert.ToInt64(countObj);
                                    Console.WriteLine($"[Spawner] Post-insert: Room {room.RoomID} now has {count} item(s) in DB (OwnerType='Room').");
                                }

                                // Persist room (includes inventory sync via RoomPersistence)
                                RoomPersistence.SaveRoom(conn, room);

                                // Broadcast updated room + spawn message
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => n.DisplayName).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        world));

                                    var text = string.IsNullOrEmpty(room.SpawnMessage)
                                        ? $"{item.Name} appears."
                                        : room.SpawnMessage;
                                    MessageHandler.SendSystem(p, text);
                                }
                            }
                            catch (Exception iex)
                            {
                                Console.WriteLine($"[Spawner][ERROR] Item spawn exception (TemplateID='{templateId}', Room={room.RoomID}): {iex.Message}");
                            }
                        }
                    }

                    // Update last spawn time after successful attempt and persist room metadata
                    room.LastSpawnTime = now;
                    RoomPersistence.SaveRoom(conn, room);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Spawner] Error processing spawns for RoomID={room?.RoomID}: {ex.Message}");
                }
            }
        }
    }
}