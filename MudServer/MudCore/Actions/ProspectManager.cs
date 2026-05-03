
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudUtils;

namespace MudCore.Actions
{
    public static class ProspectManager
    {
        // Keyed by player's PlayerID (fallback to Name)
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

        private static string KeyFor(Player p) => string.IsNullOrWhiteSpace(p?.PlayerID) ? (p?.Name ?? Guid.NewGuid().ToString()) : p.PlayerID;

        public static Message? StartProspecting(Player player, string toolName, World.World world, SqliteConnection db)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.CurrentRoom == null) return MessageHandler.System("You are not in a location where you can prospect.");

            if (string.IsNullOrWhiteSpace(toolName))
                return MessageHandler.System("Usage: prospect with <tool>");

            // Prevent overlapping prospect/dig actions (single pending per player)
            var key = KeyFor(player);
            if (_pending.ContainsKey(key))
                return MessageHandler.System("You are already performing a similar action.");

            // Validate held tool
            bool holding = false;
            try
            {
                var token = toolName.Trim().ToLowerInvariant();
                foreach (var it in player.Inventory ?? Enumerable.Empty<Item>())
                {
                    if (it == null) continue;
                    bool isHeld = false;
                    try
                    {
                        if (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase)))
                            isHeld = true;
                        else if (!string.IsNullOrWhiteSpace(it.FlagsJson) && it.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0)
                            isHeld = true;
                    }
                    catch { }

                    if (!isHeld) continue;
                    var name = it.Name ?? "";
                    var tid = it.TemplateID ?? "";
                    if ((!string.IsNullOrWhiteSpace(name) && name.ToLowerInvariant().Contains(token))
                        || (!string.IsNullOrWhiteSpace(tid) && tid.ToLowerInvariant().Contains(token)))
                    {
                        holding = true;
                        break;
                    }
                }
            }
            catch { holding = false; }

            if (!holding)
                return MessageHandler.System($"You must be holding a {toolName} to prospect with it.");

            // Tool-terrain validation:
            // Rock room → pick required; shovel cannot prospect through solid rock
            // Non-rock room → shovel required; a pick is unnecessary in soft ground
            var toolTok = toolName.Trim().ToLowerInvariant();
            bool prospectRoomIsRock = player.CurrentRoom.Flags != null && player.CurrentRoom.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && string.Equals(f, "Rock", StringComparison.OrdinalIgnoreCase));
            if (prospectRoomIsRock && toolTok.Contains("shovel"))
                return MessageHandler.System("You cannot prospect through solid rock with a shovel. You need a pick.");
            if (!prospectRoomIsRock && toolTok.Contains("pick"))
                return MessageHandler.System("This area does not look particularly rocky; a pickaxe is unnecessary here.");

            // Register pending and start async 15s task
            player.PendingAction = new PendingAction { Command = "Prospect", Step = "InProgress", Data = new { StartedAtUtc = DateTime.UtcNow, RoomID = player.CurrentRoom.RoomID, Tool = toolName.Trim() } };

            var cts = new CancellationTokenSource();
            _pending[key] = cts;

            _ = RunProspectTask(player, toolName.Trim(), world, db, cts.Token);

            return MessageHandler.System("You begin prospecting... (this will take 15 seconds)");
        }

        public static void CancelPending(Player player)
        {
            if (player == null) return;
            var key = KeyFor(player);
            if (_pending.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }

            try { if (player.PendingAction != null && string.Equals(player.PendingAction.Command, "Prospect", StringComparison.OrdinalIgnoreCase)) player.PendingAction = null; } catch { }
        }

        private static async Task RunProspectTask(Player player, string toolName, World.World world, SqliteConnection db, CancellationToken ct)
        {
            var startRoom = player.CurrentRoom;
            if (startRoom == null)
            {
                CancelPending(player);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                try { MessageHandler.SendSystem(player, "Prospecting interrupted."); } catch { }
                CancelPending(player);
                return;
            }
            catch
            {
                CancelPending(player);
                return;
            }

            try
            {
                // Verify player still in same room and not in combat
                if (player.CurrentRoom == null || player.CurrentRoom.RoomID != startRoom.RoomID)
                {
                    MessageHandler.SendSystem(player, "Prospecting interrupted.");
                    CancelPending(player);
                    return;
                }

                try
                {
                    if (MudCore.Actions.CombatManager.IsEntityInCombat(player))
                    {
                        MessageHandler.SendSystem(player, "Prospecting interrupted by combat.");
                        CancelPending(player);
                        return;
                    }
                }
                catch { /* best-effort */ }

                // Enumerate buried items — both StartsBuried# (natural deposits) and Buried# (player-buried)
                var buried = startRoom.Inventory?.Where(i => i != null && i.Flags != null && i.Flags.Any(f =>
                    !string.IsNullOrWhiteSpace(f) &&
                    (f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase) ||
                     f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase)))).ToList()
                             ?? new System.Collections.Generic.List<Item>();

                if (buried.Count == 0)
                {
                    MessageHandler.SendSystem(player, "Your effort uncovers no traces of anything here.");
                    CancelPending(player);
                    return;
                }

                // Single roll for this prospect attempt
                var rng = System.Random.Shared;
                int roll = rng.Next(0, 100);

                // Detected display names (deduplicated) — report one display name per different item type.
                var detectedNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var it in buried)
                {
                    try
                    {
                        // Match StartsBuried# first (more specific), then Buried#
                        var flag = it.Flags.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) &&
                            f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase));
                        bool isStartsBuried = flag != null;
                        flag ??= it.Flags.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) &&
                            f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase));
                        if (flag == null) continue;

                        var numText = isStartsBuried
                            ? flag.Substring("StartsBuried".Length).Trim()
                            : flag.Substring("Buried".Length).Trim();
                        if (!int.TryParse(numText, out int n)) n = 0;

                        // StartsBuried#: natural deposit, N is direct detect % (original formula: 2 * N)
                        // Buried#:       player-buried,  N is difficulty so detect chance = 100 - N
                        int chance = isStartsBuried
                            ? Math.Clamp(2 * n, 0, 100)
                            : Math.Clamp(100 - n, 0, 100);
                        if (roll < chance)
                        {
                            // Resolve a friendly display name (prefer template display)
                            string display = it.Name ?? it.TemplateID ?? "something";
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(it.TemplateID) && world?.ItemFactory != null)
                                {
                                    if (world.ItemFactory.TryGetTemplateDisplayName(it.TemplateID, out var d))
                                        display = d;
                                }
                            }
                            catch { /* best-effort */ }

                            detectedNames.Add(display);
                        }
                    }
                    catch { /* skip problematic item */ }
                }

                if (detectedNames.Count == 0)
                {
                    MessageHandler.SendSystem(player, "Your effort uncovers no traces of anything here.");
                    CancelPending(player);
                    return;
                }

                // Build friendly joined string (unique types only)
                var names = detectedNames.ToList();
                string joined;
                if (names.Count == 1) joined = names[0];
                else if (names.Count == 2) joined = $"{names[0]} and {names[1]}";
                else joined = string.Join(", ", names.Take(names.Count - 1)) + $" and {names.Last()}";

                MessageHandler.SendSystem(player, $"You discover traces of {joined} here!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProspectManager] Error finishing prospect: {ex}");
                try { MessageHandler.SendSystem(player, "Prospecting failed due to a server error."); } catch { }
            }
            finally
            {
                CancelPending(player);
            }
        }
    }
}