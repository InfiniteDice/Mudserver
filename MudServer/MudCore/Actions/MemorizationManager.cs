using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MudCore.Actions
{
    /// <summary>
    /// MemorizationManager handles spell memorization mechanics.
    /// Supports multiple skill types: skill_arcane_knowledge, skill_elemental_knowledge, etc.
    /// 
    /// Memorization Requirements:
    /// 1. Takes 10 game minutes per spell
    /// 2. No interruptions (combat or room movement cancels)
    /// 3. Must have spell page/book in inventory
    /// 4. Costs 60 stamina upfront (40 refunded if interrupted, 0 if completed)
    /// 5. Insertes into PlayerSpellMem table on success
    /// 6. Multiple copies of same spell can be memorized up to retention limit
    /// </summary>
    public static class MemorizationManager
    {
        private const int MEMORIZATION_MINUTES = 10;
        private const int STAMINA_COST = 60;
        private const int STAMINA_REFUND_ON_INTERRUPT = 40;
        private const string MEMORIZING_FLAG_PREFIX = "IsMemorizing:";

        /// <summary>
        /// Start memorizing a spell from a page or book.
        /// </summary>
        public static Message StartMemorizing(Player player, string spellId, Room room, World.World world, SqliteConnection conn)
        {
            if (player == null || string.IsNullOrWhiteSpace(spellId))
                return MessageHandler.System("Invalid operation.");

            if (room == null)
                return MessageHandler.System("You are not in a valid room.");

            // Validation: Check all requirements
            var validation = CanMemorize(player, spellId, world, conn);
            if (!validation.canMemorize)
                return MessageHandler.System(validation.reason);

            // Deduct stamina immediately
            player.DecreaseStamina(STAMINA_COST, "spell memorization");

            // Create pending action
            player.PendingAction = new PendingAction
            {
                Command = "MemorizeSpell",
                Step = "InProgress",
                Data = new Dictionary<string, object>
                {
                    { "SpellId", spellId },
                    { "SkillId", validation.skillId },
                    { "StartedAtUtc", DateTime.UtcNow },
                    { "RoomID", room.RoomID }
                }
            };

            // Apply timed effect flag
            Effects.ApplyTimedEffect(player, "Memorizing", MEMORIZATION_MINUTES, conn, world);

            string msg = $"You begin memorizing {spellId}... ({MEMORIZATION_MINUTES} game minutes required). Do not move or enter combat.";
            MessageHandler.SendSystem(player, msg);

            foreach (var p in room.Players.Where(p => p != player))
            {
                MessageHandler.SendSystem(p, $"{player.Name} begins concentrating on memorizing a spell.");
            }

            return null;
        }

        /// <summary>
        /// Check if player can memorize a spell.
        /// Returns tuple (canMemorize, reason, skillId).
        /// </summary>
        private static (bool canMemorize, string reason, string skillId) CanMemorize(Player player, string spellId, World.World world, SqliteConnection conn)
        {
            // Check 1: Player has 60+ stamina
            if (player.Stamina < STAMINA_COST)
                return (false, $"You need {STAMINA_COST} stamina to memorize. You only have {player.Stamina}. Rest first.", "");

            // Check 2: Spell page/book in inventory
            var spellSource = FindSpellSourceInInventory(player, spellId, conn);
            if (spellSource == null)
                return (false, $"You do not have a page or book with {spellId}.", "");

            // Check 3: Find matching skill and validate
            Console.WriteLine($"[MEMORIZE] Looking for spell: {spellId}");
            var skillLookup = GetSkillForSpell(spellId, world);
            Console.WriteLine($"[MEMORIZE] Spell lookup result: skillId='{skillLookup.skillId}', minLevel={skillLookup.minLevel}");

            if (string.IsNullOrWhiteSpace(skillLookup.skillId))
                return (false, "That spell has no associated skill.", "");

            string skillName = GetSkillName(skillLookup.skillId);
            EnsureSkillExists(player, skillLookup.skillId, conn);
            int skillLevel = player.GetSkillLevel(skillLookup.skillId, conn);

            if (skillLevel < skillLookup.minLevel)
                return (false, $"You need {skillName} level {skillLookup.minLevel} to memorize this spell. You have level {skillLevel}.", skillLookup.skillId);

            // Check 4: Player not in combat
            if (CombatManager.IsEntityInCombat(player))
                return (false, "You cannot memorize while in combat.", skillLookup.skillId);

            // Check 5: Not already memorizing
            if (player.PendingAction != null && player.PendingAction.Command == "MemorizeSpell")
                return (false, "You are already memorizing a spell.", skillLookup.skillId);

            // Check 6: Has not reached retention limit
            int currentMemoredCount = GetCurrentMemoredCount(player, conn);
            int retentionLimit = GetSpellRetentionLimit(player, skillLookup.skillId, conn);
            if (currentMemoredCount >= retentionLimit)
                return (false, $"You can only memorize {retentionLimit} spell(s) at once. Forget a spell first.", skillLookup.skillId);

            return (true, "", skillLookup.skillId);
        }

        private static void EnsureSkillExists(Player player, string skillId, SqliteConnection conn)
        {
            if (player == null || string.IsNullOrWhiteSpace(skillId) || conn == null)
                return;

            try
            {
                // Check if skill record exists
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM PlayerSkills WHERE PlayerID = @pid AND SkillID = @skill;";
                cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                cmd.Parameters.AddWithValue("@skill", skillId);

                var result = cmd.ExecuteScalar();
                if (result != null && int.TryParse(result.ToString(), out int count) && count > 0)
                    return; // Skill already exists

                // Inject skill at level 0
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = "INSERT INTO PlayerSkills (PlayerID, SkillID, SkillLevel) VALUES (@pid, @skill, 0);";
                insertCmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                insertCmd.Parameters.AddWithValue("@skill", skillId);
                insertCmd.ExecuteNonQuery();

                Console.WriteLine($"[MEMORIZATION] Injected skill {skillId} at level 0 for player {player.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MEMORIZATION] EnsureSkillExists failed: {ex.Message}");
            }
        }

        private static string GetSkillName(string skillId)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
            Path.Combine(baseDir, "Data", "SkillTemplates", $"{skillId}.json"),
            Path.Combine(baseDir, "..", "Data", "SkillTemplates", $"{skillId}.json"),
            Path.Combine(baseDir, "..", "..", "Data", "SkillTemplates", $"{skillId}.json")
        };

                string? found = candidates.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(found))
                    return skillId;

                var json = File.ReadAllText(found);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var nameProp))
                    return nameProp.GetString() ?? skillId;
            }
            catch { }

            return skillId;
        }

        /// <summary>
        /// Handle memorization completion (called when IsMemorizing timed effect expires).
        /// </summary>
        public static Message CompleteMemorization(Player player, SqliteConnection conn)
        {
            if (player?.PendingAction == null || player.PendingAction.Command != "MemorizeSpell")
                return null;

            try
            {
                var data = player.PendingAction.Data as Dictionary<string, object>;
                if (data == null)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Memorization data lost.");
                }

                string spellId = data["SpellId"].ToString();
                int roomId = Convert.ToInt32(data["RoomID"]);

                // Insert into PlayerSpellMem
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        using var cmd = wConn.CreateCommand();
                        cmd.Transaction = wTx;
                        cmd.CommandText = "INSERT INTO PlayerSpellMem (PlayerID, SpellId) VALUES (@pid, @spell);";
                        cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        cmd.Parameters.AddWithValue("@spell", spellId);
                        cmd.ExecuteNonQuery();
                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException)
                {
                    // Fallback: direct insert
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO PlayerSpellMem (PlayerID, SpellId) VALUES (@pid, @spell);";
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    cmd.Parameters.AddWithValue("@spell", spellId);
                    cmd.ExecuteNonQuery();
                }

                // Cleanup
                player.PendingAction = null;

                string msg = $"You have successfully memorized {spellId}!";
                MessageHandler.SendSystem(player, msg);

                var room = player.CurrentRoom;
                if (room != null)
                {
                    foreach (var p in room.Players.Where(p => p != player))
                    {
                        MessageHandler.SendSystem(p, $"{player.Name} finishes memorizing a spell.");
                    }
                }

                return MessageHandler.System(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MEMORIZATION] CompleteMemorization failed: {ex.Message}");
                player.PendingAction = null;
                return MessageHandler.System("Memorization failed due to an error.");
            }
        }

        /// <summary>
        /// Handle memorization interruption (combat or room movement).
        /// Refunds 40 stamina, cancels memorization.
        /// </summary>
        public static void InterruptMemorization(Player player, string reason, SqliteConnection conn)
        {
            if (player?.PendingAction == null || player.PendingAction.Command != "MemorizeSpell")
                return;

            // Refund 40 stamina
            player.IncreaseStamina(STAMINA_REFUND_ON_INTERRUPT, "memorization interrupt recovery");

            // Remove timed effect
            var flags = player.Flags ?? new List<string>();
            flags.RemoveAll(f => f.StartsWith(MEMORIZING_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase));
            player.Flags = flags;

            // Cleanup
            player.PendingAction = null;

            string msg = $"Memorization interrupted! {reason} ({STAMINA_REFUND_ON_INTERRUPT} stamina refunded)";
            MessageHandler.SendSystem(player, msg);

            // Persist
            try
            {
                PlayerPersistence.SavePlayer(conn, player);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Forget a memorized spell (remove from PlayerSpellMem).
        /// </summary>
        public static Message ForgetSpell(Player player, string spellId, SqliteConnection conn)
        {
            if (player == null || string.IsNullOrWhiteSpace(spellId))
                return MessageHandler.System("Invalid spell.");

            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = "DELETE FROM PlayerSpellMem WHERE PlayerID = @pid AND SpellId = @spell LIMIT 1;";
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    cmd.Parameters.AddWithValue("@spell", spellId);
                    cmd.ExecuteNonQuery();
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
                // Fallback
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM PlayerSpellMem WHERE PlayerID = @pid AND SpellId = @spell LIMIT 1;";
                cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                cmd.Parameters.AddWithValue("@spell", spellId);
                cmd.ExecuteNonQuery();
            }

            return MessageHandler.System($"You have forgotten {spellId}.");
        }

        public static Message GetMemoredSpells(Player player, SqliteConnection conn)
        {
            if (player == null || conn == null)
                return MessageHandler.System("Invalid operation.");

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT SpellId FROM PlayerSpellMem WHERE PlayerID = @pid ORDER BY SpellId;";
                cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);

                var spells = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    spells.Add(reader.GetString(0));
                }

                if (spells.Count == 0)
                    return MessageHandler.System("You have not memorized any spells.");

                return MessageHandler.System($"Memorized spells: {string.Join(", ", spells)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MEMORIZATION] GetMemoredSpells failed: {ex.Message}");
                return MessageHandler.System("Failed to retrieve memorized spells.");
            }
        }

        /// <summary>
        /// Get current count of memorized spells for player.
        /// </summary>
        public static int GetCurrentMemoredCount(Player player, SqliteConnection conn)
        {
            if (player?.DbPlayerID <= 0 || conn == null)
                return 0;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM PlayerSpellMem WHERE PlayerID = @pid;";
                cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                var result = cmd.ExecuteScalar();
                if (result != null && int.TryParse(result.ToString(), out int count))
                    return count;
            }
            catch { /* best-effort */ }

            return 0;
        }

        /// <summary>
        /// Get spell retention limit from skill tier.
        /// </summary>
        public static int GetSpellRetentionLimit(Player player, string skillId, SqliteConnection conn)
        {
            if (player == null || string.IsNullOrWhiteSpace(skillId))
                return 0;

            int skillLevel = player.GetSkillLevel(skillId, conn);
            if (skillLevel <= 0)
                return 0;

            return GetRetentionFromSkillFile(skillId, skillLevel);
        }

        /// <summary>
        /// Load spell_retention value from skill JSON file.
        /// </summary>
        private static int GetRetentionFromSkillFile(string skillId, int level)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "Data", "SkillTemplates", $"{skillId}.json"),
                    Path.Combine(baseDir, "..", "Data", "SkillTemplates", $"{skillId}.json"),
                    Path.Combine(baseDir, "..", "..", "Data", "SkillTemplates", $"{skillId}.json")
                };

                string? found = null;
                foreach (var p in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    {
                        found = p;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(found))
                    return 0;

                var json = File.ReadAllText(found);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("skill_tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tier in tiers.EnumerateArray())
                    {
                        if (tier.TryGetProperty("level", out var tierLevel) && tierLevel.GetInt32() == level)
                        {
                            if (tier.TryGetProperty("spell_retention", out var retention))
                                return retention.GetInt32();
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            return 0;
        }

        /// <summary>
        /// Find spell source (page or book) in player inventory.
        /// Returns the item if found with matching spell flag.
        /// </summary>

        private static Item? FindSpellSourceInInventory(Player player, string spellId, SqliteConnection conn)
        {
            if (player?.Inventory == null)
                return null;

            // Check 1: Direct pages in inventory with matching spell flag
            var directMatch = player.Inventory.FirstOrDefault(i =>
                i != null &&
                string.Equals(i.ItemCategory, "Page", StringComparison.OrdinalIgnoreCase) &&
                i.Flags != null &&
                i.Flags.Any(f => string.Equals(f, spellId, StringComparison.OrdinalIgnoreCase)));

            if (directMatch != null)
                return directMatch;

            // Check 2: Search inside containers (books, bags, etc.) for pages with spell flag
            try
            {
                var containers = player.Inventory.Where(i =>
                    i != null &&
                    (string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase) ||
                     (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Container", StringComparison.OrdinalIgnoreCase))))).ToList();

                foreach (var container in containers)
                {
                    if (container == null || container.ItemID <= 0)
                        continue;

                    try
                    {
                        // Load contents of this container from DB
                        var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();

                        // Look for a page with matching spell flag
                        var foundPage = contents.FirstOrDefault(c =>
                            c != null &&
                            string.Equals(c.ItemCategory, "Page", StringComparison.OrdinalIgnoreCase) &&
                            c.Flags != null &&
                            c.Flags.Any(f => string.Equals(f, spellId, StringComparison.OrdinalIgnoreCase)));

                        if (foundPage != null)
                            return foundPage;
                    }
                    catch { /* best-effort */ }
                }
            }
            catch { /* best-effort */ }

            return null;
        }

        /// <summary>
        /// Get skill ID and minimum level required for spell from spell template.
        /// </summary>
        private static (string skillId, int minLevel) GetSkillForSpell(string spellId, World.World world)
        {
            try
            {
                // Construct filename: spell_[spellid].json
                string fileName = $"spell_{spellId.ToLowerInvariant()}.json";

                // Find the project root by walking up from BaseDirectory until we find the Data folder
                var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
                DirectoryInfo? dataDir = null;

                while (currentDir != null && currentDir.Parent != null)
                {
                    var candidateData = Path.Combine(currentDir.FullName, "Data", "ItemTemplates", "SpellTemplates");
                    if (Directory.Exists(candidateData))
                    {
                        dataDir = new DirectoryInfo(candidateData);
                        Console.WriteLine($"[MEMORIZE] Found SpellTemplates at: {dataDir.FullName}");
                        break;
                    }
                    currentDir = currentDir.Parent;
                }

                if (dataDir == null || !dataDir.Exists)
                {
                    Console.WriteLine($"[MEMORIZE] SpellTemplates directory not found");
                    return ("", 0);
                }

                // Search recursively for the spell file
                var files = Directory.GetFiles(dataDir.FullName, fileName, SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Console.WriteLine($"[MEMORIZE] Spell file not found: {fileName}");
                    return ("", 0);
                }

                string spellFile = files[0];
                Console.WriteLine($"[MEMORIZE] FOUND: {spellFile}");

                var json = File.ReadAllText(spellFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string skillId = "";
                int minLevel = 1;

                if (root.TryGetProperty("requiredSkill", out var skillProp))
                {
                    skillId = skillProp.GetString() ?? "";
                    Console.WriteLine($"[MEMORIZE] Found requiredSkill: '{skillId}'");
                }

                if (root.TryGetProperty("minSkillLevel", out var levelProp))
                {
                    minLevel = levelProp.GetInt32();
                    Console.WriteLine($"[MEMORIZE] Found minSkillLevel: {minLevel}");
                }

                return (skillId, minLevel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MEMORIZE] GetSkillForSpell exception: {ex.Message}");
                return ("", 0);
            }
        }
    }
}