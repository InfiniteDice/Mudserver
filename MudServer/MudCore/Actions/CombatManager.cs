using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;

namespace MudServer
{
    // Phase 1+2: Add tick-based multi-party combat loop (one attack per participant per tick)
    public static class CombatManager
    {
        private static readonly bool DebugCombat = false;

        private const int CAP_CS = 200;               // Max effective combat skill used in priority score
        private const int GLOBAL_TEAM_CAP = 5;        // Max attacks per team per tick
        private const double SLOT_EXP = 0.7;          // Exponent in slot capacity formula
        private const int EQUAL_SIZE_MAX = 4;         // Max slots when sizes equal
        private static readonly Dictionary<int, CombatState> _activeByRoom = new();

        private class CombatParticipant
        {
            public object Entity { get; init; } = default!;
            public bool IsPlayer { get; init; }
            public Player? AsPlayer { get; init; }
            public Npc? AsNpc { get; init; }
            public int SizeEnum { get; set; }
            public double EffMass { get; set; }
            // CHANGED: floating-point effective combat skill
            public double EffCombatSkill { get; set; }
            public double EffDmgDice { get; set; }
            public double EffArmorClass { get; set; }
            public object? LastHitByEntity { get; set; }
            public bool Alive => IsPlayer ? (AsPlayer?.Health ?? 0) > 0 : (AsNpc?.Health ?? 0) > 0;
            public string DisplayName => IsPlayer ? (AsPlayer?.Name ?? "Player") : (AsNpc?.DisplayName ?? "NPC");
            public int CurrentHealth => IsPlayer ? (AsPlayer?.Health ?? 0) : (AsNpc?.Health ?? 0);
            public int MaxHealth => IsPlayer ? (AsPlayer?.MaxHealth ?? 0) : (AsNpc?.HealthMax ?? 0);
        }

        private class CombatTeam
        {
            public int Id { get; init; }
            public List<CombatParticipant> Members { get; } = new();
        }

        private class CombatState
        {
            public Guid Id { get; } = Guid.NewGuid();
            public int RoomID { get; init; }
            public Room Room { get; init; } = default!;
            public bool Active { get; set; } = true;
            public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
            public int InitiatorTeamId { get; set; } = 0;
            public CombatTeam TeamA { get; } = new CombatTeam { Id = 0 };
            public CombatTeam TeamB { get; } = new CombatTeam { Id = 1 };

            public IEnumerable<CombatParticipant> AllParticipants()
            {
                foreach (var p in TeamA.Members) yield return p;
                foreach (var p in TeamB.Members) yield return p;
            }

            public bool ContainsEntity(object entity) => AllParticipants().Any(p => ReferenceEquals(p.Entity, entity));
            public CombatTeam? GetTeamOf(object entity)
            {
                if (TeamA.Members.Any(p => ReferenceEquals(p.Entity, entity))) return TeamA;
                if (TeamB.Members.Any(p => ReferenceEquals(p.Entity, entity))) return TeamB;
                return null;
            }
            public CombatTeam Opposing(CombatTeam t) => t.Id == 0 ? TeamB : TeamA;
        }

        // ----------------------- Phase 1 API: Start / Join -----------------------

        public static Message StartOrJoin(Player attacker, Npc defender, World world, SqliteConnection conn)
        {
            if (attacker?.CurrentRoom == null || defender?.CurrentRoom == null || attacker.CurrentRoom != defender.CurrentRoom)
                return MessageHandler.System("Target is not in your room.");

            var room = attacker.CurrentRoom;
            if (!_activeByRoom.TryGetValue(room.RoomID, out var state))
            {
                state = new CombatState { RoomID = room.RoomID, Room = room, InitiatorTeamId = 0 };
                _activeByRoom[room.RoomID] = state;

                var pA = BuildParticipant(attacker);
                var pD = BuildParticipant(defender);
                state.TeamA.Members.Add(pA);
                state.TeamB.Members.Add(pD);

                if (DebugCombat)
                    Console.WriteLine($"[COMBAT] State {state.Id} created in Room {room.RoomID}. TeamA=[{pA.DisplayName}] TeamB=[{pD.DisplayName}]");

                var msg = PerformImmediateAttack(attacker, defender, world, conn);
                Combat.Messaging.Engage(attacker.CurrentRoom, attacker, defender);
                return msg ?? MessageHandler.System($"{attacker.Name} engages {defender.DisplayName}!");
            }
            else
            {
                EnsureParticipantPresent(state, attacker, defender);
                var targetTeam = state.GetTeamOf(defender) ?? state.TeamB;
                var attackTeam = state.Opposing(targetTeam);
                var attackerTeam = state.GetTeamOf(attacker);

                if (attackerTeam == null)
                {
                    var pAtt = BuildParticipant(attacker);
                    attackTeam.Members.Add(pAtt);
                    if (DebugCombat)
                        Console.WriteLine($"[COMBAT] Join: {attacker.Name} added to Team{attackTeam.Id} in State {state.Id}");
                    BroadcastSystem(room, $"{attacker.Name} joins the fight against {defender.DisplayName}!");
                }

                var msg = PerformImmediateAttack(attacker, defender, world, conn);
                return msg ?? MessageHandler.System($"{attacker.Name} attacks {defender.DisplayName}.");
            }
        }

