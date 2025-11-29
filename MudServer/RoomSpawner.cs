using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudPersistence;
using MudNetworking;

namespace MudServer
{
    public static class RoomSpawner
    {
        private static readonly Random _rng = new Random();

        // Call periodically from your GameLoop with the main World and DB connection.
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

                    // compute elapsed minutes robustly (treat MinValue as infinite elapsed)
                    double elapsed;
                    if (room.LastSpawnTime == DateTime.MinValue)
                        elapsed = double.PositiveInfinity;
                    else
                        elapsed = (now - room.LastSpawnTime).TotalMinutes;

                    if (room.SpawnDelayMinutes > 0 && elapsed < room.SpawnDelayMinutes)
                        continue;

                    // Diagnostic log for visibility
                    Console.WriteLine($"[Spawner] Room {room.RoomID} spawn check: Chance={room.SpawnChance}, DelayMin={room.SpawnDelayMinutes}, ElapsedMin={elapsed:F1}, Templates=[{string.Join(',', room.SpawnTemplateList)}]");

                    // Roll spawn chance (0..100). Do NOT update LastSpawnTime on failed roll so retries continue.
                    if (_rng.Next(100) >= Math.Clamp(room.SpawnChance, 0, 100))
                    {
                        // roll failed — skip without touching LastSpawnTime so future attempts will occur per delay
                        continue;
                    }

                    // Enforce SpawnMaxRoom: count current same-type objects in room
                    if (room.SpawnType?.Equals("Npc", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (room.Npcs.Count(n => room.SpawnTemplateList.Contains(n.TemplateID)) >= Math.Max(1, room.SpawnMaxRoom))
                        {
                            room.LastSpawnTime = now;
                            RoomPersistence.SaveRoom(conn, room);
                            continue;
                        }
                    }
                    else if (room.SpawnType?.Equals("Item", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (room.Inventory.Count(i => room.SpawnTemplateList.Contains(i.TemplateID)) >= Math.Max(1, room.SpawnMaxRoom))
                        {
                            room.LastSpawnTime = now;
                            RoomPersistence.SaveRoom(conn, room);
                            continue;
                        }
                    }

                    // Choose a template randomly
                    var templateId = room.SpawnTemplateList[_rng.Next(room.SpawnTemplateList.Count)];
                    Console.WriteLine($"[Spawner] Room {room.RoomID} attempting spawn of '{templateId}' as {room.SpawnType}");

                    if (room.SpawnType?.Equals("Npc", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (world.NpcFactory == null)
                        {
                            Console.WriteLine("[Spawner] NpcFactory missing - cannot spawn NPC.");
                        }
                        else
                        {
                            var npc = world.NpcFactory.CreateNpc(templateId, room);
                            int newId = NpcPersistence.InsertNpc(conn, npc);
                            npc.NpcID = newId;
                            if (!world.Npcs.Contains(npc)) world.Npcs.Add(npc);
                            room.Npcs.Add(npc);

                            foreach (var p in room.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    room,
                                    room.Npcs.Select(n => n.Name).ToList(),
                                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    world));

                                var text = string.IsNullOrEmpty(room.SpawnMessage) ? $"{npc.Name} appears." : room.SpawnMessage;
                                MessageHandler.SendSystem(p, text);
                            }
                        }
                    }
                    else if (room.SpawnType?.Equals("Item", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (world.ItemFactory == null)
                        {
                            Console.WriteLine("[Spawner] ItemFactory missing - cannot spawn Item.");
                        }
                        else
                        {
                            // IMPORTANT: use the same OwnerType/OwnerID that other code expects ("Room", room.RoomID)
                            // and pass room.RoomID into both ownerId and roomId parameters so persistence and RoomPersistence.SaveRoom
                            // will treat the item consistently with admin-created items.
                            var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID);

                            // persist and let DB assign ItemID
                            MudPersistence.ItemPersistence.SaveItem(conn, item);

                            // add to in-memory room inventory and persist the room (so SaveRoomInventory will sync DB)
                            room.Inventory.Add(item);
                            room.IsDirty = true;
                            RoomPersistence.SaveRoom(conn, room);

                            foreach (var p in room.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    room,
                                    room.Npcs.Select(n => n.Name).ToList(),
                                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    world));

                                var text = string.IsNullOrEmpty(room.SpawnMessage) ? $"{item.Name} appears." : room.SpawnMessage;
                                MessageHandler.SendSystem(p, text);
                            }
                        }
                    }

                    // update last spawn time and persist
                    room.LastSpawnTime = now;
                    RoomPersistence.SaveRoom(conn, room);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Spawner] Error processing spawns for room {room?.RoomID}: {ex.Message}");
                }
            }
        }
    }
}