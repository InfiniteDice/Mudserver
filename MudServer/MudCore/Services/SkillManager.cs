using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudNetworking;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudCore.Services
{
    // ─── Data-model records (deserialized from JSON files) ───────────────────────

    public sealed class SkillTierDef
    {
        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
    }

    public sealed class SkillTemplateDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("skill_type")]
        public string SkillType { get; set; } = "";

        [JsonPropertyName("skill_tiers")]
        public List<SkillTierDef> SkillTiers { get; set; } = new();
    }

    public sealed class SkillTypeConfig
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("max_level")]
        public int MaxLevel { get; set; }

        [JsonPropertyName("cost_at_level")]
        public List<int> CostAtLevel { get; set; } = new();
    }

    // ─── DTOs sent to the client ─────────────────────────────────────────────────

    public sealed class SkillRow
    {
        [JsonPropertyName("skillId")]
        public string SkillId { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("skillType")]
        public string SkillType { get; set; } = "";

        [JsonPropertyName("currentLevel")]
        public int CurrentLevel { get; set; }

        [JsonPropertyName("maxLevel")]
        public int MaxLevel { get; set; }

        [JsonPropertyName("currentTitle")]
        public string CurrentTitle { get; set; } = "";

        // Cost to purchase the NEXT tier (0 if already at maxLevel)
        [JsonPropertyName("nextCost")]
        public int NextCost { get; set; }

        [JsonPropertyName("showTitle")]
        public bool ShowTitle { get; set; }

        [JsonPropertyName("isPurchased")]
        public bool IsPurchased { get; set; }
    }

    public sealed class SkillListPayload
    {
        [JsonPropertyName("skillPointsAvailable")]
        public int SkillPointsAvailable { get; set; }

        [JsonPropertyName("nextLevelSkillPoints")]
        public int NextLevelSkillPoints { get; set; }

        [JsonPropertyName("skills")]
        public List<SkillRow> Skills { get; set; } = new();
    }

    // ─── Save-command DTOs (deserialized from client JSON payload) ───────────────

    public sealed class SkillPurchaseRequest
    {
        [JsonPropertyName("skillId")]
        public string SkillId { get; set; } = "";

        // The level the player is buying INTO (must equal currentLevel + 1)
        [JsonPropertyName("buyingToLevel")]
        public int BuyingToLevel { get; set; }

        [JsonPropertyName("cost")]
        public int Cost { get; set; }
    }

    public sealed class SkillSaveRequest
    {
        [JsonPropertyName("buys")]
        public List<SkillPurchaseRequest> Buys { get; set; } = new();

        // null means no change to ShowTitle selection
        [JsonPropertyName("showTitle")]
        public ShowTitleRequest? ShowTitle { get; set; }
    }

    public sealed class ShowTitleRequest
    {
        [JsonPropertyName("skillId")]
        public string SkillId { get; set; } = "";

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    // ─── Manager ─────────────────────────────────────────────────────────────────

    public static class SkillManager
    {
        // Loaded skill templates, keyed by id (case-insensitive)
        private static readonly Dictionary<string, SkillTemplateDef> _templates =
            new(StringComparer.OrdinalIgnoreCase);

        // Skill type cost configs, keyed by type name (Mundane / Trade / Mystical)
        private static readonly Dictionary<string, SkillTypeConfig> _typeCosts =
            new(StringComparer.OrdinalIgnoreCase);

        // Ordered list of type names for display grouping (Mundane first, then Trade, then Mystical)
        private static readonly List<string> _typeOrder = new() { "Mundane", "Trade", "Mystical" };

        private static bool _initialized = false;
        private static readonly object _lock = new();

        // ── Initialize ───────────────────────────────────────────────────────────

        /// <summary>
        /// Loads all skill templates from Data/SkillTemplates/** and skill type costs from
        /// player_levels.json. Runs the ShowTitle DB migration. Safe to call multiple times.
        /// </summary>
        public static void Initialize(
            string skillTemplatesRelPath = "Data/SkillTemplates",
            string playerLevelsRelPath = "Data/config/player_levels.json")
        {
            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    LoadSkillTypeConfigs(playerLevelsRelPath);
                    LoadSkillTemplates(skillTemplatesRelPath);
                    Console.WriteLine($"[SkillManager] Loaded {_templates.Count} skill template(s) " +
                                      $"across {_typeCosts.Count} type(s).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SkillManager] Initialization error: {ex.Message}");
                }
                finally
                {
                    _initialized = true;
                }
            }
        }

        // ── DB Migration ─────────────────────────────────────────────────────────

        /// <summary>
        /// Adds ShowTitle column to PlayerSkills if it does not yet exist.
        /// Must be called after DbWriteQueue is started.
        /// </summary>
        public static void RunMigration()
        {
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    // Check whether column already exists
                    bool hasColumn = false;
                    using (var pragma = wConn.CreateCommand())
                    {
                        pragma.Transaction = wTx;
                        pragma.CommandText = "PRAGMA table_info('PlayerSkills');";
                        using var rdr = pragma.ExecuteReader();
                        while (rdr.Read())
                        {
                            var colName = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                            if (string.Equals(colName, "ShowTitle", StringComparison.OrdinalIgnoreCase))
                            {
                                hasColumn = true;
                                break;
                            }
                        }
                    }

                    if (!hasColumn)
                    {
                        using var alter = wConn.CreateCommand();
                        alter.Transaction = wTx;
                        alter.CommandText = "ALTER TABLE PlayerSkills ADD COLUMN ShowTitle INTEGER NOT NULL DEFAULT 0;";
                        alter.ExecuteNonQuery();
                        Console.WriteLine("[SkillManager] Migration: added ShowTitle column to PlayerSkills.");
                    }
                    else
                    {
                        Console.WriteLine("[SkillManager] Migration: ShowTitle column already present.");
                    }

                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] RunMigration failed: {ex.Message}");
            }
        }

        // ── Public command handlers ───────────────────────────────────────────────

        /// <summary>
        /// Builds the skilllist message payload for the player and returns it.
        /// </summary>
        public static Message HandleSkillListCommand(Player player, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!_initialized) Initialize();

            try
            {
                var payload = BuildSkillListPayload(player, conn);
                return MessageHandler.SkillList(payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] HandleSkillListCommand failed for {player?.Name}: {ex.Message}");
                return MessageHandler.System("Failed to load skills due to a server error.");
            }
        }

        /// <summary>
        /// Parses and validates the skillsave command payload, then commits purchases
        /// and ShowTitle changes.
        /// </summary>
        public static Message HandleSkillSaveCommand(Player player, string rawCommand, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.DbPlayerID <= 0) return MessageHandler.System("Your account is not saved yet.");
            if (!_initialized) Initialize();

            // rawCommand format: "skillsave <json>"
            var jsonStart = rawCommand.IndexOf(' ');
            if (jsonStart < 0 || jsonStart >= rawCommand.Length - 1)
                return MessageHandler.System("Invalid skillsave format.");

            var json = rawCommand.Substring(jsonStart + 1).Trim();

            SkillSaveRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<SkillSaveRequest>(json);
            }
            catch
            {
                return MessageHandler.System("Failed to parse skill save data.");
            }

            if (request == null)
                return MessageHandler.System("Empty skill save request.");

            return CommitSkillSave(player, request, conn);
        }

        /// <summary>
        /// Returns the shown title string (e.g. "[Grandmaster Smith]") for the player,
        /// or null if none is set.
        /// </summary>
        public static string? GetShownTitle(Player player, SqliteConnection conn)
        {
            if (player == null || conn == null || player.DbPlayerID <= 0) return null;

            try
            {
                string? title = null;

                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = @"
                        SELECT SkillID, SkillLevel
                        FROM PlayerSkills
                        WHERE PlayerID = @pid AND ShowTitle = 1
                        LIMIT 1;";
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);

                    using var rdr = cmd.ExecuteReader();
                    if (rdr.Read())
                    {
                        var skillId = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                        var level = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);

                        if (!string.IsNullOrWhiteSpace(skillId) && level > 0
                            && _templates.TryGetValue(skillId, out var tmpl))
                        {
                            var tier = tmpl.SkillTiers.FirstOrDefault(t => t.Level == level);
                            if (tier != null && !string.IsNullOrWhiteSpace(tier.Title))
                                title = $"[{tier.Title}]";
                        }
                    }
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(3));

                return title;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] GetShownTitle failed for {player?.Name}: {ex.Message}");
                return null;
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

        private static SkillListPayload BuildSkillListPayload(Player player, SqliteConnection conn)
        {
            // Fetch next level skill points from AdvancementManager
            int nextLevelSp = 0;
            try
            {
                var nextEntry = AdvancementManager.GetLevelEntry(player.Level + 1);
                nextLevelSp = nextEntry?.skill_points ?? 0;
            }
            catch { /* best-effort */ }

            // Load all PlayerSkills rows for this player in one query
            var dbLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var dbShowTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = @"
                        SELECT SkillID, SkillLevel, ShowTitle
                        FROM PlayerSkills
                        WHERE PlayerID = @pid;";
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);

                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var sid = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                        var lvl = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                        var showTitleVal = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);

                        if (!string.IsNullOrWhiteSpace(sid))
                        {
                            dbLevels[sid] = lvl;
                            if (showTitleVal == 1)
                                dbShowTitles.Add(sid);
                        }
                    }
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] BuildSkillListPayload DB read failed: {ex.Message}");
            }

            // Build ordered skill rows: group by type order, then alphabetical within group
            var rows = new List<SkillRow>();

            foreach (var typeName in _typeOrder)
            {
                var group = _templates.Values
                    .Where(t => string.Equals(t.SkillType, typeName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var tmpl in group)
                {
                    _typeCosts.TryGetValue(tmpl.SkillType, out var cfg);
                    int maxLevel = cfg?.MaxLevel ?? tmpl.SkillTiers.Count;
                    int currentLevel = dbLevels.TryGetValue(tmpl.Id, out var dl) ? dl : 0;
                    bool showTitle = dbShowTitles.Contains(tmpl.Id);

                    string currentTitle = "";
                    if (currentLevel > 0)
                    {
                        var tier = tmpl.SkillTiers.FirstOrDefault(t => t.Level == currentLevel);
                        currentTitle = tier?.Title ?? "";
                    }

                    int nextCost = 0;
                    if (currentLevel < maxLevel && cfg != null)
                    {
                        // cost_at_level is 0-indexed: index 0 = cost to buy level 1
                        int idx = currentLevel; // buying FROM currentLevel TO currentLevel+1
                        if (idx < cfg.CostAtLevel.Count)
                            nextCost = cfg.CostAtLevel[idx];
                    }

                    rows.Add(new SkillRow
                    {
                        SkillId = tmpl.Id,
                        Name = tmpl.Name,
                        SkillType = tmpl.SkillType,
                        CurrentLevel = currentLevel,
                        MaxLevel = maxLevel,
                        CurrentTitle = currentTitle,
                        NextCost = nextCost,
                        ShowTitle = showTitle,
                        IsPurchased = currentLevel > 0
                    });
                }
            }

            return new SkillListPayload
            {
                SkillPointsAvailable = player.SkillPoints,
                NextLevelSkillPoints = nextLevelSp,
                Skills = rows
            };
        }

        private static Message CommitSkillSave(Player player, SkillSaveRequest request, SqliteConnection conn)
        {
            if (!_initialized) Initialize();

            var buys = request.Buys ?? new List<SkillPurchaseRequest>();

            // ── Validation pass ───────────────────────────────────────────────────

            // Load current levels fresh from DB to prevent client-side spoofing
            var currentLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = "SELECT SkillID, SkillLevel FROM PlayerSkills WHERE PlayerID = @pid;";
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);

                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        var sid = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                        var lvl = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                        if (!string.IsNullOrWhiteSpace(sid))
                            currentLevels[sid] = lvl;
                    }
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] CommitSkillSave DB pre-read failed: {ex.Message}");
                return MessageHandler.System("Failed to validate skill save. Please try again.");
            }

            // Simulate purchases to validate totals
            int totalCost = 0;
            bool anyMystical = false;
            var simulatedLevels = new Dictionary<string, int>(currentLevels, StringComparer.OrdinalIgnoreCase);

            foreach (var buy in buys)
            {
                if (!_templates.TryGetValue(buy.SkillId, out var tmpl))
                    return MessageHandler.System($"Unknown skill: '{buy.SkillId}'.");

                _typeCosts.TryGetValue(tmpl.SkillType, out var cfg);
                int maxLevel = cfg?.MaxLevel ?? tmpl.SkillTiers.Count;

                simulatedLevels.TryGetValue(buy.SkillId, out int simCurrent);

                // Must be buying exactly one tier above current (no skipping)
                if (buy.BuyingToLevel != simCurrent + 1)
                    return MessageHandler.System($"Invalid tier purchase for '{tmpl.Name}'. You must buy tiers in order.");

                if (buy.BuyingToLevel > maxLevel)
                    return MessageHandler.System($"'{tmpl.Name}' is already at maximum level.");

                // Re-derive cost server-side; ignore client-supplied cost
                int expectedCost = 0;
                if (cfg != null && simCurrent < cfg.CostAtLevel.Count)
                    expectedCost = cfg.CostAtLevel[simCurrent];

                totalCost += expectedCost;
                simulatedLevels[buy.SkillId] = buy.BuyingToLevel;

                if (string.Equals(tmpl.SkillType, "Mystical", StringComparison.OrdinalIgnoreCase))
                    anyMystical = true;
            }

            if (totalCost > player.SkillPoints)
                return MessageHandler.System($"Not enough skill points. Need {totalCost}, have {player.SkillPoints}.");

            // Validate ShowTitle request if present
            if (request.ShowTitle != null)
            {
                var st = request.ShowTitle;
                if (!_templates.TryGetValue(st.SkillId, out var stTmpl))
                    return MessageHandler.System($"Unknown skill for Show Title: '{st.SkillId}'.");

                int effectiveLevel = simulatedLevels.TryGetValue(st.SkillId, out var sl) ? sl : 0;
                if (effectiveLevel <= 0)
                    return MessageHandler.System($"You must own at least level 1 of '{stTmpl.Name}' to show its title.");

                if (stTmpl.SkillTiers.All(t => t.Level != st.Level))
                    return MessageHandler.System($"Invalid tier level for Show Title on '{stTmpl.Name}'.");

                if (st.Level > effectiveLevel)
                    return MessageHandler.System($"You have not yet reached tier {st.Level} of '{stTmpl.Name}'.");
            }

            // ── Commit phase ──────────────────────────────────────────────────────
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    // 1) Apply each purchase (upsert)
                    foreach (var buy in buys)
                    {
                        _typeCosts.TryGetValue(_templates[buy.SkillId].SkillType, out var cfg2);
                        int maxLevel2 = cfg2?.MaxLevel ?? _templates[buy.SkillId].SkillTiers.Count;

                        using var upsert = wConn.CreateCommand();
                        upsert.Transaction = wTx;
                        upsert.CommandText = @"
                            INSERT INTO PlayerSkills (PlayerID, SkillID, SkillLevel, SkillHasLevels, ShowTitle)
                            VALUES (@pid, @sid, @lvl, @has, 0)
                            ON CONFLICT(PlayerID, SkillID)
                            DO UPDATE SET SkillLevel = excluded.SkillLevel;";
                        upsert.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        upsert.Parameters.AddWithValue("@sid", buy.SkillId);
                        upsert.Parameters.AddWithValue("@lvl", buy.BuyingToLevel);
                        upsert.Parameters.AddWithValue("@has", maxLevel2 > 1 ? 1 : 0);
                        upsert.ExecuteNonQuery();
                    }

                    // 2) Update ShowTitle flags (clear all, then set chosen one)
                    if (request.ShowTitle != null)
                    {
                        using var clearSt = wConn.CreateCommand();
                        clearSt.Transaction = wTx;
                        clearSt.CommandText = "UPDATE PlayerSkills SET ShowTitle = 0 WHERE PlayerID = @pid;";
                        clearSt.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        clearSt.ExecuteNonQuery();

                        using var setSt = wConn.CreateCommand();
                        setSt.Transaction = wTx;
                        setSt.CommandText = @"
                            UPDATE PlayerSkills SET ShowTitle = 1
                            WHERE PlayerID = @pid AND SkillID = @sid;";
                        setSt.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        setSt.Parameters.AddWithValue("@sid", request.ShowTitle.SkillId);
                        setSt.ExecuteNonQuery();
                    }

                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] CommitSkillSave write failed for {player?.Name}: {ex.Message}");
                return MessageHandler.System("Failed to save skills due to a server error.");
            }

            // 3) Deduct skill points in memory and persist player
            player.SkillPoints -= totalCost;

            try
            {
                PlayerPersistence.SavePlayer(conn, player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] SavePlayer after skill purchase failed: {ex.Message}");
            }

            // 4) Refresh MaxPower if any Mystical skill was purchased
            if (anyMystical)
            {
                try
                {
                    PlayerPersistence.RefreshMaxPowerFromSkills(player, conn);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SkillManager] RefreshMaxPowerFromSkills failed: {ex.Message}");
                }
            }

            int bought = buys.Count;
            string summary = bought == 0
                ? "No skills purchased."
                : $"{bought} skill tier{(bought == 1 ? "" : "s")} purchased. {totalCost} skill point{(totalCost == 1 ? "" : "s")} spent.";

            Console.WriteLine($"[SkillManager] {player.Name} saved skills: {summary} SP remaining={player.SkillPoints}");
            return MessageHandler.System(summary);
        }

        // ── File loading helpers ──────────────────────────────────────────────────

        private static void LoadSkillTypeConfigs(string relativePath)
        {
            var path = ResolvePath(relativePath);
            if (path == null)
            {
                Console.WriteLine($"[SkillManager] player_levels.json not found at relative path: {relativePath}");
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("skill_type", out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine("[SkillManager] skill_type array not found in player_levels.json");
                    return;
                }

                foreach (var el in arr.EnumerateArray())
                {
                    try
                    {
                        var cfg = JsonSerializer.Deserialize<SkillTypeConfig>(el.GetRawText());
                        if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Name))
                            _typeCosts[cfg.Name] = cfg;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SkillManager] Failed to parse skill_type entry: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkillManager] LoadSkillTypeConfigs failed: {ex.Message}");
            }
        }

        private static void LoadSkillTemplates(string relativePath)
        {
            var dir = ResolvePath(relativePath, isDirectory: true);
            if (dir == null)
            {
                Console.WriteLine($"[SkillManager] SkillTemplates directory not found at relative path: {relativePath}");
                return;
            }

            var files = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories);
            int loaded = 0;

            foreach (var file in files)
            {
                try
                {
                    var rawJson = File.ReadAllText(file);

                    // Skill template files currently lack an outer braces wrapper in some cases;
                    // try parsing as-is first, then wrap if needed.
                    string jsonToParse = rawJson.TrimStart();
                    if (!jsonToParse.StartsWith("{"))
                        jsonToParse = "{" + jsonToParse + "}";

                    var tmpl = JsonSerializer.Deserialize<SkillTemplateDef>(jsonToParse,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.Id))
                    {
                        Console.WriteLine($"[SkillManager] Skipping '{Path.GetFileName(file)}': missing id.");
                        continue;
                    }

                    _templates[tmpl.Id] = tmpl;
                    loaded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SkillManager] Failed to load '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            Console.WriteLine($"[SkillManager] Loaded {loaded} skill template(s) from {dir}");
        }

        /// <summary>
        /// Resolves a relative path against the runtime base directory, walking upward to the
        /// .csproj project root if needed — same pattern used by AdvancementManager.
        /// </summary>
        private static string? ResolvePath(string relativePath, bool isDirectory = false)
        {
            var baseDir = AppContext.BaseDirectory;
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

            // 1) Direct output path
            var outputPath = Path.Combine(baseDir, normalized);
            if (isDirectory ? Directory.Exists(outputPath) : File.Exists(outputPath))
                return outputPath;

            // 2) Walk upward to find .csproj project root
            var dir = new DirectoryInfo(baseDir);
            while (dir != null && dir.Exists)
            {
                if (dir.GetFiles("*.csproj").Any())
                {
                    var candidate = Path.Combine(dir.FullName, normalized);
                    if (isDirectory ? Directory.Exists(candidate) : File.Exists(candidate))
                        return candidate;
                    break;
                }
                dir = dir.Parent;
            }

            // 3) Sibling path (handles some deployment layouts)
            try
            {
                var sibling = Path.GetFullPath(Path.Combine(baseDir, "..", "..", normalized));
                if (isDirectory ? Directory.Exists(sibling) : File.Exists(sibling))
                    return sibling;
            }
            catch { /* best-effort */ }

            return null;
        }
    }
}