
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudPersistence;
using MudNetworking;

namespace MudCore.Actions
{
    /// <summary>
    /// Lightweight manager for timed digging/mining actions.
    /// - Non-blocking Task-based implementation.
    /// - Cancellation on movement or combat start via public CancelPending.
    /// - Uses ItemPersistence.SaveItem for ownership transfer (SaveItem routes through writer queue).
    /// </summary>
    public static class DigManager
    {
        // Keyed by player's PlayerID (fallback to Name when PlayerID empty)
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

        private static string KeyFor(Player p) => string.IsNullOrWhiteSpace(p?.PlayerID) ? (p?.Name ?? Guid.NewGuid().ToString()) : p.PlayerID;

        public static Message? StartDigging(Player player, string action, string targetExpr, string toolName, World.World world, SqliteConnection db)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.CurrentRoom == null) return MessageHandler.System("You are not in a location where you can dig or mine.");

            if (string.IsNullOrWhiteSpace(targetExpr))
                return MessageHandler.System("Usage: dig <item> with <tool>  or  mine <item> with <tool>");

            // Use centralized pending dictionary to determine whether the player already has an in-progress action.
            var key = KeyFor(player);
            if (_pending.ContainsKey(key))
                return MessageHandler.System("You are already digging or mining.");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Normalize tokens
            var toolToken = (toolName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(toolToken))
                return MessageHandler.System($"Usage: {action} <item> with <tool>");

            // Resolve the target in the room and include buried items
            var opts = new MudUtils.ItemParserOptions
            {
                IncludeBuried = true,
                AllowStartsWith = true,
                IncludeDescriptorTokens = false,
                IncludeMaterialTokens = false
            };

            MudUtils.ItemResolutionResult pres;
            try
            {
                pres = MudUtils.ItemParser.ResolveForGet(player, targetExpr, opts);
            }
            catch
            {
                pres = new MudUtils.ItemResolutionResult { Reason = "Parser failure." };
            }

            if (pres.Winner == null)
            {
                if (pres.Candidates != null && pres.Candidates.Count > 0)
                {
                    var list = string.Join(", ", pres.Candidates.Select(i => i.Name));
                    return MessageHandler.System($"Multiple matches: {list}. Be more specific (e.g. 'dig ore with shovel').");
                }
                return MessageHandler.System($"No '{targetExpr}' found here to {action}.");
            }

            var target = pres.Winner;

            // Ensure resolved instance is a room item present in this room
            var live = room.Inventory?.FirstOrDefault(i => i != null && i.ItemID == target.ItemID)
                       ?? room.Inventory?.FirstOrDefault(i => i != null && string.Equals(i.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                       ?? null;
            if (live == null)
                return MessageHandler.System($"No '{targetExpr}' found here to {action}.");

            // Verify the item is actually buried
            bool isBuried = false;
            try
            {
                if (live.Flags != null && live.Flags.Any(f => !string.IsNullOrWhiteSpace(f) &&
                    (f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) || f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase))))
                    isBuried = true;
            }
            catch { isBuried = false; }

            if (!isBuried)
                return MessageHandler.System($"{live.Name} is already visible here.");