        // Public join (assist) API: join an existing combat without immediate attack.
        // target can be an NPC or Player name in the same room.
        public static Message Assist(Player joiner, string targetName, World world, SqliteConnection conn)
        {
            if (joiner?.CurrentRoom == null)
                return MessageHandler.System("You are not in a valid room.");

            var room = joiner.CurrentRoom;
            if (!_activeByRoom.TryGetValue(room.RoomID, out var state) || !state.Active)
                return MessageHandler.System("There is no ongoing combat here.");

            // Try resolve target as player first
            Player? targetPlayer = room.Players.FirstOrDefault(p =>
                p != null && p.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

            Npc? targetNpc = null;
            if (targetPlayer == null)
            {
                // Resolve NPC via display name or template/name starts-with
                targetNpc = room.Npcs.FirstOrDefault(n =>
                    n.DisplayName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(n.Name) && n.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)) ||
                    n.DisplayName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase));
            }

            object? targetEntity = (object?)targetPlayer ?? targetNpc;
            if (targetEntity == null)
                return MessageHandler.System($"No combatant matching '{targetName}' here.");

            var targetTeam = state.GetTeamOf(targetEntity);
            if (targetTeam == null)
                return MessageHandler.System("That target is not currently in combat.");

            // Determine which team joiner should enter:
            // If assisting a player: same team.
            // If assisting an NPC (attacking it): opposing team.
            CombatTeam desiredTeam;
            if (targetPlayer != null)
            {
                desiredTeam = targetTeam; // ally assist
            }
            else
            {
                desiredTeam = state.Opposing(targetTeam); // enemy oppose
            }

            var existingTeam = state.GetTeamOf(joiner);

            if (existingTeam != null)
            {
                if (existingTeam.Id == desiredTeam.Id)
                    return MessageHandler.System("You are already part of this combat.");
                else
                    return MessageHandler.System("You are already engaged on the opposing side."); // prevent unsafe switch for now
            }

            // Add joiner
            var participant = BuildParticipant(joiner);
            desiredTeam.Members.Add(participant);

            if (DebugCombat)
                Console.WriteLine($"[COMBAT] Assist: {joiner.Name} joined Team{desiredTeam.Id} in State={state.Id} (target={GetEntityName(targetEntity)})");

            Combat.Messaging.Assist(room, joiner, targetPlayer != null ? targetPlayer.Name : GetEntityName(targetEntity), assistingPlayer: targetPlayer != null);

            // Provide personal confirmation
            string perspective = targetPlayer != null
                ? $"You join {targetPlayer.Name}'s side."
                : $"You join the fight against {GetEntityName(targetEntity)}.";

