using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudPersistence;
using MudNetworking;
using System.Text.Json;

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
            if (world == null || conn == null)
            {
                Console.WriteLine("[Spawner][ERROR] ProcessSpawns called with null world or conn.");
                return;
            }

            var now = DateTime.UtcNow;
            //Console.WriteLine($"[Spawner][TRACE] Tick at {now:o}, Rooms={world.Rooms.Count}");

            int spawnerRooms = world.Rooms.Count(r => r.IsSpawner);
            if (spawnerRooms == 0)
                Console.WriteLine("[Spawner][WARN] No rooms marked IsSpawner=true. No spawns will be processed.");

            foreach (var room in world.Rooms)
            {
                try
                {
                    if (!room.IsSpawner)
                    {
                        //Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} skipped: IsSpawner=false.");
                        continue;
                    }
                    if (room.SpawnTemplateList == null || room.SpawnTemplateList.Count == 0)
                    {
                        Console.WriteLine($"[Spawner][WARN] Room {room.RoomID} has empty SpawnTemplateList.");
                        continue;
                    }

                    double elapsedMinutes = room.LastSpawnTime == DateTime.MinValue
                        ? double.PositiveInfinity
                        : (now - room.LastSpawnTime).TotalMinutes;

                    if (room.SpawnDelayMinutes > 0 && elapsedMinutes < room.SpawnDelayMinutes)
                    {
                        //Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} delaying: elapsed={elapsedMinutes:F1} < delay={room.SpawnDelayMinutes}.");
                        continue;
                    }

                    //DEPRECATED// Previously we attempted spawn every tick once delay elapsed.
                    // New: consume the window immediately to allow exactly one attempt per delay period.
                    room.LastSpawnTime = now;
                    RoomPersistence.SaveRoom(conn, room);

                    Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} check: Type={room.SpawnType}, Chance={room.SpawnChance}, DelayMin={room.SpawnDelayMinutes}, ElapsedMin={elapsedMinutes:F1}, Templates=[{string.Join(',', room.SpawnTemplateList)}]");

                    int roll = _rng.Next(100);
                    if (roll >= Math.Clamp(room.SpawnChance, 0, 100))
                    {
                        Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} chance roll={roll} >= SpawnChance={room.SpawnChance}; no spawn this tick.");
                        //DEPRECATED// Old behavior would retry next tick; now delay is consumed, next attempt after full window.
                        continue;
                    }

                    if (room.SpawnType?.Equals("Item", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        int currentCount = room.Inventory.Count(i => room.SpawnTemplateList.Contains(i.TemplateID));
                        int max = Math.Max(1, room.SpawnMaxRoom);
                        Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} item count for templates={currentCount}, limit={max}.");

                        if (currentCount >= max)
                        {
                            //CODE REMOVED - 2025-12-02 23:59:00 // 'room.LastSpawnTime = now; RoomPersistence.SaveRoom(conn, room);'
                            //DEPRECATED// Limit reached: window already consumed above; wait next full delay.
                            Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} at limit; updated LastSpawnTime.");
                            continue;
                        }
                    }
                    else if (room.SpawnType?.Equals("Npc", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        int currentCount = room.Npcs.Count(n => room.SpawnTemplateList.Contains(n.TemplateID));
                        int max = Math.Max(1, room.SpawnMaxRoom);
                        Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} NPC count for templates={currentCount}, limit={max}.");

                        if (currentCount >= max)
                        {
                            //CODE REMOVED - 2025-12-02 23:59:00 // 'room.LastSpawnTime = now; RoomPersistence.SaveRoom(conn, room);'
                            //DEPRECATED// Limit reached: window already consumed above; wait next full delay.
                            Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} at NPC limit; updated LastSpawnTime.");
                            continue;
                        }
                    }
                    else
                    {
                        room.LastSpawnTime = now;
                        RoomPersistence.SaveRoom(conn, room);
                        Console.WriteLine($"[Spawner][ERROR] Room {room.RoomID} unknown SpawnType='{room.SpawnType}'.");
                        continue;
                    }

                    var templateId = room.SpawnTemplateList[_rng.Next(room.SpawnTemplateList.Count)];
                    Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} attempting spawn of '{templateId}' as {room.SpawnType}");

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
                            // Verify WanderAreas before persisting
                            Console.WriteLine($"[Spawner][TRACE] NPC {npc.Name} WanderAreas={JsonSerializer.Serialize(npc.WanderAreas ?? new List<int>())} CurrentAreaID={npc.CurrentAreaID}");
                            // Persist NPC
                            int newId = NpcPersistence.InsertNpc(conn, npc);
                            npc.NpcID = newId;
                            // After InsertNpc(conn, npc) and npc.NpcID assignment
                            if (!world.Npcs.Any(n => n.NpcID == npc.NpcID))
                            {
                                world.Npcs.Add(npc);
                                Console.WriteLine($"[WORLD][TRACE] NPC added to world list: NpcID={npc.NpcID} Name='{npc.Name}' RoomID={room.RoomID}");
                            }
                            else
                            {
                                //DEPRECATED// Duplicate add attempted; already present in world.Npcs.
                                Console.WriteLine($"[WORLD][TRACE] NPC already present in world list: NpcID={npc.NpcID}");
                            }

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

                            Console.WriteLine($"[Spawner] NPC '{npc.Name}' spawned in room {room.RoomID}.");
                            foreach (var p in room.Players)
                            {
                                MudNetworking.MessageHandler.SendSystem(p, room.SpawnMessage?.Length > 0 ? room.SpawnMessage : $"An NPC appears: {npc.Name}");
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
                                Console.WriteLine($"[Spawner][TRACE] Creating item from template='{templateId}' for RoomID={room.RoomID}, AreaID={room.AreaID}.");
                                // Create fully populated runtime item
                                var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID);
                                item.OwnerType = "Room";
                                item.OwnerID = room.RoomID;
                                item.RoomID = room.RoomID;
                                item.OwnerAreaID = room.AreaID;

                                Console.WriteLine($"[Spawner][TRACE] Pre-SaveItem: ItemID={item.ItemID}, TemplateID='{item.TemplateID}', Name='{item.Name}', OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}, FlagsJson='{item.FlagsJson}'.");

                                ItemPersistence.SaveItem(conn, item);

                                Console.WriteLine($"[Spawner][TRACE] Post-SaveItem: ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}.");

                                if (item.ItemID == 0)
                                {
                                    Console.WriteLine($"[Spawner][ERROR] ItemID still 0 after SAVE (TemplateID='{templateId}', Name='{item.Name}'). Skipping add to room!");
                                }
                                else
                                {
                                    room.Inventory.Add(item);
                                    room.IsDirty = true;

                                    using var verifyCmd = conn.CreateCommand();
                                    verifyCmd.CommandText = "SELECT COUNT(*) FROM Item WHERE OwnerType='Room' AND OwnerID=@rid";
                                    verifyCmd.Parameters.AddWithValue("@rid", room.RoomID);
                                    var countObj = verifyCmd.ExecuteScalar();
                                    long count = countObj is long l ? l : Convert.ToInt64(countObj);
                                    Console.WriteLine($"[Spawner][TRACE] DB verify: Room {room.RoomID} now has {count} item(s) persisted as OwnerType='Room'.");
                                }

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

                                Console.WriteLine($"[Spawner] Item '{item.Name}' spawned in room {room.RoomID}.");
                                foreach (var p in room.Players)
                                {
                                    MudNetworking.MessageHandler.SendSystem(p, room.SpawnMessage?.Length > 0 ? room.SpawnMessage : $"An item appears: {item.Name}");
                                }
                            }
                            catch (Exception iex)
                            {
                                Console.WriteLine($"[Spawner][ERROR] Item spawn exception (TemplateID='{templateId}', Room={room.RoomID}): {iex}");
                            }
                        }
                    }

                    // Update last spawn time after successful attempt and persist room metadata
                    //DEPRECATED// Window was already consumed at delay gate; redundant to set LastSpawnTime here.
                    if ((now - room.LastSpawnTime).TotalSeconds < 1)
                    {
                        room.LastSpawnTime = now;
                        RoomPersistence.SaveRoom(conn, room);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Spawner][ERROR] Processing room {room?.RoomID}: {ex}");
                }
            }
        }
    }
}