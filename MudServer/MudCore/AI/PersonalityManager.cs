using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using DbWriteQueue = global::MudServer.DbWriteQueue;

namespace MudCore.AI
{
    /// <summary>
    /// Manages NPC personality traits for AI-driven conversations.
    /// Only NPCs with the "AiCanTalk" flag can participate.
    /// </summary>
    public class PersonalityManager
    {
        // Diagnostic toggle
        public static bool DiagnosticsEnabled { get; set; } = false;

        // Cache of loaded personalities (NpcID -> PersonalityData)
        private static readonly Dictionary<int, PersonalityData> _cache
            = new Dictionary<int, PersonalityData>();

        // Trait configuration loaded from personality_ai.json
        private static PersonalityTraitConfig? _traitConfig;
        private static readonly object _configLock = new object();
        private static readonly Random _rng = new Random();

        /// <summary>
        /// Trait configuration structure matching personality_ai.json
        /// </summary>
        public class PersonalityTraitConfig
        {
            public List<string> AlignmentGood { get; set; } = new List<string>();
            public List<string> AlignmentEvil { get; set; } = new List<string>();
            public List<string> AlignmentNeutral { get; set; } = new List<string>();
            public List<string> Primary { get; set; } = new List<string>();
            public List<string> Secondary { get; set; } = new List<string>();
        }

        /// <summary>
        /// Personality data structure matching the DB schema.
        /// </summary>
        public class PersonalityData
        {
            public int PersonalityID { get; set; }          // Equal to NpcID
            public string PrimaryTrait { get; set; } = "Neutral";
            public string? SecondaryTrait { get; set; }
            public string AlignmentTrait { get; set; } = "Neutral";
            public List<int> NpcContacts { get; set; } = new List<int>();   // NpcIDs
            public List<int> RumourAreas { get; set; } = new List<int>();   // AreaIDs
        }

        /// <summary>
        /// Load trait configuration from personality_ai.json (cached).
        /// </summary>
        private static PersonalityTraitConfig LoadTraitConfig()
        {
            lock (_configLock)
            {
                if (_traitConfig != null) return _traitConfig;

                try
                {
                    var path = Path.Combine("Data", "config", "personality_ai.json");
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        _traitConfig = JsonSerializer.Deserialize<PersonalityTraitConfig>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (_traitConfig != null)
                        {
                            if (DiagnosticsEnabled)
                                Console.WriteLine($"[PERSONALITY] Loaded trait config: Primary={_traitConfig.Primary.Count}, Secondary={_traitConfig.Secondary.Count}, AlignmentGood={_traitConfig.AlignmentGood.Count}, AlignmentNeutral={_traitConfig.AlignmentNeutral.Count}, AlignmentEvil={_traitConfig.AlignmentEvil.Count}");

                            return _traitConfig;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PERSONALITY][ERROR] Failed to load personality_ai.json: {ex.Message}");
                }

                // Fallback defaults
                _traitConfig = new PersonalityTraitConfig
                {
                    AlignmentGood = new List<string> { "Honest", "Compassionate", "Optimistic" },
                    AlignmentNeutral = new List<string> { "Reserved", "Pragmatic", "Observant" },
                    AlignmentEvil = new List<string> { "Arrogant", "Manipulative", "Cynical" },
                    Primary = new List<string> { "Friendly", "Blunt", "Charismatic" },
                    Secondary = new List<string> { "Stable", "Confident", "Warm" }
                };

                return _traitConfig;
            }
        }

        /// <summary>
        /// Get a random trait from a list.
        /// </summary>
        private static string? GetRandomTrait(List<string>? traits)
        {
            if (traits == null || traits.Count == 0) return null;
            return traits[_rng.Next(traits.Count)];
        }

        /// <summary>
        /// Get a random AlignmentTrait based on NPC's alignment.
        /// </summary>
        private static string GetRandomAlignmentTrait(Npc npc)
        {
            var config = LoadTraitConfig();
            var alignment = DeriveAlignment(npc);

            List<string> traitPool = alignment switch
            {
                "Good" => config.AlignmentGood,
                "Evil" => config.AlignmentEvil,
                _ => config.AlignmentNeutral
            };

            return GetRandomTrait(traitPool) ?? "Reserved";
        }

        /// <summary>
        /// Get a random PrimaryTrait.
        /// </summary>
        private static string GetRandomPrimaryTrait()
        {
            var config = LoadTraitConfig();
            return GetRandomTrait(config.Primary) ?? "Friendly";
        }

        /// <summary>
        /// Get a random SecondaryTrait.
        /// </summary>
        private static string? GetRandomSecondaryTrait()
        {
            var config = LoadTraitConfig();
            return GetRandomTrait(config.Secondary);
        }

