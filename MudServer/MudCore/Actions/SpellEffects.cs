using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using System;
using System.Collections.Generic;
using System.Linq;
using static MudCore.Actions.SpellTemplate;

namespace MudCore.Actions
{
    /// <summary>
    /// SpellEffects orchestrates spell effect delivery and application.
    /// Supports multiple delivery methods (Projectile, Touch, Hit) and effect types (Damage, Heal, TimedEffect).
    /// 
    /// Design:
    /// - DeliverSpell: routes to appropriate delivery method based on template
    /// - ApplyHealthEffect: reusable health modifier (damage/heal) for any spell type
    /// - ApplyTimedEffect: applies status effects (Stun, Poison, etc.)
    /// - Future: ApplyItemEffect, ApplyRoomEffect for broader spell scope
    /// </summary>
    public static class SpellEffects
    {
        /// <summary>
        /// Main entry point: deliver spell effect to target(s) based on spell template.
        /// Called from SpellCastingManager.CompleteCast after all validation.
        /// </summary>
        public static void DeliverSpell(
    Player caster,
    string spellId,
    SpellTemplate template,
    int targetNpcId,
    int targetPlayerId,
    bool targetIsSelf,
    Room room,
    World.World world,
    SqliteConnection conn)
        {
            if (caster == null || template == null || room == null)
                return;

            string deliveryMethod = template.DeliveryMethod ?? "Projectile";
            var effect = template.SpellEffects?.FirstOrDefault();
            if (effect == null)
                return;

            try
            {
                switch (deliveryMethod.ToUpperInvariant())
                {
                    case "PROJECTILE":
                        DeliverProjectile(caster, template, effect, targetNpcId, targetPlayerId, room, world, conn);
                        break;

                    case "TOUCH":
                        DeliverTouch(caster, template, effect, targetNpcId, targetPlayerId, targetIsSelf, room, world, conn);
                        break;

                    case "HIT":
                        DeliverHit(caster, template, effect, targetNpcId, targetPlayerId, targetIsSelf, room, world, conn);
                        break;

                    default:
                        Console.WriteLine($"[SPELL] Unknown deliveryMethod: {deliveryMethod}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPELL] DeliverSpell failed for {spellId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Projectile: hits creatures in room, including flying creatures.
        /// </summary>
        private static void DeliverProjectile(
            Player caster,
            SpellTemplate template,
            SpellEffect effect,
            int targetNpcId,
            int targetPlayerId,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            // Projectile can hit NPCs or players
            if (targetNpcId > 0)
            {
                var targetNpc = room.Npcs.FirstOrDefault(n => n.NpcID == targetNpcId);
                if (targetNpc != null)
                {
                    ApplyEffect(caster, targetNpc, template, effect, room, world, conn);
                }
            }
            else if (targetPlayerId > 0)
            {
                var targetPlayer = room.Players.FirstOrDefault(p => p.DbPlayerID == targetPlayerId);
                if (targetPlayer != null)
                {
                    ApplyEffect(caster, targetPlayer, template, effect, room, world, conn);
                }
            }
        }

        /// <summary>
        /// Touch: affects single target (player or NPC) not in combat and not flying.
        /// </summary>
        private static void DeliverTouch(
            Player caster,
            SpellTemplate template,
            SpellEffect effect,
            int targetNpcId,
            int targetPlayerId,
            bool targetIsSelf,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (targetIsSelf)
            {
                ApplyEffect(caster, caster, template, effect, room, world, conn);
            }
            else if (targetNpcId > 0)
            {
                var targetNpc = room.Npcs.FirstOrDefault(n => n.NpcID == targetNpcId);
                if (targetNpc == null)
                    return;

                // Skip flying creatures for Touch delivery
                if (targetNpc.IsFlying)
                {
                    MessageHandler.SendSystem(caster, $"{targetNpc.DisplayName} is flying — you cannot touch it!");
                    return;
                }

                if (!CombatManager.IsEntityInCombat(targetNpc))
                {
                    ApplyEffect(caster, targetNpc, template, effect, room, world, conn);
                }
            }
            else if (targetPlayerId > 0)
            {
                var targetPlayer = room.Players.FirstOrDefault(p => p.DbPlayerID == targetPlayerId);
                if (targetPlayer == null)
                    return;

                // Skip flying players for Touch delivery
                if (Effects.IsPlayerFlying(targetPlayer))
                {
                    MessageHandler.SendSystem(caster, $"{targetPlayer.Name} is flying — you cannot touch them!");
                    return;
                }

                if (!CombatManager.IsEntityInCombat(targetPlayer))
                {
                    ApplyEffect(caster, targetPlayer, template, effect, room, world, conn);
                }
            }
        }

        /// <summary>
        /// Hit: affects target in room (not flying), initiates combat if target is NPC.
        /// </summary>
        private static void DeliverHit(
            Player caster,
            SpellTemplate template,
            SpellEffect effect,
            int targetNpcId,
            int targetPlayerId,
            bool targetIsSelf,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (targetIsSelf)
            {
                ApplyEffect(caster, caster, template, effect, room, world, conn);
            }
            else if (targetNpcId > 0)
            {
                var targetNpc = room.Npcs.FirstOrDefault(n => n.NpcID == targetNpcId);
                if (targetNpc == null)
                    return;

                // Skip flying creatures for Hit delivery
                if (targetNpc.IsFlying)
                {
                    MessageHandler.SendSystem(caster, $"{targetNpc.DisplayName} is flying — hit spell cannot reach!");
                    return;
                }

                ApplyEffect(caster, targetNpc, template, effect, room, world, conn);

                // Initiate combat if not already in combat
                if (!CombatManager.IsEntityInCombat(targetNpc))
                {
                    CombatManager.NpcStartOrJoin(targetNpc, caster, world, conn);
                }
            }
            else if (targetPlayerId > 0)
            {
                var targetPlayer = room.Players.FirstOrDefault(p => p.DbPlayerID == targetPlayerId);
                if (targetPlayer == null)
                    return;

                // Skip flying players for Hit delivery
                if (Effects.IsPlayerFlying(targetPlayer))
                {
                    MessageHandler.SendSystem(caster, $"{targetPlayer.Name} is flying — hit spell cannot reach!");
                    return;
                }

                ApplyEffect(caster, targetPlayer, template, effect, room, world, conn);

                // Note: Player-vs-Player combat initiation may need different handling
                // For now, just apply the effect
            }
        }

        /// <summary>
        /// Core effect application dispatcher.
        /// Routes to specific effect handlers (Damage, Heal, TimedEffect) based on effect.Type.
        /// </summary>
        private static void ApplyEffect(
            object caster,
            object target,
            SpellTemplate template,
            SpellEffect effect,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (target == null)
                return;

            try
            {
                switch (effect.Type?.ToUpperInvariant())
                {
                    case "DAMAGE":
                        ApplyHealthEffect(caster, target, effect, template, room, world, conn);
                        break;

                    case "HEAL":
                        ApplyHealthEffect(caster, target, effect, template, room, world, conn);
                        break;

                    case "TIMEDEFFECT":
                        ApplyTimedEffectFromSpell(caster, target, effect, room, world, conn);
                        break;

                    default:
                        Console.WriteLine($"[SPELL] Unknown effect type: {effect.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPELL] ApplyEffect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reusable health modifier: handles Damage (Lightning, Fireball) and Heal spells.
        /// Computes damage/heal based on effect.BaseValue + (effect.ValuePerLevel × caster level).
        /// Applies stat modifier bonus (Strength for physical, Intelligence for elemental, Wisdom for divine).
        /// </summary>
        private static void ApplyHealthEffect(
            object caster,
            object target,
            SpellEffect effect,
            SpellTemplate template,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (target == null || effect == null)
                return;

            // Compute base damage/heal amount
            int casterLevel = GetCasterLevel(caster);
            int baseAmount = effect.BaseValue ?? 0;
            int bonusPerLevel = effect.ValuePerLevel ?? 0;
            int totalAmount = baseAmount + (bonusPerLevel * casterLevel);

            // Apply stat modifier bonus (future: when player stats are implemented)
            // For now, use placeholder
            string statModifier = effect.StatModifier ?? "";
            // TODO: add bonus = GetStatBonus(caster, statModifier);
            // totalAmount += bonus;

            // Determine if this is damage or heal
            bool isDamage = string.Equals(effect.Type, "Damage", StringComparison.OrdinalIgnoreCase);
            bool isHeal = string.Equals(effect.Type, "Heal", StringComparison.OrdinalIgnoreCase);

            if (isDamage)
            {
                ApplyDamageToTarget(caster, target, totalAmount, effect.DamageType ?? "Physical", room, world, conn);
            }
            else if (isHeal)
            {
                ApplyHealToTarget(caster, target, totalAmount, room, world, conn);
            }
        }

        /// <summary>
        /// Apply damage to a single target (Player or NPC).
        /// Broadcasts damage message to room, handles NPC death if applicable.
        /// </summary>
        private static void ApplyDamageToTarget(
            object caster,
            object target,
            int damageAmount,
            string damageType,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (target == null || damageAmount <= 0)
                return;

            try
            {
                string targetName = GetEntityDisplayName(target);
                string casterName = GetEntityDisplayName(caster);

                // Apply damage
                if (target is Player player)
                {
                    Combat.ApplyDamage(player, damageAmount);
                    MessageHandler.SendSystem(player, $"You take {damageAmount} {damageType} damage from {casterName}'s spell!");

                    // Broadcast to room
                    foreach (var p in room.Players.Where(p => p != player))
                    {
                        MessageHandler.SendSystem(p, $"{targetName} takes {damageAmount} {damageType} damage!");
                    }

                    // Death check
                    if (player.Health <= 0)
                    {
                        Combat.Messaging.PlayerKo(player, "spell damage");
                    }
                }
                else if (target is Npc npc)
                {
                    Combat.ApplyDamage(npc, damageAmount);
                    foreach (var p in room.Players)
                    {
                        MessageHandler.SendSystem(p, $"{targetName} takes {damageAmount} {damageType} damage!");
                    }

                    // Death check
                    if (npc.Health <= 0)
                    {
                        var killer = room.Players.FirstOrDefault();
                        Combat.HandleNpcDeath(killer, npc, world, conn);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPELL] ApplyDamageToTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply healing to a single target (Player or NPC).
        /// </summary>
        private static void ApplyHealToTarget(
            object caster,
            object target,
            int healAmount,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (target == null || healAmount <= 0)
                return;

            try
            {
                string targetName = GetEntityDisplayName(target);

                if (target is Player player)
                {
                    int oldHp = player.Health;
                    player.Health = Math.Min(player.MaxHealth, player.Health + healAmount);
                    int actualHeal = player.Health - oldHp;

                    MessageHandler.SendSystem(player, $"You are healed for {actualHeal} health!");
                    foreach (var p in room.Players.Where(p => p != player))
                    {
                        MessageHandler.SendSystem(p, $"{targetName} is healed!");
                    }
                }
                else if (target is Npc npc)
                {
                    int oldHp = npc.Health;
                    
                    npc.Health = Math.Min(npc.HealthMax, npc.Health + healAmount);
                    int actualHeal = npc.Health - oldHp;

                    foreach (var p in room.Players)
                    {
                        MessageHandler.SendSystem(p, $"{targetName} is healed for {actualHeal} health!");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPELL] ApplyHealToTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply timed effect (status effect) from spell.
        /// </summary>
        private static void ApplyTimedEffectFromSpell(
            object caster,
            object target,
            SpellEffect effect,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (target == null || string.IsNullOrWhiteSpace(effect.FlagEffect))
                return;

            try
            {
                string effectName = effect.FlagEffect;
                int durationSeconds = effect.DurationSeconds ?? 30;

                // Check chance
                double chance = effect.Chance ?? 1.0;
                if (new Random().NextDouble() > chance)
                    return;

                if (target is Player player)
                {
                    // Build effect token and apply
                    string effectToken = $"{effectName}:0:T"; // Temporary effect
                    var (added, _) = Effects.AddDebuff(player, effectToken);
                    if (added)
                    {
                        MessageHandler.SendSystem(player, $"You are {effectName.ToLowerInvariant()}!");
                    }
                }
                else if (target is Npc npc)
                {
                    string effectToken = $"{effectName}:0:T";
                    var (added, _) = Effects.AddDebuff(npc, effectToken);
                    if (added)
                    {
                        foreach (var p in room.Players)
                        {
                            MessageHandler.SendSystem(p, $"{npc.DisplayName} is {effectName.ToLowerInvariant()}!");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPELL] ApplyTimedEffectFromSpell failed: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static int GetCasterLevel(object caster)
        {
            if (caster is Player player)
                return player.Level;
            if (caster is Npc npc)
                return npc.Level ?? 1;
            return 1;
        }

        private static string GetEntityDisplayName(object entity)
        {
            if (entity is Player player)
                return player.Name;
            if (entity is Npc npc)
                return npc.DisplayName;
            return "Unknown";
        }

        /// <summary>
        /// Deliver Grow spell: increase target's SizeEnum (1-10) and add flag for restoration on expiry.
        ///
        /// Flag formats (all data embedded so server restarts can recover fully):
        ///   Player: "IsGrown:{originalSize}:{actualDuration}"
        ///     e.g.  "IsGrown:4:120"
        ///     - Matched by TimedEffectRegex via "Is([A-Za-z_]+):(\d+)" on first number
        ///     - parts[1] = originalSize, parts[2] = durationSeconds
        ///     - ProcessTimedEffects seeds TimedEffectSeconds["Grown"] from parts[2] on restart
        ///     - On expiry ProcessTimedEffects reads parts[1] to restore SizeEnum
        ///
        ///   NPC:    "Grown:originalSize:{originalSize}:{actualDuration}"
        ///     e.g.  "Grown:originalSize:4:120"
        ///     - parts[0] = effectName, parts[1] = restoreKey, parts[2] = restoreValue, parts[3] = durationSeconds
        ///     - ProcessNpcTimedEffects seeds TimedEffectSeconds["Grown"] from parts[3] on restart
        ///     - On expiry ProcessNpcTimedEffects calls RestoreEffect(npc, "Grown", "originalSize", "4")
        /// </summary>
        public static void DeliverGrow(
            Player caster,
            SpellTemplate template,
            SpellEffect effect,
            int targetNpcId,
            int targetPlayerId,
            bool targetIsSelf,
            Room room,
            World.World world,
            SqliteConnection conn,
            int sizeChoice = 1,
            double durationModifier = 1.0)
        {
            object target = null;
            if (targetNpcId > 0)
                target = room.Npcs.FirstOrDefault(n => n.NpcID == targetNpcId);
            else if (targetPlayerId > 0)
                target = room.Players.FirstOrDefault(p => p.DbPlayerID == targetPlayerId);
            else if (targetIsSelf)
                target = caster;

            if (target == null)
            {
                MessageHandler.SendSystem(caster, "Target not found.");
                return;
            }

            try
            {
                // Get original size before changing it
                int originalSize = target is Player p ? p.SizeEnum : (target as Npc)?.SizeEnum ?? 4;

                // Compute new size (clamped 1-10)
                int newSize = Math.Clamp(originalSize + sizeChoice, 1, 10);

                // Compute actual duration: base * modifier, halved if choice == 2
                int baseDuration = effect.DurationSeconds ?? 120;
                int actualDuration = (int)Math.Round(baseDuration * durationModifier / (sizeChoice == 2 ? 2.0 : 1.0));

                if (target is Player playerTarget)
                {
                    // PLAYER flag: "IsGrown:{originalSize}:{actualDuration}"
                    // TimedEffectRegex matches "IsGrown:{originalSize}" (first number after Is prefix)
                    // ProcessTimedEffects reads parts[1]=originalSize, parts[2]=actualDuration for restart recovery
                    if (playerTarget.Flags == null) playerTarget.Flags = new List<string>();

                    // Remove any existing Grown flag before adding new one
                    playerTarget.Flags.RemoveAll(f => f.StartsWith("IsGrown:", StringComparison.OrdinalIgnoreCase));

                    // Flag: "IsGrown:1:{originalSize}:{actualDuration}"
                    // group2=1 (minutes placeholder for expiry check)
                    // group3=originalSize (used to restore SizeEnum on expiry)
                    // group4=actualDuration (used to re-seed TimedEffectSeconds on server restart)
                    playerTarget.Flags.Add($"IsGrown:1:{originalSize}:{actualDuration}");
                    playerTarget.SizeEnum = newSize;
                    playerTarget.TimedEffectSeconds["Grown"] = actualDuration;

                    // ApplySizeChange forces base stats to update from new SizeEnum
                    // InitializeSizeDefaults would skip them since they are already > 0
                    Combat.ApplySizeChange(playerTarget);
                    Combat.UpdateEffectiveStats(playerTarget);

                    try { MudPersistence.PlayerPersistence.SavePlayer(conn, playerTarget); }
                    catch (Exception exPersist) { Console.WriteLine($"[SPELL] Grow player persist failed: {exPersist.Message}"); }
                }
                else if (target is Npc npcTarget)
                {
                    // NPC flag: "Grown:originalSize:{originalSize}:{actualDuration}"
                    // ProcessNpcTimedEffects reads:
                    //   parts[1] = "originalSize" -> RestoreEffect switch case key
                    //   parts[2] = originalSize value -> RestoreEffect restoreValue
                    //   parts[3] = actualDuration -> seeds TimedEffectSeconds on restart
                    if (npcTarget.Flags == null) npcTarget.Flags = new List<string>();

                    // Remove any existing Grown flag before adding new one
                    npcTarget.Flags.RemoveAll(f => f.StartsWith("Grown:", StringComparison.OrdinalIgnoreCase));

                    npcTarget.Flags.Add($"Grown:originalSize:{originalSize}:{actualDuration}");
                    npcTarget.SizeEnum = newSize;

                    // Seed countdown so it starts ticking immediately without waiting for restart
                    if (npcTarget.TimedEffectSeconds == null)
                        npcTarget.TimedEffectSeconds = new Dictionary<string, int>();
                    npcTarget.TimedEffectSeconds["Grown"] = actualDuration;

                    // ApplySizeChange forces base stats to update from new SizeEnum
                    Combat.ApplySizeChange(npcTarget);
                    Combat.UpdateEffectiveStats(npcTarget);

                    try { MudPersistence.NpcPersistence.UpdateNpc(conn, npcTarget); }
                    catch (Exception exPersist) { Console.WriteLine($"[SPELL] Grow NPC persist failed: {exPersist.Message}"); }
                }

                string targetName = target is Player pMsg ? pMsg.Name : (target as Npc)?.DisplayName ?? "Unknown";

                MessageHandler.SendSystem(caster, $"You cast Grow on {targetName}! {targetName} grows larger (+{sizeChoice} size).");
                if (room != null)
                {
                    foreach (var pB in room.Players.Where(p => p != caster))
                        MessageHandler.SendSystem(pB, $"{caster.Name} casts Grow on {targetName}! {targetName} grows larger.");
                }

                Console.WriteLine($"[SPELL] Grow: {targetName} size {originalSize} -> {newSize}, duration {actualDuration}s, choice={sizeChoice}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPELL] DeliverGrow failed: {ex.Message}");
            }
        }
    }
}