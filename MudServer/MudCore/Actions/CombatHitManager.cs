using System;
using System.Collections.Generic;
using System.Linq;
using MudCore.Entities;

namespace MudCore.Actions
{
    /// <summary>
    /// Body location enum for humanoid and non-humanoid creatures.
    /// Used to track where attacks hit and which armor provides protection.
    /// </summary>
    public enum BodyLocation
    {
        // Humanoid locations (listed bottom-to-top for probability weighting)
        Foot_Right = 0,
        Foot_Left = 1,
        Leg_Right = 2,
        Leg_Left = 3,
        Body = 4,
        Hand_Right = 5,
        Hand_Left = 6,
        Arm_Right = 7,
        Arm_Left = 8,
        Neck = 9,
        Head = 10,

        // Non-humanoid locations
        Body_NonHumanoid = 100,
        Head_NonHumanoid = 101
    }

    /// <summary>
    /// Maps Item.WearLoc strings to protective BodyLocation(s).
    /// Handles singular/plural mappings and ambiguous cases.
    /// </summary>
    public static class BodyLocationMapper
    {
        /// <summary>
        /// Convert an Item.WearLoc (e.g., "head", "feet", "body") to list of BodyLocations it protects.
        /// </summary>
        public static List<BodyLocation> MapWearLocToBodyLocations(string? wearLoc, bool isHumanoid = true)
        {
            if (string.IsNullOrWhiteSpace(wearLoc))
                return new List<BodyLocation>();

            var normalized = wearLoc.Trim().ToLowerInvariant();

            // Humanoid mappings
            if (isHumanoid)
            {
                return normalized switch
                {
                    "head" => new() { BodyLocation.Head },
                    "neck" => new() { BodyLocation.Neck },
                    "body" or "chest" or "torso" => new() { BodyLocation.Body },
                    "arm" or "arms" => new() { BodyLocation.Arm_Right, BodyLocation.Arm_Left },
                    "arm_right" or "right arm" => new() { BodyLocation.Arm_Right },
                    "arm_left" or "left arm" => new() { BodyLocation.Arm_Left },
                    "hand" or "hands" => new() { BodyLocation.Hand_Right, BodyLocation.Hand_Left },
                    "hand_right" or "right hand" => new() { BodyLocation.Hand_Right },
                    "hand_left" or "left hand" => new() { BodyLocation.Hand_Left },
                    "leg" or "legs" => new() { BodyLocation.Leg_Right, BodyLocation.Leg_Left },
                    "leg_right" or "right leg" => new() { BodyLocation.Leg_Right },
                    "leg_left" or "left leg" => new() { BodyLocation.Leg_Left },
                    "foot" or "feet" => new() { BodyLocation.Foot_Right, BodyLocation.Foot_Left },
                    "foot_right" or "right foot" => new() { BodyLocation.Foot_Right },
                    "foot_left" or "left foot" => new() { BodyLocation.Foot_Left },
                    // Ambiguous or multi-part armor
                    "forearm" or "forearms" => new() { BodyLocation.Arm_Right, BodyLocation.Arm_Left },
                    "shoulder" or "shoulders" => new() { BodyLocation.Arm_Right, BodyLocation.Arm_Left, BodyLocation.Neck },
                    "waist" or "belt" => new() { BodyLocation.Body },
                    "back" => new() { BodyLocation.Body, BodyLocation.Arm_Right, BodyLocation.Arm_Left },
                    "finger" or "fingers" => new() { BodyLocation.Hand_Right, BodyLocation.Hand_Left },
                    _ => new() // Unknown location, no protection
                };
            }
            else
            {
                // Non-humanoid: only Head or Body
                return normalized switch
                {
                    "head" => new() { BodyLocation.Head_NonHumanoid },
                    "body" or "chest" or "torso" or "scales" => new() { BodyLocation.Body_NonHumanoid },
                    _ => new()
                };
            }
        }

        /// <summary>
        /// Get a human-readable name for a body location.
        /// </summary>
        public static string GetBodyLocationName(BodyLocation location)
        {
            return location switch
            {
                BodyLocation.Foot_Right => "right foot",
                BodyLocation.Foot_Left => "left foot",
                BodyLocation.Leg_Right => "right leg",
                BodyLocation.Leg_Left => "left leg",
                BodyLocation.Body => "body",
                BodyLocation.Hand_Right => "right hand",
                BodyLocation.Hand_Left => "left hand",
                BodyLocation.Arm_Right => "right arm",
                BodyLocation.Arm_Left => "left arm",
                BodyLocation.Neck => "neck",
                BodyLocation.Head => "head",
                BodyLocation.Body_NonHumanoid => "body",
                BodyLocation.Head_NonHumanoid => "head",
                _ => "unknown"
            };
        }

        /// <summary>
        /// Get all valid humanoid body locations in order (bottom to top).
        /// </summary>
        public static List<BodyLocation> GetHumanoidLocations()
        {
            return new()
            {
                BodyLocation.Foot_Right,
                BodyLocation.Foot_Left,
                BodyLocation.Leg_Right,
                BodyLocation.Leg_Left,
                BodyLocation.Body,
                BodyLocation.Hand_Right,
                BodyLocation.Hand_Left,
                BodyLocation.Arm_Right,
                BodyLocation.Arm_Left,
                BodyLocation.Neck,
                BodyLocation.Head
            };
        }

