using Microsoft.Data.Sqlite;
using MudCore.Actions;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using System;
using System.Collections.Generic;
using System.Linq;
using static MudCore.Entities.Npc;


namespace MudCore.AI
{
    /// <summary>
    /// NpcCombatAi: Specialized combat AI behaviors for NPCs.
    /// Currently handles: SkyHunter predator behavior (flying NPCs attacking from sky)
    /// 
    /// Design:
    /// - In-memory attack tracking per NPC per game period
    /// - One check per hour within the configured TimePeriod
    /// - Supports two attack types: DeathFromAbove (sky-to-ground) and CrippleFlyer (air-to-air)
    /// - Integration point: Called from NpcAI.Wander() for flying NPCs in sky rooms
    /// </summary>
    public static class NpcCombatAi
    {
        // LOGGING TOGGLE - Set to true for debugging SkyHunter behavior
        private const bool EnableDebugLogs = false;

        // THROTTLE CONTROL - Set to minutes between SkyHunter attack checks (default: 1 minute)
        private const int CheckThrottleMinutes = 5;

        // In-memory tracking: NpcID -> (LastCheckHour, AttackCountThisPeriod, LastPeriod)
        // Reset when GameClock period changes
        private static readonly Dictionary<int, (int lastCheckMinute, int attackCount, string lastPeriod)> _skyHunterState
            = new Dictionary<int, (int, int, string)>();

        private static readonly object _stateLock = new object();

        /// <summary>
        /// Main entry point: Check if a flying SkyHunter NPC should attack now.
        /// Called once per tick from NpcAI.Wander() for flying NPCs with SkyHunter behavior.
        /// </summary>
        public static bool CheckSkyHunterAttack(Npc npc, World.World world, SqliteConnection conn, GameClock gameClock)
        {
            if (npc == null || world == null || gameClock == null || npc.Template?.AiBehaviorsRaw == null)
                return false;

            // Guard: NPC must not already be in combat
            if (npc.HasInterrupt(InterruptType.Combat))
            {
                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] {npc.DisplayName}: BLOCKED - already in combat");
                return false;
            }
                
            // Parse SkyHunter behavior config
            var behaviorConfig = ParseSkyHunterConfig(npc.Template.AiBehaviorsRaw);
            if (behaviorConfig == null)
                return false;

