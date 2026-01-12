
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

        // Public entry: run ItemMover checks once per game hour (idempotent).
        public static void ProcessItemMovers(World.World world, SqliteConnection conn)
        {
            try
            {
                if (world == null) return;
                var gc = world.GameTime ?? new GameClock();

                long totalMonths = ((gc.Year - 1) * 12L) + (gc.Month - 1);
                long totalDays = (totalMonths * 30L) + (gc.Day - 1);
                long totalHours = (totalDays * 24L) + gc.Hour;

                // initialize baseline (do not retroactively process missed hours)
                if (_lastItemMoverProcessedTotalHours == long.MinValue)
                {
                    _lastItemMoverProcessedTotalHours = totalHours;
                    if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER] Initialized baseline at totalHours={totalHours}");
                    return;
                }

                long deltaHours = totalHours - _lastItemMoverProcessedTotalHours;
                if (deltaHours < 1) return;

                long steps = deltaHours; // one check per hour
                for (long s = 0; s < steps; s++)
                {
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
                                if (!string.IsNullOrWhiteSpace(row.TemplateID) && takeMap.TryGetValue(row.TemplateID, out var qSpec) && qSpec.HasValue)
                                    want = Math.Min(want, qSpec.Value);

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
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][ACCEPT] ItemID={row.ItemID} Tmpl='{row.TemplateID}' QtyAvail={avail} Want={want} UnitKg={unitKg:F3}");
                                currentKg += unitKg * want;
                            }

                            if (takenList.Count == 0)
                            {
                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} found nothing to gather in RoomID={room.RoomID}.");
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

                                        // Persist source reduction/delete
                                        try
                                        {
                                            if (take < avail)
                                            {
                                                // update quantity on existing row
                                                var updated = new MudCore.Entities.Item { ItemID = authoritative.ItemID, Quantity = avail - take };
                                                MudPersistence.ItemPersistence.SaveItem(wConn, updated, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] UPDATE ItemID={authoritative.ItemID} NewQty={avail - take}");
                                            }
                                            else
                                            {
                                                MudPersistence.ItemPersistence.DeleteItem(wConn, authoritative.ItemID, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] DELETE ItemID={authoritative.ItemID}");
                                            }
                                        }
                                        catch (Exception exTxSrc)
                                        {
                                            Console.WriteLine($"[ITEMMOVER][GATHER][TX][ERROR] Failed to reduce/delete source ItemID={authoritative.ItemID}: {exTxSrc.Message}");
                                            continue; // best-effort per-row
                                        }

                                        // Create new NPC-owned row
                                        try
                                        {
                                            var created = world.ItemFactory.CreateItem(authoritative.TemplateID ?? authoritative.Name ?? "", "NPC", npc.NpcID, npc.CurrentRoom.RoomID, take);
                                            created.Name = authoritative.Name;
                                            created.ItemCategory = authoritative.ItemCategory;

                                            // Try merge first (already inside DbWriteQueue context, wTx provided)
                                            var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                wConn, wTx, "NPC", npc.NpcID, npc.CurrentRoom.RoomID,
                                                created.TemplateID ?? "", created.FlagsJson ?? "[]", take);

                                            if (merged.mergedId != 0)
                                            {
                                                // Merged into existing stack
                                                created.ItemID = merged.mergedId;
                                                created.Quantity = merged.newQty;
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] MERGED into ItemID={merged.mergedId} NewQty={merged.newQty}");
                                            }
                                            else
                                            {
                                                // No existing stack - insert new
                                                ItemPersistence.SaveItem(wConn, created, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] INSERT new ItemID={created.ItemID} Qty={created.Quantity}");
                                            }

                                            newlyAdded.Add(created);
                                            //created.Name = authoritative.Name;
                                            //created.ItemCategory = authoritative.ItemCategory;
                                            //MudPersistence.ItemPersistence.SaveItem(wConn, created, wTx);
                                            //newlyAdded.Add(created);

                                            //if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER][TX] INSERT -> NPC ItemID={created.ItemID} Tmpl='{created.TemplateID}' Qty={created.Quantity}");
                                        }
                                        catch (Exception exCreate)
                                        {
                                            Console.WriteLine($"[ITEMMOVER][GATHER][TX][ERROR] Failed to create npc-owned item for Template='{authoritative.TemplateID}': {exCreate.Message}");
                                            // Attempt to rollback source change is not done here; rely on writer transactional semantics (exception would abort tx)
                                            continue;
                                        }

                                        // In-memory best-effort: remove from room.Inventory if present (top-level)
                                        try
                                        {
                                            if (string.Equals(authoritative.OwnerType ?? "", "Room", StringComparison.OrdinalIgnoreCase))
                                            {
                                                room.Inventory?.RemoveAll(i => i != null && i.ItemID == authoritative.ItemID);
                                            }
                                            else if (string.Equals(authoritative.OwnerType ?? "", "Item", StringComparison.OrdinalIgnoreCase))
                                            {
                                                // Container contents may not be in memory; best-effort remove if present
                                                foreach (var cont in room.Inventory?.Where(it => it != null && it.ItemID == authoritative.OwnerID).ToList() ?? new List<MudCore.Entities.Item>())
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

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][GATHER] NpcID={npc.NpcID} gather completed; added {npc.Inventory?.Count ?? 0} item(s) in-memory (best-effort).");
                        }
                        catch (Exception exGather)
                        {
                            Console.WriteLine($"[ITEMMOVER][GATHER][ERROR] NpcID={npc.NpcID} gather flow failed: {exGather.Message}");
                        }

                        continue;
                    }

                    // DROP remains room/container only
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
                                continue;
                            }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} preparing to drop {dropList.Count} item types.");

                            // Execute drop in writer transaction
                            try
                            {
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                                {
                                    foreach (var (item, qty) in dropList)
                                    {
                                        // Load authoritative source
                                        var authRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", npc.NpcID, wTx);
                                        var auth = authRows.FirstOrDefault(r => r.ItemID == item.ItemID);
                                        if (auth == null) continue;

                                        int avail = Math.Max(1, auth.Quantity);
                                        int drop = Math.Min(avail, qty);
                                        if (drop <= 0) continue;

                                        // Reduce/delete source
                                        if (drop < avail)
                                        {
                                            var updated = new Item { ItemID = auth.ItemID, Quantity = avail - drop };
                                            ItemPersistence.SaveItem(wConn, updated, wTx);
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] UPDATE NPC ItemID={auth.ItemID} NewQty={avail - drop}");
                                        }
                                        else
                                        {
                                            ItemPersistence.DeleteItem(wConn, auth.ItemID, wTx);
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP][TX] DELETE NPC ItemID={auth.ItemID}");
                                        }

                                        // Create dropped item
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

                                        // Remove from NPC in-memory
                                        npc.Inventory?.RemoveAll(i => i != null && i.ItemID == auth.ItemID);
                                    }

                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(30));
                            }
                            catch (Exception exTx)
                            {
                                Console.WriteLine($"[ITEMMOVER][DROP][ERROR] Transaction failed for NpcID={npc.NpcID}: {exTx.Message}");
                                continue;
                            }

                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][DROP] NpcID={npc.NpcID} drop completed.");
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

                            // NEW: Perform exchange in writer transaction with TWO phases: GIVE then TAKE
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
                                        // Filter mover's items matching what target needs
                                        var moverCandidates = moverRows.Where(i => i != null && required.Contains(i.TemplateID, StringComparer.OrdinalIgnoreCase)).ToList();

                                        if (DiagnosticsEnabled)
                                            Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} has {moverCandidates.Count} items matching target's needs");

                                        foreach (var mv in moverCandidates)
                                        {
                                            if (mv == null) continue;

                                            // Load authoritative mover item from DB
                                            var moverAuthRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", npc.NpcID, wTx);
                                            var moverAuth = moverAuthRows.FirstOrDefault(r => r.ItemID == mv.ItemID);
                                            if (moverAuth == null)
                                            {
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] Mover ItemID={mv.ItemID} not found in DB; skipping give.");
                                                continue;
                                            }

                                            int giveQty = Math.Max(1, moverAuth.Quantity);

                                            // Delete from mover
                                            ItemPersistence.DeleteItem(wConn, moverAuth.ItemID, wTx);
                                            if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] DELETE from mover ItemID={moverAuth.ItemID} Template='{moverAuth.TemplateID}' Qty={giveQty}");

                                            // Try to merge into target's existing stack
                                            var mergedToTarget = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                wConn, wTx, "NPC", target.NpcID, target.CurrentRoom?.RoomID ?? 0,
                                                moverAuth.TemplateID ?? "", moverAuth.FlagsJson ?? "[]", giveQty);

                                            if (mergedToTarget.mergedId != 0)
                                            {
                                                // Merged into existing target stack
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] MERGED give item into target ItemID={mergedToTarget.mergedId} NewQty={mergedToTarget.newQty}");

                                                // Update in-memory target inventory
                                                try
                                                {
                                                    var existingInMem = target.Inventory?.FirstOrDefault(i => i != null && i.ItemID == mergedToTarget.mergedId);
                                                    if (existingInMem != null)
                                                    {
                                                        existingInMem.Quantity = mergedToTarget.newQty;
                                                    }
                                                    else
                                                    {
                                                        // Not in memory - create placeholder with merged quantity
                                                        if (target.Inventory == null) target.Inventory = new List<Item>();
                                                        var placeholder = new Item
                                                        {
                                                            ItemID = mergedToTarget.mergedId,
                                                            TemplateID = moverAuth.TemplateID,
                                                            Name = moverAuth.Name,
                                                            ItemCategory = moverAuth.ItemCategory,
                                                            Weight = moverAuth.Weight,
                                                            Quantity = mergedToTarget.newQty,
                                                            OwnerType = "NPC",
                                                            OwnerID = target.NpcID,
                                                            RoomID = target.CurrentRoom?.RoomID ?? 0,
                                                            Flags = moverAuth.Flags,
                                                            FlagsJson = moverAuth.FlagsJson
                                                        };
                                                        target.Inventory.Add(placeholder);
                                                    }
                                                }
                                                catch { /* best-effort in-memory update */ }
                                            }
                                            else
                                            {
                                                // No merge - create new target-owned item
                                                var returned = world.ItemFactory.CreateItem(
                                                    moverAuth.TemplateID ?? moverAuth.Name ?? "",
                                                    "NPC",
                                                    target.NpcID,
                                                    target.CurrentRoom?.RoomID ?? 0,
                                                    giveQty);
                                                returned.Name = moverAuth.Name;
                                                returned.ItemCategory = moverAuth.ItemCategory;

                                                ItemPersistence.SaveItem(wConn, returned, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] INSERT give item to target ItemID={returned.ItemID} Template='{returned.TemplateID}' Qty={returned.Quantity}");

                                                // Add to target in-memory inventory
                                                try
                                                {
                                                    if (target.Inventory == null) target.Inventory = new List<Item>();
                                                    target.Inventory.Add(returned);
                                                }
                                                catch { /* best-effort */ }
                                            }

                                            // Remove from mover in-memory inventory
                                            try
                                            {
                                                npc.Inventory?.RemoveAll(i => i != null && i.ItemID == moverAuth.ItemID);
                                            }
                                            catch { /* best-effort */ }
                                        }
                                    }

                                    // ===== PHASE 2: TAKE items from target -> mover (based on mover's TakeItems/TakeItemCategories) =====
                                    if (DiagnosticsEnabled)
                                        Console.WriteLine($"[ITEMMOVER][EXCHANGE] NpcID={npc.NpcID} Phase 2 (TAKE): scanning target inventory");

                                    // Reload target's inventory (may have been updated in Phase 1)
                                    var targetRows = ItemPersistence.LoadItemsByOwner(wConn, "NPC", target.NpcID, wTx) ?? new List<Item>();
                                    var candidateRows = new List<Item>(targetRows);

                                    // Identify containers owned by target and load their contents
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
                                            catch { /* best-effort per-container */ }
                                        }
                                    }
                                    catch { }

                                    // Select items from candidateRows according to takeMap, categories, and capacity
                                    var takenList = new List<(Item SourceRow, int QtyTaken)>();
                                    foreach (var row in candidateRows.OrderByDescending(r => r.Quantity))
                                    {
                                        if (currentKg >= capacityKg) break;
                                        if (row == null || string.IsNullOrWhiteSpace(row.TemplateID)) continue;

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

                                        var newlyAddedToMover = new List<Item>();

                                        foreach (var (srcRow, qty) in takenList)
                                        {
                                            Item authoritative = null;
                                            try
                                            {
                                                // Load latest authoritative rows for the correct owner
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

                                            // Reduce or remove source
                                            if (take < avail)
                                            {
                                                var updated = new Item { ItemID = authoritative.ItemID, Quantity = avail - take };
                                                ItemPersistence.SaveItem(wConn, updated, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] UPDATE target ItemID={authoritative.ItemID} NewQty={avail - take}");
                                            }
                                            else
                                            {
                                                ItemPersistence.DeleteItem(wConn, authoritative.ItemID, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] DELETE target ItemID={authoritative.ItemID}");
                                            }

                                            // Create item owned by mover NPC (with merge)
                                            var added = world.ItemFactory.CreateItem(authoritative.TemplateID ?? authoritative.Name ?? "", "NPC", npc.NpcID, npc.CurrentRoom.RoomID, take);
                                            added.Name = authoritative.Name;
                                            added.ItemCategory = authoritative.ItemCategory;

                                            // Try merge first (same pattern as Gather)
                                            var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                                wConn, wTx, "NPC", npc.NpcID, npc.CurrentRoom.RoomID,
                                                added.TemplateID ?? "", added.FlagsJson ?? "[]", take);

                                            if (merged.mergedId != 0)
                                            {
                                                // Merged into existing mover stack
                                                added.ItemID = merged.mergedId;
                                                added.Quantity = merged.newQty;
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] MERGED take into mover ItemID={merged.mergedId} NewQty={merged.newQty}");
                                            }
                                            else
                                            {
                                                // No existing stack - insert new
                                                ItemPersistence.SaveItem(wConn, added, wTx);
                                                if (DiagnosticsEnabled) Console.WriteLine($"[ITEMMOVER][EXCHANGE][TX] INSERT take to mover ItemID={added.ItemID} Qty={added.Quantity}");
                                            }

                                            newlyAddedToMover.Add(added);

                                            // reflect in-memory best-effort
                                            try
                                            {
                                                // Remove from target's in-memory inventory where present
                                                target.Inventory?.RemoveAll(i => i != null && i.ItemID == authoritative.ItemID);
                                            }
                                            catch { }
                                        }

                                        // Add newly created items to mover in-memory
                                        try
                                        {
                                            if (npc.Inventory == null) npc.Inventory = new List<Item>();
                                            foreach (var a in newlyAddedToMover) npc.Inventory.Add(a);
                                        }
                                        catch { }
                                    }

                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(30));
                            }
                            catch (Exception exTx)
                            {
                                Console.WriteLine($"[ITEMMOVER] Exchange transaction failed for NpcID={npc.NpcID}: {exTx.Message}");
                                continue;
                            }

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