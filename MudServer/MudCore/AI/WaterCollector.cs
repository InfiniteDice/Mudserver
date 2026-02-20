using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;
using DbWriteQueue = global::MudServer.DbWriteQueue;

namespace MudCore.AI
{
    /// <summary>
    /// Handles NPC water collection behavior.
    /// Task syntax: CollectWater:[roomId]:[hour|timeperiod]
    /// Example: CollectWater:45:Morning or CollectWater:45:23
    /// </summary>
    public static class WaterCollector
    {
        public static bool DiagnosticsEnabled { get; set; } = false;

        // Track NPCs currently collecting water (NpcID -> target RoomID)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _collectingNpcs
            = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        // Water item template ID
        private const string WaterTemplateId = "water";
        private const double DefaultWaterWeight = 0.25; // kg per water item

        /// <summary>
        /// Parse a CollectWater task string and return components.
        /// Format: CollectWater:[roomId]:[hour|timeperiod]
        /// </summary>
        public static bool TryParseTask(string task, out int roomId, out string trigger)
        {
            roomId = 0;
            trigger = "";

            if (string.IsNullOrWhiteSpace(task)) return false;
            if (!task.StartsWith("CollectWater:", StringComparison.OrdinalIgnoreCase)) return false;

            var parts = task.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            if (!int.TryParse(parts[1].Trim(), out roomId)) return false;
            trigger = parts[2].Trim();

            return roomId > 0 && !string.IsNullOrWhiteSpace(trigger);
        }

        /// <summary>
        /// Check if trigger matches current game time (hour or period).
        /// </summary>
        public static bool IsTriggerActive(string trigger, GameClock gc)
        {
            if (gc == null || string.IsNullOrWhiteSpace(trigger)) return false;

            // Try parse as hour (0-23)
            if (int.TryParse(trigger, out var hour))
            {
                return gc.Hour == hour;
            }

            // Otherwise treat as period descriptor
            return gc.IsInPeriod(trigger);
        }