            // Guard: Check if current time matches TimePeriod (supports array of periods)
            bool periodMatch = false;
            if (behaviorConfig.TimePeriods != null && behaviorConfig.TimePeriods.Count > 0)
            {
                periodMatch = behaviorConfig.TimePeriods.Any(period => gameClock.IsInPeriod(period));
                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] {npc.DisplayName}: Checking periods - Current={gameClock.GetPeriodDescriptor()}, Match={periodMatch}");
            }
            else if (!string.IsNullOrWhiteSpace(behaviorConfig.TimePeriod))
            {
                periodMatch = gameClock.IsInPeriod(behaviorConfig.TimePeriod);
                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] {npc.DisplayName}: Checking single period - Current={gameClock.GetPeriodDescriptor()}, Match={periodMatch}");
            }

            if (!periodMatch)
            {
                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] {npc.DisplayName}: Period check FAILED - not attacking");
                return false;
            }

            // Guard: Check attack limit and throttle (one check per configured minutes)
            lock (_stateLock)
            {
                // Calculate current game minute (useful for sub-hour throttling)
                int currentGameMinute = (int)(gameClock.Hour * 60 + gameClock.Minute);

                // NEW: Get the actual current period descriptor
                string currentPeriod = gameClock.GetPeriodDescriptor();

                // Reset attack counter if period has changed
                if (!_skyHunterState.TryGetValue(npc.NpcID, out var state) || state.lastPeriod != currentPeriod)
                {
                    _skyHunterState[npc.NpcID] = (currentGameMinute, 0, currentPeriod);
                    state = _skyHunterState[npc.NpcID];
                }

                // Check if we've already attacked the max times this period
                if (state.attackCount >= behaviorConfig.AttacksPerPeriod)
                {
                    if (EnableDebugLogs)
                        Console.WriteLine($"[SkyHunter] {npc.DisplayName} (ID={npc.NpcID}): Limit reached - {state.attackCount}/{behaviorConfig.AttacksPerPeriod} attacks used this {currentPeriod}");
                    return false;
                }

                // Check if we've already rolled within the throttle window (in game minutes)
                if (Math.Abs(currentGameMinute - state.lastCheckMinute) < CheckThrottleMinutes)
                    return false; // Not yet enough minutes have passed

                // Update last check time to current game minute (consume the throttle slot whether attack happens or not)
                _skyHunterState[npc.NpcID] = (currentGameMinute, state.attackCount, currentPeriod);
            }

            // Roll AttackChance (e.g., 50% = random 0-100, if < 50, proceed)
            var roll = new Random().Next(100);
            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] {npc.DisplayName}: Chance roll={roll}, need <{behaviorConfig.AttackChance}");

            if (roll >= behaviorConfig.AttackChance)
                return false; // Chance failed, no attack this hour

            // Determine which combat move to use and find targets
            string selectedMove = SelectCombatMove(npc, world, behaviorConfig);
            if (string.IsNullOrWhiteSpace(selectedMove))
                return false; // No valid targets found

            if (EnableDebugLogs)
                Console.WriteLine($"[SkyHunter] {npc.DisplayName} (ID={npc.NpcID}): Roll {roll}% vs Chance {behaviorConfig.AttackChance}% = PASS | Move: {selectedMove}");

            // Find target(s) based on selected move
            var targets = FindSkyHunterTargets(npc, world, behaviorConfig, selectedMove);
            if (targets == null || targets.Count == 0)
                return false; // No valid targets

            // Select random target from list
            var selectedTarget = targets[new Random().Next(targets.Count)];
            var (targetObj, targetRoom) = selectedTarget;

            if (targetObj == null)
                return false;

            // Log target selection
            if (EnableDebugLogs)
            {
                string targetInfo = (targetObj is Player p)
                    ? $"Player '{p.Name}'"
                    : $"NPC '{((Npc)targetObj).DisplayName}' (ID={((Npc)targetObj).NpcID})";
                Console.WriteLine($"[SkyHunter] {npc.DisplayName} (ID={npc.NpcID}): Target Selected - {targetInfo} in Room {targetRoom?.RoomID}");
            }

            // If DeathFromAbove: move attacker to target room first
            if (selectedMove.Equals("DeathFromAbove", StringComparison.OrdinalIgnoreCase))
            {
                if (targetRoom != null && targetRoom.RoomID != npc.CurrentRoom?.RoomID)
                {
                    // Remove from current room, add to target room
                    if (npc.CurrentRoom != null)
                    {
                        try { npc.CurrentRoom.Npcs.Remove(npc); } catch { }
                    }
                    npc.CurrentRoom = targetRoom;
                    targetRoom.Npcs.Add(npc);

                    // Broadcast dive message
                    string targetName = (targetObj is Player p) ? p.Name : ((Npc)targetObj).DisplayName;
                    string diveMsg = $"{npc.DisplayName} dives in from the skies above to suddenly attack {targetName}!";
                    foreach (var player in targetRoom.Players)
                    {
                        MessageHandler.SendSystem(player, diveMsg);
                    }
                }
            }

            // Initiate combat
            try
            {
                if (targetObj is Player targetPlayer)
                {
                    CombatManager.NpcStartOrJoin(npc, targetPlayer, world, conn);
                }
                else if (targetObj is Npc targetNpc)
                {
                    CombatManager.NpcStartOrJoin(npc, targetNpc, world, conn);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkyHunter] Failed to initiate combat for NPC {npc.Name}: {ex.Message}");
                return false;
            }

            // Increment attack counter (on successful combat start)
            lock (_stateLock)
            {
                if (_skyHunterState.TryGetValue(npc.NpcID, out var state))
                {
                    int newCount = state.attackCount + 1;
                    _skyHunterState[npc.NpcID] = (state.lastCheckMinute, newCount, state.lastPeriod);
                }
            }

            return true;
        }

        /// <summary>
        /// Select which combat move to execute based on available targets.
        /// Priority: CrippleFlyer if natural flyers exist in same sky room, else DeathFromAbove if ground targets exist.
        /// </summary>
        private static string SelectCombatMove(Npc npc, World.World world, SkyHunterConfig config)
        {
            if (!npc.CurrentRoom.Flags?.Any(f => string.Equals(f, "IsSkyRoom", StringComparison.OrdinalIgnoreCase)) ?? true)
                return null;

            // Check for natural flyers in current sky room (air-to-air targets)
            var naturalFlyers = FindNaturalFlyersInSkyRoom(npc.CurrentRoom, npc);
            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] SelectCombatMove: Found {naturalFlyers.Count} natural flyers in sky room");
            if (naturalFlyers.Count > 0)
                return "CrippleFlyer"; // Prioritize air combat if flyers present

            // Check for ground targets below sky room
            var groundTargets = FindGroundTargetsBelowSkyRoom(npc, world, config);
            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] SelectCombatMove: Found {groundTargets.Count} ground targets below");
            if (groundTargets.Count > 0)
                return "DeathFromAbove";

            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] SelectCombatMove: NO TARGETS FOUND for either move");
            return null; // No valid targets for either move
        }

        /// <summary>
        /// Find all valid targets for the selected combat move.
        /// Returns list of (object target, Room targetRoom) tuples.
        /// </summary>
        private static List<(object target, Room targetRoom)> FindSkyHunterTargets(Npc npc, World.World world, SkyHunterConfig config, string moveType)
        {
            var result = new List<(object, Room)>();

            if (moveType.Equals("CrippleFlyer", StringComparison.OrdinalIgnoreCase))
            {
                // Air-to-air: find natural flyers in current sky room
                var flyers = FindNaturalFlyersInSkyRoom(npc.CurrentRoom, npc);
                foreach (var flyer in flyers)
                {
                    result.Add((flyer, npc.CurrentRoom));
                }
            }
            else if (moveType.Equals("DeathFromAbove", StringComparison.OrdinalIgnoreCase))
            {
                // Sky-to-ground: find targets in rooms below
                var groundTargets = FindGroundTargetsBelowSkyRoom(npc, world, config);
                foreach (var (target, room) in groundTargets)
                {
                    result.Add((target, room));
                }
            }

            return result;
        }

        /// <summary>
        /// Find all natural flying NPCs in the current sky room (not players with potion effects).
        /// Natural flyers have "Flying:" in their template flags.
        /// </summary>
        private static List<Npc> FindNaturalFlyersInSkyRoom(Room skyRoom, Npc excludeNpc)
        {
            var flyers = new List<Npc>();
            if (skyRoom == null)
                return flyers;

            foreach (var npc in skyRoom.Npcs)
            {
                if (npc == null || npc.Template == null || npc.NpcID == excludeNpc.NpcID)
                    continue;

                // Check if template has Flying: flag (natural flyer, not magical)
                bool isNaturalFlyer = npc.Template.Flags?.Any(f =>
                    !string.IsNullOrWhiteSpace(f) && f.StartsWith("Flying:", StringComparison.OrdinalIgnoreCase)) ?? false;

                if (isNaturalFlyer)
                    flyers.Add(npc);
            }

            return flyers;
        }

        /// <summary>
        /// Find all valid ground targets in the same area as the sky room.
        /// Filters by AvoidRoomFlag, PreferredTarget, and MaxTargetSizeEnum.
        /// SEARCH STRATEGY: Look in ALL rooms in same AREA (not just those with UP exit).
        /// </summary>
        private static List<(object target, Room room)> FindGroundTargetsBelowSkyRoom(Npc skyHunterNpc, World.World world, SkyHunterConfig config)
        {
            var result = new List<(object, Room)>();

            if (skyHunterNpc.CurrentRoom == null || world == null || world.Rooms == null)
                return result;

            var skyRoom = skyHunterNpc.CurrentRoom;

            // Step 1: Find the area this sky room is in
            int skyAreaId = skyRoom.AreaID;
            if (skyAreaId <= 0)
            {
                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Sky room has no AreaID");
                return result;
            }

            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Sky room in AreaID={skyAreaId}");

            // Step 2: Collect all rooms in the same area (excluding sky room itself, excluding Indoor rooms)
            var validRooms = new List<Room>();
            foreach (var room in world.Rooms)
            {
                if (room == null || room.RoomID == skyRoom.RoomID)
                    continue;

                // Only check rooms in same area
                if (room.AreaID != skyAreaId)
                    continue;

                // Skip rooms with AvoidRoomFlag (e.g., "Indoor")
                if (!string.IsNullOrWhiteSpace(config.AvoidRoomFlag))
                {
                    bool shouldAvoid = room.Flags?.Any(f =>
                        string.Equals(f, config.AvoidRoomFlag, StringComparison.OrdinalIgnoreCase)) ?? false;
                    if (shouldAvoid)
                        continue;
                }

                // Skip sky rooms themselves
                bool isSkyRoom = room.Flags?.Any(f => string.Equals(f, "IsSkyRoom", StringComparison.OrdinalIgnoreCase)) ?? false;
                if (isSkyRoom)
                    continue;

                validRooms.Add(room);
            }

            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Found {validRooms.Count} valid ground rooms in area");

            // Step 3: Priority search for PreferredTarget (e.g., "hare")
            if (!string.IsNullOrWhiteSpace(config.PreferredTarget))
            {
                foreach (var room in validRooms)
                {
                    // Check NPCs for preferred target
                    foreach (var npc in room.Npcs)
                    {
                        if (npc == null || npc.NpcID == skyHunterNpc.NpcID)
                            continue;

                        // Match by template name (case-insensitive substring match)
                        if (npc.Template?.Name != null &&
                            npc.Template.Name.Contains(config.PreferredTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            // Check size constraint
                            if (npc.Template.SizeEnum <= config.MaxTargetSizeEnum)
                            {
                                result.Add((npc, room));
                                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Found preferred target '{npc.DisplayName}' (SizeEnum={npc.Template.SizeEnum}) in Room {room.RoomID}");
                            }
                        }
                    }

                    // Check Players if PreferredTarget is "Player"
                    if (config.PreferredTarget.Equals("Player", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var player in room.Players)
                        {
                            if (player != null)
                                result.Add((player, room));
                        }
                    }
                }

                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Preferred target search found {result.Count} targets");
            }

            // Step 4: Fallback search - if no preferred targets found, get ALL valid targets by size
            if (result.Count == 0)
            {
                foreach (var room in validRooms)
                {
                    foreach (var npc in room.Npcs)
                    {
                        if (npc == null || npc.NpcID == skyHunterNpc.NpcID)
                            continue;

                        if (npc.Template?.SizeEnum <= config.MaxTargetSizeEnum)
                        {
                            result.Add((npc, room));
                            if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Fallback found '{npc.DisplayName}' (SizeEnum={npc.Template.SizeEnum}) in Room {room.RoomID}");
                        }
                    }

                    // Add players (no size constraint)
                    foreach (var player in room.Players)
                    {
                        if (player != null)
                            result.Add((player, room));
                    }
                }

                if (EnableDebugLogs) Console.WriteLine($"[SkyHunter] FindGroundTargets: Fallback search found {result.Count} targets");
            }

            return result;
        }

        /// <summary>
        /// Parse SkyHunter behavior configuration from NPC template AiBehaviors.
        /// Returns null if behavior not found or invalid.
        /// </summary>
        private static SkyHunterConfig ParseSkyHunterConfig(List<System.Text.Json.JsonElement> aiBehaviorsRaw)
        {
            if (aiBehaviorsRaw == null)
                return null;

            try
            {
                foreach (var el in aiBehaviorsRaw)
                {
                    if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;

                    if (el.TryGetProperty("Behavior", out var behaviorEl) &&
                    behaviorEl.GetString()?.Equals("SkyHunter", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var config = new SkyHunterConfig();

                        // NEW: Deserialize TimePeriod (can be string or array of strings)
                        if (el.TryGetProperty("TimePeriod", out var timePeriodEl))
                        {
                            DeserializeTimePeriod(timePeriodEl, config);
                        }

                        if (el.TryGetProperty("AttackChance", out var chanceEl))
                            config.AttackChance = chanceEl.GetInt32();

                        if (el.TryGetProperty("AttacksPerPeriod", out var attacksEl))
                            config.AttacksPerPeriod = attacksEl.GetInt32();

                        if (el.TryGetProperty("MaxTargetSizeEnum", out var sizeEl))
                            config.MaxTargetSizeEnum = sizeEl.GetInt32();

                        if (el.TryGetProperty("AvoidRoomFlag", out var avoidEl))
                            config.AvoidRoomFlag = avoidEl.GetString();

                        if (el.TryGetProperty("PreferredTarget", out var prefEl))
                            config.PreferredTarget = prefEl.GetString();

                        if (el.TryGetProperty("CombatMoveSkill", out var skillEl))
                            config.CombatMoveSkill = skillEl.GetInt32();

                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkyHunter] Failed to parse behavior config: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// NEW HELPER: Deserialize TimePeriod from JSON - handles both string and array formats.
        /// Populates config.TimePeriod (fallback) and config.TimePeriods (array).
        /// </summary>
        private static void DeserializeTimePeriod(System.Text.Json.JsonElement timePeriodEl, SkyHunterConfig config)
        {
            try
            {
                if (timePeriodEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // Single string format: "Night"
                    var period = timePeriodEl.GetString();
                    if (!string.IsNullOrWhiteSpace(period))
                    {
                        config.TimePeriod = period;
                        config.TimePeriods = new List<string> { period };
                    }
                }
                else if (timePeriodEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // Array format: ["Evening", "Morning", "Afternoon"]
                    var periods = new List<string>();
                    foreach (var periodEl in timePeriodEl.EnumerateArray())
                    {
                        var period = periodEl.GetString();
                        if (!string.IsNullOrWhiteSpace(period))
                            periods.Add(period);
                    }

                    config.TimePeriods = periods;
                    // Set fallback TimePeriod to first entry
                    config.TimePeriod = periods.Count > 0 ? periods[0] : "Night";

                    if (EnableDebugLogs)
                    {
                        Console.WriteLine($"[SkyHunter] Deserialized TimePeriods array: {string.Join(", ", periods)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkyHunter] Failed to deserialize TimePeriod: {ex.Message}");
                config.TimePeriod = "Night";
                config.TimePeriods = new List<string> { "Night" };
            }
        }

        /// <summary>
        /// Helper for GameLoop: Handle NPC crash-to-ground after CrippleFlyer critical hit.
        /// Removes Flying flag and moves NPC to random valid room below.
        /// Called from CombatManager after CrippleFlyer critical removes flight.
        /// </summary>
        public static void HandleNpcCrashToGround(Npc crashingNpc, Room currentSkyRoom, World.World world)
        {
            if (crashingNpc == null || currentSkyRoom == null || world == null)
                return;

            // Find rooms below current sky room (same logic as DeathFromAbove targeting)
            var roomsBelow = new List<Room>();
            foreach (var room in world.Rooms)
            {
                if (room == null || room.RoomID == currentSkyRoom.RoomID)
                    continue;

                var upExit = room.Exits?.FirstOrDefault(e =>
                    e.Direction.Equals("UP", StringComparison.OrdinalIgnoreCase) &&
                    e.ToRoomID == currentSkyRoom.RoomID);

                if (upExit != null)
                    roomsBelow.Add(room);
            }

            if (roomsBelow.Count == 0)
                return; // No valid rooms below (shouldn't happen, but safe guard)

            // Pick random room below
            var landingRoom = roomsBelow[new Random().Next(roomsBelow.Count)];

            // Move NPC from sky room to landing room
            try
            {
                currentSkyRoom.Npcs.Remove(crashingNpc);
            }
            catch { }

            crashingNpc.CurrentRoom = landingRoom;
            landingRoom.Npcs.Add(crashingNpc);

            // Broadcast crash message
            string crashMsg = $"{crashingNpc.DisplayName} crashes to the ground!";
            foreach (var player in landingRoom.Players)
            {
                MessageHandler.SendSystem(player, crashMsg);
            }

            Console.WriteLine($"[SkyHunter] NPC {crashingNpc.Name} crashed from sky to room {landingRoom.RoomID}.");
        }
    }

    /// <summary>
    /// Configuration parsed from SkyHunter AiBehavior JSON.
    /// </summary>
    internal class SkyHunterConfig
    {
        public string TimePeriod { get; set; } = "Night";  // Fallback for single string
        public List<string> TimePeriods { get; set; } = new();  // NEW: Array support
        public int AttackChance { get; set; } = 50;
        public int AttacksPerPeriod { get; set; } = 2;
        public int MaxTargetSizeEnum { get; set; } = 4;
        public string AvoidRoomFlag { get; set; } = "Indoor";
        public string PreferredTarget { get; set; } = null;
        public int CombatMoveSkill { get; set; } = 3;
    }
}