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
    ///     - For NPCs: counts NPCs with matching TemplateID in CurrentRoomID (checks DB)
    ///     - For Items: counts items with OwnerType='Room' and OwnerID=RoomID where TemplateID matches (checks DB)
    ///     SAMPLE PROHBABILITIES:
    /// Formula: `1 / probability = 1 / 0.08 = 12.5 checks`
    /// **12.5 checks × 60 seconds = 750 seconds = 12.5 minutes**
    /// **Most likely:** Success within **5-20 minutes**
    /// **Average:** Success at **12.5 minutes**
    ///  **Rare but possible:** Could take up to **30-60 minutes** for unlucky streaks
    /// </summary>
    public static class RoomSpawner
    {
        private static readonly Random _rng = new Random();
        static bool useVerboseLogging = false;

        // NEW: Exposed spawn check interval - how often to roll SpawnChance (in seconds)
        public static int SpawnCheckIntervalSeconds { get; set; } = 60; // Check every 60 seconds - Important, the room spawner sets SpawnDelayMinutes in minutes this must also be passed in order to work correctly.

        private static DateTime _lastSpawnCheckTime = DateTime.MinValue;

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

            // NEW: Only process spawns every SpawnCheckIntervalSeconds
            
            if ((now - _lastSpawnCheckTime).TotalSeconds < SpawnCheckIntervalSeconds)
            {
                return; // Not time to check spawns yet
            }

            _lastSpawnCheckTime = now;
            if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Spawn check interval passed, processing spawns...");

            int spawnerRooms = world.Rooms.Count(r => r.IsSpawner);
            if (spawnerRooms == 0)
                Console.WriteLine("[Spawner][WARN] No rooms marked IsSpawner=true. No spawns will be processed.");

            foreach (var room in world.Rooms)
            {
                try
                {
                    if (!room.IsSpawner)
                        continue;

                    // Defensive: treat a spawn list that is null or contains only empty/whitespace entries as empty.
                    if (room.SpawnTemplateList == null || !room.SpawnTemplateList.Any(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        Console.WriteLine($"[Spawner][WARN] Room {room.RoomID} has empty SpawnTemplateList.");
                        continue;
                    }

                    double elapsedMinutes = room.LastSpawnTime == DateTime.MinValue
                        ? double.PositiveInfinity
                        : (now - room.LastSpawnTime).TotalMinutes;

                    // FIXED: Only skip if delay hasn't passed yet
                    if (room.SpawnDelayMinutes > 0 && elapsedMinutes < room.SpawnDelayMinutes)
                    {
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} delaying: elapsed={elapsedMinutes:F1} < delay={room.SpawnDelayMinutes}.");
                        continue;
                    }

                    // EVERY TICK: Roll SpawnChance (this happens every 3 seconds when delay has passed)
                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} delay passed (elapsed={elapsedMinutes:F1}), rolling chance: Type={room.SpawnType}, Chance={room.SpawnChance}, Templates=[{string.Join(',', room.SpawnTemplateList)}]");

                    int roll = _rng.Next(100);
                    if (roll >= Math.Clamp(room.SpawnChance, 0, 100))
                    {
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} chance roll FAILED ({roll} >= {room.SpawnChance}); no spawn this tick.");
                        continue; // Don't update LastSpawnTime - try again next tick
                    }

                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} chance roll PASSED ({roll} < {room.SpawnChance}), will attempt spawn.");

                    // Select template entry (may include ':' quantity suffix) up-front.
                    // NEW: support optional per-template trailing weight token, e.g. "goblin70" or "goblin_assassin20".
                    // Also preserve existing quantity suffix form "templateId:qty".
                    string selectedRaw = null!;
                    string templateId = null!;
                    int requestedQty = 1;

                    try
                    {
                        // Build candidate list with parsed templateId, optional trailing weight, and optional quantity
                        var candidates = room.SpawnTemplateList
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s =>
                            {
                                var entry = s.Trim();
                                int qty = 1;
                                // handle quantity suffix "template:qty" (colon)
                                var idxCol = entry.IndexOf(':');
                                string left = entry;
                                if (idxCol >= 0)
                                {
                                    left = entry.Substring(0, idxCol).Trim();
                                    var qtyStr = entry.Substring(idxCol + 1).Trim();
                                    if (!int.TryParse(qtyStr, out qty)) qty = 1;
                                }

                                // parse trailing digits as weight: e.g. "goblin70" -> id="goblin", weight=70
                                int weight = 0;
                                string tid = left;
                                // parse trailing digits as weight: e.g. "goblin70" -> id="goblin", weight=70
                                // FIXED: Use greedy group for id with explicit underscore/letter boundary
                                // The pattern requires the weight digits to follow a non-digit character
                                
                                try
                                {
                                    // Match: everything up to (and including) the last non-digit, then capture trailing digits
                                    // This ensures "goblin_boss10" -> id="goblin_boss", wt=10
                                    // And "goblin65" -> id="goblin", wt=65
                                    var m = System.Text.RegularExpressions.Regex.Match(left, @"^(?<id>.*[^0-9])(?<wt>\d+)$");
                                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["id"].Value))
                                    {
                                        tid = m.Groups["id"].Value;
                                        if (!int.TryParse(m.Groups["wt"].Value, out weight)) weight = 0;
                                    }
                                }
                                catch { /* best-effort parse */ }

                                return new { Raw = s, Template = tid, Qty = Math.Max(1, qty), Weight = Math.Max(0, weight) };
                            })
                            .Where(c => !string.IsNullOrWhiteSpace(c.Template))
                            .ToList();

                        if (candidates.Count == 0)
                        {
                            Console.WriteLine($"[Spawner][WARN] Room {room.RoomID} spawn parsing yielded no valid candidates from list: [{string.Join(',', room.SpawnTemplateList ?? new List<string>())}]");
                            continue;
                        }

                        // If any candidate has a positive weight, perform weighted random selection.
                        var totalWeight = candidates.Sum(c => c.Weight);
						if (totalWeight > 0)
						{
							var pick = _rng.Next(totalWeight);
							int acc = 0;

							foreach (var c in candidates)
							{
								int nextAcc = acc + c.Weight;
								if (pick >= acc && pick < nextAcc)  // ← FIXED: Range check instead of accumulator check
								{
									selectedRaw = c.Raw;
									templateId = c.Template;
									requestedQty = c.Qty;
									break;
								}
								acc = nextAcc;
							}
						}
						else
                        {
                            // fallback: uniform random selection (preserves original behavior)
                            var idx = _rng.Next(candidates.Count);
                            var c = candidates[idx];
                            selectedRaw = c.Raw;
                            templateId = c.Template;
                            requestedQty = c.Qty;
                        }
                    }
                    catch (Exception exSelect)
                    {
                        Console.WriteLine($"[Spawner][ERROR] Template selection failed in room {room.RoomID}: {exSelect.Message}");
                        // fallback to original simple choice to avoid blocking spawns
                        try
                        {
                            var rawEntry = room.SpawnTemplateList.Where(s => !string.IsNullOrWhiteSpace(s)).ElementAt(_rng.Next(Math.Max(1, room.SpawnTemplateList.Count(s => !string.IsNullOrWhiteSpace(s)))));
                            // strip qty if present
                            var idxCol = rawEntry.IndexOf(':');
                            var left = idxCol >= 0 ? rawEntry.Substring(0, idxCol).Trim() : rawEntry.Trim();
                            // parse trailing digits as weight: e.g. "goblin70" -> id="goblin", weight=70
                            // FIXED: Use greedy group for id with explicit underscore/letter boundary
                            // The pattern requires the weight digits to follow a non-digit character
                            int weight = 0;
                            string tid = left;
                            try
                            {
                                // Match: everything up to (and including) the last non-digit, then capture trailing digits
                                // This ensures "goblin_boss10" -> id="goblin_boss", wt=10
                                // And "goblin65" -> id="goblin", wt=65
                                var m = System.Text.RegularExpressions.Regex.Match(left, @"^(?<id>.*[^0-9])(?<wt>\d+)$");
                                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["id"].Value))
                                {
                                    tid = m.Groups["id"].Value;
                                    if (!int.TryParse(m.Groups["wt"].Value, out weight)) weight = 0;
                                }
                            }
                            catch { /* best-effort parse */ }
                            requestedQty = 1;
                            selectedRaw = rawEntry;
                        }
                        catch
                        {
                            // last resort - skip this room
                            Console.WriteLine($"[Spawner][ERROR] Fallback template selection also failed for room {room.RoomID}; skipping spawn.");
                            continue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(templateId))
                    {
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} resolved empty templateId from '{selectedRaw ?? "(null)"}', skipping.");
                        continue;
                    }

                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} selected spawn='{templateId}' qty={requestedQty} (raw='{selectedRaw}')");

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

                        // NEW: Check area-level MaxNpcs limit before spawning
                        var area = world.Areas?.FirstOrDefault(a => a?.AreaID == room.AreaID);
                        if (area != null && area.MaxNpcs > 0)
                        {
                            int areaCurrentCount = 0;
                            try
                            {
                                using var areaCountCmd = conn.CreateCommand();
                                areaCountCmd.CommandText = "SELECT COUNT(*) FROM Npc WHERE CurrentAreaID = @areaId";
                                areaCountCmd.Parameters.AddWithValue("@areaId", room.AreaID);
                                var result = areaCountCmd.ExecuteScalar();
                                areaCurrentCount = result != null ? Convert.ToInt32(result) : 0;
                            }
                            catch (Exception exAreaCount)
                            {
                                Console.WriteLine($"[Spawner][ERROR] Failed to count NPCs in area {room.AreaID}: {exAreaCount.Message}");
                                continue;
                            }

                            if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Area {room.AreaID} NPC count: {areaCurrentCount}/{area.MaxNpcs}");

                            if (areaCurrentCount >= area.MaxNpcs)
                            {
                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Area {room.AreaID} reached MaxNpcs limit ({areaCurrentCount}/{area.MaxNpcs}); skipping spawn.");
                                continue;
                            }
                        }

                        // CRITICAL FIX: Check SpawnMaxRoom BEFORE spawning NPC
                        int maxAllowed = Math.Max(1, room.SpawnMaxRoom);
                        int currentCount = 0;

                        try
                        {
                            using var countCmd = conn.CreateCommand();
                            countCmd.CommandText = "SELECT COUNT(*) FROM Npc WHERE TemplateID = @template AND CurrentRoomID = @roomid";
                            countCmd.Parameters.AddWithValue("@template", templateId);
                            countCmd.Parameters.AddWithValue("@roomid", room.RoomID);
                            var result = countCmd.ExecuteScalar();
                            currentCount = result != null ? Convert.ToInt32(result) : 0;
                        }
                        catch (Exception exCount)
                        {
                            Console.WriteLine($"[Spawner][ERROR] Failed to count NPCs for template '{templateId}' in room {room.RoomID}: {exCount.Message}");
                            continue;
                        }

                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} NPC count: {currentCount}/{maxAllowed} for template '{templateId}'");

                        if (currentCount >= maxAllowed)
                        {
                            if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} NPC limit reached ({currentCount}/{maxAllowed}); skipping spawn.");
                            continue;
                        }

                        // Extra sanity: log essential template fields
                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Template '{templateId}' ok: Name='{tmpl.Name}', Level={tmpl.Level ?? 0}, IsHumanoid={tmpl.IsHumanoid}, CS={tmpl.CombatSkill ?? 0}");

                        // Create NPC
                        var npc = world.NpcFactory.CreateNpc(templateId, room);

                        // Ensure hydrated defaults (redundant but safe)
                        npc.ApplyTemplateDefaults();

                        // CRITICAL: explicitly ensure new spawn has description from template when missing.
                        try
                        {
                            if (string.IsNullOrWhiteSpace(npc.Description) && npc.Template != null && !string.IsNullOrWhiteSpace(npc.Template.Description))
                            {
                                npc.Description = npc.Template.Description;
                            }
                        }
                        catch { /* best-effort */ }

                        // Persist core fields including description/humanoid/CS
                        npc.CurrentAreaID = room.AreaID;

                        // ===== NEW: IfSpawnSetWanderAreas flag processing =====
                        // If room has a flag like "IfSpawnSetWanderAreas:1,7,8", inherit those area IDs into the spawned NPC's WanderAreas
                        try
                        {
                            if (room.Flags != null)
                            {
                                foreach (var flag in room.Flags)
                                {
                                    if (string.IsNullOrWhiteSpace(flag)) continue;
                                    if (flag.StartsWith("IfSpawnSetWanderAreas:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var payload = flag.Substring("IfSpawnSetWanderAreas:".Length).Trim();
                                        if (!string.IsNullOrWhiteSpace(payload))
                                        {
                                            var parts = payload.Split(',', StringSplitOptions.RemoveEmptyEntries);
                                            if (npc.WanderAreas == null)
                                                npc.WanderAreas = new List<int>();

                                            foreach (var p in parts)
                                            {
                                                if (int.TryParse(p.Trim(), out int areaId) && areaId > 0)
                                                {
                                                    if (!npc.WanderAreas.Contains(areaId))
                                                        npc.WanderAreas.Add(areaId);
                                                }
                                            }

                                            if (useVerboseLogging)
                                                Console.WriteLine($"[Spawner][TRACE] NPC '{npc.Name}' inherited WanderAreas from room flag: [{string.Join(",", npc.WanderAreas)}]");
                                        }
                                        break; // Only process first matching flag
                                    }
                                }
                            }
                        }
                        catch (Exception exWander)
                        {
                            Console.WriteLine($"[Spawner][WARN] Failed to process IfSpawnSetWanderAreas for NPC '{npc.Name}': {exWander.Message}");
                        }
                        // ===== END IfSpawnSetWanderAreas processing =====

                        if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] NPC {npc.Name} WanderAreas={JsonSerializer.Serialize(npc.WanderAreas ?? new List<int>())} CurrentAreaID={npc.CurrentAreaID}");
                        int newId = NpcPersistence.InsertNpc(conn, npc);
                                                
                        npc.NpcID = newId;

                        // Persist trader initial state into Flags if this NPC is a trader and an ItemTrader instance exists.
                        try
                        {
                            try
                            {
                                if (MudCore.AI.ItemTraderManager.TryGet(npc, out var traderInstance))
                                {
                                    // Persist using current writer connection to ensure atomicity with spawn insert
                                    try { traderInstance.PersistState(conn); }
                                    catch (Exception exPers) { Console.WriteLine($"[Spawner][WARN] Failed to persist TraderState for NpcID={npc.NpcID}: {exPers.Message}"); }
                                }
                            }
                            catch { /* best-effort */ }
                        }
                        catch { /* best-effort */ }

                        //// Equip from template and persist to NPC ownership  // OLD SPAWN WITH ITEMS
                        //try
                        //{
                        //    var equipTemplates = npc.Template?.EquipmentTemplateIds;
                        //    if (equipTemplates != null && equipTemplates.Count > 0 && world.ItemFactory != null)
                        //    {
                        //        foreach (var equipTemplateId in equipTemplates)
                        //        {
                        //            try
                        //            {
                        //                var item = world.ItemFactory.CreateItem(equipTemplateId, "NPC", npc.NpcID, room.RoomID);
                        //                item.OwnerType = "NPC";
                        //                item.OwnerID = npc.NpcID;
                        //                item.RoomID = room.RoomID;
                        //                item.OwnerAreaID = 0;

                        //                // Ensure descriptor assigned for spawned/equipped items
                        //                try { ItemDescriptorManager.AssignDescriptorIfApplicable(item); } catch { /* best-effort */ }

                        //                ItemPersistence.SaveItem(conn, item);
                        //                npc.Inventory.Add(item);
                        //            }
                        //            catch (Exception iex)
                        //            {
                        //                Console.WriteLine($"[Spawner][NPC] Failed to create/equip item '{equipTemplateId}' for NPC {npc.Name}: {iex.Message}");
                        //            }
                        //        }
                        //    }
                        //}
                        //catch (Exception exEquip)
                        //{
                        //    Console.WriteLine($"[Spawner][NPC] Equipment assignment error for NPC {npc.Name}: {exEquip.Message}");
                        //}

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
                                room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                world));

                            var text = string.IsNullOrEmpty(room.SpawnMessage)
                                ? $"{npc.DisplayName} appears."
                                : room.SpawnMessage;
                            MessageHandler.SendSystem(p, text);
                        }

                        Console.WriteLine($"[Spawner] NPC '{npc.Name}' spawned in room {room.RoomID}.");

                        // CRITICAL FIX: Update LastSpawnTime AFTER successful spawn
                        room.LastSpawnTime = now;
                        try
                        {
                            RoomPersistence.SaveRoom(conn, room);
                            if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Updated LastSpawnTime for room {room.RoomID} after NPC spawn.");
                        }
                        catch (Exception exTime)
                        {
                            Console.WriteLine($"[Spawner][WARN] Failed to persist LastSpawnTime for Room {room.RoomID}: {exTime.Message}");
                        }
                    }
                    else
                    {
                        // Item spawn branch
                        if (world.ItemFactory == null)
                        {
                            Console.WriteLine("[Spawner][WARN] ItemFactory missing - cannot spawn Item.");
                            continue;
                        }

                        try
                        {
                            // Determine effective per-room limit (CUMULATIVE across all spawnable templates)
                            int max = Math.Max(1, room.SpawnMaxRoom);

                            // CRITICAL FIX: Query DB for authoritative item count
                            // Extract template IDs from spawn list (handles "template:qty" and "template80" weight syntax)
                            var spawnableTemplateIds = room.SpawnTemplateList
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Select(s =>
                                {
                                    var cleaned = s.Trim();
                                    // Strip ":qty" suffix
                                    var idxColon = cleaned.IndexOf(':');
                                    if (idxColon >= 0)
                                        cleaned = cleaned.Substring(0, idxColon).Trim();
                                    // Strip trailing weight digits (e.g., "mushroom80" -> "mushroom")
                                    var m = System.Text.RegularExpressions.Regex.Match(cleaned, @"^(?<id>.*[^0-9])(?<wt>\d+)$");
                                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["id"].Value))
                                        cleaned = m.Groups["id"].Value;
                                    return cleaned;
                                })
                                .Where(tid => !string.IsNullOrWhiteSpace(tid))
                                .ToList();

                            // Query DB for current count (authoritative)
                            int totalExistingCount = 0;
                            try
                            {
                                if (spawnableTemplateIds.Count > 0)
                                {
                                    var placeholders = string.Join(",", spawnableTemplateIds.Select((_, i) => $"@tid{i}"));
                                    using var countCmd = conn.CreateCommand();
                                    countCmd.CommandText = $@"
                                        SELECT COALESCE(SUM(Quantity), 0) 
                                        FROM Item 
                                        WHERE OwnerType = 'Room' 
                                          AND OwnerID = @roomid 
                                          AND TemplateID IN ({placeholders})";
                                    countCmd.Parameters.AddWithValue("@roomid", room.RoomID);
                                    for (int i = 0; i < spawnableTemplateIds.Count; i++)
                                    {
                                        countCmd.Parameters.AddWithValue($"@tid{i}", spawnableTemplateIds[i]);
                                    }
                                    var result = countCmd.ExecuteScalar();
                                    totalExistingCount = result != null ? Convert.ToInt32(result) : 0;
                                }
                            }
                            catch (Exception exCount)
                            {
                                Console.WriteLine($"[Spawner][ERROR] Failed to query item count for room {room.RoomID}: {exCount.Message}");
                                // Fallback to in-memory count
                                totalExistingCount = room.Inventory
                                    .Where(i => !string.IsNullOrEmpty(i.TemplateID) && spawnableTemplateIds.Contains(i.TemplateID))
                                    .Sum(i => Math.Max(1, i.Quantity));
                            }

                            if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} TOTAL spawnable items = {totalExistingCount}, limit={max}");

                            if (totalExistingCount >= max)
                            {
                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} reached cumulative limit of {max}; skipping spawn.");
                                continue;
                            }

                            // Build a prototype item (in-memory only) to learn flags (and therefore stackability).
                            var proto = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID, 1);
                            bool templateStackable = proto.Flags != null && proto.Flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase));
                            string protoFlagsJson = proto.FlagsJson ?? "[]";

                            if (templateStackable)
                            {
                                // For stackable items: check if we can still add to this specific template's stack
                                int existingQtyThisTemplate = room.Inventory
                                    .Where(i => !string.IsNullOrEmpty(i.TemplateID)
                                                && i.TemplateID == templateId
                                                && string.Equals(i.FlagsJson ?? "[]", protoFlagsJson, StringComparison.Ordinal))
                                    .Sum(i => Math.Max(1, i.Quantity));

                                // Calculate remaining capacity in the room (cumulative limit minus total current)
                                int remainingRoomCapacity = max - totalExistingCount;

                                if (remainingRoomCapacity <= 0)
                                {
                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} no remaining capacity for stackable '{templateId}'; skipping spawn.");
                                    continue;
                                }

                                // Only spawn up to remaining room capacity
                                int spawnQty = Math.Min(requestedQty, remainingRoomCapacity);
                                if (spawnQty <= 0) continue;

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} remaining capacity={remainingRoomCapacity}, spawning {spawnQty} of '{templateId}'.");

                                var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID, spawnQty);
                                item.OwnerType = "Room";
                                item.OwnerID = room.RoomID;
                                item.RoomID = room.RoomID;
                                item.OwnerAreaID = room.AreaID;

                                // Convert StartsBuriedN (template) into persistent BuriedN flag on created item
                                try
                                {
                                    if (item.Flags == null)
                                        item.Flags = new List<string>();

                                    if ((item.Flags == null || item.Flags.Count == 0) && !string.IsNullOrEmpty(item.FlagsJson))
                                    {
                                        try
                                        {
                                            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(item.FlagsJson);
                                            if (parsed != null && parsed.Count > 0)
                                                item.Flags = parsed;
                                        }
                                        catch { /* ignore parse errors */ }
                                    }

                                    var existingFlags = item.Flags.ToList();
                                    foreach (var f in existingFlags)
                                    {
                                        if (string.IsNullOrWhiteSpace(f)) continue;
                                        if (f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var num = f.Substring("StartsBuried".Length).Trim();
                                            if (int.TryParse(num, out var pct))
                                            {
                                                try { item.Flags.Remove(f); } catch { }
                                                var buriedFlag = $"Buried{pct}";
                                                if (!item.Flags.Any(ff => string.Equals(ff, buriedFlag, StringComparison.OrdinalIgnoreCase)))
                                                    item.Flags.Add(buriedFlag);
                                            }
                                        }
                                    }

                                    // ensure FlagsJson updated deterministically before saving
                                    try
                                    {
                                        var sorted = (item.Flags ?? new List<string>()).OrderBy(s => s ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                                        item.FlagsJson = System.Text.Json.JsonSerializer.Serialize(sorted);
                                    }
                                    catch { /* best-effort */ }
                                }
                                catch (Exception exFlag)
                                {
                                    Console.WriteLine($"[Spawner][WARN] Failed to convert StartsBuried flag for spawned item '{item.Name}': {exFlag.Message}");
                                }

                                try { ItemDescriptorManager.AssignDescriptorIfApplicable(item); } catch { /* best-effort */ }

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Pre-SaveItem: Template='{templateId}', Name='{item.Name}', Quantity={item.Quantity}, FlagsJson='{item.FlagsJson}'.");

                                // Track if this is a NEW item or MERGED item
                                bool itemWasMerged = false;
                                int qtyBeforeSave = room.Inventory
                                    .Where(i => i != null &&
                                                i.TemplateID == templateId &&
                                                string.Equals(i.FlagsJson ?? "[]", item.FlagsJson ?? "[]", StringComparison.Ordinal))
                                    .Sum(i => Math.Max(1, i.Quantity));

                                ItemPersistence.SaveItem(conn, item);

                                if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Post-SaveItem: ItemID={item.ItemID}, Quantity={item.Quantity}.");

                                if (item.ItemID == 0)
                                {
                                    Console.WriteLine($"[Spawner][ERROR] ItemID still 0 after SAVE (TemplateID='{templateId}', Name='{item.Name}'). Skipping add to room!");
                                }
                                else
                                {
                                    // CRITICAL FIX: Find existing in-memory item by TemplateID + Flags (NOT by ItemID)
                                    var existingInMemory = room.Inventory.FirstOrDefault(i =>
                                        i != null &&
                                        !string.IsNullOrEmpty(i.TemplateID) &&
                                        i.TemplateID == templateId &&
                                        string.Equals(i.FlagsJson ?? "[]", item.FlagsJson ?? "[]", StringComparison.Ordinal));

                                    if (existingInMemory != null)
                                    {
                                        // Item was MERGED in DB - update the in-memory object
                                        existingInMemory.ItemID = item.ItemID;
                                        existingInMemory.Quantity = item.Quantity;
                                        itemWasMerged = true;

                                        if (useVerboseLogging)
                                            Console.WriteLine($"[Spawner][TRACE] MERGED: ItemID={existingInMemory.ItemID}, OldQty={qtyBeforeSave}, NewQty={existingInMemory.Quantity}");
                                    }
                                    else
                                    {
                                        // New item - add to room
                                        room.Inventory.Add(item);

                                        if (useVerboseLogging)
                                            Console.WriteLine($"[Spawner][TRACE] NEW: Added ItemID={item.ItemID}, Qty={item.Quantity}");
                                    }

                                    room.IsDirty = true;
                                }

                                try { RoomPersistence.SaveRoom(conn, room); } catch (Exception exRoomSave) { Console.WriteLine($"[Spawner][WARN] Failed to save room after item spawn: {exRoomSave.Message}"); }

                                // Send room update and message ONLY if item was newly created (not merged)
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        world));

                                    // CRITICAL FIX: Only send "appears" message if this was a NEW item, not a merge
                                    if (!itemWasMerged)
                                    {
                                        try
                                        {
                                            bool isBuried = item.Flags != null && item.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase));
                                            if (!isBuried)
                                            {
                                                var text = string.IsNullOrEmpty(room.SpawnMessage)
                                                    ? $"{item.Name} appears."
                                                    : room.SpawnMessage;
                                                MessageHandler.SendSystem(p, text);
                                            }
                                        }
                                        catch { /* best-effort */ }
                                    }
                                }

                                if (useVerboseLogging)
                                {
                                    var finalQty = room.Inventory
                                        .Where(i => i != null && i.TemplateID == templateId && string.Equals(i.FlagsJson ?? "[]", protoFlagsJson, StringComparison.Ordinal))
                                        .Sum(i => Math.Max(1, i.Quantity));
                                    Console.WriteLine($"[Spawner] Item '{proto.Name}' spawned in room {room.RoomID} (wasMerged={itemWasMerged}, totalQty={finalQty}).");
                                }

                                // CRITICAL FIX: Update LastSpawnTime AFTER successful item spawn
                                room.LastSpawnTime = now;
                                try
                                {
                                    RoomPersistence.SaveRoom(conn, room);
                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Updated LastSpawnTime for room {room.RoomID} after item spawn.");
                                }
                                catch (Exception exTime)
                                {
                                    Console.WriteLine($"[Spawner][WARN] Failed to persist LastSpawnTime for Room {room.RoomID}: {exTime.Message}");
                                }
                            }
                            else
                            {
                                // Non-stackable: respect cumulative room limit
                                int remainingRoomCapacity = max - totalExistingCount;

                                if (remainingRoomCapacity <= 0)
                                {
                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Room {room.RoomID} no remaining capacity for non-stackable '{templateId}'; skipping spawn.");
                                    continue;
                                }

                                // Determine how many individual items we can create (limited by remaining room capacity)
                                int allowedToSpawn = Math.Min(requestedQty, remainingRoomCapacity);
                                for (int i = 0; i < allowedToSpawn; i++)
                                {
                                    var item = world.ItemFactory.CreateItem(templateId, "Room", room.RoomID, room.RoomID, 1);
                                    item.OwnerType = "Room";
                                    item.OwnerID = room.RoomID;
                                    item.RoomID = room.RoomID;
                                    item.OwnerAreaID = room.AreaID;

                                    // Convert StartsBuriedN -> BuriedN on non-stackable item
                                    try
                                    {
                                        if (item.Flags == null)
                                            item.Flags = new List<string>();

                                        if ((item.Flags == null || item.Flags.Count == 0) && !string.IsNullOrEmpty(item.FlagsJson))
                                        {
                                            try
                                            {
                                                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(item.FlagsJson);
                                                if (parsed != null && parsed.Count > 0)
                                                    item.Flags = parsed;
                                            }
                                            catch { /* ignore parse errors */ }
                                        }

                                        var existingFlags = item.Flags.ToList();
                                        foreach (var f in existingFlags)
                                        {
                                            if (string.IsNullOrWhiteSpace(f)) continue;
                                            if (f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var num = f.Substring("StartsBuried".Length).Trim();
                                                if (int.TryParse(num, out var pct))
                                                {
                                                    try { item.Flags.Remove(f); } catch { }
                                                    var buriedFlag = $"Buried{pct}";
                                                    if (!item.Flags.Any(ff => string.Equals(ff, buriedFlag, StringComparison.OrdinalIgnoreCase)))
                                                        item.Flags.Add(buriedFlag);
                                                }
                                            }
                                        }

                                        try
                                        {
                                            var sorted = (item.Flags ?? new List<string>()).OrderBy(s => s ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                                            item.FlagsJson = System.Text.Json.JsonSerializer.Serialize(sorted);
                                        }
                                        catch { /* best-effort */ }
                                    }
                                    catch (Exception exFlag)
                                    {
                                        Console.WriteLine($"[Spawner][WARN] Failed to convert StartsBuried flag for spawned item '{item.Name}': {exFlag.Message}");
                                    }

                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(item); } catch { /* best-effort */ }

                                    ItemPersistence.SaveItem(conn, item);

                                    if (item.ItemID == 0)
                                    {
                                        Console.WriteLine($"[Spawner][ERROR] ItemID still 0 after SAVE (TemplateID='{templateId}', Name='{item.Name}'). Skipping add to room!");
                                        continue;
                                    }

                                    room.Inventory.Add(item);
                                    room.IsDirty = true;

                                    // Notify players incrementally: enqueue room payload (it filters buried items)
                                    foreach (var p in room.Players)
                                    {
                                        p.PendingMessageQueue.Add(MessageHandler.Room(
                                            room,
                                            room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                            room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                            world));

                                        // Only announce spawn if not buried
                                        try
                                        {
                                            bool isBuried = item.Flags != null && item.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase));
                                            if (!isBuried)
                                            {
                                                var text = string.IsNullOrEmpty(room.SpawnMessage)
                                                    ? $"{item.Name} appears."
                                                    : room.SpawnMessage;
                                                MessageHandler.SendSystem(p, text);
                                            }
                                        }
                                        catch { /* best-effort */ }
                                    }

                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Spawned non-stack item '{item.Name}' ItemID={item.ItemID} in Room {room.RoomID}.");
                                }

                                try { RoomPersistence.SaveRoom(conn, room); } catch (Exception exRoomSave) { Console.WriteLine($"[Spawner][WARN] Failed to save room after non-stack item spawn: {exRoomSave.Message}"); }

                                Console.WriteLine($"[Spawner] Spawned up to {allowedToSpawn} non-stack item(s) '{templateId}' in room {room.RoomID}.");

                                // CRITICAL FIX: Update LastSpawnTime AFTER successful non-stackable item spawn
                                room.LastSpawnTime = now;
                                try
                                {
                                    RoomPersistence.SaveRoom(conn, room);
                                    if (useVerboseLogging) Console.WriteLine($"[Spawner][TRACE] Updated LastSpawnTime for room {room.RoomID} after non-stackable item spawn.");
                                }
                                catch (Exception exTime)
                                {
                                    Console.WriteLine($"[Spawner][WARN] Failed to persist LastSpawnTime for Room {room.RoomID}: {exTime.Message}");
                                }
                            }
                        }
                        catch (Exception iex)
                        {
                            Console.WriteLine($"[Spawner][ERROR] Item spawn exception (TemplateID='{templateId}', Room={room.RoomID}): {iex}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Spawner][ERROR] Processing room {room?.RoomID}: {ex}");
                }
            }
        } // end ProcessSpawns
    }
}