        /// <summary>
        /// Check if NPC has at least one HoldsLiquid container in inventory.
        /// </summary>
        public static bool HasLiquidContainer(Npc npc)
        {
            if (npc?.Inventory == null || npc.Inventory.Count == 0) return false;

            return npc.Inventory.Any(item =>
                item != null &&
                item.Flags != null &&
                item.Flags.Any(f => string.Equals(f, "HoldsLiquid", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Get all HoldsLiquid containers from NPC inventory.
        /// </summary>
        public static List<Item> GetLiquidContainers(Npc npc)
        {
            if (npc?.Inventory == null) return new List<Item>();

            return npc.Inventory
                .Where(item => item != null &&
                               item.Flags != null &&
                               item.Flags.Any(f => string.Equals(f, "HoldsLiquid", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// Parse capacity from item flags (e.g., "Capacity5" -> 5.0 kg).
        /// Returns 0 if no capacity flag found.
        /// </summary>
        public static double GetContainerCapacity(Item container)
        {
            if (container?.Flags == null) return 0;

            foreach (var flag in container.Flags)
            {
                if (string.IsNullOrWhiteSpace(flag)) continue;
                if (flag.StartsWith("Capacity", StringComparison.OrdinalIgnoreCase))
                {
                    var numPart = flag.Substring("Capacity".Length).Trim();
                    if (double.TryParse(numPart, out var cap))
                        return cap;
                }
            }

            return 0;
        }

        /// <summary>
        /// Start water collection for an NPC (called when trigger activates).
        /// Returns true if NPC should move to collect water.
        /// </summary>
        public static bool StartCollection(Npc npc, int targetRoomId, World.World world)
        {
            if (npc == null || world == null) return false;

            // Pre-condition: must have HoldsLiquid container
            if (!HasLiquidContainer(npc))
            {
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} has no HoldsLiquid container; skipping collection");
                return false;
            }

            // Mark NPC as collecting
            _collectingNpcs[npc.NpcID] = targetRoomId;

            if (DiagnosticsEnabled)
                Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} starting water collection, target RoomID={targetRoomId}");

            return true;
        }

        /// <summary>
        /// Check if NPC is currently in water collection mode.
        /// </summary>
        public static bool IsCollecting(int npcId)
        {
            return _collectingNpcs.ContainsKey(npcId);
        }

        /// <summary>
        /// Stop water collection for an NPC.
        /// </summary>
        public static void StopCollection(int npcId)
        {
            _collectingNpcs.TryRemove(npcId, out _);
        }

        /// <summary>
        /// Process water collection tick for an NPC.
        /// Called each tick while NPC is in collection mode.
        /// Returns true if collection is complete (all containers full).
        /// </summary>
        public static bool ProcessCollectionTick(Npc npc, World.World world, SqliteConnection conn, GameClock gc)
        {
            if (npc == null || world == null) return true;

            // Must be in collection mode
            if (!_collectingNpcs.TryGetValue(npc.NpcID, out var targetRoomId))
                return true;

            // If the configured trigger period is no longer active, stop collecting.
            try
            {
                bool anyMatchingTask = false;
                bool anyActiveTrigger = false;
                var tasks = npc.Template?.AiTaskList ?? new List<string>();
                foreach (var t in tasks)
                {
                    if (!TryParseTask(t, out var roomId, out var trigger)) continue;
                    if (roomId != targetRoomId) continue;
                    anyMatchingTask = true;
                    try
                    {
                        if (IsTriggerActive(trigger, gc))
                        {
                            anyActiveTrigger = true;
                            break;
                        }
                    }
                    catch { /* best-effort */ }
                }

                // If there were matching CollectWater tasks for this target but none are active now, stop collection.
                if (anyMatchingTask && !anyActiveTrigger)
                {
                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} collection period ended for target RoomID={targetRoomId}; stopping collection");
                    StopCollection(npc.NpcID);
                    return true;
                }

                // If there were no matching tasks at all (config changed), also stop.
                if (!anyMatchingTask)
                {
                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[WATERCOLLECTOR][WARN] NpcID={npc.NpcID} no CollectWater task matches target RoomID={targetRoomId}; aborting");
                    StopCollection(npc.NpcID);
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (DiagnosticsEnabled) Console.WriteLine($"[WATERCOLLECTOR][ERROR] Trigger re-check failed for NpcID={npc.NpcID}: {ex.Message}");
                // conservative: stop collection on unexpected error to avoid runaway state
                StopCollection(npc.NpcID);
                return true;
            }

            // Must not be moving
            if (npc.IsOnMove)
            {
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} still moving; waiting");
                return false;
            }

            // Must be in correct room
            if (npc.CurrentRoom == null || npc.CurrentRoom.RoomID != targetRoomId)
            {
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} not in target room {targetRoomId} yet");
                return false;
            }

            // Room must have Water flag
            if (npc.CurrentRoom.Flags == null ||
                !npc.CurrentRoom.Flags.Any(f => string.Equals(f, "Water", StringComparison.OrdinalIgnoreCase)))
            {
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[WATERCOLLECTOR][WARN] NpcID={npc.NpcID} room RoomID={npc.CurrentRoom.RoomID} has no Water flag; aborting");
                StopCollection(npc.NpcID);
                return true;
            }

            // Get water template weight
            double waterWeight = DefaultWaterWeight;
            try
            {
                if (world.ItemFactory != null)
                {
                    // Try to get weight from template
                    var testItem = world.ItemFactory.CreateItem(WaterTemplateId, "NPC", 0, 0, 1);
                    if (testItem != null && testItem.Weight > 0)
                        waterWeight = testItem.Weight;
                }
            }
            catch
            {
                // Use default weight
            }

            // Get all liquid containers
            var containers = GetLiquidContainers(npc);
            if (containers.Count == 0)
            {
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[WATERCOLLECTOR][WARN] NpcID={npc.NpcID} lost HoldsLiquid containers; aborting");
                StopCollection(npc.NpcID);
                return true;
            }

            bool anyFilled = false;
            bool allFull = true;

            foreach (var container in containers)
            {
                if (container == null || container.ItemID <= 0) continue;

                double capacity = GetContainerCapacity(container);
                if (capacity <= 0)
                {
                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[WATERCOLLECTOR][DBG] Container ItemID={container.ItemID} has no Capacity flag; skipping");
                    continue;
                }

                int maxItems = (int)Math.Floor(capacity / waterWeight);
                if (maxItems <= 0) continue;

                // Load current contents
                int currentWaterCount = 0;
                double currentWeight = 0;
                try
                {
                    var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                    foreach (var item in contents)
                    {
                        if (item == null) continue;
                        currentWeight += item.Weight * Math.Max(1, item.Quantity);
                        if (string.Equals(item.TemplateID, WaterTemplateId, StringComparison.OrdinalIgnoreCase))
                            currentWaterCount += item.Quantity;
                    }
                }
                catch (Exception ex)
                {
                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[WATERCOLLECTOR][ERROR] Failed to load container contents: {ex.Message}");
                    continue;
                }

                // Check if container is full
                if (currentWeight >= capacity || currentWaterCount >= maxItems)
                {
                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[WATERCOLLECTOR][DBG] Container ItemID={container.ItemID} is full ({currentWaterCount}/{maxItems} water)");
                    continue;
                }

                allFull = false;

                // Add one water item this tick
                try
                {
                    DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        // Try to merge into existing water stack
                        var merged = InventoryManager.TryMergeIntoExistingStack(
                            wConn, wTx, "Item", container.ItemID, npc.CurrentRoom.RoomID,
                            WaterTemplateId, "[]", 1);

                        if (merged.mergedId != 0)
                        {
                            if (DiagnosticsEnabled)
                                Console.WriteLine($"[WATERCOLLECTOR][TX] MERGED water into ItemID={merged.mergedId} NewQty={merged.newQty}");
                        }
                        else
                        {
                            // Create new water item
                            var waterItem = world.ItemFactory.CreateItem(WaterTemplateId, "Item", container.ItemID, npc.CurrentRoom.RoomID, 1);
                            ItemPersistence.SaveItem(wConn, waterItem, wTx);
                            if (DiagnosticsEnabled)
                                Console.WriteLine($"[WATERCOLLECTOR][TX] INSERT water ItemID={waterItem.ItemID} into container ItemID={container.ItemID}");
                        }

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(10));

                    anyFilled = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WATERCOLLECTOR][ERROR] Failed to add water to container: {ex.Message}");
                }
            }

            // Broadcast message if any container was filled
            if (anyFilled)
            {
                try
                {
                    var containerNames = string.Join(", ", containers.Select(c => c.Name ?? c.TemplateID ?? "container"));
                    var message = $"{npc.Name ?? "Someone"} fills the {containerNames} with water.";

                    // Broadcast to room
                    foreach (var player in npc.CurrentRoom.Players ?? new List<Player>())
                    {
                        try
                        {
                            MessageHandler.SendSystem(player, message);
                        }
                        catch { /* best-effort */ }
                    }

                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[WATERCOLLECTOR][DBG] Broadcast: {message}");
                }
                catch { /* best-effort */ }
            }

            // Check if all containers are now full
            if (allFull)
            {
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} all containers full; collection complete");
                StopCollection(npc.NpcID);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Process all NPCs with CollectWater tasks (called from World.Tick or NpcAI).
        /// </summary>
        public static void ProcessWaterCollectors(World.World world, SqliteConnection conn, GameClock gc)
        {
            if (world == null || gc == null) return;

            try
            {
                var snapshot = world.Npcs.ToList();

                foreach (var npc in snapshot)
                {
                    if (npc == null || npc.Template == null) continue;

                    // Check if already collecting
                    if (IsCollecting(npc.NpcID))
                    {
                        ProcessCollectionTick(npc, world, conn, gc);
                        continue;
                    }

                    // Check for CollectWater tasks in AiTaskList
                    var tasks = npc.Template.AiTaskList ?? new List<string>();
                    foreach (var task in tasks)
                    {
                        if (!TryParseTask(task, out var targetRoomId, out var trigger))
                            continue;

                        if (!IsTriggerActive(trigger, gc))
                            continue;

                        // Trigger is active - start collection if preconditions met
                        if (StartCollection(npc, targetRoomId, world))
                        {
                            // If NPC not already in target room, initiate movement via MoveSchedules
                            if (npc.CurrentRoom == null || npc.CurrentRoom.RoomID != targetRoomId)
                            {
                                try
                                {
                                    // Check if path exists before scheduling movement
                                    var path = Pathfinder.FindPath(world, npc.CurrentRoom?.RoomID ?? 0, targetRoomId);
                                    if (path != null && path.Count > 0)
                                    {
                                        // Add a temporary MoveSchedule entry so NpcTravelScheduler picks it up
                                        if (npc.MoveSchedules == null)
                                            npc.MoveSchedules = new List<Npc.MoveSchedule>();

                                        // Add schedule for current period to trigger immediate movement
                                        var currentPeriod = gc?.GetPeriodDescriptor() ?? "Morning";
                                        var existingSchedule = npc.MoveSchedules.FirstOrDefault(s => s.RoomId == targetRoomId);
                                        if (existingSchedule == null)
                                        {
                                            npc.MoveSchedules.Insert(0, new Npc.MoveSchedule { RoomId = targetRoomId, Period = currentPeriod });
                                        }

                                        npc.MoveRoomId = targetRoomId;
                                        npc.MovePeriod = currentPeriod;
                                        npc.IsOnMove = true;

                                        if (DiagnosticsEnabled)
                                            Console.WriteLine($"[WATERCOLLECTOR][DBG] NpcID={npc.NpcID} scheduled move to RoomID={targetRoomId}, path length={path.Count}");
                                    }
                                    else
                                    {
                                        if (DiagnosticsEnabled)
                                            Console.WriteLine($"[WATERCOLLECTOR][WARN] NpcID={npc.NpcID} no path to RoomID={targetRoomId}; aborting");
                                        StopCollection(npc.NpcID);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[WATERCOLLECTOR][ERROR] Pathfinding/scheduling failed: {ex.Message}");
                                    StopCollection(npc.NpcID);
                                }
                            }
                        }

                        break; // Only process one CollectWater task per tick
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WATERCOLLECTOR][ERROR] ProcessWaterCollectors failed: {ex.Message}");
            }
        }
    }
}