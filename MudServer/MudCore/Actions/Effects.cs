using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MudCore.Entities;

namespace MudServer
{
    /// <summary>
    /// Small, focused Effects helper.
    /// Responsibilities:
    /// - Provide pure computations for heal amounts from various sources (food, spell, worn item, etc.)
    /// - Parse and represent simple effect tokens (temporary vs permanent)
    /// - Evaluate hit-trigger flags (e.g. PoisonHit40 -> Poisoned)
    /// - Compute per-tick outcome for temporary and permanent effects (returns actions; caller applies/persists)
    /// Notes:
    /// - This class is intentionally side-effect free: callers must perform messaging and persistence.
    /// - Existing heal logic used by eating was preserved exactly (ComputeHealAmount / ComputeAppliedHeal).
    /// </summary>
    public static class Effects
    {
        private static readonly Random _rng = new();

        // --------------------------------------------------------------------
        // Healing utilities (preserved behavior)
        // --------------------------------------------------------------------
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

        // --------------------------------------------------------------------
        // Effect token parsing and representation
        // Formats supported:
        //  - "Poisoned:30:T"  (preferred: Name:Severity:PermFlag where PermFlag 'P' = permanent)
        //  - "Poisoned30P"    (legacy compact form)
        // --------------------------------------------------------------------
        public static (string name, int severity, bool isPermanent) ParseEffectToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return ("", 0, false);

            token = token.Trim();

            // colon-separated form preferred: Name:Severity:Perm
            if (token.Contains(':'))
            {
                var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(p => p.Trim()).ToArray();
                string name = parts.Length > 0 ? parts[0] : "";
                int severity = 0;
                if (parts.Length > 1) int.TryParse(parts[1], out severity);
                bool perm = false;
                if (parts.Length > 2)
                {
                    var p = parts[2].Trim();
                    perm = p.StartsWith("P", StringComparison.OrdinalIgnoreCase);
                }
                return (name, Math.Max(0, severity), perm);
            }

            // fallback: Name then digits then optional trailing letter (P/T)
            var m = Regex.Match(token, @"^([A-Za-z_]+)(\d*)([PT]?)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string name = m.Groups[1].Value;
                int severity = 0;
                if (!string.IsNullOrEmpty(m.Groups[2].Value))
                    int.TryParse(m.Groups[2].Value, out severity);
                bool perm = m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value) &&
                            m.Groups[3].Value.StartsWith("P", StringComparison.OrdinalIgnoreCase);
                return (name, Math.Max(0, severity), perm);
            }