        /// <summary>
        /// Get all valid non-humanoid body locations.
        /// </summary>
        public static List<BodyLocation> GetNonHumanoidLocations()
        {
            return new()
            {
                BodyLocation.Body_NonHumanoid,
                BodyLocation.Head_NonHumanoid
            };
        }
    }

    /// <summary>
    /// Represents a hit that occurred during combat, including location and armor affected.
    /// </summary>
    public class CombatHit
    {
        public BodyLocation TargetLocation { get; set; }
        public List<Item> ArmorHit { get; set; } = new();
        public int TotalArmorACApplied { get; set; } = 0;
        public string LocationName => BodyLocationMapper.GetBodyLocationName(TargetLocation);
    }

    /// <summary>
    /// Selects random body locations for attacks based on attacker/defender size and humanoid status.
    /// Handles probability weighting, size-based shifts, and optional skill-based bypasses.
    /// </summary>
    public static class BodyLocationSelector
    {
        private static readonly Random _rng = new Random();

        /// <summary>
        /// Select a body location for an attack.
        /// Optionally respects a skill-based override (e.g., "target head").
        /// Otherwise, uses weighted probability based on size differential.
        /// </summary>
        /// <param name="attacker">Combat participant performing the attack</param>
        /// <param name="defender">Combat participant being attacked</param>
        /// <param name="skillOverrideLocation">Optional body location from a skill (null = use probability)</param>
        /// <returns>Selected BodyLocation</returns>
        public static BodyLocation SelectHitLocation(
            object attacker,
            object defender,
            BodyLocation? skillOverrideLocation = null)
        {
            // NEW: Placeholder for skill-based targeting
            // Once skills are implemented, they can pass skillOverrideLocation directly
            if (skillOverrideLocation.HasValue)
            {
                return skillOverrideLocation.Value;
            }

            // Get size and humanoid status
            int attackerSize = GetEntitySize(attacker);
            int defenderSize = GetEntitySize(defender);
            bool defenderIsHumanoid = GetEntityIsHumanoid(defender);

            if (defenderIsHumanoid)
            {
                return SelectHumanoidLocation(attackerSize, defenderSize);
            }
            else
            {
                return SelectNonHumanoidLocation(attackerSize, defenderSize);
            }
        }

        /// <summary>
        /// Select a hit location for a humanoid defender.
        /// Uses size-differential weighting: equal sizes favor body, larger attacker favors upper body, smaller attacker favors lower body.
        /// </summary>
        private static BodyLocation SelectHumanoidLocation(int attackerSize, int defenderSize)
        {
            var locations = BodyLocationMapper.GetHumanoidLocations();
            var probabilities = ComputeHumanoidProbabilities(attackerSize, defenderSize);

            return SelectLocationByWeightedProbability(locations, probabilities);
        }

        /// <summary>
        /// Compute probability distribution for humanoid hit locations.
        /// Base: Body 35%, Legs 20%, Arms 15%, Hands 10%, Neck 5%, Head 5%, Feet 5%.
        /// Shift based on size difference: +5% per size step towards upper body if attacker larger.
        /// </summary>
        private static Dictionary<BodyLocation, double> ComputeHumanoidProbabilities(int attackerSize, int defenderSize)
        {
            var probs = new Dictionary<BodyLocation, double>
            {
                { BodyLocation.Foot_Right, 0.025 },
                { BodyLocation.Foot_Left, 0.025 },
                { BodyLocation.Leg_Right, 0.100 },
                { BodyLocation.Leg_Left, 0.100 },
                { BodyLocation.Body, 0.350 },
                { BodyLocation.Hand_Right, 0.050 },
                { BodyLocation.Hand_Left, 0.050 },
                { BodyLocation.Arm_Right, 0.075 },
                { BodyLocation.Arm_Left, 0.075 },
                { BodyLocation.Neck, 0.050 },
                { BodyLocation.Head, 0.050 }
            };

            // Apply size-based shift
            int sizeDiff = attackerSize - defenderSize;
            if (sizeDiff != 0)
            {
                // Shift magnitude: 5% per size step
                double shiftPerStep = 0.05;
                double totalShift = sizeDiff * shiftPerStep;

                if (sizeDiff > 0)
                {
                    // Attacker larger: shift probability UP (towards upper body)
                    ShiftProbabilitiesUp(probs, totalShift);
                }
                else if (sizeDiff < 0)
                {
                    // Attacker smaller: shift probability DOWN (towards lower body)
                    ShiftProbabilitiesDown(probs, Math.Abs(totalShift));
                }
            }

            NormalizeProbabilities(probs);
            return probs;
        }