            return MessageHandler.System(perspective);
        }

        // ----------------------- Phase 2: Tick loop -----------------------

        public static void Tick(World world, SqliteConnection conn)
        {
            if (_activeByRoom.Count == 0) return;

            var endedStates = new List<int>();

            foreach (var kvp in _activeByRoom.ToList())
            {
                var roomId = kvp.Key;
                var state = kvp.Value;
                if (!state.Active) { endedStates.Add(roomId); continue; }

                var room = state.Room;
                if (room == null || room.RoomID != roomId)
                {
                    endedStates.Add(roomId);
                    continue;
                }

                // Prune moved or dead participants
                PruneParticipants(state, world);

                if (!state.Active || state.TeamA.Members.Count == 0 || state.TeamB.Members.Count == 0)
                {
                    EndState(state, "Team empty / disengage");
                    endedStates.Add(roomId);
                    continue;
                }

                // Refresh effective stats each tick
                RefreshEffectiveStats(state);

                // NOTE: Per-tick effects processing was relocated to the global GameLoop to ensure
                // effects (spells, items, poison, regen) tick regardless of combat state.
                // CombatManager must not apply effect tick damage/heal/removals to avoid duplicate
                // application. The removed block below was replaced by a marker per CONTRIBUTING.md.
                //CODE REMOVED - 2025-12-16 12:00:00 // 'foreach (var part in state.AllParticipants().ToList()) { var outcome = Effects.TickEffects(part.Entity); ... }'
                // Rationale: Effects.TickEffects remains pure; the GameLoop is the single authoritative applier/persister.


                // CHANGED: sum as double, then cast to int where needed
                double skillASum = state.TeamA.Members.Where(m => m.Alive).Sum(m => m.EffCombatSkill);
                double skillBSum = state.TeamB.Members.Where(m => m.Alive).Sum(m => m.EffCombatSkill);

                if (skillASum <= 0 && skillBSum <= 0)
                {
                    EndState(state, "No skill / all incapacitated");
                    endedStates.Add(roomId);
                    continue;
                }

                int skillA = Math.Max(0, (int)Math.Round(skillASum));
                int skillB = Math.Max(0, (int)Math.Round(skillBSum));

                int minSkill = Math.Max(1, Math.Min(skillA > 0 ? skillA : int.MaxValue, skillB > 0 ? skillB : int.MaxValue));
                int baseA = skillA > 0 ? Math.Max(1, (int)Math.Round((double)skillA / minSkill)) : 0;
                int baseB = skillB > 0 ? Math.Max(1, (int)Math.Round((double)skillB / minSkill)) : 0;

                // Attacker availability & global cap
                int aliveA = state.TeamA.Members.Count(m => m.Alive);
                int aliveB = state.TeamB.Members.Count(m => m.Alive);
                baseA = Math.Min(baseA, aliveA);
                baseB = Math.Min(baseB, aliveB);
                baseA = Math.Min(baseA, GLOBAL_TEAM_CAP);
                baseB = Math.Min(baseB, GLOBAL_TEAM_CAP);

                // Slot capacity constraints vs defenders
                int capacityA = ComputeTotalSlotCapacity(state.TeamB.Members.Where(m => m.Alive), state.TeamA.Members.Where(m => m.Alive));
                int capacityB = ComputeTotalSlotCapacity(state.TeamA.Members.Where(m => m.Alive), state.TeamB.Members.Where(m => m.Alive));
                int finalA = Math.Min(baseA, capacityA);
                int finalB = Math.Min(baseB, capacityB);

                if (DebugCombat)
                {
                    Console.WriteLine($"[COMBAT] Tick State={state.Id} Room={roomId} SkillA={skillA} SkillB={skillB} baseA={baseA} baseB={baseB} capA={capacityA} capB={capacityB} finalA={finalA} finalB={finalB}");
                }

                // Execute attacks for Team A then Team B (ordering could alternate later)
                var usedAttackersA = new HashSet<object>();
                var usedAttackersB = new HashSet<object>();
                var defenderSlotsUsed = new Dictionary<object, int>();

                ExecuteTeamAttacks(state, state.TeamA, state.TeamB, finalA, usedAttackersA, defenderSlotsUsed, world, conn);
                ExecuteTeamAttacks(state, state.TeamB, state.TeamA, finalB, usedAttackersB, defenderSlotsUsed, world, conn);

                // Clean up (dead participants removal)
                PruneParticipants(state, world);

                // If one team gone -> end
                if (state.TeamA.Members.Count(m => m.Alive) == 0 || state.TeamB.Members.Count(m => m.Alive) == 0)
                {
                    EndState(state, "Team eliminated");
                    endedStates.Add(roomId);
                }
            }

            foreach (var r in endedStates)
                _activeByRoom.Remove(r);
        }

        private static void ExecuteTeamAttacks(
            CombatState state,
            CombatTeam attackers,
            CombatTeam defenders,
            int budget,
            HashSet<object> usedAttackers,
            Dictionary<object, int> defenderSlotsUsed,
            World world,
            SqliteConnection conn)
        {
            if (budget <= 0) return;

            // CHANGED: order by double CS; use CAP_CS as double
            var orderedAttackers = attackers.Members
                .Where(a => a.Alive)
                .OrderBy(a => ((double)CAP_CS - a.EffCombatSkill) + a.EffMass)
                .ToList();

            int performed = 0;

            foreach (var attacker in orderedAttackers)
            {
                if (performed >= budget) break;
                if (usedAttackers.Contains(attacker.Entity)) continue;

                var defender = SelectDefender(attacker, defenders.Members.Where(d => d.Alive), defenderSlotsUsed);
                if (defender == null) break;

                // Calculate damage and whether it was a critical
                var (dmg, isCrit) = Combat.CalculateDamage(attacker.Entity, defender.Entity);

                if (!attacker.IsPlayer && attacker.AsNpc != null)
                {
                    try { if (conn != null) NpcPersistence.UpdateNpc(conn, attacker.AsNpc); } catch { }
                }
                if (!defender.IsPlayer && defender.AsNpc != null)
                {
                    try { if (conn != null) NpcPersistence.UpdateNpc(conn, defender.AsNpc); } catch { }
                }
                if (attacker.IsPlayer && attacker.AsPlayer != null)
                {
                    try { if (conn != null) PlayerPersistence.SavePlayer(conn, attacker.AsPlayer); } catch { }
                }
                if (defender.IsPlayer && defender.AsPlayer != null)
                {
                    try { if (conn != null) PlayerPersistence.SavePlayer(conn, defender.AsPlayer); } catch { }
                }

                // Record last hitter for defender
                defender.LastHitByEntity = attacker.Entity;

                // Replace attacker/defender/others texts and send loops
                var defName = defender.DisplayName;
                var atkName = attacker.DisplayName;
                var defCurr = defender.CurrentHealth;
                var defMax = defender.MaxHealth;

                Combat.Messaging.Attack(state.Room,
                    attacker.AsPlayer,
                    atkName,
                    defName,
                    dmg,
                    defCurr,
                    defMax,
                    defender.AsPlayer);

                // CRITICAL: notify players when a critical occurred
                if (isCrit)
                {
                    try
                    {
                        if (attacker.IsPlayer && attacker.AsPlayer != null)
                        {
                            foreach (var p in state.Room.Players)
                            {
                                if (p == attacker.AsPlayer)
                                    MessageHandler.SendSystem(p, "You score a critical hit!");
                                else
                                    MessageHandler.SendSystem(p, $"{attacker.DisplayName} scores a critical hit on {defender.DisplayName}!");
                            }
                        }
                        else
                        {
                            // NPC attacker
                            foreach (var p in state.Room.Players)
                            {
                                if (defender.IsPlayer && defender.AsPlayer != null && p == defender.AsPlayer)
                                    MessageHandler.SendSystem(p, $"{attacker.DisplayName} scores a critical hit on you!");
                                else
                                    MessageHandler.SendSystem(p, $"{attacker.DisplayName} scores a critical hit on {defender.DisplayName}!");
                            }
                        }
                    }
                    catch { /* best-effort only */ }
                }

                // Ensure any affected player(s) receive an authoritative player_update (health + xp)
                if (attacker.IsPlayer && attacker.AsPlayer != null)
                {
                    try { if (conn != null) PlayerPersistence.LoadExtendedCombat(conn, attacker.AsPlayer); } catch { }
                    try { MessageHandler.EnqueuePlayerUpdate(attacker.AsPlayer); } catch { }
                }

                if (defender.IsPlayer && defender.AsPlayer != null)
                {
                    try { if (conn != null) PlayerPersistence.LoadExtendedCombat(conn, defender.AsPlayer); } catch { }
                    try { MessageHandler.EnqueuePlayerUpdate(defender.AsPlayer); } catch { }
                }



                if (DebugCombat)
                {
                    Console.WriteLine($"[COMBAT] Attack State={state.Id} {attacker.DisplayName} -> {defender.DisplayName} dmg={Math.Round(dmg,2)} hp={defender.CurrentHealth}/{defender.MaxHealth}");
                }

                // Apply hit-trigger effects on critical hits
                if (isCrit)
                {
                    try
                    {
                        var effectTokens = Effects.EvaluateHitTriggers(attacker.Entity, true);
                        if (effectTokens != null && effectTokens.Count > 0)
                        {
                            foreach (var token in effectTokens)
                            {
                                try
                                {
                                    var (added, needsPersist) = Effects.AddDebuff(defender.Entity, token);
                                    if (added)
                                    {
                                        var (ename, _, _) = Effects.ParseEffectToken(token);
                                        foreach (var p in state.Room.Players)
                                        {
                                            if (attacker.IsPlayer && attacker.AsPlayer != null && p == attacker.AsPlayer)
                                                MessageHandler.SendSystem(p, $"Your critical hit afflicted {defender.DisplayName} with {ename}.");
                                            else
                                                MessageHandler.SendSystem(p, $"{attacker.DisplayName}'s critical hit afflicted {defender.DisplayName} with {ename}.");
                                        }

                                        // Persist the change (persist temporary and permanent effects so they survive restarts)
                                        try
                                        {
                                            if (conn != null)
                                            {
                                                if (defender.IsPlayer && defender.AsPlayer != null)
                                                    PlayerPersistence.SavePlayer(conn, defender.AsPlayer);
                                                else if (!defender.IsPlayer && defender.AsNpc != null)
                                                    NpcPersistence.UpdateNpc(conn, defender.AsNpc);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[COMBAT] Failed to persist effect '{token}' for defender: {ex.Message}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[COMBAT] Failed to apply effect token '{token}' to {defender?.DisplayName}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[COMBAT] EvaluateHitTriggers failed: {ex.Message}");
                    }
                }

                usedAttackers.Add(attacker.Entity);
                performed++;

                // Death handling (NPC) with CS award distribution
                if (!defender.IsPlayer && defender.AsNpc != null && defender.AsNpc.Health <= 0)
                {
                    if (DebugCombat) Console.WriteLine($"[COMBAT] Death NPC {defender.DisplayName}");

                    // Build contributors robustly from the actual team in the CombatState (not the local attackers snapshot)
                    var winningTeam = state.GetTeamOf(attacker.Entity) ?? attackers;
                    var winners = winningTeam.Members
                        .Where(m => m.IsPlayer && m.AsPlayer != null && m.Alive && m.AsPlayer.CurrentRoom == state.Room)
                        .Select(m => m.AsPlayer!)
                        .ToList();

                    // Fallback: ensure at least the immediate attacker is credited if no contributors found
                    if (winners.Count == 0 && attacker.IsPlayer && attacker.AsPlayer != null && attacker.Alive)
                        winners.Add(attacker.AsPlayer);

                    if (DebugCombat) Console.WriteLine($"[COMBAT] Contributors for death: {string.Join(", ", winners.Select(p => p?.Name ?? "<null>"))}");

                    // Route to central Combat.HandleNpcDeath with contributors and the 'world' parameter
                    Combat.HandleNpcDeath(attacker.AsPlayer ?? state.Room.Players.FirstOrDefault(), defender.AsNpc, world, conn, winners);

                    defenders.Members.Remove(defender);
                    break;
                }

                // Player KO handling
                if (defender.IsPlayer && defender.AsPlayer != null && defender.AsPlayer.Health <= 0)
                {
                    Combat.Messaging.PlayerKo(defender.AsPlayer, attacker.DisplayName);
                    if (DebugCombat) Console.WriteLine($"[COMBAT] Player KO {defender.DisplayName}");
                    defenders.Members.Remove(defender);
                    break;
                }
            }
        }

        private static CombatParticipant? SelectDefender(
            CombatParticipant attacker,
            IEnumerable<CombatParticipant> possibleDefenders,
            Dictionary<object, int> defenderSlotsUsed)
        {
            // Prefer last-hit target if still alive & slot available
            var last = possibleDefenders.FirstOrDefault(d => ReferenceEquals(d.Entity, attacker.LastHitByEntity) && d.Alive);
            if (last != null && HasOpenSlot(last, attacker, defenderSlotsUsed))
                return last;

            // Try lowest health with open slot
            var sorted = possibleDefenders
                .OrderBy(d => d.CurrentHealth)
                .ThenBy(d => d.DisplayName)
                .ToList();

            foreach (var d in sorted)
            {
                if (HasOpenSlot(d, attacker, defenderSlotsUsed))
                    return d;
            }

            return null;
        }

        private static bool HasOpenSlot(CombatParticipant defender, CombatParticipant attacker, Dictionary<object, int> used)
        {
            int usedCount = used.TryGetValue(defender.Entity, out var val) ? val : 0;
            int capacity = Slots(defender.SizeEnum, attacker.SizeEnum);
            if (usedCount < capacity)
            {
                used[defender.Entity] = usedCount + 1;
                return true;
            }
            return false;
        }

        // Slot formula: Slots(T,A) = max(1, floor(4 * (T/A)^0.7))
        private static int Slots(int targetSize, int attackerSize)
        {
            if (attackerSize <= 0) attackerSize = 1;
            double raw = EQUAL_SIZE_MAX * Math.Pow((double)targetSize / attackerSize, SLOT_EXP);
            int val = (int)Math.Floor(raw);
            return Math.Max(1, val);
        }

        private static int ComputeTotalSlotCapacity(IEnumerable<CombatParticipant> defenders, IEnumerable<CombatParticipant> attackers)
        {
            var atkList = attackers.ToList();
            var defList = defenders.ToList();
            if (atkList.Count == 0 || defList.Count == 0) return 0;

            // Approximate total capacity: sum over defenders of max slots relative to average attacker size
            double avgAttSize = atkList.Average(a => a.SizeEnum);
            int total = 0;
            foreach (var d in defList)
            {
                int cap = Slots(d.SizeEnum, (int)Math.Round(avgAttSize));
                total += cap;
            }
            // Cannot exceed number of attackers (one attack per attacker per tick)
            return Math.Min(total, atkList.Count);
        }

        private static void RefreshEffectiveStats(CombatState state)
        {
            foreach (var p in state.AllParticipants())
            {
                if (!p.Alive) continue;
                Combat.InitializeSizeDefaults(p.Entity);
                Combat.UpdateEffectiveStats(p.Entity);

                if (p.IsPlayer && p.AsPlayer != null)
                {
                    p.SizeEnum = p.AsPlayer.SizeEnum;
                    p.EffMass = p.AsPlayer.EffMass;
                    p.EffCombatSkill = p.AsPlayer.EffCombatSkill;
                    p.EffDmgDice = p.AsPlayer.EffDmgDice;
                    p.EffArmorClass = p.AsPlayer.EffArmorClass;
                }
                else if (!p.IsPlayer && p.AsNpc != null)
                {
                    p.SizeEnum = p.AsNpc.SizeEnum ?? 4;
                    p.EffMass = p.AsNpc.EffMass ?? p.AsNpc.Mass ?? 85.0;
                    p.EffCombatSkill = p.AsNpc.EffCombatSkill ?? (p.AsNpc.CombatSkill ?? 50.0);
                    p.EffDmgDice = p.AsNpc.EffDmgDice;
                    p.EffArmorClass = p.AsNpc.EffArmorClass;
                }
            }
        }

  

        // Phase 1 immediate attack helper (updated to solo CS award fallback through new method)
        private static Message? PerformImmediateAttack(Player attacker, Npc defender, World world, SqliteConnection conn)
        {
            Combat.InitializeSizeDefaults(attacker);
            Combat.InitializeSizeDefaults(defender);
            Combat.UpdateEffectiveStats(attacker);
            Combat.UpdateEffectiveStats(defender);

            // Calculate damage and critical flag
            var (dmg, isCrit) = Combat.CalculateDamage(attacker, defender);
            var msg = $"{attacker.Name} attacks {defender.DisplayName} for {Math.Round(dmg, 1)} damage. ({Math.Round((double)defender.Health, 0)}/{Math.Round((double)defender.HealthMax, 0)})";

            foreach (var p in attacker.CurrentRoom.Players)
                MessageHandler.SendSystem(p, msg);

            try { if (conn != null) NpcPersistence.UpdateNpc(conn, defender); } catch { }

            // Apply hit-trigger effects on critical
            if (isCrit)
            {
                try
                {
                    var effectTokens = Effects.EvaluateHitTriggers(attacker, true);
                    if (effectTokens != null && effectTokens.Count > 0)
                    {
                        foreach (var token in effectTokens)
                        {
                            try
                            {
                                var (added, needsPersist) = Effects.AddDebuff(defender, token);
                                if (added)
                                {
                                    var (ename, _, _) = Effects.ParseEffectToken(token);
                                    foreach (var p in attacker.CurrentRoom.Players)
                                        MessageHandler.SendSystem(p, $"{attacker.Name}'s critical hit afflicted {defender.DisplayName} with {ename}.");

                                    try
                                    {
                                        if (conn != null) NpcPersistence.UpdateNpc(conn, defender);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[COMBAT] Failed to persist effect '{token}' for defender: {ex.Message}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[COMBAT] Failed to apply effect token '{token}' to {defender?.DisplayName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[COMBAT] EvaluateHitTriggers failed: {ex.Message}");
                }
            }

            if (defender.Health <= 0)
            {
                if (DebugCombat) Console.WriteLine($"[COMBAT] ImmediateAttack(death) → {defender.DisplayName} died.");

                // Solo kill path: contributors = attacker only
                var soloContributors = new List<Player> { attacker };
                Combat.HandleNpcDeath(attacker, defender, world, conn, soloContributors);
            }
            return MessageHandler.System(msg);
        }

        private static CombatParticipant BuildParticipant(Player p)
        {
            Combat.InitializeSizeDefaults(p);
            Combat.UpdateEffectiveStats(p);
            return new CombatParticipant
            {
                Entity = p,
                IsPlayer = true,
                AsPlayer = p,
                SizeEnum = p.SizeEnum,
                EffMass = p.EffMass,
                EffCombatSkill = p.EffCombatSkill,
                EffDmgDice = p.EffDmgDice,
                EffArmorClass = p.EffArmorClass
            };
        }

        private static CombatParticipant BuildParticipant(Npc n)
        {
            Combat.InitializeSizeDefaults(n);
            Combat.UpdateEffectiveStats(n);
            return new CombatParticipant
            {
                Entity = n,
                IsPlayer = false,
                AsNpc = n,
                SizeEnum = n.SizeEnum ?? 4,
                EffMass = n.EffMass ?? n.Mass ?? 85.0,
                // CHANGED: use 50.0 literal to match double
                EffCombatSkill = n.EffCombatSkill ?? (n.CombatSkill ?? 50.0),
                EffDmgDice = n.EffDmgDice,
                EffArmorClass = n.EffArmorClass
            };
        }

        private static void EnsureParticipantPresent(CombatState state, Player attacker, Npc defender)
        {
            if (!state.ContainsEntity(attacker))
            {
                var pA = BuildParticipant(attacker);
                var defTeam = state.GetTeamOf(defender) ?? state.TeamB;
                var atkTeam = state.Opposing(defTeam);
                atkTeam.Members.Add(pA);
            }
            if (!state.ContainsEntity(defender))
            {
                var pD = BuildParticipant(defender);
                var atkTeam = state.GetTeamOf(attacker) ?? state.TeamA;
                var defTeam = state.Opposing(atkTeam);
                defTeam.Members.Add(pD);
            }
        }

        // NEW: prune participants helper (moved back from prior iteration)
        private static void PruneParticipants(CombatState state, World world)
        {
            // Remove entities that left the room or are dead
            state.TeamA.Members.RemoveAll(p => !IsStillInRoom(p, state.Room) || !p.Alive);
            state.TeamB.Members.RemoveAll(p => !IsStillInRoom(p, state.Room) || !p.Alive);

            if (state.TeamA.Members.Count == 0 || state.TeamB.Members.Count == 0)
                state.Active = false;
        }

        private static bool IsStillInRoom(CombatParticipant p, Room room)
        {
            if (p.IsPlayer && p.AsPlayer != null)
                return p.AsPlayer.CurrentRoom == room;
            if (!p.IsPlayer && p.AsNpc != null)
                return p.AsNpc.CurrentRoom == room;
            return false;
        }

        // NEW: end state helper (moved back from prior iteration)
        private static void EndState(CombatState state, string reason)
        {
            if (!state.Active) return;
            state.Active = false;
            foreach (var pl in state.Room.Players)
                MessageHandler.SendSystem(pl, "Combat ends.");
            if (DebugCombat) Console.WriteLine($"[COMBAT] End State={state.Id} Reason={reason}");
        }

        
        private static void BroadcastSystem(Room room, string text)
        {
            foreach (var p in room.Players)
                MessageHandler.SendSystem(p, text);
        }

        private static string GetEntityName(object e)
        {
            if (e is Player pl) return pl.Name;
            if (e is Npc npc) return npc.DisplayName;
            return "Unknown";
        }

        // Query: is this entity currently in an active combat state?
        public static bool IsEntityInCombat(object entity)
        {
            foreach (var kvp in _activeByRoom)
            {
                var state = kvp.Value;
                if (!state.Active) continue;
                if (state.ContainsEntity(entity))
                    return true;
            }
            return false;
        }
    }
}