            // If mining, require rock context
            if (string.Equals(action, "mine", StringComparison.OrdinalIgnoreCase))
            {
                var hasRock = room.Flags != null && room.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && string.Equals(f, "Rock", StringComparison.OrdinalIgnoreCase));
                if (!hasRock) return MessageHandler.System("This area does not look particularly rocky; a pickaxe is unnecessary here.");
            }

            // Validate held tool presence (must be held)
            bool holding = false;
            try
            {
                var inv = player.Inventory;
                if (inv != null)
                {
                    var token = toolToken.ToLowerInvariant();
                    foreach (var it in inv)
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
                        catch { /* best-effort per-item */ }

                        if (!isHeld) continue;

                        try
                        {
                            var name = it.Name ?? "";
                            var tid = it.TemplateID ?? "";
                            if ((!string.IsNullOrWhiteSpace(name) && name.ToLowerInvariant().Contains(token))
                                || (!string.IsNullOrWhiteSpace(tid) && tid.ToLowerInvariant().Contains(token)))
                            {
                                holding = true;
                                break;
                            }
                        }
                        catch { /* best-effort per-item */ }
                    }
                }
            }
            catch { holding = false; }

            if (!holding)
                return MessageHandler.System($"You must be holding a {toolToken} to {action} with it.");

            // Register pending action and CTS (ensure dictionary entry exists immediately)
            player.PendingAction = new PendingAction
            {
                Command = "Dig",
                Step = action, // "dig" or "mine"
                Data = new { StartedAtUtc = DateTime.UtcNow, RoomID = room.RoomID, Tool = toolToken, TargetItemId = live.ItemID }
            };

            var cts = new CancellationTokenSource();
            _pending[key] = cts;

            // Start the background job (fire-and-forget)
            _ = RunDigTask(player, action, live.ItemID, toolToken, world, db, cts.Token);

            return MessageHandler.System($"You begin {(action == "mine" ? "mining" : "digging")} {live.Name}... (this will take 60 seconds)");
        }//End StartDigging

        public static void CancelPending(Player player)
        {
            if (player == null) return;
            var key = KeyFor(player);
            if (_pending.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }

            // Clear player's pending action object so UI/handlers won't treat them as active
            try { if (player.PendingAction != null && (player.PendingAction.Command == "Dig" || player.PendingAction.Command == "Mine")) player.PendingAction = null; } catch { }
        }

        private static async Task RunDigTask(Player player, string action, int itemId, string toolName, World.World world, SqliteConnection db, CancellationToken ct)
        {
            Room startRoom = player.CurrentRoom;
            if (startRoom == null)
            {
                CancelPending(player);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                try { MessageHandler.SendSystem(player, $"{(action == "mine" ? "Mining" : "Digging")} interrupted."); } catch { }
                CancelPending(player);
                return;
            }
            catch
            {
                CancelPending(player);
                return;
            }

            // Finished delay - verify player still eligible
            try
            {
                // Confirm player is still in the same room
                if (player.CurrentRoom == null || player.CurrentRoom.RoomID != startRoom.RoomID)
                {
                    MessageHandler.SendSystem(player, $"{(action == "mine" ? "Mining" : "Digging")} interrupted.");
                    CancelPending(player);
                    return;
                }

                // Cancel if player is in combat
                try
                {
                    if (MudCore.Actions.CombatManager.IsEntityInCombat(player))
                    {
                        MessageHandler.SendSystem(player, $"{(action == "mine" ? "Mining" : "Digging")} interrupted by combat.");
                        CancelPending(player);
                        return;
                    }
                }
                catch { /* best-effort */ }

                // Re-locate the item in room inventory (it may have been removed)
                var target = startRoom.Inventory?.FirstOrDefault(it => it != null && it.ItemID == itemId);
                if (target == null)
                {
                    MessageHandler.SendSystem(player, $"{(action == "mine" ? "Mining" : "Digging")} has not found anything of value.");
                    CancelPending(player);
                    return;
                }

                // Parse BuriedN or StartsBuriedN flag
                var buriedFlag = target.Flags?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && (f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) || f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase)));
                int chance = 0;
                if (!string.IsNullOrWhiteSpace(buriedFlag))
                {
                    var num = buriedFlag.StartsWith("Buried", StringComparison.OrdinalIgnoreCase)
                        ? buriedFlag.Substring("Buried".Length).Trim()
                        : buriedFlag.Substring("StartsBuried".Length).Trim();
                    if (!int.TryParse(num, out chance)) chance = 0;
                }

                // If no numeric chance found, default to 100 (safe)
                if (chance <= 0) chance = 100;

                var rng = new Random();
                bool success = rng.Next(100) < Math.Clamp(chance, 0, 100);

                if (success)
                {
                    // Remove buried markers (both BuriedN and StartsBuriedN) and update FlagsJson deterministically
                    try
                    {
                        if (target.Flags != null)
                        {
                            target.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                                                        (f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) || f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase)));
                            var sorted = target.Flags.OrderBy(s => s ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                            target.FlagsJson = System.Text.Json.JsonSerializer.Serialize(sorted);
                        }
                        else
                        {
                            target.Flags = new System.Collections.Generic.List<string>();
                            target.FlagsJson = "[]";
                        }
                    }
                    catch { /* best-effort */ }

                    // Transfer ownership to player
                    try
                    {
                        // Remove from room list (in-memory)
                        startRoom.Inventory.Remove(target);

                        target.OwnerType = "Player";
                        target.OwnerID = player.DbPlayerID;
                        target.RoomID = 0;
                        target.OwnerAreaID = player.LastAreaID > 0 ? player.LastAreaID : (player.CurrentRoom?.AreaID ?? 0);

                        // Persist item change (SaveItem handles writer queue)
                        try { ItemPersistence.SaveItem(db, target); } catch (Exception ex) { Console.WriteLine($"[DigManager] SaveItem failed: {ex.Message}"); }

                        // Give to player in-memory inventory
                        player.Inventory.Add(target);

                        // Notify player and room
                        MessageHandler.SendSystem(player, $"You have uncovered a {target.Name} while {(action == "mine" ? "mining" : "digging")}!");
                        foreach (var p in startRoom.Players.Where(pl => pl != player))
                        {
                            MessageHandler.SendSystem(p, $"{player.Name} uncovers something while {(action == "mine" ? "mining" : "digging")}.");
                            // push authoritative room update so clients refresh NPC/Item lists
                            p.PendingMessageQueue.Add(MessageHandler.Room(startRoom, startRoom.Npcs.Select(n => n.DisplayName).ToList(), startRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), world));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DigManager] Transfer succeeded but follow-up failed: {ex.Message}");
                        MessageHandler.SendSystem(player, $"You uncovered something but an error occurred while transferring it to your inventory.");
                    }
                }
                else
                {
                    MessageHandler.SendSystem(player, $"{(action == "mine" ? "Mining" : "Digging")} has not found anything of value.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DigManager] Error finishing dig task: {ex.Message}");
            }
            finally
            {
                CancelPending(player);
            }
        }//End RunDigTask
    }
}