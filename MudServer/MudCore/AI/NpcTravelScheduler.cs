using System;
using System.Collections.Generic;
using System.Linq;
using MudCore.Entities;
using MudCore.World;

namespace MudCore.AI
{
    // Responsible only for routing decisions by weight.
    // Execution (mutations, persistence, notifications) remains in NpcAI.
    public static class NpcTravelScheduler
    {
        public static bool DiagnosticsEnabled { get; set; } = false;

        // ===== NEW: AvoidDaylight helpers =====

        /// <summary>
        /// Returns true if the current time period is considered daylight (Morning, Noon, Afternoon).
        /// </summary>
        public static bool IsDaylightPeriod(GameClock gc)
        {
            if (gc == null) return false;
            var period = gc.GetPeriodDescriptor();
            return string.Equals(period, "Morning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(period, "Noon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(period, "Afternoon", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the NPC has the AvoidDaylight flag (instance or template).
        /// </summary>
        public static bool HasAvoidDaylightFlag(Npc npc)
        {
            if (npc == null) return false;

            // Check instance flags
            if (npc.Flags != null && npc.Flags.Any(f => string.Equals(f, "AvoidDaylight", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Check FlagsJson
            if (!string.IsNullOrEmpty(npc.FlagsJson) && npc.FlagsJson.IndexOf("AvoidDaylight", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Check template flags
            if (npc.Template?.Flags != null && npc.Template.Flags.Any(f => string.Equals(f, "AvoidDaylight", StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        /// <summary>
        /// Returns true if the NPC's current room or area is considered Indoor (safe from daylight).
        /// </summary>
        public static bool IsInIndoorLocation(Npc npc, World.World world)
        {
            if (npc == null || world == null) return false;

            var room = npc.CurrentRoom;
            if (room == null) return false;

            // Check room flags for Indoor
            if (room.Flags != null && room.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Check area Climate for Indoor
            var area = world.Areas?.FirstOrDefault(a => a.AreaID == room.AreaID);
            if (area != null && string.Equals(area.Climate, "Indoor", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Returns true if the NPC is exposed to daylight (has AvoidDaylight, is daylight period, and not in Indoor location).
        /// </summary>
        public static bool IsExposedToDaylight(Npc npc, World.World world, GameClock gc)
        {
            if (!HasAvoidDaylightFlag(npc)) return false;
            if (!IsDaylightPeriod(gc)) return false;
            if (IsInIndoorLocation(npc, world)) return false;
            return true;
        }

        /// <summary>
        /// Find the nearest Indoor room the NPC can flee to.
        /// Priority: 1) Any room in WanderAreas with Indoor climate, 2) BFS to find room with Indoor flag.
        /// Returns null if no safe room found.
        /// </summary>
        public static int? FindNearestIndoorRoom(Npc npc, World.World world)
        {
            if (npc == null || world == null) return null;

            // 1) Check WanderAreas for any area with Climate == "Indoor"
            var wanderAreas = npc.WanderAreas ?? new List<int>();
            foreach (var areaId in wanderAreas)
            {
                var area = world.Areas?.FirstOrDefault(a => a.AreaID == areaId);
                if (area != null && string.Equals(area.Climate, "Indoor", StringComparison.OrdinalIgnoreCase))
                {
                    // Return first room in this area
                    var safeRoom = world.Rooms?.FirstOrDefault(r => r.AreaID == areaId);
                    if (safeRoom != null)
                    {
                        if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} found Indoor area {areaId}, target RoomID={safeRoom.RoomID}");
                        return safeRoom.RoomID;
                    }
                }
            }

            // 2) BFS from current room to find any room with Indoor flag
            var startRoom = npc.CurrentRoom;
            if (startRoom == null) return null;

            var visited = new HashSet<int> { startRoom.RoomID };
            var queue = new Queue<int>();
            queue.Enqueue(startRoom.RoomID);

            int maxSteps = 50; // Limit search depth
            int steps = 0;

            while (queue.Count > 0 && steps < maxSteps)
            {
                steps++;
                var curId = queue.Dequeue();
                var curRoom = world.Rooms?.FirstOrDefault(r => r.RoomID == curId);
                if (curRoom == null) continue;

                // Check if this room is Indoor
                if (curRoom.Flags != null && curRoom.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase)))
                {
                    if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} BFS found Indoor room RoomID={curRoom.RoomID}");
                    return curRoom.RoomID;
                }

                // Check if this room's area is Indoor climate
                var curArea = world.Areas?.FirstOrDefault(a => a.AreaID == curRoom.AreaID);
                if (curArea != null && string.Equals(curArea.Climate, "Indoor", StringComparison.OrdinalIgnoreCase))
                {
                    if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} BFS found Indoor-climate room RoomID={curRoom.RoomID}");
                    return curRoom.RoomID;
                }

                // Enqueue neighbors
                foreach (var exit in curRoom.Exits ?? Enumerable.Empty<Room.RoomExit>())
                {
                    if (exit == null) continue;
                    if (!visited.Contains(exit.ToRoomID))
                    {
                        visited.Add(exit.ToRoomID);
                        queue.Enqueue(exit.ToRoomID);
                    }
                }
            }

            if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} no Indoor room found within {maxSteps} steps");
            return null;
        }

        /// <summary>
        /// Check if a target room is safe for an AvoidDaylight NPC during daylight.
        /// </summary>
        public static bool IsTargetRoomSafeFromDaylight(int targetRoomId, World.World world)
        {
            if (world == null) return true; // Assume safe if we can't check

            var targetRoom = world.Rooms?.FirstOrDefault(r => r.RoomID == targetRoomId);
            if (targetRoom == null) return true;

            // Check room Indoor flag
            if (targetRoom.Flags != null && targetRoom.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Check area Indoor climate
            var targetArea = world.Areas?.FirstOrDefault(a => a.AreaID == targetRoom.AreaID);
            if (targetArea != null && string.Equals(targetArea.Climate, "Indoor", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        // ===== END AvoidDaylight helpers =====



        // Public enum describing movement categories
        public enum TravelType
        {
            None = 0,
            ForcedMove,
            ScheduledMove,
            SleepTravel,
            Wander
        }

        // Decision returned by the scheduler (pure)
        public class NpcTravelDecision
        {
            public TravelType Selected { get; set; } = TravelType.None;
            public double Weight { get; set; } = 0.0;
            public int TargetRoomId { get; set; } = 0;
            public string Reason { get; set; } = "";
        }

        /// <summary>
        /// Helper to check if current game hour matches a schedule entry.
        /// Supports both period-based matching (e.g., "Morning") and hour-based matching (e.g., "13" or "1").
        /// Returns a tuple: (isMatch, isHourBased) to allow priority ordering.
        /// </summary>
        private static (bool isMatch, bool isHourBased) IsScheduleActive(GameClock gc, string scheduleToken)
        {
            if (string.IsNullOrWhiteSpace(scheduleToken)) return (false, false);

            try
            {
                // First try parsing as an integer hour (0-23)
                if (int.TryParse(scheduleToken.Trim(), out int hour))
                {
                    // Hour-based match: exact hour comparison
                    if (hour >= 0 && hour <= 23)
                    {
                        bool matches = gc.Hour == hour;
                        return (matches, true); // isHourBased = true
                    }
                    // Invalid hour value, fall through to period check
                }

                // Fall back to period-based matching (e.g., "Morning", "Afternoon")
                bool periodMatch = gc.IsInPeriod(scheduleToken);
                return (periodMatch, false); // isHourBased = false
            }
            catch
            {
                return (false, false);
            }
        }

        // Main pure decision function: compute candidate weights and choose winner deterministically.
        public static NpcTravelDecision Decide(Npc npc, World.World world, GameClock gc)
        {
            var dec = new NpcTravelDecision { Selected = TravelType.None, Weight = 0.0, TargetRoomId = 0, Reason = "none" };
            if (npc == null || world == null || gc == null) return dec;

            // Skip movement decisions for NPCs being led or hitched
            if (npc.IsBeingLed || npc.IsHitched)
            {
                dec.Selected = TravelType.None;
                dec.Weight = 0.0;
                dec.Reason = npc.IsBeingLed ? "being-led" : "hitched";
                if (DiagnosticsEnabled)
                    Console.WriteLine($"[NpcTravelScheduler] NpcID={npc.NpcID} skipped: {dec.Reason}");
                return dec;
            }

            try
            {
                // Early diagnostics: print schedules and runtime move state when debug enabled.
                try
                {
                    if (NpcTravelScheduler.DiagnosticsEnabled)
                    {
                        try
                        {
                            var ms = npc.MoveSchedules ?? new List<Npc.MoveSchedule>();
                            Console.WriteLine($"[NpcTravelScheduler][DBG] NpcID={npc.NpcID} Hour={gc.Hour} Period={gc.GetPeriodDescriptor()} IsOnMove={npc.IsOnMove} MoveSchedules.Count={ms.Count}");
                            for (int i = 0; i < ms.Count; i++)
                            {
                                var s = ms[i];
                                Console.WriteLine($"[NpcTravelScheduler][DBG]   [{i}] RoomId={s.RoomId} Period='{s.Period}'");
                            }
                        }
                        catch { /* best-effort */ }
                    }
                }
                catch { /* best-effort */ }

                // If any runtime interrupt is active (sleep, trade, combat, admin edit, talk, etc.)
                // the scheduler must not select movement. Honor the interrupt system introduced on Npc.
                try
                {
                    if (npc.HasAnyInterrupt())
                    {
                        var ints = string.Join(",", npc.ListInterrupts().Select(kv => kv.Key.ToString()));
                        dec.Selected = TravelType.None;
                        dec.Weight = 0.0;
                        dec.Reason = $"interrupted:{ints}";
                        if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler] NpcID={npc?.NpcID} interrupted ({ints}), skipping movement.");
                        return dec;
                    }
                }
                catch { /* best-effort */ }

                double forcedWeight = 0.0;
                double scheduledWeight = 0.0;
                double sleepWeight = 0.0;
                double wanderWeight = 0.0;

                wanderWeight = Math.Clamp(npc.WanderChance, 0.0, 1.0);

                // ScheduledMove: check MoveSchedules for current period OR hour match
                // Priority: hour-based schedules take precedence over period-based schedules
                int scheduledTarget = 0;
                try
                {
                    var schedules = npc.MoveSchedules ?? new List<Npc.MoveSchedule>();

                    // Find all matching schedules and prioritize hour-based over period-based
                    Npc.MoveSchedule? matched = null;
                    bool foundHourMatch = false;

                    foreach (var s in schedules)
                    {
                        if (string.IsNullOrWhiteSpace(s?.Period)) continue;

                        var (isMatch, isHourBased) = IsScheduleActive(gc, s.Period);

                        if (isMatch)
                        {
                            // If we find an hour-based match, it takes priority immediately
                            if (isHourBased)
                            {
                                matched = s;
                                foundHourMatch = true;
                                if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][DBG] NpcID={npc.NpcID} found HOUR-BASED match '{s.Period}' (hour={gc.Hour}) -> RoomId={s.RoomId}");
                                break; // Hour match found, stop searching
                            }

                            // If we haven't found an hour match yet, keep the first period match as fallback
                            if (!foundHourMatch && matched == null)
                            {
                                matched = s;
                                if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][DBG] NpcID={npc.NpcID} found period-based match '{s.Period}' -> RoomId={s.RoomId}");
                            }
                        }
                    }

                    if (matched != null)
                    {
                        scheduledWeight = 0.7;
                        scheduledTarget = matched.RoomId;
                        string matchType = foundHourMatch ? "hour-based" : "period-based";
                        if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][DBG] NpcID={npc.NpcID} SELECTED {matchType} Move schedule '{matched.Period}' -> RoomId={matched.RoomId}");
                    }
                    else if ((schedules != null && schedules.Count > 0) && npc.IsOnMove)
                    {
                        // If NPC is already on a move, keep scheduled weight reasonably high so move continues.
                        // Use first schedule as the fallback continuation target (preserves original behavior).
                        var first = schedules.FirstOrDefault();
                        if (first != null)
                        {
                            scheduledWeight = 0.6;
                            scheduledTarget = first.RoomId;
                            if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][DBG] NpcID={npc.NpcID} is already OnMove; continuing to first configured RoomId={first.RoomId}");
                        }
                    }
                }
                catch { scheduledWeight = 0.0; }

                // SleepTravel
                int sleepTarget = 0;
                try
                {
                    if (npc.SleepRoomId > 0 && !string.IsNullOrWhiteSpace(npc.SleepStartPeriod))
                    {
                        if (gc.IsInPeriod(npc.SleepStartPeriod))
                        {
                            sleepWeight = 0.9;
                            sleepTarget = npc.SleepRoomId;
                        }
                    }
                }
                catch { sleepWeight = 0.0; }

                // ===== NEW: AvoidDaylight forced move =====
                double avoidDaylightWeight = 0.0;
                int avoidDaylightTarget = 0;

                try
                {
                    if (HasAvoidDaylightFlag(npc) && IsDaylightPeriod(gc))
                    {
                        bool currentlyIndoor = IsInIndoorLocation(npc, world);
                        if (!currentlyIndoor)
                        {
                            int? safeRoom = FindNearestIndoorRoom(npc, world);
                            if (safeRoom.HasValue)
                            {
                                avoidDaylightWeight = 0.95; // Very high priority - flee from daylight
                                avoidDaylightTarget = safeRoom.Value;
                                forcedWeight = avoidDaylightWeight; // Use ForcedMove type
                                if (DiagnosticsEnabled)
                                    Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} fleeing daylight to RoomID={safeRoom.Value}");
                            }
                            else
                            {
                                if (DiagnosticsEnabled)
                                    Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} exposed to daylight but no safe room found");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] Error: {ex.Message}");
                }
                // ===== END AvoidDaylight forced move =====

                var candidates = new List<(TravelType Type, double Weight, int Target, string Reason)>
                {
                    (TravelType.ForcedMove, forcedWeight, avoidDaylightTarget > 0 ? avoidDaylightTarget : 0, avoidDaylightTarget > 0 ? "avoid-daylight" : "forced"),
                    (TravelType.ScheduledMove, scheduledWeight, scheduledTarget, scheduledWeight>0 ? "scheduled-match" : "not-active"),
                    (TravelType.SleepTravel, sleepWeight, sleepTarget, sleepWeight>0 ? "sleep-period" : "not-active"),
                    (TravelType.Wander, wanderWeight, npc.CurrentRoomID, "wander")
                };

                double best = candidates.Max(c => c.Weight);
                if (best <= 0.0)
                {
                    dec.Selected = TravelType.None;
                    dec.Weight = 0.0;
                    dec.Reason = "no-eligible";
                    return dec;
                }

                var bestCandidates = candidates.Where(c => Math.Abs(c.Weight - best) <= 1e-9).ToList();

                TravelType[] precedence = { TravelType.ScheduledMove, TravelType.SleepTravel, TravelType.ForcedMove, TravelType.Wander };
                var chosen = bestCandidates
                    .OrderBy(c =>
                    {
                        var idx = Array.IndexOf(precedence, c.Type);
                        return idx < 0 ? int.MaxValue : idx;
                    })
                    .ThenBy(c => (npc?.NpcID ?? 0))
                    .First();

                dec.Selected = chosen.Type;
                dec.Weight = chosen.Weight;
                dec.TargetRoomId = chosen.Target;
                dec.Reason = chosen.Reason;

                // ===== NEW: Block AvoidDaylight NPC from moving INTO daylight areas =====
                try
                {
                    if (HasAvoidDaylightFlag(npc) && IsDaylightPeriod(gc) && dec.TargetRoomId > 0)
                    {
                        // If currently indoor and target is NOT indoor, block the move
                        bool currentlyIndoor = IsInIndoorLocation(npc, world);
                        bool targetSafe = IsTargetRoomSafeFromDaylight(dec.TargetRoomId, world);

                        if (currentlyIndoor && !targetSafe)
                        {
                            if (DiagnosticsEnabled)
                                Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] NpcID={npc.NpcID} BLOCKED from leaving Indoor safety to RoomID={dec.TargetRoomId}");
                            dec.Selected = TravelType.None;
                            dec.Weight = 0.0;
                            dec.TargetRoomId = 0;
                            dec.Reason = "avoid-daylight-blocked";
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler][AvoidDaylight] Block check error: {ex.Message}");
                }
                // ===== END Block AvoidDaylight =====

                if (DiagnosticsEnabled)
                {
                    Console.WriteLine($"[NpcTravelScheduler][DBG] NpcID={npc.NpcID} decision -> {dec.Selected} TargetRoomId={dec.TargetRoomId} Reason={dec.Reason} Weight={dec.Weight}");
                }

                return dec;
            }
            catch (Exception ex)
            {
                if (DiagnosticsEnabled) Console.WriteLine($"[NpcTravelScheduler] Decide failed for NpcID={npc?.NpcID}: {ex.Message}");
                return dec;
            }
        } //end Decide


        
    }
}