
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudPersistence;
using MudNetworking;

namespace MudCore.Services
{
    public sealed class LevelEntry
    {
        public int level { get; set; }
        public long xp_to_next { get; set; }
        public long cumulative_xp { get; set; }
        public int skill_points { get; set; }
    }

    public static class AdvancementManager
    {
        private static readonly List<LevelEntry> _levels = new List<LevelEntry>();
        private static bool _initialized = false;
        private static readonly object _lock = new object();

        private static bool showDiagnostics = false;

        // Call once at startup. Default path relative to AppContext.BaseDirectory.

        public static void Initialize(string relativePath = "Data/config/player_levels.json")
        {
            lock (_lock)
            {
                if (_initialized) return;
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    // Try runtime output path first
                    var outputPath = Path.Combine(baseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    string pathToUse = null;

                    if (File.Exists(outputPath))
                    {
                        pathToUse = outputPath;
                    }
                    else
                    {
                        // Walk upward to find the project directory (with .csproj) and try relative path from there.
                        var dir = new DirectoryInfo(baseDir);
                        DirectoryInfo? projectDir = null;

                        while (dir != null && dir.Exists)
                        {
                            if (dir.GetFiles("*.csproj").Any())
                            {
                                projectDir = dir;
                                break;
                            }
                            dir = dir.Parent;
                        }

                        if (projectDir != null)
                        {
                            var tryPath = Path.Combine(projectDir.FullName, relativePath);
                            if (File.Exists(tryPath))
                                pathToUse = tryPath;
                        }

                        // Also try nearby Data/config sibling of outputDir (handles some deployment layouts)
                        if (pathToUse == null)
                        {
                            try
                            {
                                var sibling = Path.GetFullPath(Path.Combine(baseDir, "..", "..", relativePath));
                                if (File.Exists(sibling))
                                    pathToUse = sibling;
                            }
                            catch { }
                        }
                    }

                    if (pathToUse == null)
                    {
                        Console.WriteLine($"[AdvancementManager] ERROR: player_levels.json not found at: {outputPath}");
                        _initialized = true; // avoid repeated attempts; diagnostics already logged
                        return;
                    }

                    var json = File.ReadAllText(pathToUse);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("player_levels", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    {
                        Console.WriteLine("[AdvancementManager] ERROR: player_levels array missing or invalid in JSON.");
                        _initialized = true;
                        return;
                    }

                    var parsed = new List<LevelEntry>();
                    foreach (var el in arr.EnumerateArray())
                    {
                        try
                        {
                            var entry = JsonSerializer.Deserialize<LevelEntry>(el.GetRawText());
                            if (entry != null) parsed.Add(entry);
                        }
                        catch (Exception exPer) { Console.WriteLine($"[AdvancementManager] WARNING: failed parse entry: {exPer.Message}"); }
                    }

                    // Ensure sorted by level / cumulative_xp ascending
                    _levels.Clear();
                    _levels.AddRange(parsed.OrderBy(l => l.level).ThenBy(l => l.cumulative_xp));

                    Console.WriteLine($"[AdvancementManager] Loaded {_levels.Count} player_levels from: {pathToUse}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AdvancementManager] Failed to load player_levels.json: {ex.Message}");
                }
                finally
                {
                    _initialized = true;
                }
            }
        }

        // Returns the LevelEntry that matches the highest cumulative_xp <= xp.
        // If no levels configured, returns null.
        public static LevelEntry? GetLevelForXp(long xp)
        {
            if (!_initialized) Initialize();
            if (_levels.Count == 0) return null;
            // find highest cumulative_xp <= xp
            for (int i = _levels.Count - 1; i >= 0; i--)
            {
                if (xp >= _levels[i].cumulative_xp) return _levels[i];
            }
            return _levels.First();
        }

        // Returns how much XP is required to reach the next level from current xp.
        // If already at max level returns 0.
        public static long GetXpToNext(long xp)
        {
            if (!_initialized) Initialize();
            if (_levels.Count == 0) return 0;
            var current = GetLevelForXp(xp) ?? _levels.First();
            var idx = _levels.FindIndex(l => l.level == current.level);
            if (idx < 0 || idx == _levels.Count - 1) return 0; // max
            var next = _levels[idx + 1];
            var remain = next.cumulative_xp - xp;
            return remain > 0 ? remain : 0;
        }

        // Returns the LevelEntry for a concrete level number, or null if not found.
        public static LevelEntry? GetLevelEntry(int level)
        {
            if (!_initialized) Initialize();
            return _levels.FirstOrDefault(l => l.level == level);
        }

        // - conn is used for persistence (PlayerPersistence.SavePlayer will route through writer queue).
        // - world is used for room/world context when announcing.
        // Returns a System Message intended for the actor (or null if nothing to report).

        public static Message? CheckAndNotifyLevelFromXp(Player player, SqliteConnection conn, World.World world)
        {
            if (player == null) return null;
            if (!_initialized) Initialize();

            // Diagnostic: show when called (one-line)
            try
            {
               if (showDiagnostics) Console.WriteLine($"[AdvancementManager] Check level for player='{player?.Name}' DbPlayerID={player?.DbPlayerID} XP={player?.Experience} Level={player?.Level} LoadedLevels={_levels.Count}");
            }
            catch { /* best-effort logging only */ }

            // Determine current target level from player's cumulative XP (do NOT modify player.Experience here).
            var beforeLevel = player.Level;
            var targetEntry = GetLevelForXp(player.Experience);
            int targetLevel = targetEntry?.level ?? player.Level;

            if (targetLevel <= beforeLevel)
                return null; // no change

            // Sum skill points for levels crossed (beforeLevel+1 .. targetLevel)
            int addedSkillPoints = 0;
            for (int lvl = beforeLevel + 1; lvl <= targetLevel; lvl++)
            {
                var ent = GetLevelEntry(lvl);
                if (ent != null) addedSkillPoints += ent.skill_points;
            }

            try
            {
                player.SkillPoints = checked(player.SkillPoints + addedSkillPoints);
            }
            catch (OverflowException)
            {
                player.SkillPoints = int.MaxValue;
            }

            player.Level = targetLevel;

            // Persist player via existing persistence API (uses DbWriteQueue when available)
            try
            {
                PlayerPersistence.SavePlayer(conn, player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AdvancementManager] Failed to persist player after level-up: {ex.Message}");
            }

            // Build notification text for the leveled player (returned to caller to enqueue once)
            string text = $"You are now level {player.Level}, you have earned {addedSkillPoints} skill point{(addedSkillPoints == 1 ? "" : "s")}.";

            // Notify room peers immediately (do NOT notify the leveled player here to avoid duplicate messages;
            // caller (GameLoop or other) will enqueue the returned Message for the player).
            try
            {
                var room = player.CurrentRoom;
                if (room != null)
                {
                    foreach (var p in room.Players.Where(pl => pl != player))
                    {
                        try { MessageHandler.SendSystem(p, $"{player.Name} is now level {player.Level}!"); } catch { }
                    }
                }
            }
            catch { }

            // Diagnostic: successful promotion
            try
            {
                Console.WriteLine($"[AdvancementManager] Player '{player.Name}' promoted {beforeLevel} -> {player.Level}, +{addedSkillPoints} SP (total SP={player.SkillPoints}).");
            }
            catch { /* best-effort */ }

            // Return the player-targeted system message for the caller to enqueue (single notification).
            return MessageHandler.System(text);
        }

        // Public helper to award XP programmatically (for non-combat systems).
        // Adds XP to the player, persists, then runs the level-check/notify pass.
        // Returns the message produced (level-up message when occurred) or an informational XP-gain message.
        public static Message GainXp(Player player, long xpGained, World.World world, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (xpGained <= 0) return MessageHandler.System("No XP awarded.");

            if (!_initialized) Initialize();

            // Add XP safely
            try
            {
                checked { player.Experience = (int)(player.Experience + xpGained); }
            }
            catch (OverflowException)
            {
                player.Experience = int.MaxValue;
            }

            // Persist XP change (writer queue preferred inside SavePlayer)
            try
            {
                PlayerPersistence.SavePlayer(conn, player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AdvancementManager] Failed to save player XP: {ex.Message}");
            }

            // Check for level ups and notify if applicable
            var levelMsg = CheckAndNotifyLevelFromXp(player, conn, world);
            if (levelMsg != null) return levelMsg;

            // No level up: return XP progress info
            long toNext = GetXpToNext(player.Experience);
            return MessageHandler.System($"You gained {xpGained} XP. {(toNext > 0 ? $"{toNext} XP to next level." : "You are at max level.")}");
        }
    }
}