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
    ///     - Each entry may be a plain template id ("mushroom_brown") or optionally include
    ///       a quantity suffix using ':' like "mushroom_brown:5" to request spawning a stack.
    ///   SpawnChance (0..100) and SpawnDelayMinutes control timing
    ///   SpawnMaxRoom limits simultaneous instances of those templates
    ///     - For non-stackable templates SpawnMaxRoom limits the number of separate item rows.
    ///     - For stackable templates SpawnMaxRoom limits the total quantity in the room for that template+flags.
    /// </summary>
    public static class RoomSpawner
    {
        private static readonly Random _rng = new Random();
        static bool useVerboseLogging = false;

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
                        if(useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} delaying: elapsed={elapsedMinutes:F1} < delay={room.SpawnDelayMinutes}.");
                        continue;
                    }

                    //DEPRECATED// Previously we attempted spawn every tick once delay elapsed.
                    // New: consume the window immediately to allow exactly one attempt per delay period.
                    room.LastSpawnTime = now;
                    RoomPersistence.SaveRoom(conn, room);

                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} check: Type={room.SpawnType}, Chance={room.SpawnChance}, DelayMin={room.SpawnDelayMinutes}, ElapsedMin={elapsedMinutes:F1}, Templates=[{string.Join(',', room.SpawnTemplateList)}]");

                    int roll = _rng.Next(100);
                    if (roll >= Math.Clamp(room.SpawnChance, 0, 100))
                    {
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} chance roll={roll} >= SpawnChance={room.SpawnChance}; no spawn this tick.");
                        continue;
                    }

                    // Select template entry (may include ':' quantity suffix) up-front.
                    var rawEntry = room.SpawnTemplateList[_rng.Next(room.SpawnTemplateList.Count)];
                    string templateId = rawEntry;
                    int requestedQty = 1;

                    if (!string.IsNullOrWhiteSpace(rawEntry) && rawEntry.Contains(':'))
                    {
                        var parts = rawEntry.Split(new[] { ':' }, 2);
                        if (!string.IsNullOrWhiteSpace(parts[0]))
                        {
                            templateId = parts[0].Trim();
                            if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsedQty) && parsedQty > 0)
                            {
                                requestedQty = Math.Min(parsedQty, 10000);
                            }
                        }
                    }

                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} attempting spawn of '{templateId}' (raw='{rawEntry}') qtyRequested={requestedQty} as {room.SpawnType}");

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
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Template '{templateId}' ok: Name='{tmpl.Name}', Level={tmpl.Level ?? 0}, IsHumanoid={tmpl.IsHumanoid}, CS={tmpl.CombatSkill ?? 0}");

                        // Create NPC
                        var npc = world.NpcFactory.CreateNpc(templateId, room);

                        // Ensure hydrated defaults (redundant but safe)
                        npc.ApplyTemplateDefaults();

                        // Persist core fields including description/humanoid/CS
                        npc.CurrentAreaID = room.AreaID;
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] NPC {npc.Name} WanderAreas={JsonSerializer.Serialize(npc.WanderAreas ?? new List<int>())} CurrentAreaID={npc.CurrentAreaID}");
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
                            if (useVerboseLogging) Console.WriteLine($"[WORLD][TRACE] NPC added to world list: NpcID={npc.NpcID} Name='{npc.Name}' RoomID={room.RoomID}");
                        }
                        else
                        {
                            if (useVerboseLogging) Console.WriteLine($"[WORLD][TRACE] NPC already present in world list: NpcID={npc.NpcID}");
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
                        // Item spawn branch — now enforces SpawnMaxRoom differently for stackable vs non-stackable templates.
                        if (world.ItemFactory == null)
                        {
                            Console.WriteLine("[Spawner][WARN] ItemFactory missing - cannot spawn Item.");
                            continue;
                        }

                        try
                        {
                            // Determine effective per-room limit
                            int max = Math.Max(1, room.SpawnMaxRoom);

                            // Build a prototype item (in-memory only) to learn flags (and therefore stackability).
                            var proto = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID, 1);
                            bool templateStackable = proto.Flags != null && proto.Flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase));
                            string protoFlagsJson = proto.FlagsJson ?? "[]";

                            if (templateStackable)
                            {
                                // Sum existing quantity for matching template+flags in this room
                                int existingQty = room.Inventory
                                    .Where(i => !string.IsNullOrEmpty(i.TemplateID)
                                                && i.TemplateID == templateId
                                                && string.Equals(i.FlagsJson ?? "[]", protoFlagsJson, StringComparison.Ordinal))
                                    .Sum(i => Math.Max(1, i.Quantity));

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} existing total qty for '{templateId}' = {existingQty}, limit={max}");

                                if (existingQty >= max)
                                {
                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} reached stack quantity limit for '{templateId}'; skipping spawn.");
                                    continue;
                                }

                                // Only spawn up to remaining capacity
                                int allowedToAdd = max - existingQty;
                                int spawnQty = Math.Min(requestedQty, allowedToAdd);
                                if (spawnQty <= 0) continue;

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Creating stackable item '{templateId}' qty={spawnQty} in Room {room.RoomID}.");

                                var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID, spawnQty);
                                item.OwnerType = "Room";
                                item.OwnerID = room.RoomID;
                                item.RoomID = room.RoomID;
                                item.OwnerAreaID = room.AreaID;

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Pre-SaveItem: Template='{templateId}', Name='{item.Name}', Quantity={item.Quantity}, FlagsJson='{item.FlagsJson}'.");

                                ItemPersistence.SaveItem(conn, item);

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Post-SaveItem: ItemID={item.ItemID}, Quantity={item.Quantity}.");

                                if (item.ItemID == 0)
                                {
                                    Console.WriteLine($"[Spawner][ERROR] ItemID still 0 after SAVE (TemplateID='{templateId}', Name='{item.Name}'). Skipping add to room!");
                                }
                                else
                                {
                                    // reflect merged/updated row into in-memory room.Inventory
                                    var existing = room.Inventory.FirstOrDefault(i => i.ItemID == item.ItemID);
                                    if (existing != null)
                                    {
                                        existing.Quantity = item.Quantity;
                                    }
                                    else
                                    {
                                        var merged = room.Inventory.FirstOrDefault(i =>
                                            !string.IsNullOrEmpty(i.TemplateID) &&
                                            i.TemplateID == item.TemplateID &&
                                            string.Equals(i.FlagsJson ?? "[]", item.FlagsJson ?? "[]", StringComparison.Ordinal));
                                        if (merged != null)
                                        {
                                            merged.Quantity = item.Quantity;
                                            merged.ItemID = item.ItemID;
                                        }
                                        else
                                        {
                                            room.Inventory.Add(item);
                                        }
                                    }

                                    room.IsDirty = true;
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

                                if (useVerboseLogging) Console.WriteLine($"[Spawner] Item '{proto.Name}' spawned in room {room.RoomID} (qty added={spawnQty}, newTotal={room.Inventory.FirstOrDefault(i => i.TemplateID == templateId && string.Equals(i.FlagsJson ?? "[]", protoFlagsJson, StringComparison.Ordinal))?.Quantity}).");
                            }
                            else
                            {
                                // Non-stackable: count existing rows for this template (or across templates, preserve previous semantics per-template)
                                int currentCount = room.Inventory.Count(i => !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == templateId);
                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} non-stack currentCount for '{templateId}' = {currentCount}, limit={max}");

                                if (currentCount >= max)
                                {
                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} at non-stack item limit for '{templateId}'; skipping spawn.");
                                    continue;
                                }

                                // Determine how many individual items we can create
                                int allowedToSpawn = Math.Min(requestedQty, max - currentCount);
                                for (int i = 0; i < allowedToSpawn; i++)
                                {
                                    var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID, 1);
                                    item.OwnerType = "Room";
                                    item.OwnerID = room.RoomID;
                                    item.RoomID = room.RoomID;
                                    item.OwnerAreaID = room.AreaID;

                                    ItemPersistence.SaveItem(conn, item);

                                    if (item.ItemID == 0)
                                    {
                                        Console.WriteLine($"[Spawner][ERROR] ItemID still 0 after SAVE (TemplateID='{templateId}', Name='{item.Name}'). Skipping add to room!");
                                        continue;
                                    }

                                    room.Inventory.Add(item);
                                    room.IsDirty = true;

                                    // Notify players incrementally
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

                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Spawned non-stack item '{item.Name}' ItemID={item.ItemID} in Room {room.RoomID}.");
                                }

                                RoomPersistence.SaveRoom(conn, room);

                                Console.WriteLine($"[Spawner] Spawned up to {allowedToSpawn} non-stack item(s) '{templateId}' in room {room.RoomID}.");
                            }
                        }
                        catch (Exception iex)
                        {
                            Console.WriteLine($"[Spawner][ERROR] Item spawn exception (TemplateID='{templateId}', Room={room.RoomID}): {iex}");
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