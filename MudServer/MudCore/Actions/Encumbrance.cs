using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MudCore.Actions
{
    /// <summary>
    /// Encumbrance system: manages weight-based mechanics for players and NPCs.
    /// 
    /// Design:
    /// - Calculates inventory mass and effective mass (base + carried items)
    /// - For Players: applies stamina penalties to movement based on load percentage
    /// - For NPCs: calculates EffMass but does NOT apply stamina penalties (NPCs don't have stamina)
    /// - Updates every 10 game seconds from GameLoop.cs for efficiency
    /// 
    /// Key constraints:
    /// - Max load capacity = 60% of base Mass
    /// - First 20% of max capacity is free (no penalty)
    /// - Then +1 stamina per 10% after that (up to 60%)
    /// - Overloaded (>100%): harsh +10 stamina penalty (2x penalty zone)
    /// </summary>
    public static class Encumbrance
    {
        private const bool EnableDetailedLogging = true;

        // In-memory cache: PlayerDbId -> MaxLoadCapacity (calculated once, rarely changes)
        private static readonly Dictionary<int, double> _playerMaxLoadCache = new Dictionary<int, double>();
        private static readonly Dictionary<int, double> _npcMaxLoadCache = new Dictionary<int, double>();
        private static readonly object _cacheLock = new object();

        // =====================================================================
        // PUBLIC: Core Calculation Methods (used by all callers)
        // =====================================================================

        /// <summary>
        /// Calculate total inventory mass (kg) for a player or NPC.
        /// Iterates all items and sums: weight * quantity.
        /// </summary>
        public static double GetInventoryMass(object entity)
        {
            if (entity == null) return 0.0;

            try
            {
                var inventoryProp = entity.GetType().GetProperty("Inventory");
                if (inventoryProp == null) return 0.0;

                var inventory = inventoryProp.GetValue(entity) as List<Item>;
                if (inventory == null || inventory.Count == 0) return 0.0;

                double totalMass = 0.0;
                foreach (var item in inventory)
                {
                    if (item == null) continue;
                    int qty = Math.Max(1, item.Quantity);
                    totalMass += item.Weight * qty;
                }

                return Math.Max(0.0, totalMass);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encumbrance] GetInventoryMass failed: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// Get base mass (body mass, not including items).
        /// Handles both Player and Npc.
        /// </summary>
        /// <summary>
        /// Get base mass (body mass, not including items).
        /// Handles both Player and Npc.
        /// Throws if mass cannot be determined.
        /// </summary>
        public static double GetBaseMass(object entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity), "Entity cannot be null for GetBaseMass");

            try
            {
                if (entity is Player player)
                {
                    if (player.Mass <= 0)
                        throw new InvalidOperationException($"Player {player.Name} has invalid mass: {player.Mass}");
                    return player.Mass;
                }

                if (entity is Npc npc)
                {
                    if (npc.Mass == null || npc.Mass <= 0)
                        throw new InvalidOperationException($"NPC {npc.DisplayName} (ID={npc.NpcID}) has invalid mass: {npc.Mass}");
                    return npc.Mass.Value;
                }

                // For other entity types, try reflection as last resort
                var massProp = entity.GetType().GetProperty("Mass");
                if (massProp == null)
                    throw new InvalidOperationException($"Entity type {entity.GetType().Name} has no Mass property");

                var val = massProp.GetValue(entity);
                if (val == null)
                    throw new InvalidOperationException($"Entity {entity.GetType().Name} Mass property returned null");

                double mass = 0.0;
                if (val is double d)
                    mass = d;
                else if (val is int i)
                    mass = (double)i;
                else if (double.TryParse(val.ToString(), out double parsed))
                    mass = parsed;
                else
                    throw new InvalidOperationException($"Cannot convert Mass property value '{val}' to double for {entity.GetType().Name}");

                if (mass <= 0)
                    throw new InvalidOperationException($"Entity {entity.GetType().Name} has invalid mass: {mass}");

                return mass;
            }
            catch (Exception ex) when (!(ex is InvalidOperationException || ex is ArgumentNullException))
            {
                throw new InvalidOperationException($"Failed to get mass for entity {entity.GetType().Name}", ex);
            }
        }

        /// <summary>
        /// Get max load capacity (60% of base mass).
        /// Cached per entity for performance.
        /// </summary>
        public static double GetMaxLoadCapacity(object entity)
        {
            if (entity == null) return 51.0; // 60% of 85

            try
            {
                int cacheKey = GetEntityCacheKey(entity);
                if (cacheKey > 0)
                {
                    lock (_cacheLock)
                    {
                        var cache = entity is Player ? _playerMaxLoadCache : _npcMaxLoadCache;
                        if (cache.TryGetValue(cacheKey, out var cached))
                            return cached;
                    }
                }

                double baseMass = GetBaseMass(entity);
                double capacity = baseMass * 0.60;

                // Cache it
                if (cacheKey > 0)
                {
                    lock (_cacheLock)
                    {
                        var cache = entity is Player ? _playerMaxLoadCache : _npcMaxLoadCache;
                        cache[cacheKey] = capacity;
                    }
                }

                return capacity;
            }
            catch
            {
                return 51.0;
            }
        }

        /// <summary>
        /// Calculate total loaded mass (base + inventory).
        /// </summary>
        public static double GetTotalLoadedMass(object entity)
        {
            double baseMass = GetBaseMass(entity);
            double inventoryMass = GetInventoryMass(entity);
            return baseMass + inventoryMass;
        }

        /// <summary>
        /// Calculate load percentage (0-100+).
        /// Load% = (InventoryMass / MaxLoadCapacity) * 100
        /// </summary>
        public static double GetLoadPercentage(object entity)
        {
            try
            {
                double inventoryMass = GetInventoryMass(entity);
                if (inventoryMass <= 0.0)
                    return 0.0;

                double maxCapacity = GetMaxLoadCapacity(entity);
                if (maxCapacity <= 0.0)
                    return 0.0;

                double percentage = (inventoryMass / maxCapacity) * 100.0;
                return Math.Max(0.0, percentage);
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Update entity's EffMass to equal current total loaded mass.
        /// Called periodically (every 10 game seconds) to stay in sync.
        /// </summary>
        public static void UpdateEffMass(object entity)
        {
            if (entity == null) return;

            try
            {
                double totalMass = GetTotalLoadedMass(entity);

                if (entity is Player player)
                {
                    player.EffMass = totalMass;
                }
                else if (entity is Npc npc)
                {
                    npc.EffMass = totalMass;
                }
                else
                {
                    var effMassProp = entity.GetType().GetProperty("EffMass");
                    if (effMassProp != null && effMassProp.CanWrite)
                    {
                        effMassProp.SetValue(entity, totalMass);
                    }
                }

                if (EnableDetailedLogging)
                {
                    var name = (entity as Player)?.Name ?? (entity as Npc)?.DisplayName ?? "Unknown";
                    //Console.WriteLine($"[Encumbrance] {name}: EffMass updated to {totalMass:F1} kg");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encumbrance] UpdateEffMass failed: {ex.Message}");
            }
        }

        // =====================================================================
        // PUBLIC: Stamina Penalty (Players Only)
        // =====================================================================

        /// <summary>
        /// Calculate additional stamina cost per move based on current load.
        /// 
        /// Penalty tiers:
        /// - 0-20%: +0 (free)
        /// - 20-30%: +1
        /// - 30-40%: +2
        /// - ... (+1 per 10% above 20%)
        /// - 60-100%: +5
        /// - 100%+: +10 (harsh overload penalty)
        /// </summary>
        public static int CalculateStaminaPenalty(Player player)
        {
            if (player == null)
                return 0;

            try
            {
                double loadPercentage = GetLoadPercentage(player);

                // Free zone: 0-20%
                if (loadPercentage <= 20.0)
                    return 0;

                // Overloaded: >100% (2x harsh penalty)
                if (loadPercentage > 100.0)
                    return 10;

                // Graduated penalty: 20-100%
                // Formula: floor((loadPercentage - 20) / 10) + 1
                int penalty = (int)Math.Floor((loadPercentage - 20.0) / 10.0) + 1;

                // Clamp to reasonable range (0-5 for normal loads, 10 for overloaded)
                penalty = Math.Max(0, Math.Min(penalty, 5));

                return penalty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encumbrance] CalculateStaminaPenalty failed for {player?.Name}: {ex.Message}");
                return 0; // Fail-safe: no penalty on error
            }
        }

        /// <summary>
        /// Get human-readable load status.
        /// Returns: "Light (15%)", "Heavy (62%)", "OVERLOADED (120%)", etc.
        /// </summary>
        public static string GetLoadDescription(object entity)
        {
            try
            {
                double loadPercentage = GetLoadPercentage(entity);

                string category = loadPercentage switch
                {
                    <= 0 => "Unloaded",
                    <= 20 => "Light",
                    <= 40 => "Moderate",
                    <= 60 => "Heavy",
                    <= 100 => "Very Heavy",
                    _ => "Extreme"
                };

                return $"{category}";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Get detailed diagnostic info for admin commands or logging.
        /// </summary>
        public static string GetEncumbranceDetails(object entity)
        {
            try
            {
                if (entity == null) return "Entity is null";

                double baseMass = GetBaseMass(entity);
                double inventoryMass = GetInventoryMass(entity);
                double maxCapacity = GetMaxLoadCapacity(entity);
                double loadPercentage = GetLoadPercentage(entity);
                double totalMass = GetTotalLoadedMass(entity);

                string name = (entity as Player)?.Name ?? (entity as Npc)?.DisplayName ?? "Unknown";
                string loadDesc = GetLoadDescription(entity);

                var details = new System.Text.StringBuilder();
                details.AppendLine($"=== Encumbrance for {name} ===");
                details.AppendLine($"Base Mass: {baseMass:F1} kg");
                details.AppendLine($"Inventory Mass: {inventoryMass:F1} kg");
                details.AppendLine($"Total Loaded Mass: {totalMass:F1} kg");
                details.AppendLine($"Max Capacity (60%): {maxCapacity:F1} kg");
                details.AppendLine($"Load: {loadDesc}");

                if (entity is Player player)
                {
                    int penalty = CalculateStaminaPenalty(player);
                    details.AppendLine($"Stamina Penalty per Move: +{penalty}");
                }

                details.AppendLine($"EffMass: {GetTotalLoadedMass(entity):F1} kg");

                return details.ToString();
            }
            catch (Exception ex)
            {
                return $"Error generating details: {ex.Message}";
            }
        }

        // =====================================================================
        // PUBLIC: Initialization & Updates
        // =====================================================================

        /// <summary>
        /// Called when a player joins the server (after inventory is loaded from DB).
        /// Initializes EffMass and caches max load capacity.
        /// Should be called from SessionManager or PlayerPersistence after loading inventory.
        /// </summary>
        public static void InitializeForPlayer(Player player)
        {
            if (player == null) return;

            try
            {
                // Pre-cache max load capacity
                GetMaxLoadCapacity(player);

                // Update EffMass to current state
                UpdateEffMass(player);

                if (EnableDetailedLogging)
                {
                    Console.WriteLine($"[Encumbrance] Player {player.Name} initialized: {GetEncumbranceDetails(player)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encumbrance] InitializeForPlayer failed for {player?.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when an NPC is created or loaded.
        /// Initializes EffMass (does NOT calculate stamina penalties - NPCs don't have stamina).
        /// Should be called from NpcFactory.CreateNpc or NpcPersistence.LoadAllNpcs.
        /// </summary>
        public static void InitializeForNpc(Npc npc)
        {
            if (npc == null) return;

            try
            {
                // Pre-cache max load capacity
                GetMaxLoadCapacity(npc);

                // Update EffMass to current state
                UpdateEffMass(npc);

                if (EnableDetailedLogging)
                {
                    Console.WriteLine($"[Encumbrance] NPC {npc.DisplayName} initialized: EffMass={npc.EffMass:F1} kg");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encumbrance] InitializeForNpc failed for {npc?.DisplayName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called periodically from GameLoop (every 10 game seconds) to recalculate EffMass
        /// for all players and NPCs. Keeps load state in sync with current inventory.
        /// </summary>
        public static void UpdateAllEncumbrance(World.World world)
        {
            if (world == null) return;

            try
            {
                // Update all players
                if (world.Players != null)
                {
                    foreach (var player in world.Players.Where(p => p != null))
                    {
                        try
                        {
                            UpdateEffMass(player);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Encumbrance] Failed to update player {player?.Name}: {ex.Message}");
                        }
                    }
                }

                // Update all NPCs
                if (world.Npcs != null)
                {
                    foreach (var npc in world.Npcs.Where(n => n != null))
                    {
                        try
                        {
                            UpdateEffMass(npc);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Encumbrance] Failed to update NPC {npc?.DisplayName}: {ex.Message}");
                        }
                    }
                }

                if (EnableDetailedLogging)
                {
                    Console.WriteLine($"[Encumbrance] Updated all entities: {world.Players?.Count ?? 0} players, {world.Npcs?.Count ?? 0} NPCs");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Encumbrance] UpdateAllEncumbrance failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Invalidate max load cache when entity mass changes (rare).
        /// Should be called if an admin modifies a player/NPC's base mass.
        /// </summary>
        public static void InvalidateMaxLoadCache(int entityId, bool isPlayer = true)
        {
            lock (_cacheLock)
            {
                var cache = isPlayer ? _playerMaxLoadCache : _npcMaxLoadCache;
                cache.Remove(entityId);
            }
        }

        // =====================================================================
        // PRIVATE: Helpers
        // =====================================================================

        /// <summary>
        /// Get unique cache key for an entity (DbPlayerID for Player, NpcID for Npc).
        /// Returns 0 if entity has no persistent ID (not yet saved).
        /// </summary>
        private static int GetEntityCacheKey(object entity)
        {
            try
            {
                if (entity is Player player && player.DbPlayerID > 0)
                    return player.DbPlayerID;
                else if (entity is Npc npc && npc.NpcID > 0)
                    return npc.NpcID;
            }
            catch { }

            return 0;
        }
    }
}