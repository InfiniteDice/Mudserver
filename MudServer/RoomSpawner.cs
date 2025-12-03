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

            if (world.TemplateManager == null)
            {
                Console.WriteLine("[Spawner][ERROR] TemplateManager is null; cannot spawn NPCs. Ensure templates are loaded at startup.");
            }
            if (world.NpcFactory == null)
            {
                Console.WriteLine("[Spawner][ERROR] NpcFactory is null; cannot spawn NPCs. Ensure factory is constructed with the template manager.");
            }

            var now = DateTime.UtcNow;

            int spawnerRooms = world.Rooms.Count(r => r.IsSpawner);
            if (spawnerRooms == 0)
                Console.WriteLine("[Spawner][WARN] No rooms marked IsSpawner=true. No spawns will be processed.");

            foreach (var room in world.Rooms)
            {
                try
                {
                    if (!room.IsSpawner)
                        continue;
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
                        Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} delaying: elapsed={elapsedMinutes:F1} < delay={room.SpawnDelayMinutes}.");
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
                        continue;
                    }

                    if (room.SpawnType?.Equals("Item", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        int currentCount = room.Inventory.Count(i => room.SpawnTemplateList.Contains(i.TemplateID));
                        int max = Math.Max(1, room.SpawnMaxRoom);
                        Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} item count for templates={currentCount}, limit={max}.");

                        if (currentCount >= max)
                        {
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
                        if (world.NpcFactory == null || world.TemplateManager == null)
                        {
                            Console.WriteLine("[Spawner][ERROR] Cannot spawn NPC: NpcFactory or TemplateManager is null.");
                            continue;
                        }

                        // Verify template exists before calling factory
                        if (!world.TemplateManager.TryGet(templateId, out var tmpl))
                        {
                            Console.WriteLine($"[Spawner][ERROR] NPC template '{templateId}' not loaded; skipping spawn.");
                            continue;
                        }

                        // Extra sanity: log essential template fields
                        Console.WriteLine($"[Spawner][TRACE] Template '{templateId}' ok: Name='{tmpl.Name}', Level={tmpl.Level ?? 0}, IsHumanoid={tmpl.IsHumanoid}, CS={tmpl.CombatSkill ?? 0}");

                        // Create NPC
                        var npc = world.NpcFactory.CreateNpc(templateId, room);

                        // Ensure hydrated defaults (redundant but safe)
                        npc.ApplyTemplateDefaults();

                        // Persist core fields including description/humanoid/CS
                        npc.CurrentAreaID = room.AreaID;
                        Console.WriteLine($"[Spawner][TRACE] NPC {npc.Name} WanderAreas={JsonSerializer.Serialize(npc.WanderAreas ?? new List<int>())} CurrentAreaID={npc.CurrentAreaID}");
                        int newId = NpcPersistence.InsertNpc(conn, npc);
                        npc.NpcID = newId;

                        // Equip from template and persist to NPC ownership
                        try
                        {
                            var equipTemplates = npc.Template?.EquipmentTemplateIds;
                            if (equipTemplates != null && equipTemplates.Count > 0 && world.ItemFactory != null)
                            {
                                foreach (var equipTemplateId in equipTemplates)
                                {
                                    try
                                    {
                                        var item = world.ItemFactory.CreateItem(equipTemplateId, "NPC", npc.NpcID, room.RoomID);
                                        item.OwnerType = "NPC";
                                        item.OwnerID = npc.NpcID;
                                        item.RoomID = room.RoomID;
                                        item.OwnerAreaID = 0;
                                        ItemPersistence.SaveItem(conn, item);
                                        npc.Inventory.Add(item);
                                    }
                                    catch (Exception iex)
                                    {
                                        Console.WriteLine($"[Spawner][NPC] Failed to create/equip item '{equipTemplateId}' for NPC {npc.Name}: {iex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception exEquip)
                        {
                            Console.WriteLine($"[Spawner][NPC] Equipment assignment error for NPC {npc.Name}: {exEquip.Message}");
                        }

                        if (!world.Npcs.Any(n => n.NpcID == npc.NpcID))
                        {
                            world.Npcs.Add(npc);
                            Console.WriteLine($"[WORLD][TRACE] NPC added to world list: NpcID={npc.NpcID} Name='{npc.Name}' RoomID={room.RoomID}");
                        }
                        else
                        {
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
                    else
                    {
                        // Item branch unchanged...
                        if (world.ItemFactory == null)
                        {
                            Console.WriteLine("[Spawner][WARN] ItemFactory missing - cannot spawn Item.");
                        }
                        else
                        {
                            try
                            {
                                Console.WriteLine($"[Spawner][TRACE] Creating item from template='{templateId}' for RoomID={room.RoomID}, AreaID={room.AreaID}.");
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