        /// <summary>
        /// Derive alignment category from NPC (Good, Neutral, Evil).
        /// </summary>
        private static string DeriveAlignment(Npc npc)
        {
            if (npc == null) return "Neutral";

            try
            {
                // Check NPC's Alignment enum if available
                if (npc.Alignment == Alignment.Good) return "Good";
                if (npc.Alignment == Alignment.Evil) return "Evil";

                // Check flags for alignment hints
                var flags = npc.Flags ?? new List<string>();
                if (npc.Template?.Flags != null)
                    flags = flags.Concat(npc.Template.Flags).ToList();

                if (flags.Any(f => f.IndexOf("Evil", StringComparison.OrdinalIgnoreCase) >= 0))
                    return "Evil";
                if (flags.Any(f => f.IndexOf("Good", StringComparison.OrdinalIgnoreCase) >= 0))
                    return "Good";
            }
            catch { /* best-effort */ }

            return "Neutral";
        }

        /// <summary>
        /// Check if an NPC can participate in AI talk.
        /// </summary>
        public static bool CanTalk(Npc npc)
        {
            if (npc == null) return false;

            // Check for AiCanTalk flag
            bool hasFlag = false;
            try
            {
                if (npc.Flags != null)
                    hasFlag = npc.Flags.Any(f => string.Equals(f, "AiCanTalk", StringComparison.OrdinalIgnoreCase));

                if (!hasFlag && npc.Template?.Flags != null)
                    hasFlag = npc.Template.Flags.Any(f => string.Equals(f, "AiCanTalk", StringComparison.OrdinalIgnoreCase));
            }
            catch { hasFlag = false; }

            if (DiagnosticsEnabled)
                Console.WriteLine($"[PERSONALITY] CanTalk NpcID={npc.NpcID} Name='{npc.Name}' HasAiCanTalk={hasFlag}");

            return hasFlag;
        }

        /// <summary>
        /// Get the rejection message when NPC cannot talk.
        /// </summary>
        public static string GetCannotTalkMessage(Npc npc)
        {
            string name = npc?.DisplayName ?? npc?.Name ?? "creature";
            return $"The {name} looks at you momentarily. Do you really think they can understand?";
        }