            // ultimate fallback: treat whole token as name
            return (token, 0, false);
        }

        public static string BuildEffectToken(string name, int severity, bool isPermanent)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (severity <= 0)
                return isPermanent ? $"{name}:0:P" : $"{name}:0:T";
            return isPermanent ? $"{name}:{severity}:P" : $"{name}:{severity}:T";
        }

        // --------------------------------------------------------------------
        // Storage helpers (reflect on entity properties Buffs/Debuffs/Flags)
        // --------------------------------------------------------------------
        private static List<string> GetStringListProp(object entity, string propName)
        {
            try
            {
                var t = entity.GetType();
                var p = t.GetProperty(propName);
                if (p == null) return new List<string>();
                var val = p.GetValue(entity);
                if (val is List<string> list) return list;
            }
            catch { }
            return new List<string>();
        }

        private static void EnsureStringListProp(object entity, string propName)
        {
            try
            {
                var t = entity.GetType();
                var p = t.GetProperty(propName);
                if (p == null) return;
                var val = p.GetValue(entity);
                if (val == null)
                {
                    p.SetValue(entity, new List<string>());
                }
            }
            catch { }
        }

        private static void AddToStringListProp(object entity, string propName, string token)
        {
            try
            {
                EnsureStringListProp(entity, propName);
                var t = entity.GetType();
                var p = t.GetProperty(propName);
                var val = p?.GetValue(entity) as List<string>;
                if (val != null)
                {
                    // replace existing same-name token with new one
                    var parsed = ParseEffectToken(token);
                    var existing = val.FirstOrDefault(s => string.Equals(ParseEffectToken(s).name, parsed.name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        val.Remove(existing);
                    }
                    val.Add(token);
                }
            }
            catch { }
        }

        private static bool RemoveFromStringListProp(object entity, string propName, string effectName)
        {
            try
            {
                var t = entity.GetType();
                var p = t.GetProperty(propName);
                if (p == null) return false;
                var val = p.GetValue(entity) as List<string>;
                if (val == null) return false;
                var toRemove = val.Where(s => string.Equals(ParseEffectToken(s).name, effectName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (toRemove.Count == 0) return false;
                foreach (var r in toRemove) val.Remove(r);
                return true;
            }
            catch { return false; }
        }

        // --------------------------------------------------------------------
        // Public API: presence and add/remove
        // --------------------------------------------------------------------
        public static bool HasEffect(object entity, string effectName)
        {
            if (entity == null || string.IsNullOrWhiteSpace(effectName)) return false;
            try
            {
                var names = new[] { "Buffs", "Debuffs", "Flags" };
                foreach (var prop in names)
                {
                    var list = GetStringListProp(entity, prop);
                    foreach (var tok in list)
                    {
                        var parsed = ParseEffectToken(tok);
                        if (string.Equals(parsed.name, effectName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // Returns (added, needsPersistence) where needsPersistence == true when the effect is permanent and caller should persist Flags.
        public static (bool added, bool needsPersistence) AddDebuff(object entity, string effectToken)
        {
            if (entity == null || string.IsNullOrWhiteSpace(effectToken)) return (false, false);
            var (name, severity, isPermanent) = ParseEffectToken(effectToken);
            if (string.IsNullOrWhiteSpace(name)) return (false, false);

            if (isPermanent)
            {
                AddToStringListProp(entity, "Flags", BuildEffectToken(name, severity, true));
                return (true, true);
            }
            else
            {
                AddToStringListProp(entity, "Debuffs", BuildEffectToken(name, severity, false));
                return (true, false);
            }
        }

        public static (bool added, bool needsPersistence) AddBuff(object entity, string effectToken)
        {
            if (entity == null || string.IsNullOrWhiteSpace(effectToken)) return (false, false);
            var (name, severity, isPermanent) = ParseEffectToken(effectToken);
            if (string.IsNullOrWhiteSpace(name)) return (false, false);

            if (isPermanent)
            {
                AddToStringListProp(entity, "Flags", BuildEffectToken(name, severity, true));
                return (true, true);
            }
            else
            {
                AddToStringListProp(entity, "Buffs", BuildEffectToken(name, severity, false));
                return (true, false);
            }
        }

        public static bool RemoveEffect(object entity, string effectName)
        {
            if (entity == null || string.IsNullOrWhiteSpace(effectName)) return false;
            bool removed = false;
            removed |= RemoveFromStringListProp(entity, "Buffs", effectName);
            removed |= RemoveFromStringListProp(entity, "Debuffs", effectName);
            removed |= RemoveFromStringListProp(entity, "Flags", effectName);
            return removed;
        }

        // --------------------------------------------------------------------
        // Trigger mapping: attacker flag prefix -> resulting effect name
        // Example: "PoisonHit40" (attacker flag) -> attempt to apply "Poisoned"
        // --------------------------------------------------------------------
        private static readonly Dictionary<string, string> TriggerToEffect = new(StringComparer.OrdinalIgnoreCase)
        {
            { "PoisonHit", "Poisoned" },
            { "SlashHit", "Bleeding" }, // example for slashing weapons/attacks.
            { "DrainLife", "Weakened" } // example mapping
        };

        // Evaluate attacker.Flags and return a list of effect tokens that succeeded their chance roll.
        // This function does NOT apply effects to targets; it simply returns tokens the caller should attempt to apply.
        public static List<string> EvaluateHitTriggers(object attacker, bool isCritical)
        {
            var result = new List<string>();
            if (attacker == null) return result;

            // Only evaluate on critical hits for now (caller decides)
            if (!isCritical) return result;

            // Gather flags from the attacker entity itself
            var flags = new List<string>();
            try
            {
                flags.AddRange(GetStringListProp(attacker, "Flags"));
            }
            catch { /* best-effort */ }

            // Also gather flags from held weapon(s) in attacker's inventory (if present).
            // This allows weapon templates (e.g. "SlashHit30T") to trigger effects.
            try
            {
                var atype = attacker.GetType();
                var pinv = atype.GetProperty("Inventory");
                if (pinv != null)
                {
                    var invObj = pinv.GetValue(attacker);
                    if (invObj is System.Collections.IEnumerable ien)
                    {
                        foreach (var o in ien)
                        {
                            if (o == null) continue;
                            try
                            {
                                // Prefer Item typed objects (runtime Item class)
                                if (o is Item it)
                                {
                                    // Only consider held offensive items (Held + Weapon/Handheld category)
                                    bool isHeld = false;
                                    if (it.Flags != null) isHeld = it.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase));
                                    if (!isHeld && !string.IsNullOrEmpty(it.FlagsJson) && it.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0)
                                        isHeld = true;

                                    var cat = it.ItemCategory ?? string.Empty;
                                    if (!isHeld) continue;
                                    if (!string.Equals(cat, "Weapon", StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(cat, "Handheld", StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    // merge flags from the held item
                                    if (it.Flags != null) flags.AddRange(it.Flags);
                                    if (!string.IsNullOrEmpty(it.FlagsJson))
                                    {
                                        try
                                        {
                                            var jlist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(it.FlagsJson);
                                            if (jlist != null) flags.AddRange(jlist);
                                        }
                                        catch { /* ignore malformed item FlagsJson */ }
                                    }
                                }
                                else
                                {
                                    // Generic fallback: try reflection properties Flags / FlagsJson on inventory object
                                    var t = o.GetType();
                                    var pFlags = t.GetProperty("Flags");
                                    if (pFlags != null)
                                    {
                                        if (pFlags.GetValue(o) is System.Collections.IEnumerable pf)
                                        {
                                            foreach (var v in pf) if (v != null) flags.Add(v.ToString() ?? "");
                                        }
                                    }
                                    var pFlagsJson = t.GetProperty("FlagsJson");
                                    if (pFlagsJson != null)
                                    {
                                        try
                                        {
                                            var fj = pFlagsJson.GetValue(o) as string;
                                            if (!string.IsNullOrEmpty(fj))
                                            {
                                                var jlist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(fj);
                                                if (jlist != null) flags.AddRange(jlist);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { /* ignore per-item */ }
                        }
                    }
                }
            }
            catch { /* best-effort inventory inspection */ }

            // Evaluate unique flags (dedupe)
            var uniqueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in flags)
            {
                if (!string.IsNullOrWhiteSpace(f)) uniqueFlags.Add(f);
            }

            foreach (var flag in uniqueFlags)
            {
                try
                {
                    var (name, severity, isPerm) = ParseEffectToken(flag);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    foreach (var kv in TriggerToEffect)
                    {
                        if (name.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            int chance = Math.Max(0, Math.Min(100, severity));
                            if (chance <= 0) continue;

                            if (_rng.Next(0, 100) < chance)
                            {
                                var effName = kv.Value;
                                var effToken = BuildEffectToken(effName, severity, isPerm);
                                result.Add(effToken);
                            }
                        }
                    }
                }
                catch { /* ignore per-flag */ }
            }

            return result;
        }

        // --------------------------------------------------------------------
        // Per-tick processing
        // - Returns actions for caller to apply (damage/heal/remove)
        // - Fade chance for temporary effects = currentHealth / maxHealth
        // - Severity influences per-tick magnitude only (amount = max(1, severity/10))
        // - Permanent Flags do not fade here
        // --------------------------------------------------------------------
        public enum TickActionType { Damage, Heal, RemoveEffect }

        public class TickAction
        {
            public TickActionType Type { get; set; }
            public int Amount { get; set; }
            public string EffectToken { get; set; } = "";
        }

        public class TickOutcome
        {
            public List<TickAction> Actions { get; } = new();
            public List<string> EffectsToRemove { get; } = new();
        }

        // Local mapping of effect name -> per-tick health direction (hMod): -1 damage, +1 heal, 0 none
        // Kept local to avoid coupling with Combat private members.
        private static readonly Dictionary<string, int> EffectHealthMod = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Regeneration", +1 },
            { "Bleeding", -1 },
            { "Poisoned", -1 },
            { "Weakened", 0 }
        };

        public static TickOutcome TickEffects(object entity)
        {
            var outcome = new TickOutcome();
            if (entity == null) return outcome;

            double GetHealth()
            {
                try
                {
                    var t = entity.GetType();
                    var p = t.GetProperty("Health");
                    if (p != null) return Convert.ToDouble(p.GetValue(entity));
                    p = t.GetProperty("HP");
                    if (p != null) return Convert.ToDouble(p.GetValue(entity));
                }
                catch { }
                return 0.0;
            }
            double GetMaxHealth()
            {
                try
                {
                    var t = entity.GetType();
                    var p = t.GetProperty("MaxHealth");
                    if (p != null) return Convert.ToDouble(p.GetValue(entity));
                    p = t.GetProperty("HealthMax");
                    if (p != null) return Convert.ToDouble(p.GetValue(entity));
                }
                catch { }
                return 100.0;
            }

            double currHp = Math.Max(0.0, GetHealth());
            double maxHp = Math.Max(1.0, GetMaxHealth());
            double fadeBaseChance = Math.Clamp(currHp / maxHp, 0.0, 1.0);

            var debuffs = GetStringListProp(entity, "Debuffs").ToList();
            var buffs = GetStringListProp(entity, "Buffs").ToList();

            foreach (var tok in debuffs)
            {
                var (name, severity, isPerm) = ParseEffectToken(tok);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (EffectHealthMod.TryGetValue(name, out var hMod) && hMod != 0)
                {
                    int amount = Math.Max(1, severity / 10);
                    if (hMod < 0) outcome.Actions.Add(new TickAction { Type = TickActionType.Damage, Amount = amount, EffectToken = tok });
                    else outcome.Actions.Add(new TickAction { Type = TickActionType.Heal, Amount = amount, EffectToken = tok });
                }

                if (!isPerm)
                {
                    // Changed removal chance: make fade probability inversely scale with severity.
                    // severity/10 gives a coarse severity scale (e.g. 90 -> 9). We divide the base health ratio
                    // by that scale so higher-severity effects fade much more slowly.
                    double severityScale = Math.Max(1.0, severity / 10.0);
                    double fadeChance = Math.Clamp((currHp / maxHp) / severityScale, 0.0, 1.0);

                    if (_rng.NextDouble() <= fadeChance)
                    {
                        outcome.EffectsToRemove.Add(tok);
                        outcome.Actions.Add(new TickAction { Type = TickActionType.RemoveEffect, Amount = 0, EffectToken = tok });
                    }
                }
            }

            foreach (var tok in buffs)
            {
                var (name, severity, isPerm) = ParseEffectToken(tok);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (EffectHealthMod.TryGetValue(name, out var hMod) && hMod != 0)
                {
                    int amount = Math.Max(1, severity / 10);
                    if (hMod > 0) outcome.Actions.Add(new TickAction { Type = TickActionType.Heal, Amount = amount, EffectToken = tok });
                    else outcome.Actions.Add(new TickAction { Type = TickActionType.Damage, Amount = amount, EffectToken = tok });
                }

                if (!isPerm)
                {
                    if (_rng.NextDouble() <= fadeBaseChance)
                    {
                        outcome.EffectsToRemove.Add(tok);
                        outcome.Actions.Add(new TickAction { Type = TickActionType.RemoveEffect, Amount = 0, EffectToken = tok });
                    }
                }
            }

            var flags = GetStringListProp(entity, "Flags").ToList();
            foreach (var tok in flags)
            {
                var (name, severity, isPerm) = ParseEffectToken(tok);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (EffectHealthMod.TryGetValue(name, out var hMod) && hMod != 0)
                {
                    int amount = Math.Max(1, severity / 10);
                    if (hMod < 0) outcome.Actions.Add(new TickAction { Type = TickActionType.Damage, Amount = amount, EffectToken = tok });
                    else outcome.Actions.Add(new TickAction { Type = TickActionType.Heal, Amount = amount, EffectToken = tok });
                }
            }

            return outcome;
        }
    }
}