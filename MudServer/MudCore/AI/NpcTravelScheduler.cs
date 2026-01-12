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

                var candidates = new List<(TravelType Type, double Weight, int Target, string Reason)>
                {
                    (TravelType.ForcedMove, forcedWeight, 0, "forced"),
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