        /// <summary>
        /// Load personality from database (with caching).
        /// </summary>
        public static PersonalityData? GetPersonality(int npcId, SqliteConnection conn)
        {
            if (npcId <= 0) return null;

            // Check cache first
            if (_cache.TryGetValue(npcId, out var cached))
                return cached;

            // Load from DB
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT PersonalityID, PrimaryTrait, SecondaryTrait, AlignmentTrait, NpcContacts, RumourAreas
                    FROM Personality
                    WHERE PersonalityID = @id
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", npcId);

                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    var data = new PersonalityData
                    {
                        PersonalityID = rdr.GetInt32(0),
                        PrimaryTrait = rdr.IsDBNull(1) ? "Neutral" : rdr.GetString(1),
                        SecondaryTrait = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                        AlignmentTrait = rdr.IsDBNull(3) ? "Neutral" : rdr.GetString(3),
                        NpcContacts = ParseJsonIntArray(rdr.IsDBNull(4) ? "[]" : rdr.GetString(4)),
                        RumourAreas = ParseJsonIntArray(rdr.IsDBNull(5) ? "[]" : rdr.GetString(5))
                    };

                    _cache[npcId] = data;

                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[PERSONALITY] Loaded NpcID={npcId} Primary='{data.PrimaryTrait}' Secondary='{data.SecondaryTrait}' Alignment='{data.AlignmentTrait}'");

                    return data;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PERSONALITY][ERROR] Failed to load PersonalityID={npcId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Create or update personality in database.
        /// </summary>
        public static bool SavePersonality(PersonalityData data, SqliteConnection conn)
        {
            if (data == null || data.PersonalityID <= 0) return false;

            try
            {
                var contactsJson = JsonSerializer.Serialize(data.NpcContacts ?? new List<int>());
                var areasJson = JsonSerializer.Serialize(data.RumourAreas ?? new List<int>());

                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = @"
                        INSERT INTO Personality (PersonalityID, PrimaryTrait, SecondaryTrait, AlignmentTrait, NpcContacts, RumourAreas, UpdatedAt)
                        VALUES (@id, @primary, @secondary, @alignment, @contacts, @areas, CURRENT_TIMESTAMP)
                        ON CONFLICT(PersonalityID) DO UPDATE SET
                            PrimaryTrait = @primary,
                            SecondaryTrait = @secondary,
                            AlignmentTrait = @alignment,
                            NpcContacts = @contacts,
                            RumourAreas = @areas,
                            UpdatedAt = CURRENT_TIMESTAMP;";

                    cmd.Parameters.AddWithValue("@id", data.PersonalityID);
                    cmd.Parameters.AddWithValue("@primary", data.PrimaryTrait ?? "Friendly");
                    cmd.Parameters.AddWithValue("@secondary", (object?)data.SecondaryTrait ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@alignment", data.AlignmentTrait ?? "Reserved");
                    cmd.Parameters.AddWithValue("@contacts", contactsJson);
                    cmd.Parameters.AddWithValue("@areas", areasJson);

                    cmd.ExecuteNonQuery();
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));

                // Update cache
                _cache[data.PersonalityID] = data;

                if (DiagnosticsEnabled)
                    Console.WriteLine($"[PERSONALITY] Saved PersonalityID={data.PersonalityID} Primary='{data.PrimaryTrait}' Secondary='{data.SecondaryTrait}' Alignment='{data.AlignmentTrait}'");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PERSONALITY][ERROR] Failed to save PersonalityID={data.PersonalityID}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Initialize personality for an NPC (auto-populates from NPC data with random traits).
        /// Call this when NPC spawns or first interacts.
        /// </summary>
        public static PersonalityData? InitializeForNpc(Npc npc, World.World world, SqliteConnection conn)
        {
            if (npc == null || npc.NpcID <= 0) return null;
            if (!CanTalk(npc)) return null;

            // Check if already exists
            var existing = GetPersonality(npc.NpcID, conn);
            if (existing != null) return existing;

            // Create new personality with random traits from personality_ai.json
            var data = new PersonalityData
            {
                PersonalityID = npc.NpcID,
                PrimaryTrait = GetRandomPrimaryTrait(),
                SecondaryTrait = GetRandomSecondaryTrait(),
                AlignmentTrait = GetRandomAlignmentTrait(npc),
                NpcContacts = new List<int>(),
                RumourAreas = new List<int>(npc.WanderAreas ?? new List<int>())
            };

            // Add current area if not in wander areas
            if (npc.CurrentRoom != null && !data.RumourAreas.Contains(npc.CurrentRoom.AreaID))
                data.RumourAreas.Add(npc.CurrentRoom.AreaID);

            SavePersonality(data, conn);

            if (DiagnosticsEnabled)
                Console.WriteLine($"[PERSONALITY] Initialized NpcID={npc.NpcID} Name='{npc.Name}' -> Primary='{data.PrimaryTrait}' Secondary='{data.SecondaryTrait}' Alignment='{data.AlignmentTrait}'");

            return data;
        }

        /// <summary>
        /// Update NpcContacts when NPC enters a room with other NPCs.
        /// </summary>
        public static void UpdateContacts(Npc npc, Room room, SqliteConnection conn)
        {
            if (npc == null || room == null || !CanTalk(npc)) return;

            var data = GetPersonality(npc.NpcID, conn);
            if (data == null) return;

            bool changed = false;
            foreach (var other in room.Npcs ?? new List<Npc>())
            {
                if (other == null || other.NpcID == npc.NpcID) continue;
                if (other.NpcID <= 0) continue;

                if (!data.NpcContacts.Contains(other.NpcID))
                {
                    data.NpcContacts.Add(other.NpcID);
                    changed = true;

                    if (DiagnosticsEnabled)
                        Console.WriteLine($"[PERSONALITY] NpcID={npc.NpcID} added contact NpcID={other.NpcID} Name='{other.Name}'");
                }
            }

            if (changed)
                SavePersonality(data, conn);
        }

        /// <summary>
        /// Clear the personality cache (call on server restart or major state change).
        /// </summary>
        public static void ClearCache()
        {
            _cache.Clear();
            lock (_configLock) { _traitConfig = null; }

            if (DiagnosticsEnabled)
                Console.WriteLine("[PERSONALITY] Cache cleared");
        }

        /// <summary>
        /// Reload trait configuration from disk (use after editing personality_ai.json).
        /// </summary>
        public static void ReloadTraitConfig()
        {
            lock (_configLock) { _traitConfig = null; }
            LoadTraitConfig();

            if (DiagnosticsEnabled)
                Console.WriteLine("[PERSONALITY] Trait config reloaded");
        }

        /// <summary>
        /// Delete personality from database.
        /// </summary>
        public static bool DeletePersonality(int npcId, SqliteConnection conn)
        {
            if (npcId <= 0) return false;

            try
            {
                DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = "DELETE FROM Personality WHERE PersonalityID = @id;";
                    cmd.Parameters.AddWithValue("@id", npcId);
                    cmd.ExecuteNonQuery();
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));

                _cache.Remove(npcId);

                if (DiagnosticsEnabled)
                    Console.WriteLine($"[PERSONALITY] Deleted PersonalityID={npcId}");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PERSONALITY][ERROR] Failed to delete PersonalityID={npcId}: {ex.Message}");
                throw;
            }
        }

        // --- Helpers ---

        private static List<int> ParseJsonIntArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int>();

            try
            {
                return JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>();
            }
            catch
            {
                return new List<int>();
            }
        }
    }
}