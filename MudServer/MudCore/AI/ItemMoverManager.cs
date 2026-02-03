
using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudCore.Entities;
using MudCore.Templates;
using MudPersistence;
using MudNetworking;
using MudCore;
using DbWriteQueue = global::MudServer.DbWriteQueue;

namespace MudCore.AI
{
    // Dedicated manager for hourly ItemMover worker behaviors.
    // Keeps logic isolated from NpcAI to minimize risky edits to the large file.
    public static class ItemMoverManager
    {
        // Diagnostic toggle (off by default).
        public static bool DiagnosticsEnabled { get; set; } = true;

        // Last processed total-hours baseline so this is idempotent across ticks.
        private static long _lastItemMoverProcessedTotalHours = long.MinValue;

        // Pending NPCs that were on-route when an ItemMover check ran.
        // Key: NpcID -> Value: time first scheduled (UTC).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _pendingRechecks
            = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>();

        // How long to keep retrying for a scheduled NPC before giving up.
        private static readonly TimeSpan _pendingRecheckTtl = TimeSpan.FromMinutes(30);

        // Track the last-minute retry window so we don't repeat it within the same period.
        private static string _lastMinuteRecheckKey = string.Empty;

        // NEW: Track failed Gather/Drop attempts for end-of-period retry
        private class RetryKey
        {
            public int NpcID { get; set; }
            public string Period { get; set; }
            public string Action { get; set; }

            public override bool Equals(object obj)
            {
                if (obj is RetryKey other)
                    return NpcID == other.NpcID &&
                           string.Equals(Period, other.Period, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(Action, other.Action, StringComparison.OrdinalIgnoreCase);
                return false;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(NpcID, Period?.ToLowerInvariant() ?? "", Action?.ToLowerInvariant() ?? "");
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<RetryKey, (DateTime firstAttempt, NpcTemplate.AiBehaviorConfig config)> _pendingRetries
            = new System.Collections.Concurrent.ConcurrentDictionary<RetryKey, (DateTime, NpcTemplate.AiBehaviorConfig)>();

        private static string _lastProcessedPeriod = "";

        // Public entry: run ItemMover checks once per game hour (idempotent).
        public static void ProcessItemMovers(World.World world, SqliteConnection conn)
        {

            try
            {
                if (world == null) return;
                var gc = world.GameTime ?? new GameClock();

                var currentPeriodNow = gc.GetPeriodDescriptor();
                if (gc.Minute == 55 &&
                    GameClock.TryGetPeriodHourRange(currentPeriodNow, out var range) &&
                    gc.Hour == range.EndHour)
                {
                    var minuteKey = $"{gc.Year}-{gc.Month}-{gc.Day}-{gc.Hour}-{currentPeriodNow}";
                    if (!string.Equals(_lastMinuteRecheckKey, minuteKey, StringComparison.Ordinal))
                    {
                        if (DiagnosticsEnabled)
                            Console.WriteLine($"[ITEMMOVER] Last-minute window reached for period '{currentPeriodNow}' (Hour={gc.Hour}). Running retries before the transition.");
                        ProcessEndOfPeriodRetries(world, conn, currentPeriodNow);
                        _lastMinuteRecheckKey = minuteKey;
                    }
                }
                else
                {
                    if (gc.Minute < 55 || gc.Minute > 55)
                        _lastMinuteRecheckKey = string.Empty;
                }

                long totalMonths = ((gc.Year - 1) * 12L) + (gc.Month - 1);
                long totalDays = (totalMonths * 30L) + (gc.Day - 1);
                long totalHours = (totalDays * 24L) + gc.Hour;

                // initialize baseline (do not retroactively process missed hours)
                if (_lastItemMoverProcessedTotalHours == long.MinValue)
                {
                    _lastItemMoverProcessedTotalHours = totalHours;
                    _lastProcessedPeriod = gc.GetPeriodDescriptor(); // NEW
                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER] Initialized baseline at totalHours={totalHours}");
                    return;
                }

                long deltaHours = totalHours - _lastItemMoverProcessedTotalHours;
                if (deltaHours < 1) return;

                long steps = deltaHours; // one check per hour
                for (long s = 0; s < steps; s++)
                {
                    // NEW: Check if period changed - process end-of-period retries BEFORE normal step
                    string currentPeriod = gc.GetPeriodDescriptor();
                    if (!string.Equals(currentPeriod, _lastProcessedPeriod, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(_lastProcessedPeriod)) // Skip first initialization
                        {
                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER] Period changed from '{_lastProcessedPeriod}' to '{currentPeriod}' - processing end-of-period retries");
                            ProcessEndOfPeriodRetries(world, conn, _lastProcessedPeriod);
                        }
                        _lastProcessedPeriod = currentPeriod;
                    }

                    ProcessItemMoverStep(world, conn);
                    _lastItemMoverProcessedTotalHours += 1;
                }

                // clamp if lagging
                if (_lastItemMoverProcessedTotalHours < totalHours)
                    _lastItemMoverProcessedTotalHours = totalHours;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ITEMMOVER] Unexpected error in ProcessItemMovers: {ex.Message}");
            }
        }

        // Single-hour processing step: iterate NPC snapshot and run per-NPC mover logic.
        private static void ProcessItemMoverStep(World.World world, SqliteConnection conn)
        {
            var snapshot = world.Npcs.ToList();
            foreach (var npc in snapshot)
            {
                try
                {
                    ProcessItemMoverForNpc(npc, world, conn);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ITEMMOVER] Error processing NPC NpcID={npc?.NpcID}: {ex.Message}");
                }
            }

            // Process pending rechecks: if an NPC was scheduled because it was moving at the hour,
            // run the mover for it as soon as IsOnMove == false (or drop expired entries).
            try
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _pendingRechecks.ToArray())
                {
                    int npcId = kv.Key;
                    DateTime scheduledAt = kv.Value;

                    // TTL expired -> remove
                    if (now - scheduledAt > _pendingRecheckTtl)
                    {
                        _pendingRechecks.TryRemove(npcId, out _);
                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] Recheck TTL expired for NpcID={npcId}; removed from pending.");
                        continue;
                    }

                    var npc = world.Npcs.FirstOrDefault(n => n != null && n.NpcID == npcId);
                    if (npc == null)
                    {
                        _pendingRechecks.TryRemove(npcId, out _);
                        continue;
                    }