        /// <summary>
        /// Shift probabilities upward (towards head/arms/neck), reducing lower body probabilities.
        /// </summary>
        private static void ShiftProbabilitiesUp(Dictionary<BodyLocation, double> probs, double amount)
        {
            // NEW: Remove from lower body, add to upper body
            var lowerLocations = new[]
            {
                BodyLocation.Foot_Right, BodyLocation.Foot_Left,
                BodyLocation.Leg_Right, BodyLocation.Leg_Left
            };
            var upperLocations = new[]
            {
                BodyLocation.Arm_Right, BodyLocation.Arm_Left,
                BodyLocation.Neck, BodyLocation.Head
            };

            double removedTotal = 0.0;
            foreach (var loc in lowerLocations)
            {
                double remove = Math.Min(probs[loc], amount / lowerLocations.Length);
                probs[loc] -= remove;
                removedTotal += remove;
            }

            // Distribute removed probability to upper body
            foreach (var loc in upperLocations)
            {
                probs[loc] += removedTotal / upperLocations.Length;
            }
        }

        /// <summary>
        /// Shift probabilities downward (towards feet/legs), reducing upper body probabilities.
        /// </summary>
        private static void ShiftProbabilitiesDown(Dictionary<BodyLocation, double> probs, double amount)
        {
            var upperLocations = new[]
            {
                BodyLocation.Arm_Right, BodyLocation.Arm_Left,
                BodyLocation.Neck, BodyLocation.Head, BodyLocation.Hand_Right, BodyLocation.Hand_Left
            };
            var lowerLocations = new[]
            {
                BodyLocation.Foot_Right, BodyLocation.Foot_Left,
                BodyLocation.Leg_Right, BodyLocation.Leg_Left
            };

            double removedTotal = 0.0;
            foreach (var loc in upperLocations)
            {
                double remove = Math.Min(probs[loc], amount / upperLocations.Length);
                probs[loc] -= remove;
                removedTotal += remove;
            }

            foreach (var loc in lowerLocations)
            {
                probs[loc] += removedTotal / lowerLocations.Length;
            }
        }

        /// <summary>
        /// Select a hit location for a non-humanoid defender.
        /// Body 70% (equal size), Head 30%.
        /// Shift based on size: smaller = more head hits, larger = more body hits.
        /// </summary>
        private static BodyLocation SelectNonHumanoidLocation(int attackerSize, int defenderSize)
        {
            var locations = BodyLocationMapper.GetNonHumanoidLocations();
            var probs = new Dictionary<BodyLocation, double>();

            int sizeDiff = attackerSize - defenderSize;

            if (sizeDiff == 0)
            {
                probs[BodyLocation.Body_NonHumanoid] = 0.70;
                probs[BodyLocation.Head_NonHumanoid] = 0.30;
            }
            else if (sizeDiff < 0)
            {
                // Attacker smaller: more head hits
                probs[BodyLocation.Head_NonHumanoid] = 0.60;
                probs[BodyLocation.Body_NonHumanoid] = 0.40;
            }
            else
            {
                // Attacker larger: more body hits
                probs[BodyLocation.Body_NonHumanoid] = 0.80;
                probs[BodyLocation.Head_NonHumanoid] = 0.20;
            }

            return SelectLocationByWeightedProbability(locations, probs);
        }

        /// <summary>
        /// Select a location from a weighted probability distribution.
        /// Cumulative selection based on RNG value between 0 and 1.
        /// </summary>
        private static BodyLocation SelectLocationByWeightedProbability(
            List<BodyLocation> locations,
            Dictionary<BodyLocation, double> probabilities)
        {
            double rng = _rng.NextDouble(); // 0.0 to 1.0
            double cumulative = 0.0;

            foreach (var location in locations)
            {
                if (!probabilities.TryGetValue(location, out var prob))
                    prob = 0.0;

                cumulative += prob;
                if (rng <= cumulative)
                    return location;
            }

            // Fallback to last location (should not happen if probabilities sum to 1.0)
            return locations.LastOrDefault();
        }

        /// <summary>
        /// Ensure probabilities sum to 1.0 by normalizing.
        /// </summary>
        private static void NormalizeProbabilities(Dictionary<BodyLocation, double> probs)
        {
            double total = probs.Values.Sum();
            if (total <= 0.0)
                return;

            var keys = probs.Keys.ToList();
            foreach (var key in keys)
            {
                probs[key] /= total;
            }
        }

        /// <summary>
        /// Get the size enum of an entity (Player or Npc).
        /// Defaults to 4 if not found.
        /// </summary>
        private static int GetEntitySize(object entity)
        {
            if (entity is Player player)
                return player.SizeEnum;
            else if (entity is Npc npc)
                return npc.SizeEnum ?? 4;
            else
                return 4; // Default to human-sized
        }

        /// <summary>
        /// Determine if an entity is humanoid.
        /// Players are always humanoid. NPCs check template IsHumanoid flag.
        /// </summary>
        private static bool GetEntityIsHumanoid(object entity)
        {
            if (entity is Player)
                return true; // Players always humanoid

            if (entity is Npc npc && npc.Template != null)
            {
                // Check if template has IsHumanoid flag (if it exists)
                // If template doesn't have this, assume non-humanoid for safety
                // NEW: This will need template support in next phase
                return npc.Template.IsHumanoid == true;
            }

            return false; // Default to non-humanoid
        }
    }
}