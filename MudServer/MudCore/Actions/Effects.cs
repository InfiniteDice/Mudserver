using System;
using MudCore.Entities;

namespace MudServer
{
    /// <summary>
    /// Small, focused Effects helper.  Starts with Heal support only.
    /// Responsibilities:
    /// - Provide pure computations for heal amounts from various sources (food, spell, worn item, etc.)
    /// - No persistence, no messaging, no side-effects (caller applies changes and persists).
    /// </summary>
    public static class Effects
    {
        // Tunable scalars. Keep these here so balancing is localized.
        private const double FOOD_SCALAR = 50.0;    // existing food formula used weight * 50
        private const double WORN_SCALAR = 1.0;     // placeholder for periodic/regenerative worn-item effects
        private const double SPELL_SCALAR = 1.0;    // spells typically provide explicit magnitude

        /// <summary>
        /// Source of the heal.  Allows source-specific formulas and future branching.
        /// </summary>
        public enum HealSource
        {
            Food,
            Spell,
            WornItem,
            Ability,
            Other
        }

        /// <summary>
        /// Compute the (proposed) heal amount for the given item/source.
        /// - Pure calculation only: does not modify Player or Item.
        /// - For Food: follows the existing heuristic (weight * FOOD_SCALAR, ceil, min 1).
        /// - For Spell/Ability: uses optional magnitude (if provided) or 0.
        /// - For WornItem: small placeholder formula (tunable); can be zero by default.
        /// </summary>
        /// <param name="item">Item that is the source of healing (may be null for non-item sources).</param>
        /// <param name="source">Heal source type.</param>
        /// <param name="magnitude">Optional explicit magnitude (useful for spells/abilities).</param>
        /// <returns>Proposed heal amount (non-negative integer). Caller decides cap/application.</returns>
        public static int ComputeHealAmount(Item? item, HealSource source = HealSource.Food, double? magnitude = null)
        {
            if (source == HealSource.Spell || source == HealSource.Ability)
            {
                // If caller supplies an explicit magnitude use it; otherwise fallback to 0.
                if (magnitude.HasValue)
                    return (int)Math.Max(0, Math.Ceiling(magnitude.Value * SPELL_SCALAR));
                return 0;
            }

            if (source == HealSource.WornItem)
            {
                // Worn items might provide small periodic healing; use weight as a hint if present.
                if (item == null) return 0;
                double w = item.Weight <= 0 ? 0.0 : item.Weight;
                // Example: very small regen per tick; tune WORN_SCALAR if you enable periodic application.
                double val = w * WORN_SCALAR;
                var h = (int)Math.Ceiling(val);
                return Math.Max(0, h);
            }

            // Default / Food path
            if (item == null) return 0;
            double weight = item.Weight <= 0 ? 0.02 : item.Weight; // keep the tiny fallback used before
            double baseHeal = weight * FOOD_SCALAR * (magnitude ?? 1.0);
            int proposed = (int)Math.Ceiling(baseHeal);
            if (proposed < 1) proposed = 1;
            return proposed;
        }

        /// <summary>
        /// Helper: compute how much HP would actually be applied after clamping to MaxHealth.
        /// Pure: does not modify the player.
        /// This mirrors the small step currently done in CommandHandler so callers can get the "actual applied" value for messages.
        /// </summary>
        /// <param name="player">Target player (must be non-null).</param>
        /// <param name="proposedHeal">Proposed heal amount (from ComputeHealAmount).</param>
        /// <returns>Integer amount that would be applied to player's health (>= 0).</returns>
        public static int ComputeAppliedHeal(Player player, int proposedHeal)
        {
            if (player == null) return 0;
            if (proposedHeal <= 0) return 0;
            int oldHp = player.Health;
            int newHp = Math.Min(player.MaxHealth, oldHp + proposedHeal);
            return Math.Max(0, newHp - oldHp);
        }

        // NOTE:
        // - This file intentionally contains no persistence or messaging. CommandHandler retains
        //   responsibility for removing items, saving player state, and sending the "You feel better" text.
        // - When you later add other flag effects (Poison, Buff, etc.), add handlers here returning
        //   structured results; keep side-effects in the callers for minimal disruption.
    }
}