                    // If NPC still moving, leave scheduled; otherwise process immediately and remove
                    if (!npc.IsOnMove)
                    {
                        try
                        {
                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] Processing pending recheck for NpcID={npcId} (arrived).");
                            ProcessItemMoverForNpc(npc, world, conn);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ITEMMOVER] Pending recheck failed for NpcID={npcId}: {ex.Message}");
                        }
                        finally
                        {
                            _pendingRechecks.TryRemove(npcId, out _);
                        }
                    }
                }
            }
            catch { /* best-effort */ }
        }

        // NEW: Process all pending retries for a period that just ended
        private static void ProcessEndOfPeriodRetries(World.World world, SqliteConnection conn, string endedPeriod)
        {
            try
            {
                var keysToRetry = _pendingRetries.Keys.Where(k => string.Equals(k.Period, endedPeriod, StringComparison.OrdinalIgnoreCase)).ToList();

                if (keysToRetry.Count == 0)
                {
                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][RETRY] No pending retries for period '{endedPeriod}'");
                    return;
                }

                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][RETRY] Processing {keysToRetry.Count} retry attempts for ended period '{endedPeriod}'");

                foreach (var key in keysToRetry)
                {
                    try
                    {
                        if (!_pendingRetries.TryRemove(key, out var retryData)) continue;

                        var npc = world.Npcs.FirstOrDefault(n => n != null && n.NpcID == key.NpcID);
                        if (npc == null || npc.IsOnMove)
                        {
                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][RETRY] Skipping NpcID={key.NpcID} - not found or still moving");
                            continue;
                        }

                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][RETRY] Attempting {key.Action} retry for NpcID={key.NpcID}");

                        // Re-run the same behavior logic (inline simplified version)
                        var b = retryData.config;
                        var gc = world.GameTime ?? new GameClock();

                        // Skip if somehow no longer in the period (edge case)
                        if (!gc.IsInPeriod(b.TimePeriod ?? ""))
                        {
                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][RETRY] NpcID={key.NpcID} period mismatch, skipping");
                            continue;
                        }

                        // Call ProcessItemMoverForNpc to re-execute the behavior
                        ProcessItemMoverForNpc(npc, world, conn);
                    }
                    catch (Exception exRetry)
                    {
                        Console.WriteLine($"[ITEMMOVER][RETRY] Error processing retry for NpcID={key.NpcID}: {exRetry.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ITEMMOVER][RETRY] Error in ProcessEndOfPeriodRetries: {ex.Message}");
            }
        }

        private static List<MudCore.Entities.Item> EnumerateRoomAndContainerItems(MudCore.World.Room room, Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            var results = new List<MudCore.Entities.Item>();
            if (room == null) return results;

            // Keep track of seen ItemIDs to avoid duplicates when mixing in-memory and DB rows
            var seen = new HashSet<int>();

            // 1) Add top-level room inventory (in-memory)
            try
            {
                if (room.Inventory != null)
                {
                    foreach (var it in room.Inventory)
                    {
                        if (it == null) continue;
                        results.Add(it);
                        if (it.ItemID != 0) seen.Add(it.ItemID);
                    }
                }
            }
            catch { /* best-effort */ }

            // 2) Ensure DB-backed room items are included (covers cases where room.Inventory wasn't populated)
            try
            {
                if (conn != null)
                {
                    var dbRoomItems = MudPersistence.ItemPersistence.LoadItemsByOwner(conn, "Room", room.RoomID) ?? new List<MudCore.Entities.Item>();
                    foreach (var it in dbRoomItems)
                    {
                        if (it == null) continue;
                        if (it.ItemID != 0 && seen.Contains(it.ItemID)) continue;
                        results.Add(it);
                        if (it.ItemID != 0) seen.Add(it.ItemID);
                    }
                }
            }
            catch { /* best-effort */ }

            // 3) Identify containers among collected top-level items and load their contents (DB authoritative)
            try
            {
                var containers = results
                    .Where(it => it != null && (
                        string.Equals(it.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase)
                        || (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Container", StringComparison.OrdinalIgnoreCase)))
                    ))
                    .ToList();

                foreach (var c in containers)
                {
                    if (c == null || c.ItemID == 0) continue;
                    try
                    {
                        var contents = MudPersistence.ItemPersistence.LoadItemsByOwner(conn, "Item", c.ItemID) ?? new List<MudCore.Entities.Item>();
                        foreach (var ci in contents)
                        {
                            if (ci == null) continue;
                            if (ci.ItemID != 0 && seen.Contains(ci.ItemID)) continue;
                            results.Add(ci);
                            if (ci.ItemID != 0) seen.Add(ci.ItemID);
                        }
                    }
                    catch { /* best-effort – ignore container load failures */ }
                }
            }
            catch { /* best-effort */ }

            return results;
        }

        // Schedule an NPC for an item-mover recheck when it later finishes its move flow.
        // Public so Movement/Travel scheduler can call this (optional).
        public static void ScheduleRecheckForNpc(int npcId)
        {
            if (npcId <= 0) return;
            _pendingRechecks[npcId] = DateTime.UtcNow;
        }



        // Per-NPC ItemMover logic (full implementation).
        public static void ProcessItemMoverForNpc(Npc npc, World.World world, SqliteConnection conn)
        {
            if (npc == null || world == null) return;
            if (npc.Template == null) return;
            if (npc.CurrentRoom == null) return;

            // ADD THIS DEBUG OUTPUT
            if (npc.NpcID == 820)
            {
                var gc2= world.GameTime ?? new GameClock();
                Console.WriteLine($"[ITEMMOVER][820-DEBUG] ProcessItemMoverForNpc called at Hour={gc2.Hour} Period={gc2.GetPeriodDescriptor()}");
                Console.WriteLine($"[ITEMMOVER][820-DEBUG] Template.AiBehaviorConfigs count={npc.Template.AiBehaviorConfigs?.Count ?? 0}");
                if (npc.Template.AiBehaviorConfigs != null)
                {
                    foreach (var b in npc.Template.AiBehaviorConfigs)
                    {
                        Console.WriteLine($"[ITEMMOVER][820-DEBUG]   Behavior={b.Behavior} TimePeriod={b.TimePeriod} Action={b.Action} GiveLocation={b.GiveLocation}");
                    }
                }
            }

            if (npc.IsOnMove)
            {
                try
                {
                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} is currently moving; scheduling recheck.");
                    _pendingRechecks[npc.NpcID] = DateTime.UtcNow;
                }
                catch { /* best-effort */ }
                return;
            }

            var gc = world.GameTime ?? new GameClock();
            var behaviors = npc.Template.AiBehaviorConfigs ?? new List<NpcTemplate.AiBehaviorConfig>();
            if (behaviors.Count == 0) return;

            foreach (var b in behaviors)
            {
                try
                {
                    if (b == null) continue;
                    if (!string.Equals(b.Behavior ?? "", "ItemMover", StringComparison.OrdinalIgnoreCase)) continue;

                    // Time gating
                    if (!gc.IsInPeriod(b.TimePeriod ?? ""))
                    {
                        if (DiagnosticsEnabled)
                            Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} Behavior=ItemMover TimePeriod='{b.TimePeriod}' not active at Hour={gc.Hour} ({gc.GetPeriodDescriptor()})");
                        continue;
                    }

                    // Chance gating
                    double chance = Math.Clamp(b.Chance, 0.0, 1.0);
                    if (chance > 0.0)
                    {
                        double roll = Random.Shared.NextDouble();
                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} ChanceRoll={roll:F4} <= Chance={chance:F4} => {(roll <= chance ? "PASS" : "FAIL")}");
                        if (roll > chance) continue;
                    }

                    if (b.MaxCarryKg <= 0)
                    {
                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} MaxCarryKg <= 0; skipping behavior.");
                        continue;
                    }

                    var action = (b.Action ?? "Exchange").Trim();
                    var giveLoc = (b.GiveLocation ?? "Npc").Trim();
                    if (!(string.Equals(giveLoc, "Npc", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(giveLoc, "Room", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(giveLoc, "Container", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][WARN] NpcID={npc.NpcID} invalid GiveLocation='{b.GiveLocation}' in template; skipping.");
                        continue;
                    }

                    // Build takeMap (template -> optional qty)
                    var takeMap = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var list = b.TakeItems ?? new List<string>();
                        foreach (var s in list)
                        {
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            var p = s.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                            var tmpl = p[0].Trim();
                            int? q = null;
                            if (p.Length == 2 && int.TryParse(p[1].Trim(), out var v)) q = Math.Max(0, v);
                            if (!string.IsNullOrWhiteSpace(tmpl) && !takeMap.ContainsKey(tmpl))
                                takeMap[tmpl] = q;
                        }
                    }
                    catch { /* best-effort */ }

                    // Current carried weight (kg) for mover
                    double currentKg = 0.0;
                    try { if (npc.Inventory != null) currentKg = npc.Inventory.Where(i => i != null).Sum(i => i.Weight * Math.Max(1, i.Quantity)); } catch { currentKg = 0.0; }
                    double capacityKg = b.MaxCarryKg;

                    if (DiagnosticsEnabled)
                    {
                        Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} Action='{action}' GiveLocation='{giveLoc}' CapacityKg={capacityKg} CurrentKg={currentKg}");
                        Console.WriteLine($"[ITEMMOVER][DBG]   TakeItems={string.Join(',', (b.TakeItems ?? new List<string>()))} TakeCategories={string.Join(',', (b.TakeItemCategories ?? new List<string>()))} TakeProducerReqItems={b.TakeProducerReqItems}");
                    }

                    // GATHER (room -> npc)
                    // Summary:
                    // - Collects items from the NPC's current room (floor + containers) into NPC inventory
                    // - Searches: Room floor items (OwnerType='Room') + all containers in room + container contents (OwnerType='Item')
                    // - Filters: TakeItemCategories (e.g., ["Food"]), TakeItems whitelist (e.g., ["grain_wheat"]) all the qty.
                    // - Supports quantity syntax: "template:qty" to limit how many to take (e.g., "grain_wheat:5" = up to 5)
                    // - Respects: MaxCarryKg capacity (stops when NPC reaches weight limit)
                    // - Ignores: NPC-owned and Player-owned items (only takes from room/containers)
                    // - Merges: Stackable items into existing NPC inventory stacks when possible
                    // - Transaction: Atomic DB operation (delete/update sources, create/merge NPC items)
                    if (string.Equals(action, "Gather", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var room = npc.CurrentRoom;
                            if (room == null)
                            {
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][WARN] NpcID={npc.NpcID} has no current room.");
                                continue;
                            }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} scanning RoomID={room.RoomID} for gatherable items.");

                            // Enumerate top-level room items and container contents (DB-authoritative for nested)
                            var candidates = EnumerateRoomAndContainerItems(room, conn) ?? new List<MudCore.Entities.Item>();
                            if (DiagnosticsEnabled)
                            {
                                Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} CandidateCount={candidates.Count}");
                                if (candidates.Count > 0)
                                {
                                    var sample = candidates.Take(6).Select(it => $"{it.ItemID}:{it.TemplateID ?? it.Name}({it.Quantity})");
                                    Console.WriteLine($"[ITEMMOVER][GATHER]   Samples: {string.Join(", ", sample)}");
                                }
                            }

                            // Select rows to take, respecting categories, takeMap, and capacity
                            // Track how much of each template we've already taken (for absolute limits)
                            var takenByTemplate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                            var takenList = new List<(MudCore.Entities.Item Row, int Qty)>();
                            foreach (var row in candidates.OrderByDescending(r => r.Quantity))
                            {
                                if (currentKg >= capacityKg) break;
                                if (row == null) continue;

                                // Only consider room/container-owned items (owner types 'Room' or 'Item')
                                if (!string.Equals(row.OwnerType ?? "", "Room", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(row.OwnerType ?? "", "Item", StringComparison.OrdinalIgnoreCase))
                                {
                                    // skip NPC/player owned items
                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][SKIP] ItemID={row.ItemID} OwnerType='{row.OwnerType}' not eligible.");
                                    continue;
                                }

                                // category filter
                                if (b.TakeItemCategories != null && b.TakeItemCategories.Count > 0)
                                {
                                    if (string.IsNullOrWhiteSpace(row.ItemCategory) || !b.TakeItemCategories.Any(c => string.Equals(c, row.ItemCategory, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][SKIP] ItemID={row.ItemID} Template='{row.TemplateID}' reason=CategoryMismatch");
                                        continue;
                                    }
                                }

                                // template whitelist filter
                                if (takeMap.Count > 0)
                                {
                                    if (string.IsNullOrWhiteSpace(row.TemplateID) || !takeMap.ContainsKey(row.TemplateID))
                                    {
                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][SKIP] ItemID={row.ItemID} Template='{row.TemplateID}' not in TakeItems");
                                        continue;
                                    }
                                }

                                int avail = Math.Max(1, row.Quantity);
                                int want = avail;

                                // FIXED: Apply ABSOLUTE limit across all rows for this template
                                if (!string.IsNullOrWhiteSpace(row.TemplateID) && takeMap.TryGetValue(row.TemplateID, out var qSpec) && qSpec.HasValue)
                                {
                                    int alreadyTaken = takenByTemplate.TryGetValue(row.TemplateID, out var at) ? at : 0;
                                    int remaining = Math.Max(0, qSpec.Value - alreadyTaken);

                                    if (remaining <= 0)
                                    {
                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][SKIP] ItemID={row.ItemID} Template='{row.TemplateID}' already reached limit of {qSpec.Value}");
                                        continue;
                                    }

                                    want = Math.Min(want, remaining);
                                }

                                double unitKg = Math.Max(0.0, row.Weight);
                                if (unitKg > 0.0)
                                {
                                    int maxFit = (int)Math.Floor((capacityKg - currentKg) / unitKg);
                                    if (maxFit <= 0)
                                    {
                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][BREAK] NpcID={npc.NpcID} capacity reached for unitKg={unitKg}");
                                        break;
                                    }
                                    want = Math.Min(want, maxFit);
                                }

                                if (want <= 0) continue;

                                takenList.Add((row, want));

                                // Track how much we're taking of this template
                                if (!string.IsNullOrWhiteSpace(row.TemplateID))
                                {
                                    if (!takenByTemplate.ContainsKey(row.TemplateID))
                                        takenByTemplate[row.TemplateID] = 0;
                                    takenByTemplate[row.TemplateID] += want;
                                }

                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][ACCEPT] ItemID={row.ItemID} Tmpl='{row.TemplateID}' QtyAvail={avail} Want={want} UnitKg={unitKg:F3} TotalTaken={takenByTemplate.GetValueOrDefault(row.TemplateID ?? "", 0)}");
                                currentKg += unitKg * want;
                            }

                            if (takenList.Count == 0)
                            {
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} found nothing to gather in RoomID={room.RoomID}.");

                                // NEW: Schedule end-of-period retry
                                var retryKey = new RetryKey { NpcID = npc.NpcID, Period = b.TimePeriod ?? "", Action = "Gather" };
                                _pendingRetries[retryKey] = (DateTime.UtcNow, b);

                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} scheduled for end-of-period retry");

                                continue;
                            }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} preparing to take {takenList.Count} candidate rows (expected kg delta ~{(currentKg - (npc.Inventory?.Sum(i => i.Weight * Math.Max(1, i.Quantity)) ?? 0.0)):F2}).");

                            // Writer transaction: remove/update source rows and create NPC-owned rows
                            try
                            {
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                                {
                                    var newlyAdded = new List<MudCore.Entities.Item>();

                                    foreach (var (srcRow, qty) in takenList)
                                    {
                                        MudCore.Entities.Item authoritative = null;
                                        try
                                        {
                                            // Determine authoritative owner lookup: Room or Item (container)
                                            if (string.Equals(srcRow.OwnerType ?? "", "Room", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var rows = MudPersistence.ItemPersistence.LoadItemsByOwner(wConn, "Room", srcRow.OwnerID, wTx);
                                                authoritative = rows.FirstOrDefault(r => r.ItemID == srcRow.ItemID) ?? rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.TemplateID) && string.Equals(r.TemplateID, srcRow.TemplateID, StringComparison.OrdinalIgnoreCase));
                                            }
                                            else if (string.Equals(srcRow.OwnerType ?? "", "Item", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var rows = MudPersistence.ItemPersistence.LoadItemsByOwner(wConn, "Item", srcRow.OwnerID, wTx);
                                                authoritative = rows.FirstOrDefault(r => r.ItemID == srcRow.ItemID) ?? rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.TemplateID) && string.Equals(r.TemplateID, srcRow.TemplateID, StringComparison.OrdinalIgnoreCase));
                                            }
                                        }
                                        catch
                                        {
                                            authoritative = srcRow;
                                        }

                                        if (authoritative == null)
                                        {
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] Source ItemID={srcRow.ItemID} not found in authoritative rows; skipping.");
                                            continue;
                                        }

                                        int avail = Math.Max(1, authoritative.Quantity);
                                        int take = Math.Min(avail, qty);
                                        if (take <= 0)
                                        {
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] ItemID={authoritative.ItemID} nothing to take (avail={avail}, requested={qty}).");
                                            continue;
                                        }

                                        // Capture original owner info from the source row (do NOT rely on 'authoritative' after it may be mutated)
                                        var originalOwnerType = srcRow.OwnerType ?? "";
                                        var originalOwnerId = srcRow.OwnerID;
                                        var originalItemId = srcRow.ItemID;

                                        // Persist source reduction or transfer ownership
                                        try
                                        {
                                            if (take < avail)
                                            {
                                                // Partial take from stack - reduce quantity on source
                                                var updated = new MudCore.Entities.Item { ItemID = authoritative.ItemID, Quantity = avail - take };
                                                MudPersistence.ItemPersistence.SaveItem(wConn, updated, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] UPDATE ItemID={authoritative.ItemID} NewQty={avail - take}");

                                                // Create new item for the taken portion and try to merge into NPC inventory
                                                var created = world.ItemFactory.CreateItem(authoritative.TemplateID ?? authoritative.Name ?? "", "NPC", npc.NpcID, npc.CurrentRoom.RoomID, take);
                                                created.Name = authoritative.Name;
                                                created.ItemCategory = authoritative.ItemCategory;

                                                var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                    wConn, wTx, "NPC", npc.NpcID, npc.CurrentRoom.RoomID,
                                                    created.TemplateID ?? "", created.FlagsJson ?? "[]", take);

                                                if (merged.mergedId != 0)
                                                {
                                                    created.ItemID = merged.mergedId;
                                                    created.Quantity = merged.newQty;
                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] MERGED into ItemID={merged.mergedId} NewQty={merged.newQty}");
                                                }
                                                else
                                                {
                                                    ItemPersistence.SaveItem(wConn, created, wTx);
                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] INSERT new ItemID={created.ItemID} Qty={created.Quantity}");
                                                }

                                                newlyAdded.Add(created);
                                            }
                                            else
                                            {
                                                // Full take - check if stackable and can merge into NPC inventory
                                                bool isStackable = authoritative.Flags != null &&
                                                    authoritative.Flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase));

                                                if (isStackable)
                                                {
                                                    // Try to merge into existing NPC stack
                                                    var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                        wConn, wTx, "NPC", npc.NpcID, npc.CurrentRoom.RoomID,
                                                        authoritative.TemplateID ?? "", authoritative.FlagsJson ?? "[]", take);

                                                    if (merged.mergedId != 0)
                                                    {
                                                        // Merged successfully - delete the source item
                                                        MudPersistence.ItemPersistence.DeleteItem(wConn, authoritative.ItemID, wTx);
                                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] MERGED ItemID={authoritative.ItemID} into ItemID={merged.mergedId} NewQty={merged.newQty}");

                                                        // Update in-memory reference for newlyAdded
                                                        var placeholder = new MudCore.Entities.Item
                                                        {
                                                            ItemID = merged.mergedId,
                                                            TemplateID = authoritative.TemplateID,
                                                            Name = authoritative.Name,
                                                            ItemCategory = authoritative.ItemCategory,
                                                            Quantity = merged.newQty,
                                                            OwnerType = "NPC",
                                                            OwnerID = npc.NpcID
                                                        };
                                                        newlyAdded.Add(placeholder);
                                                    }
                                                    else
                                                    {
                                                        // No existing stack - update ownership in place (preserves ItemID)
                                                        using var updCmd = wConn.CreateCommand();
                                                        updCmd.Transaction = wTx;
                                                        updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                                        updCmd.Parameters.AddWithValue("@ot", "NPC");
                                                        updCmd.Parameters.AddWithValue("@oid", npc.NpcID);
                                                        updCmd.Parameters.AddWithValue("@rid", npc.CurrentRoom.RoomID);
                                                        updCmd.Parameters.AddWithValue("@id", authoritative.ItemID);
                                                        updCmd.ExecuteNonQuery();

                                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] UPDATE ItemID={authoritative.ItemID} -> OwnerType='NPC' OwnerID={npc.NpcID}");

                                                        authoritative.OwnerType = "NPC";
                                                        authoritative.OwnerID = npc.NpcID;
                                                        authoritative.RoomID = npc.CurrentRoom.RoomID;
                                                        newlyAdded.Add(authoritative);
                                                    }
                                                }
                                                else
                                                {
                                                    // Non-stackable: update ownership in place (preserves ItemID)
                                                    using var updCmd = wConn.CreateCommand();
                                                    updCmd.Transaction = wTx;
                                                    updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                                    updCmd.Parameters.AddWithValue("@ot", "NPC");
                                                    updCmd.Parameters.AddWithValue("@oid", npc.NpcID);
                                                    updCmd.Parameters.AddWithValue("@rid", npc.CurrentRoom.RoomID);
                                                    updCmd.Parameters.AddWithValue("@id", authoritative.ItemID);
                                                    updCmd.ExecuteNonQuery();

                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] UPDATE ItemID={authoritative.ItemID} -> OwnerType='NPC' OwnerID={npc.NpcID}");

                                                    authoritative.OwnerType = "NPC";
                                                    authoritative.OwnerID = npc.NpcID;
                                                    authoritative.RoomID = npc.CurrentRoom.RoomID;
                                                    newlyAdded.Add(authoritative);
                                                }
                                            }
                                        }
                                        catch (Exception exTxSrc)
                                        {
                                            Console.WriteLine($"[ITEMMOVER][GATHER][TX][ERROR] Failed to process source ItemID={authoritative.ItemID}: {exTxSrc.Message}");
                                            continue;
                                        }

                                        // In-memory best-effort: remove from room.Inventory if present (top-level)
                                        try
                                        {
                                            // Use the ORIGINAL owner info (from srcRow) rather than 'authoritative' which may have been mutated above.
                                            if (string.Equals(originalOwnerType ?? "", "Room", StringComparison.OrdinalIgnoreCase))
                                            {
                                                room.Inventory?.RemoveAll(i => i != null && i.ItemID == originalItemId);
                                            }
                                            else if (string.Equals(originalOwnerType ?? "", "Item", StringComparison.OrdinalIgnoreCase))
                                            {
                                                // Container contents may not be in memory; best-effort remove if present
                                                foreach (var cont in room.Inventory?.Where(it => it != null && it.ItemID == originalOwnerId).ToList() ?? new List<MudCore.Entities.Item>())
                                                {
                                                    try
                                                    {
                                                        // no in-memory per-container list available; nothing to remove
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        catch { /* best-effort */ }
                                    } // foreach selected row

                                    // Append newly added items to NPC in-memory inventory
                                    try
                                    {
                                        if (npc.Inventory == null) npc.Inventory = new List<MudCore.Entities.Item>();
                                        foreach (var it in newlyAdded) npc.Inventory.Add(it);
                                    }
                                    catch { /* best-effort */ }

                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(30));
                            }
                            catch (Exception exTx)
                            {
                                Console.WriteLine($"[ITEMMOVER][GATHER][ERROR] Writer transaction failed for NpcID={npc.NpcID}: {exTx.Message}");
                                continue;
                            }

                            // Broadcast room update to all players in the room
                            try
                            {
                                if (room != null && room.Players != null)
                                {
                                    foreach (var p in room.Players)
                                    {
                                        p.PendingMessageQueue.Add(MessageHandler.Room(
                                            room,
                                            room.Npcs.Select(n => n.DisplayName).ToList(),
                                            room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                            world));
                                    }
                                }
                            }
                            catch { /* best-effort */ }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} gather completed; added {npc.Inventory?.Count ?? 0} item(s) in-memory (best-effort).");
                        }
                        catch (Exception exGather)
                        {
                            Console.WriteLine($"[ITEMMOVER][GATHER][ERROR] NpcID={npc.NpcID} gather flow failed: {exGather.Message}");
                        }

                        continue;
                    }

                    // DROP remains room/container only
                    // Summary:
                    // - Transfers items from NPC inventory to room floor or a specific container
                    // - Target: Controlled by GiveLocation parameter
                    //   * "Room" → Drops items onto room floor (OwnerType='Room', OwnerID=RoomID)
                    //   * "Container" → Drops into specific container (uses IfDropToContainerTemplate to find target)
                    // - Filters: TakeItemCategories (e.g., ["Food"]), TakeItems whitelist (e.g., ["grain_wheat:5"])
                    // - Supports quantity syntax: "template:qty" to limit how many to drop (e.g., "grain_wheat:5" = up to 5)
                    // - By default: Drops ALL matching items in NPC inventory (unless quantity limit specified)
                    // - Merges: Stackable items into existing target stacks when possible
                    // - Container validation: If GiveLocation="Container", target container must exist in room (by TemplateID)
                    // - Transaction: Atomic DB operation (delete/update NPC items, create/merge target items)
                    // - In-memory sync: Removes from NPC inventory, adds to room/container (best-effort)
                    if (string.Equals(action, "Drop", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var room = npc.CurrentRoom;
                            if (room == null)
                            {
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][WARN] NpcID={npc.NpcID} has no current room.");
                                continue;
                            }

                            // Check IfDropToRoomFlag: if set and room doesn't have the flag, skip drop
                            var requiredRoomFlag = (b.IfDropToRoomFlag ?? "None").Trim();
                            if (!string.IsNullOrWhiteSpace(requiredRoomFlag) && !string.Equals(requiredRoomFlag, "None", StringComparison.OrdinalIgnoreCase))
                            {
                                bool roomHasFlag = room.Flags != null && room.Flags.Any(f => string.Equals(f, requiredRoomFlag, StringComparison.OrdinalIgnoreCase));
                                if (!roomHasFlag)
                                {
                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][SKIP] NpcID={npc.NpcID} room RoomID={room.RoomID} missing required flag '{requiredRoomFlag}'; scheduling retry.");

                                    // Schedule end-of-period retry since NPC may travel to correct room later
                                    var retryKey2 = new RetryKey { NpcID = npc.NpcID, Period = b.TimePeriod ?? "", Action = "Drop" };
                                    _pendingRetries[retryKey2] = (DateTime.UtcNow, b);

                                    continue;
                                }
                            }

                            // Determine drop target: Room or Container
                            Item targetContainer = null;
                            if (string.Equals(giveLoc, "Container", StringComparison.OrdinalIgnoreCase))
                            {
                                // Find container matching IfDropToContainerTemplate
                                var containerTemplate = b.IfDropToContainerTemplate ?? "None";
                                if (!string.IsNullOrWhiteSpace(containerTemplate) && !string.Equals(containerTemplate, "None", StringComparison.OrdinalIgnoreCase))
                                {
                                    targetContainer = room.Inventory?.FirstOrDefault(i =>
                                        i != null &&
                                        string.Equals(i.TemplateID, containerTemplate, StringComparison.OrdinalIgnoreCase));

                                    if (targetContainer == null)
                                    {
                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][WARN] NpcID={npc.NpcID} container template '{containerTemplate}' not found in room.");
                                        continue;
                                    }
                                }
                            }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} dropping to {(targetContainer != null ? $"Container ItemID={targetContainer.ItemID}" : $"Room RoomID={room.RoomID}")}");

                            // Load NPC's current inventory
                            var npcItems = ItemPersistence.LoadItemsByOwner(conn, "NPC", npc.NpcID) ?? new List<Item>();

                            // Filter items to drop based on takeMap and categories
                            var dropList = new List<(Item item, int qty)>();
                            foreach (var item in npcItems.OrderByDescending(i => i.Quantity))
                            {
                                if (item == null || string.IsNullOrWhiteSpace(item.TemplateID)) continue;

                                // Category filter
                                if (b.TakeItemCategories != null && b.TakeItemCategories.Count > 0)
                                {
                                    if (string.IsNullOrWhiteSpace(item.ItemCategory) ||
                                        !b.TakeItemCategories.Any(c => string.Equals(c, item.ItemCategory, StringComparison.OrdinalIgnoreCase)))
                                        continue;
                                }

                                // Template filter
                                if (takeMap.Count > 0 && !takeMap.ContainsKey(item.TemplateID))
                                    continue;

                                int avail = Math.Max(1, item.Quantity);
                                int want = avail; // Drop all by default

                                // Optional: respect takeMap quantity limits
                                if (takeMap.TryGetValue(item.TemplateID, out var qSpec) && qSpec.HasValue)
                                    want = Math.Min(want, qSpec.Value);

                                if (want > 0)
                                    dropList.Add((item, want));
                            }

                            if (dropList.Count == 0)
                            {
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} has nothing to drop.");

                                // NEW: Schedule end-of-period retry
                                var retryKey2 = new RetryKey { NpcID = npc.NpcID, Period = b.TimePeriod ?? "", Action = "Drop" };
                                _pendingRetries[retryKey2] = (DateTime.UtcNow, b);

                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} scheduled for end-of-period retry");

                                continue;
                            }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} preparing to drop {dropList.Count} item types.");

                            // ADD THIS DEBUG BLOCK
                            if (DiagnosticsEnabled)
                            {
                                Console.WriteLine($"[ITEMMOVER][DROP][DEBUG] NpcID={npc.NpcID} dropList contents:");
                                foreach (var (it, q) in dropList)
                                {
                                    Console.WriteLine($"[ITEMMOVER][DROP][DEBUG]   ItemID={it.ItemID} TemplateID='{it.TemplateID}' Category='{it.ItemCategory}' Qty={q}");
                                }
                            }

                            // Execute drop in writer transaction
                            try
                            {
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                                {
                                    // ADD THIS AT START OF TRANSACTION
                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] NpcID={npc.NpcID} transaction started");

                                    foreach (var (item, qty) in dropList)
                                    {
                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] Processing ItemID={item.ItemID} TemplateID='{item.TemplateID}'");
                                        // Load authoritative source
                                        var authRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", npc.NpcID, wTx);
                                        var auth = authRows.FirstOrDefault(r => r.ItemID == item.ItemID);
                                        if (auth == null) continue;

                                        int avail = Math.Max(1, auth.Quantity);
                                        int drop = Math.Min(avail, qty);
                                        if (drop <= 0) continue;

                                        // Update source item ownership (DO NOT delete and recreate - preserves ItemID)
                                        if (drop < avail)
                                        {
                                            // Partial drop from stack - reduce quantity on source
                                            var updated = new Item { ItemID = auth.ItemID, Quantity = avail - drop };
                                            ItemPersistence.SaveItem(wConn, updated, wTx);
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] UPDATE NPC ItemID={auth.ItemID} NewQty={avail - drop}");

                                            // Create new item for the dropped portion (stackable partial)
                                            string targetOwnerType = targetContainer != null ? "Item" : "Room";
                                            int targetOwnerId = targetContainer != null ? targetContainer.ItemID : room.RoomID;

                                            var dropped = world.ItemFactory.CreateItem(auth.TemplateID ?? auth.Name ?? "", targetOwnerType, targetOwnerId, room.RoomID, drop);
                                            dropped.Name = auth.Name;
                                            dropped.ItemCategory = auth.ItemCategory;

                                            // Try merge into target
                                            var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                wConn, wTx, targetOwnerType, targetOwnerId, room.RoomID,
                                                dropped.TemplateID ?? "", dropped.FlagsJson ?? "[]", drop);

                                            if (merged.mergedId != 0)
                                            {
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] MERGED into ItemID={merged.mergedId} NewQty={merged.newQty}");
                                            }
                                            else
                                            {
                                                ItemPersistence.SaveItem(wConn, dropped, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] INSERT new ItemID={dropped.ItemID} Qty={dropped.Quantity}");

                                                // Add to room in-memory inventory
                                                if (targetContainer == null)
                                                    room.Inventory?.Add(dropped);
                                            }
                                        }
                                        else
                                        {
                                            // Full drop - check if stackable and can merge first
                                            string targetOwnerType = targetContainer != null ? "Item" : "Room";
                                            int targetOwnerId = targetContainer != null ? targetContainer.ItemID : room.RoomID;

                                            bool isStackable = auth.Flags != null && auth.Flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase));

                                            if (isStackable)
                                            {
                                                // Try to merge into existing stack at target
                                                var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                    wConn, wTx, targetOwnerType, targetOwnerId, room.RoomID,
                                                    auth.TemplateID ?? "", auth.FlagsJson ?? "[]", auth.Quantity);

                                                if (merged.mergedId != 0)
                                                {
                                                    // Merged successfully - delete the source item
                                                    ItemPersistence.DeleteItem(wConn, auth.ItemID, wTx);
                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] MERGED ItemID={auth.ItemID} into ItemID={merged.mergedId} NewQty={merged.newQty}");

                                                    // Remove from NPC in-memory
                                                    npc.Inventory?.RemoveAll(i => i != null && i.ItemID == auth.ItemID);
                                                    continue;
                                                }
                                            }

                                            // No merge possible (or not stackable) - update ownership in place (preserves ItemID)
                                            using var updCmd = wConn.CreateCommand();
                                            updCmd.Transaction = wTx;
                                            updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                            updCmd.Parameters.AddWithValue("@ot", targetOwnerType);
                                            updCmd.Parameters.AddWithValue("@oid", targetOwnerId);
                                            updCmd.Parameters.AddWithValue("@rid", room.RoomID);
                                            updCmd.Parameters.AddWithValue("@id", auth.ItemID);
                                            updCmd.ExecuteNonQuery();

                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] UPDATE ItemID={auth.ItemID} -> OwnerType='{targetOwnerType}' OwnerID={targetOwnerId} RoomID={room.RoomID}");

                                            // Update in-memory item
                                            auth.OwnerType = targetOwnerType;
                                            auth.OwnerID = targetOwnerId;
                                            auth.RoomID = room.RoomID;

                                            // Add to room in-memory inventory (same item, just moved)
                                            if (targetContainer == null)
                                                room.Inventory?.Add(auth);
                                        }

                                        // Remove from NPC in-memory
                                        npc.Inventory?.RemoveAll(i => i != null && i.ItemID == auth.ItemID);
                                    }

                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] NpcID={npc.NpcID} transaction completing");

                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(30));
                                // ADD THIS AFTER TRANSACTION
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} transaction completed successfully");
                            }
                            catch (Exception exTx)
                            {
                                Console.WriteLine($"[ITEMMOVER][DROP][ERROR] Transaction failed for NpcID={npc.NpcID}: {exTx.Message}");
                                continue;
                            }

                            // Broadcast room update to all players in the room
                            try
                            {
                                if (room != null && room.Players != null)
                                {
                                    foreach (var p in room.Players)
                                    {
                                        p.PendingMessageQueue.Add(MessageHandler.Room(
                                            room,
                                            room.Npcs.Select(n => n.DisplayName).ToList(),
                                            room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                            world));
                                    }
                                }
                            }
                            catch { /* best-effort */ }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} drop completed.");

                            // NEW: Clear retry on success
                            var retryKey = new RetryKey { NpcID = npc.NpcID, Period = b.TimePeriod ?? "", Action = "Drop" };
                            _pendingRetries.TryRemove(retryKey, out _);
                        }
                        catch (Exception exDrop)
                        {
                            Console.WriteLine($"[ITEMMOVER][DROP][ERROR] NpcID={npc.NpcID}: {exDrop.Message}");
                        }

                        continue;
                    }

                    // EXCHANGE: strictly NPC <-> NPC (no room-sourced fallback).

                    if (string.Equals(action, "Exchange", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var targetFilter = (b.TargetNpcIdContains ?? "None").Trim();
                            if (string.IsNullOrWhiteSpace(targetFilter) || string.Equals(targetFilter, "None", StringComparison.OrdinalIgnoreCase))
                            {
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} Exchange requested but no TargetNpcIdContains specified.");
                                continue;
                            }

                            // Find target in same room
                            var target = npc.CurrentRoom.Npcs?.FirstOrDefault(x =>
                                x != null && (x.Template?.Id ?? x.TemplateID ?? "").IndexOf(targetFilter, StringComparison.OrdinalIgnoreCase) >= 0 && x.NpcID != npc.NpcID);

                            if (target == null)
                            {
                                if (DiagnosticsEnabled)
                                {
                                    Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} Exchange targetFilter='{targetFilter}'. Room Npcs:");
                                    foreach (var rn in npc.CurrentRoom.Npcs ?? new List<Npc>()) try { Console.WriteLine($"[ITEMMOVER][DBG]   NpcID={rn.NpcID} TemplateID='{rn.TemplateID}' TemplateName='{rn.Template?.Name}'"); } catch { }
                                }
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DBG] NpcID={npc.NpcID} Exchange requested but no target matching '{targetFilter}' present.");
                                continue;
                            }

                            // Diagnostics: show occupants before exchange
                            if (DiagnosticsEnabled)
                            {
                                try
                                {
                                    var occupants = npc.CurrentRoom?.Npcs ?? new List<Npc>();
                                    var players = npc.CurrentRoom?.Players ?? new List<Player>();
                                    Console.WriteLine($"[ITEMMOVER][DBG-ROOM] NpcID={npc.NpcID} RoomID={npc.CurrentRoom.RoomID} OccupantCount={occupants.Count} PlayerCount={players.Count}");
                                    foreach (var occ in occupants) Console.WriteLine($"[ITEMMOVER][DBG-ROOM]   NPC: NpcID={occ.NpcID} TemplateID='{occ.TemplateID}' Name='{occ.Name}'");
                                }
                                catch { /* best-effort */ }
                            }

                            // Perform exchange in writer transaction with TWO phases: GIVE then TAKE
                            try
                            {
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                                {
                                    // ===== PHASE 1: GIVE items from mover -> target (based on target's RequiresItem needs) =====
                                    var required = new List<string>();
                                    try
                                    {
                                        if (target.Template?.AiBehaviorConfigs != null)
                                        {
                                            foreach (var tb in target.Template.AiBehaviorConfigs.Where(x => string.Equals(x.Behavior ?? "", "ItemCreator", StringComparison.OrdinalIgnoreCase)))
                                            {
                                                foreach (var req in tb.RequiresItem ?? new List<string>())
                                                {
                                                    try
                                                    {
                                                        var p = req.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                                                        var tmpl = p[0].Trim();
                                                        if (!string.IsNullOrWhiteSpace(tmpl) && !required.Contains(tmpl, StringComparer.OrdinalIgnoreCase))
                                                            required.Add(tmpl);
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }
                                    catch { /* best-effort */ }

                                    if (DiagnosticsEnabled && required.Count > 0)
                                        Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} Phase 1 (GIVE): Target needs {string.Join(", ", required)}");

                                    // Load mover's inventory (items to GIVE)
                                    var moverRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", npc.NpcID, wTx) ?? new List<Item>();

                                    if (required.Count > 0)
                                    {
                                        var allowedCategories = (b.TakeItemCategories ?? new List<string>())
                                            .Where(c => !string.IsNullOrWhiteSpace(c))
                                            .Select(c => c.Trim())
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToList();

                                        bool CategoryAllowed(Item itm)
                                        {
                                            if (allowedCategories.Count == 0) return true;
                                            if (itm == null) return false;
                                            if (string.IsNullOrWhiteSpace(itm.ItemCategory)) return false;
                                            return allowedCategories.Any(filter => string.Equals(filter, itm.ItemCategory, StringComparison.OrdinalIgnoreCase));
                                        }

                                        // Filter mover's items matching what target needs
                                        var moverCandidates = moverRows
                                            .Where(i => i != null && !string.IsNullOrWhiteSpace(i.TemplateID)
                                                        && required.Contains(i.TemplateID, StringComparer.OrdinalIgnoreCase)
                                                        && CategoryAllowed(i))
                                            .ToList();

                                        try
                                        {
                                            var moverContainers = moverRows
                                                .Where(i => i != null && i.ItemID > 0 && (
                                                    string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase) ||
                                                    (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Container", StringComparison.OrdinalIgnoreCase)))
                                                ))
                                                .ToList();

                                            foreach (var container in moverContainers)
                                            {
                                                if (container == null || container.ItemID <= 0) continue;

                                                var contents = ItemPersistence.LoadItemsByOwner(wConn, "Item", container.ItemID, wTx) ?? new List<Item>();
                                                foreach (var content in contents)
                                                {
                                                    if (content == null || string.IsNullOrWhiteSpace(content.TemplateID)) continue;
                                                    if (!required.Contains(content.TemplateID, StringComparer.OrdinalIgnoreCase)) continue;
                                                    if (!CategoryAllowed(content)) continue;
                                                    moverCandidates.Add(content);
                                                }
                                            }
                                        }
                                        catch { /* best-effort */ }

                                        if (DiagnosticsEnabled)
                                            Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} has {moverCandidates.Count} items matching target's needs");

                                        foreach (var mv in moverCandidates)
                                        {
                                            if (mv == null) continue;

                                            List<Item> moverAuthRows;
                                            if (string.Equals(mv.OwnerType ?? "", "Item", StringComparison.OrdinalIgnoreCase) && mv.OwnerID > 0)
                                            {
                                                moverAuthRows = ItemPersistence.LoadItemsByOwner(wConn, "Item", mv.OwnerID, wTx) ?? new List<Item>();
                                            }
                                            else
                                            {
                                                moverAuthRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", npc.NpcID, wTx) ?? new List<Item>();
                                            }

                                            var moverAuth = moverAuthRows.FirstOrDefault(r => r.ItemID == mv.ItemID);
                                            if (moverAuth == null)
                                            {
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] Mover ItemID={mv.ItemID} not found in DB; skipping give.");
                                                continue;
                                            }

                                            int giveQty = Math.Max(1, moverAuth.Quantity);
                                            bool isStackable = moverAuth.Flags != null && moverAuth.Flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase));

                                            if (isStackable)
                                            {
                                                // Try to merge into target's existing stack
                                                var mergedToTarget = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                    wConn, wTx, "NPC", target.NpcID, target.CurrentRoom?.RoomID ?? 0,
                                                    moverAuth.TemplateID ?? "", moverAuth.FlagsJson ?? "[]", giveQty);

                                                if (mergedToTarget.mergedId != 0)
                                                {
                                                    // Merged - delete source
                                                    ItemPersistence.DeleteItem(wConn, moverAuth.ItemID, wTx);
                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] MERGED give item into target ItemID={mergedToTarget.mergedId} NewQty={mergedToTarget.newQty}");

                                                    try
                                                    {
                                                        var existingInMem = target.Inventory?.FirstOrDefault(i => i != null && i.ItemID == mergedToTarget.mergedId);
                                                        if (existingInMem != null)
                                                        {
                                                            existingInMem.Quantity = mergedToTarget.newQty;
                                                        }
                                                    }
                                                    catch { }
                                                }
                                                else
                                                {
                                                    // No existing stack - update ownership in place
                                                    using var updCmd = wConn.CreateCommand();
                                                    updCmd.Transaction = wTx;
                                                    updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                                    updCmd.Parameters.AddWithValue("@ot", "NPC");
                                                    updCmd.Parameters.AddWithValue("@oid", target.NpcID);
                                                    updCmd.Parameters.AddWithValue("@rid", target.CurrentRoom?.RoomID ?? 0);
                                                    updCmd.Parameters.AddWithValue("@id", moverAuth.ItemID);
                                                    updCmd.ExecuteNonQuery();

                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] UPDATE give ItemID={moverAuth.ItemID} -> target NpcID={target.NpcID}");

                                                    moverAuth.OwnerType = "NPC";
                                                    moverAuth.OwnerID = target.NpcID;
                                                    moverAuth.RoomID = target.CurrentRoom?.RoomID ?? 0;

                                                    try
                                                    {
                                                        if (target.Inventory == null) target.Inventory = new List<Item>();
                                                        target.Inventory.Add(moverAuth);
                                                    }
                                                    catch { }
                                                }
                                            }
                                            else
                                            {
                                                // Non-stackable: update ownership in place (preserves ItemID)
                                                using var updCmd = wConn.CreateCommand();
                                                updCmd.Transaction = wTx;
                                                updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                                updCmd.Parameters.AddWithValue("@ot", "NPC");
                                                updCmd.Parameters.AddWithValue("@oid", target.NpcID);
                                                updCmd.Parameters.AddWithValue("@rid", target.CurrentRoom?.RoomID ?? 0);
                                                updCmd.Parameters.AddWithValue("@id", moverAuth.ItemID);
                                                updCmd.ExecuteNonQuery();

                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] UPDATE give ItemID={moverAuth.ItemID} -> target NpcID={target.NpcID}");

                                                moverAuth.OwnerType = "NPC";
                                                moverAuth.OwnerID = target.NpcID;
                                                moverAuth.RoomID = target.CurrentRoom?.RoomID ?? 0;

                                                try
                                                {
                                                    if (target.Inventory == null) target.Inventory = new List<Item>();
                                                    target.Inventory.Add(moverAuth);
                                                }
                                                catch { }
                                            }

                                            try
                                            {
                                                npc.Inventory?.RemoveAll(i => i != null && i.ItemID == moverAuth.ItemID);
                                            }
                                            catch { /* best-effort */ }
                                        }
                                    }

                                    // ===== PHASE 2: TAKE items from target -> mover (based on mover's TakeItems/TakeItemCategories) =====
                                    bool hasTakeFilters = (takeMap.Count > 0) || (b.TakeItemCategories != null && b.TakeItemCategories.Count > 0);

                                    if (!hasTakeFilters)
                                    {
                                        if (DiagnosticsEnabled)
                                            Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} Phase 2 (TAKE): SKIPPED - no TakeItems or TakeItemCategories configured");
                                    }
                                    else
                                    {
                                        if (DiagnosticsEnabled)
                                            Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} Phase 2 (TAKE): scanning target inventory");

                                        // Build exclusion set: items the target needs for ItemCreator
                                        var targetNeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                        try
                                        {
                                            if (target.Template?.AiBehaviorConfigs != null)
                                            {
                                                foreach (var tb in target.Template.AiBehaviorConfigs.Where(x => string.Equals(x.Behavior ?? "", "ItemCreator", StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    foreach (var req in tb.RequiresItem ?? new List<string>())
                                                    {
                                                        try
                                                        {
                                                            var p = req.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                                                            var tmpl = p[0].Trim();
                                                            if (!string.IsNullOrWhiteSpace(tmpl))
                                                                targetNeeds.Add(tmpl);
                                                        }
                                                        catch { }
                                                    }
                                                }
                                            }
                                        }
                                        catch { /* best-effort */ }

                                        if (DiagnosticsEnabled && targetNeeds.Count > 0)
                                            Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} Phase 2: Excluding target's RequiresItem templates: {string.Join(", ", targetNeeds)}");

                                        // Reload target's inventory
                                        var targetRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", target.NpcID, wTx) ?? new List<Item>();
                                        var candidateRows = new List<Item>(targetRows);

                                        // Load container contents
                                        try
                                        {
                                            var containers = targetRows.Where(it => it != null && (
                                                string.Equals(it.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase)
                                                || (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Container", StringComparison.OrdinalIgnoreCase)))
                                            )).ToList();

                                            foreach (var c in containers)
                                            {
                                                try
                                                {
                                                    var contents = ItemPersistence.LoadItemsByOwner(wConn, "Item", c.ItemID, wTx) ?? new List<Item>();
                                                    foreach (var ci in contents) candidateRows.Add(ci);
                                                }
                                                catch { }
                                            }
                                        }
                                        catch { }

                                        // Select items to take
                                        var takenList = new List<(Item SourceRow, int QtyTaken)>();
                                        foreach (var row in candidateRows.OrderByDescending(r => r.Quantity))
                                        {
                                            if (currentKg >= capacityKg) break;
                                            if (row == null || string.IsNullOrWhiteSpace(row.TemplateID)) continue;

                                            if (targetNeeds.Contains(row.TemplateID))
                                            {
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][SKIP] ItemID={row.ItemID} Template='{row.TemplateID}' is needed by target for crafting");
                                                continue;
                                            }

                                            if (b.TakeItemCategories != null && b.TakeItemCategories.Count > 0)
                                            {
                                                if (string.IsNullOrWhiteSpace(row.ItemCategory) || !b.TakeItemCategories.Any(c => string.Equals(c, row.ItemCategory, StringComparison.OrdinalIgnoreCase)))
                                                    continue;
                                            }

                                            if (takeMap.Count > 0 && !takeMap.ContainsKey(row.TemplateID)) continue;

                                            int avail = Math.Max(1, row.Quantity);
                                            int want = avail;
                                            if (takeMap.TryGetValue(row.TemplateID, out var qSpec) && qSpec.HasValue)
                                                want = Math.Min(want, qSpec.Value);

                                            double unitKg = Math.Max(0.0, row.Weight);
                                            if (unitKg > 0.0)
                                            {
                                                int maxFit = (int)Math.Floor((capacityKg - currentKg) / unitKg);
                                                if (maxFit <= 0) break;
                                                want = Math.Min(want, maxFit);
                                            }

                                            if (want <= 0) continue;
                                            takenList.Add((row, want));
                                            currentKg += unitKg * want;
                                        }

                                        if (takenList.Count == 0)
                                        {
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} no items available on target NpcID={target.NpcID} for taking.");
                                        }
                                        else
                                        {
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} taking {takenList.Count} rows from target NpcID={target.NpcID}");

                                            foreach (var (srcRow, qty) in takenList)
                                            {
                                                Item authoritative = null;
                                                try
                                                {
                                                    if (string.Equals(srcRow.OwnerType, "NPC", StringComparison.OrdinalIgnoreCase) || srcRow.OwnerType == null)
                                                    {
                                                        var rows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", target.NpcID, wTx);
                                                        authoritative = rows.FirstOrDefault(r => r.ItemID == srcRow.ItemID) ?? rows.FirstOrDefault(r => string.Equals(r.TemplateID, srcRow.TemplateID, StringComparison.OrdinalIgnoreCase));
                                                    }
                                                    else
                                                    {
                                                        var rows = ItemPersistence.LoadItemsByOwner(wConn, "Item", srcRow.OwnerID, wTx);
                                                        authoritative = rows.FirstOrDefault(r => r.ItemID == srcRow.ItemID) ?? srcRow;
                                                    }
                                                }
                                                catch { authoritative = srcRow; }

                                                if (authoritative == null) continue;
                                                int avail = Math.Max(1, authoritative.Quantity);
                                                int take = Math.Min(avail, qty);
                                                if (take <= 0) continue;

                                                bool isStackable = authoritative.Flags != null && authoritative.Flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase));

                                                if (take < avail)
                                                {
                                                    // Partial take - reduce source, create new for mover
                                                    var updated = new Item { ItemID = authoritative.ItemID, Quantity = avail - take };
                                                    ItemPersistence.SaveItem(wConn, updated, wTx);
                                                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] UPDATE target ItemID={authoritative.ItemID} NewQty={avail - take}");

                                                    var added = world.ItemFactory.CreateItem(authoritative.TemplateID ?? authoritative.Name ?? "", "NPC", npc.NpcID, npc.CurrentRoom.RoomID, take);
                                                    added.Name = authoritative.Name;
                                                    added.ItemCategory = authoritative.ItemCategory;

                                                    var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                        wConn, wTx, "NPC", npc.NpcID, npc.CurrentRoom.RoomID,
                                                        added.TemplateID ?? "", added.FlagsJson ?? "[]", take);

                                                    if (merged.mergedId != 0)
                                                    {
                                                        added.ItemID = merged.mergedId;
                                                        added.Quantity = merged.newQty;
                                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] MERGED take into mover ItemID={merged.mergedId} NewQty={merged.newQty}");
                                                    }
                                                    else
                                                    {
                                                        ItemPersistence.SaveItem(wConn, added, wTx);
                                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] INSERT partial take to mover ItemID={added.ItemID} Qty={added.Quantity}");
                                                    }

                                                    try
                                                    {
                                                        if (npc.Inventory == null) npc.Inventory = new List<Item>();
                                                        npc.Inventory.Add(added);
                                                    }
                                                    catch { }
                                                }
                                                else
                                                {
                                                    // Full take
                                                    if (isStackable)
                                                    {
                                                        // Try merge into mover's existing stack
                                                        var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                            wConn, wTx, "NPC", npc.NpcID, npc.CurrentRoom.RoomID,
                                                            authoritative.TemplateID ?? "", authoritative.FlagsJson ?? "[]", take);

                                                        if (merged.mergedId != 0)
                                                        {
                                                            // Merged - delete source
                                                            ItemPersistence.DeleteItem(wConn, authoritative.ItemID, wTx);
                                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] MERGED take ItemID={authoritative.ItemID} into mover ItemID={merged.mergedId} NewQty={merged.newQty}");

                                                            try
                                                            {
                                                                var existingInMem = npc.Inventory?.FirstOrDefault(i => i != null && i.ItemID == merged.mergedId);
                                                                if (existingInMem != null)
                                                                    existingInMem.Quantity = merged.newQty;
                                                            }
                                                            catch { }
                                                        }
                                                        else
                                                        {
                                                            // No existing stack - update ownership in place
                                                            using var updCmd = wConn.CreateCommand();
                                                            updCmd.Transaction = wTx;
                                                            updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                                            updCmd.Parameters.AddWithValue("@ot", "NPC");
                                                            updCmd.Parameters.AddWithValue("@oid", npc.NpcID);
                                                            updCmd.Parameters.AddWithValue("@rid", npc.CurrentRoom.RoomID);
                                                            updCmd.Parameters.AddWithValue("@id", authoritative.ItemID);
                                                            updCmd.ExecuteNonQuery();

                                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] UPDATE take ItemID={authoritative.ItemID} -> mover NpcID={npc.NpcID}");

                                                            authoritative.OwnerType = "NPC";
                                                            authoritative.OwnerID = npc.NpcID;
                                                            authoritative.RoomID = npc.CurrentRoom.RoomID;

                                                            try
                                                            {
                                                                if (npc.Inventory == null) npc.Inventory = new List<Item>();
                                                                npc.Inventory.Add(authoritative);
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        // Non-stackable: update ownership in place
                                                        using var updCmd = wConn.CreateCommand();
                                                        updCmd.Transaction = wTx;
                                                        updCmd.CommandText = @"UPDATE Item SET OwnerType = @ot, OwnerID = @oid, RoomID = @rid WHERE ItemID = @id";
                                                        updCmd.Parameters.AddWithValue("@ot", "NPC");
                                                        updCmd.Parameters.AddWithValue("@oid", npc.NpcID);
                                                        updCmd.Parameters.AddWithValue("@rid", npc.CurrentRoom.RoomID);
                                                        updCmd.Parameters.AddWithValue("@id", authoritative.ItemID);
                                                        updCmd.ExecuteNonQuery();

                                                        if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] UPDATE take ItemID={authoritative.ItemID} -> mover NpcID={npc.NpcID}");

                                                        authoritative.OwnerType = "NPC";
                                                        authoritative.OwnerID = npc.NpcID;
                                                        authoritative.RoomID = npc.CurrentRoom.RoomID;

                                                        try
                                                        {
                                                            if (npc.Inventory == null) npc.Inventory = new List<Item>();
                                                            npc.Inventory.Add(authoritative);
                                                        }
                                                        catch { }
                                                    }

                                                    try
                                                    {
                                                        target.Inventory?.RemoveAll(i => i != null && i.ItemID == authoritative.ItemID);
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }

                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(30));
                            }
                            catch (Exception exTx)
                            {
                                Console.WriteLine($"[ITEMMOVER] Exchange transaction failed for NpcID={npc.NpcID}: {exTx.Message}");
                                continue;
                            }

                            // Broadcast room update to all players in the room
                            try
                            {
                                var room = npc.CurrentRoom;
                                if (room != null && room.Players != null)
                                {
                                    foreach (var p in room.Players)
                                    {
                                        p.PendingMessageQueue.Add(MessageHandler.Room(
                                            room,
                                            room.Npcs.Select(n => n.DisplayName).ToList(),
                                            room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                            world));
                                    }
                                }
                            }
                            catch { /* best-effort */ }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} exchange completed with target NpcID={target.NpcID}");
                        }
                        catch (Exception exEx)
                        {
                            Console.WriteLine($"[ITEMMOVER] Exchange failed for NpcID={npc.NpcID}: {exEx.Message}");
                        }

                        continue;
                    } // end Exchange


                }
                catch (Exception exBeh)
                {
                    Console.WriteLine($"[ITEMMOVER] Behavior processing failed for NpcID={npc.NpcID}: {exBeh.Message}");
                }
            } // foreach behavior
        }//End ProcessNpcItemMover
    }
}