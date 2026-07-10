
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

            // Rock rooms require a pick — a shovel cannot break solid rock
            bool roomIsRock = room.Flags != null && room.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && string.Equals(f, "Rock", StringComparison.OrdinalIgnoreCase));
            if (roomIsRock && toolToken.Contains("shovel", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System("You cannot dig through solid rock with a shovel. You need a pick.");

            // Validate held tool presence (must be held)
            bool holding = IsHoldingTool(player, toolToken);

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
            try { if (player.PendingAction != null && (player.PendingAction.Command == "Dig" || player.PendingAction.Command == "Mine" || player.PendingAction.Command == "Bury")) player.PendingAction = null; } catch { }
        }

        /// <summary>
        /// Returns true when the player has an item matching <paramref name="toolToken"/> in a held state.
        /// Extracted from StartDigging so both dig and bury flows share one implementation.
        /// </summary>
        private static bool IsHoldingTool(Player player, string toolToken)        {
            if (player?.Inventory == null || string.IsNullOrWhiteSpace(toolToken)) return false;
            var token = toolToken.Trim().ToLowerInvariant();
            foreach (var it in player.Inventory)
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
                        return true;
                }
                catch { /* best-effort per-item */ }
            }
            return false;
        }

        /// <summary>
        /// Returns true when an item already carries a Buried* or StartsBuried* flag
        /// (i.e. it is already underground and should not be a bury target).
        /// </summary>
        private static bool IsAlreadyBuried(Item item)
        {
            if (item?.Flags == null) return false;
            return item.Flags.Any(f => !string.IsNullOrWhiteSpace(f) &&
                (f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) ||
                 f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Entry point for the 'bury [itemname]' command.
        /// Requires the player to hold a shovel and the current room to have a 'Dirt' flag.
        /// Buries a carried item in the room after a 15-second timed action.
        /// The resulting Buried# difficulty is clamped to [20, 90]: roll 20-40 + 1 per 2 player levels.
        /// </summary>
        public static Message? StartBurying(Player player, string itemExpr, World.World world, SqliteConnection db)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.CurrentRoom == null) return MessageHandler.System("You are not in a valid location.");

            if (string.IsNullOrWhiteSpace(itemExpr))
                return MessageHandler.System("Usage: bury <item>");

            var key = KeyFor(player);
            if (_pending.ContainsKey(key))
                return MessageHandler.System("You are already digging or burying.");

            var room = player.CurrentRoom;

            // --- Shovel check (must be holding one) ---
            if (!IsHoldingTool(player, "shovel"))
                return MessageHandler.System("You will need a shovel to bury that here!");

            // --- Resolve the item: inventory first (full parser), then room for heavy/dragged items ---
            bool itemInRoom = false;

            // Use the same ItemParser that drop/inspect use — resolves by name, descriptor, material
            var parseRes = MudUtils.ItemParser.ResolveForInventoryCommand(player, itemExpr);
            var target = parseRes.Winner;

            if (target == null)
            {
                // Not in inventory — check room for unburied visible items (e.g. dragged heavy chest)
                var opts = new MudUtils.ItemParserOptions
                {
                    IncludeDescriptorTokens = true,
                    IncludeMaterialTokens = true,
                    AllowStartsWith = true,
                    IncludeBuried = false
                };
                var roomRes = MudUtils.ItemParser.ResolveForGet(player, itemExpr, opts);
                if (roomRes.Winner != null && room.Inventory != null &&
                    room.Inventory.Any(i => i != null && i.ItemID == roomRes.Winner.ItemID && !IsAlreadyBuried(i)))
                {
                    target = roomRes.Winner;
                    itemInRoom = true;
                }
            }

            if (target == null)
                return MessageHandler.System($"You are not carrying '{itemExpr}' and don't see it here to bury.");

            // --- Dirt flag check ---
            bool hasDirt = room.Flags != null &&
                           room.Flags.Any(f => string.Equals(f, "Dirt", StringComparison.OrdinalIgnoreCase));
            if (!hasDirt)
                return MessageHandler.System($"You cannot bury the {target.Name} here, find a more suitable area!");

            // --- Register pending action ---
            player.PendingAction = new PendingAction
            {
                Command = "Bury",
                Step = "bury",
                Data = new { StartedAtUtc = DateTime.UtcNow, RoomID = room.RoomID, TargetItemId = target.ItemID, ItemInRoom = itemInRoom }
            };

            var cts = new CancellationTokenSource();
            _pending[key] = cts;

            _ = RunBuryTask(player, target.ItemID, world, db, cts.Token);

            return MessageHandler.System($"You feel you can hide the {target.Name} here, you begin digging.");
        } // end StartBurying

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
                bool isPlayerBuried = false;
                if (!string.IsNullOrWhiteSpace(buriedFlag))
                {
                    // StartsBuried# = naturally occurring (ore etc): N is direct success %
                    // Buried#       = player-buried item:            N is difficulty, success = 100 - N
                    isPlayerBuried = buriedFlag.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) &&
                                     !buriedFlag.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase);
                    var num = isPlayerBuried
                        ? buriedFlag.Substring("Buried".Length).Trim()
                        : buriedFlag.Substring("StartsBuried".Length).Trim();
                    if (!int.TryParse(num, out chance)) chance = 0;
                }

                // If no numeric chance found, default to 100 (safe)
                if (chance <= 0) chance = 100;

                // For player-buried items invert: Buried27 = difficulty 27 = 73% success to dig up
                int successChance = isPlayerBuried ? (100 - chance) : chance;

                var rng = new Random();
                bool success = rng.Next(100) < Math.Clamp(successChance, 0, 100);

                if (success)
                {
                    // Remove buried markers and write back through the setter so FlagsJson is updated
                    try
                    {
                        var flags = target.Flags;
                        flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                            (f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) ||
                             f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase)));
                        target.Flags = flags;
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
                            p.PendingMessageQueue.Add(MessageHandler.Room(startRoom, startRoom.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(), startRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), world));
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
                    MessageHandler.SendSystem(player, $"{(action == "mine" ? "Mining" : "Digging")} has not recovered anything of value.");
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

        private static async Task RunBuryTask(Player player, int itemId, World.World world, SqliteConnection db, CancellationToken ct)
        {
            var startRoom = player.CurrentRoom;
            if (startRoom == null) { CancelPending(player); return; }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                try { MessageHandler.SendSystem(player, "Burying interrupted."); } catch { }
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
                // Confirm player is still in the same room
                if (player.CurrentRoom == null || player.CurrentRoom.RoomID != startRoom.RoomID)
                {
                    MessageHandler.SendSystem(player, "Burying interrupted — you moved.");
                    CancelPending(player);
                    return;
                }

                // Confirm player is not in combat
                try
                {
                    if (CombatManager.IsEntityInCombat(player))
                    {
                        MessageHandler.SendSystem(player, "Burying interrupted by combat.");
                        CancelPending(player);
                        return;
                    }
                }
                catch { /* best-effort */ }

                // Re-locate item — check inventory first, then room (item may have been room-sourced)
                var target = player.Inventory?.FirstOrDefault(i => i != null && i.ItemID == itemId);
                bool itemWasInRoom = false;
                if (target == null)
                {
                    target = startRoom.Inventory?.FirstOrDefault(i => i != null && i.ItemID == itemId && !IsAlreadyBuried(i));
                    if (target != null) itemWasInRoom = true;
                }
                if (target == null)
                {
                    MessageHandler.SendSystem(player, "The item is no longer here to bury.");
                    CancelPending(player);
                    return;
                }

                // --- Difficulty roll ---
                // Base: random 20-40 inclusive; bonus: +1 per 2 player levels; cap: 90
                int baseRoll = Random.Shared.Next(20, 41);
                int levelBonus = Math.Max(0, player.Level / 2);
                int difficulty = Math.Min(90, baseRoll + levelBonus);

                // --- Apply Buried# flag to item ---
                try
                {
                    var flags = target.Flags; // deserializes fresh copy from FlagsJson
                    flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                        (f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase) ||
                         f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase)));
                    flags.Add($"Buried{difficulty}");
                    target.Flags = flags; // setter serializes back into FlagsJson
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DigManager][Bury] Failed to apply Buried flag: {ex.Message}");
                }

                // --- Transfer item to room ---
                try
                {
                    if (!itemWasInRoom)
                    {
                        player.Inventory.Remove(target);
                        target.OwnerType = "Room";
                        target.OwnerID = startRoom.RoomID;
                        target.RoomID = startRoom.RoomID;
                        target.OwnerAreaID = startRoom.AreaID;
                        startRoom.Inventory ??= new System.Collections.Generic.List<Item>();
                        startRoom.Inventory.Add(target);
                    }

                    // Persist item change — same call as RunDigTask uses after digging up
                    try { ItemPersistence.SaveItem(db, target); } catch (Exception ex) { Console.WriteLine($"[DigManager][Bury] SaveItem failed: {ex.Message}"); }

                    // Notify digger (no difficulty revealed)
                    MessageHandler.SendSystem(player, $"You bury the {target.Name} out of sight.");

                    // Notify other players in room
                    foreach (var p in startRoom.Players.Where(pl => pl != null && pl != player))
                    {
                        MessageHandler.SendSystem(p, $"{player.Name} finishes burying something.");
                        // Authoritative room refresh (item now hidden)
                        p.PendingMessageQueue.Add(MessageHandler.Room(
                            startRoom,
                            startRoom.Npcs?.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList() ?? new System.Collections.Generic.List<string>(),
                            startRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                            world, db));
                    }

                    // Also refresh room for the burying player so the item disappears from their client view
                    player.PendingMessageQueue.Add(MessageHandler.Room(
                        startRoom,
                        startRoom.Npcs?.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList() ?? new System.Collections.Generic.List<string>(),
                        startRoom.Players.Where(pl => pl != null && pl != player).Select(pl => pl.Name).ToList(),
                        world, db));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DigManager][Bury] Transfer failed: {ex.Message}");
                    MessageHandler.SendSystem(player, "Something went wrong while burying the item.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DigManager][Bury] RunBuryTask error: {ex.Message}");
            }
            finally
            {
                CancelPending(player);
            }
        } // end RunBuryTask
    }
}
