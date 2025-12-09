using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using Microsoft.Data.Sqlite;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudServer.MudUtils; // <- added

namespace MudServer
{
    public class CommandHandler
    {
        private readonly World _world;
        private readonly SqliteConnection _dbConnection;
        private readonly PlayerAuthentication _auth;

        private static readonly HashSet<string> AllowedWearLocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Head", "Neck", "Body",
            "Leftleg", "Rightleg",
            "Leftarm", "Rightarm",
            "Lefthand", "Righthand",
            "Leftfoot", "Rightfoot"
        };

        public CommandHandler(World world, SqliteConnection dbConnection)
        {
            _world = world;
            _dbConnection = dbConnection;
            _auth = new PlayerAuthentication(world, dbConnection);
        }

        public Message? ProcessCommand(Player player, string rawCommand)
        {
            var pendingMsg = CheckPendingAction(player, rawCommand);
            if (pendingMsg != null) return pendingMsg;

            string command = ExtractCommandText(rawCommand).Trim();

            // EAT command (new)
            if (command.StartsWith("eat", StringComparison.OrdinalIgnoreCase))
            {
                var partsEat = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (partsEat.Length < 2)
                    return MessageHandler.System("Usage: eat <food item>");
                string foodName = partsEat[1].Trim();
                return EatItem(player, foodName);
            }

            // Handle 'get' or 'take' command
            if (command.StartsWith("get ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("take ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2);
                if (parts.Length < 2) return MessageHandler.System("Usage: get <item>");
                string itemName = parts[1].Trim();
                return GetItem(player, itemName);
            }

            // Handle 'drop' command
            if (command.StartsWith("drop ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2);
                if (parts.Length < 2) return MessageHandler.System("Usage: drop <item>");
                string itemName = parts[1].Trim();
                return DropItem(player, itemName);
            }

            var normalized = NormalizeCommandAlias(command);
            if (IsValidDirection(normalized))
                return Move(player, normalized);

            // Handle 'say' command (delegated to Chat for centralization)
            if (command.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                string text = command.Substring(4);
                Chat.Say(player, text);
                return null;
            }

            // Handle 'yell' command - delegated to Chat.Yell for centralized chat behavior
            if (command.StartsWith("yell ", StringComparison.OrdinalIgnoreCase))
            {
                string text = command.Substring(5);
                Chat.Yell(player, text, _world);
                return null;
            }

            // Handle room inventory commands
            if (command.StartsWith("additem ", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin)
                    return MessageHandler.System("Permission denied. Admins only.");
                string item = command.Substring(8).Trim();
                return AddItem(player, item);
            }

            if (command.StartsWith("removeitem ", StringComparison.OrdinalIgnoreCase))
            {
                string item = command.Substring(11).Trim();
                return RemoveItem(player, item);
            }

            if (command.Equals("listitems", StringComparison.OrdinalIgnoreCase))
            {
                return ListItems(player);
            }

            switch (command.Split(' ')[0].ToLower())
            {
                case "look":
                    {
                        var lookParts = command.Split(' ', 2);
                        if (lookParts.Length == 1)
                            return Look(player); // existing room look

                        var targetExpr = lookParts[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are lost in the void.");

                        // DEBUG: log tokens/matches
                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        Console.WriteLine($"[LOOK] target='{targetExpr}', room='{room.Name}', npcCount={room.Npcs.Count}, matches={matches.Count}");

                        if (matches.Count == 0)
                            return MessageHandler.System($"No NPC matching '{targetExpr}' found here.");

                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        var npc = matches[0];
                        var desc = string.IsNullOrWhiteSpace(npc.Description) ? "No description." : npc.Description;

                        // Append health segment; current in <span class='hp-current'> for client styling.
                        var healthSegment = $" (<span class='hp-current'>{npc.Health}</span>/{npc.HealthMax})";
                        var text = $"{npc.DisplayName}: {desc}{healthSegment}";
                        Console.WriteLine($"[LOOK] match='{npc.DisplayName}', text='{text}'");

                        return MessageHandler.System(text);
                    }
                case "move": return Move(player, command);

                case "attack":
                    {
                        var attackParts = command.Split(' ', 2);
                        if (attackParts.Length < 2) return MessageHandler.System("Usage: attack <target>");
                        var targetExpr = attackParts[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0)
                            return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple targets: {list}");
                        }

                        // Phase 1: start or join a combat state and perform one immediate attack for feedback
                        return CombatManager.StartOrJoin(player, matches[0], _world, _dbConnection);
                    }

                // NEW: 'who' command lists currently online, authenticated players
                case "who":
                    {
                        var names = _world.Players
                            .Where(p => p != null && p.IsAuthenticated)
                            .Select(p => p.Name)
                            .ToList();

                        if (names.Count == 0)
                            return MessageHandler.System("Players currently online: (none)");

                        var list = string.Join(", ", names);
                        return MessageHandler.System($"Players currently online: {list}");
                    }

                case string s when s.StartsWith("createarea", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return HandleCreateArea(player, command);

                case string s when s.StartsWith("createroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return HandleCreateRoom(player, command);

                case string s when s.StartsWith("editroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return HandleEditRoom(player);

                // Handle 'createnpc' admin command (create NPC instance from a JSON template)
                case string s when s.StartsWith("createnpc", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    var parts = command.Split(' ', 2);
                    if (parts.Length < 2) return MessageHandler.System("Usage: createnpc <templateId>");
                    string templateId = parts[1].Trim();
                    return HandleCreateNpc(player, templateId);

                // Handle 'inventory' command - show detailed carried items in modal
                case string s when s.Equals("inventory", StringComparison.OrdinalIgnoreCase) ||
                                  s.Equals("inv", StringComparison.OrdinalIgnoreCase):
                {
                        var invItems = player.Inventory?.Select(i =>
                        {
                            bool isWorn = (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase)))
                                          || (!string.IsNullOrEmpty(i.FlagsJson) && i.FlagsJson.IndexOf("\"Worn\"", StringComparison.OrdinalIgnoreCase) >= 0);

                            // Use runtime-held detection helper (checks Flags/FlagsJson) so template HandLoc doesn't imply held.
                            bool isHeld = IsRuntimeHeld(i);

                            return new MudNetworking.InventoryItem
                            {
                                Name = i.Name ?? "",
                                Weight = i.Weight,
                                Condition = i.Condition,
                                ItemCategory = i.ItemCategory ?? "",
                                ShortDesc = i.ShortDesc ?? "",
                                // NEW fields:
                                WearLoc = i.WearLoc ?? "",
                                IsWorn = isWorn,
                                // NEW: expose runtime held state and send handLoc only when runtime-held
                                IsHeld = isHeld,
                                HandLoc = isHeld ? i.HandLoc : 0
                            };
                        }).ToList() ?? new List<MudNetworking.InventoryItem>();

                        return MudNetworking.MessageHandler.Inventory(invItems);
                }

                case string s when s.StartsWith("wear", StringComparison.OrdinalIgnoreCase):
                    {
                        // Handles: "wear item", "wear   item", "wear\titem"
                        var trimmed = command.Trim();
                        var arg = trimmed.Length > 4 ? trimmed.Substring(4).Trim() : "";
                        if (string.IsNullOrWhiteSpace(arg))
                            return MessageHandler.System("Usage: wear <item name>");
                        return WearItem(player, arg);
                    }

                case string s when s.Equals("wear", StringComparison.OrdinalIgnoreCase):
                    {
                        return MessageHandler.System("Usage: wear <item name>");
                    }

                

                // handle unwear <arg>
                case string s when s.StartsWith("unwear", StringComparison.OrdinalIgnoreCase):
                {
                    var partsUnwear = command.Split(' ', 2);
                    if (partsUnwear.Length < 2) return MessageHandler.System("Usage: unwear <item name or wearloc>");
                    var arg = partsUnwear[1].Trim();
                    return UnwearItem(player, arg);
                }

                // Add handling for 'unwear' command
                case string s when s.Equals("unwear", StringComparison.OrdinalIgnoreCase):
                    {
                        return MessageHandler.System("Usage: unwear <item name or wearloc>");
                    }

                case string s when s.StartsWith("hold", StringComparison.OrdinalIgnoreCase):
                {
                    // Handles: "hold item", "hold   item", "hold\titem"
                    var trimmed = command.Trim();
                    var arg = trimmed.Length > 4 ? trimmed.Substring(4).Trim() : "";
                    if (string.IsNullOrWhiteSpace(arg))
                        return MessageHandler.System("Usage: hold <item name>");
                    return HoldItem(player, arg);
                }

                case string s when s.Equals("hold", StringComparison.OrdinalIgnoreCase):
                {
                    return MessageHandler.System("Usage: hold <item name>");
                }

                

                // handle unhold <arg>
                case string s when s.StartsWith("unhold", StringComparison.OrdinalIgnoreCase):
                {
                    var partsUnhold = command.Split(' ', 2);
                    if (partsUnhold.Length < 2) return MessageHandler.System("Usage: unhold <item name or hand>");
                    var arg = partsUnhold[1].Trim();
                    return UnholdItem(player, arg);
                }

                case string s when s.Equals("unhold", StringComparison.OrdinalIgnoreCase):
                    {
                        return MessageHandler.System("Usage: unhold <item name or hand>");
                    }

                // ADD new case inside the main switch (near attack case)
                case "assist":
                    {
                        var partsAssist = command.Split(' ', 2);
                        if (partsAssist.Length < 2) return MessageHandler.System("Usage: assist <player|npc>");
                        var targetExpr = partsAssist[1].Trim();
                        if (player.CurrentRoom == null) return MessageHandler.System("You are not in a valid room.");
                        return CombatManager.Assist(player, targetExpr, _world, _dbConnection);
                    }

                // Admin command: debugitems (prints all Item rows from the DB on the active connection)
                case "debugitems":
                {
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

                    try
                    {
                        using var cmd = _dbConnection.CreateCommand();
                        cmd.CommandText = "SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID, OwnerAreaID FROM Item ORDER BY ItemID DESC";
                        using var rdr = cmd.ExecuteReader();

                        int n = 0;
                        while (rdr.Read())
                        {
                            n++;
                            var id = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                            var tid = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                            var name = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                            var otype = rdr.IsDBNull(3) ? "" : rdr.GetString(3);
                            var oid = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                            var rid = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                            var oarea = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);

                            Console.WriteLine($"[DEBUGITEMS] id={id} tid='{tid}' name='{name}' otype='{otype}' oid={oid} rid={rid} oarea={oarea}");
                        }

                        return MessageHandler.System($"DebugItems: printed {n} row(s) to server log.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DEBUGITEMS][ERROR] {ex}");
                        return MessageHandler.System($"DebugItems failed: {ex.Message}");
                    }
                }

                default:
                    return MessageHandler.System($"Unknown command: {command}");
            }
        }

        // New eat logic
        private Message? EatItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.Inventory == null || player.Inventory.Count == 0)
                return MessageHandler.System("You carry nothing edible.");

            // Try ItemParser first for deterministic resolution within player's inventory
            var parse = ItemParser.ResolveForInventoryCommand(player, requested);
            Item? item = null;

            if (parse.Winner != null && string.Equals(parse.Winner.ItemCategory, "Food", StringComparison.OrdinalIgnoreCase))
            {
                // Find the matching instance in the player's inventory (match by ItemID or template/name)
                item = player.Inventory.FirstOrDefault(i =>
                    i != null && (i.ItemID == parse.Winner.ItemID ||
                                  (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parse.Winner.TemplateID) && i.TemplateID == parse.Winner.TemplateID) ||
                                  (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parse.Winner.Name) && string.Equals(i.Name, parse.Winner.Name, StringComparison.OrdinalIgnoreCase)))
                );
            }

            // Fallback: existing category-based search (keeps UX if parser didn't pick a winner)
            if (item == null)
            {
                var candidates = player.Inventory
                    .Where(i => i.ItemCategory != null && i.ItemCategory.Equals("Food", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 0)
                    return MessageHandler.System("You have no food items.");

                // Try exact/template match first
                item = candidates.FirstOrDefault(i =>
                    (!string.IsNullOrWhiteSpace(i.Name) && i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase)));

                // Fallback startswith
                if (item == null)
                    item = candidates.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase));

                if (item == null)
                    return MessageHandler.System($"No food item matches '{requested}'.");
            }

            // Determine if item carries Heal flag
            bool canHeal = (item.Flags != null && item.Flags.Any(f => f.Equals("Heal", StringComparison.OrdinalIgnoreCase)));

            int proposedHeal = 0;
            int appliedHeal = 0;

            if (canHeal)
            {
                // Delegate numeric computation to Effects (pure logic)
                proposedHeal = Effects.ComputeHealAmount(item, Effects.HealSource.Food);
                appliedHeal = Effects.ComputeAppliedHeal(player, proposedHeal);

                // Apply the computed (clamped) heal to the player
                if (appliedHeal > 0)
                    player.Health = Math.Min(player.MaxHealth, player.Health + appliedHeal);
            }

            // Remove item from inventory & persist removal
            if (player.Inventory.Contains(item))
                player.Inventory.Remove(item);

            try
            {
                ItemPersistence.DeleteItem(_dbConnection, item.ItemID);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EAT] Failed to delete item {item.ItemID}: {ex.Message}");
            }

            string msg = $"You eat {item.Name}.";
            if (canHeal && appliedHeal > 0)
                msg += $" You feel better (+{appliedHeal} HP).";

            // Persist player health immediately (centralized helper)
            PersistPlayerHealth(player);

            return MessageHandler.System(msg);
        }

        // Persist player's current Health to the database.
        // Centralized so future health updates (spells, regen, effects) reuse the same logic and logging.
        private void PersistPlayerHealth(Player player)
        {
            if (player == null) return;

            try
            {
                using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                    "UPDATE Player SET Health = @h WHERE PlayerID = @id", _dbConnection);
                cmd.Parameters.AddWithValue("@h", player.Health);
                cmd.Parameters.AddWithValue("@id", player.DbPlayerID);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PLAYER] Failed to persist health for {player?.Name} (ID {player?.DbPlayerID}): {ex.Message}");
            }

            // Load latest persisted combat/experience fields (do this so XP saved in DB by other flows is reflected)
            try
            {
                PlayerPersistence.LoadExtendedCombat(_dbConnection, player);
            }
            catch { }

            // Notify client with authoritative values (health + xp + carried items)
            try
            {
                MessageHandler.EnqueuePlayerUpdate(player);
            }
            catch { }
        }

        private Message? Look(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are lost in the void.");

            // Show only NPC names in room payload; action text is delivered as system messages.
            var npcs = room.Npcs
                .Select(n => n.DisplayName)
                .ToList();

            var players = room.Players
                .Where(p => p != player)
                .Select(p => p.Name)
                .ToList();

            return MessageHandler.Room(room, npcs, players, _world);
        }

        private Message? Move(Player player, string rawCommand)
        {
            string dir;
            var parts = rawCommand.Trim().Split(' ', 2);

            if (parts[0].Equals("move", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 2)
                    return MessageHandler.System("Usage: move <direction>");
                dir = NormalizeCommandAlias(parts[1].Trim()).ToUpper();
            }
            else
            {
                dir = NormalizeCommandAlias(rawCommand.Trim()).ToUpper();
            }

            return Movement.AttemptMove(player, dir, _world, _dbConnection);
        }


        // -------------------------------
        // GET / TAKE
        // -------------------------------
        private Message? GetItem(Player player, string requested)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Prefer ItemParser resolution but preserve existing multi-match UX.
            var parseRes = ItemParser.ResolveForGet(player, requested);
            List<Item> matches;

            if (parseRes.Winner != null)
            {
                // collect all room items that match the resolved winner (template/name/id)
                matches = room.Inventory
                    .Where(i => i != null && (
                        i.ItemID == parseRes.Winner.ItemID ||
                        (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parseRes.Winner.TemplateID) && i.TemplateID == parseRes.Winner.TemplateID) ||
                        (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parseRes.Winner.Name) && string.Equals(i.Name, parseRes.Winner.Name, StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                // If parser returned a winner but we couldn't map to items (unexpected), fall back to InventoryManager
                if (matches.Count == 0)
                    matches = InventoryManager.FindRoomMatches(room, requested);
            }
            else
            {
                // No deterministic winner from parser, fall back to current logic (preserves prompts)
                matches = InventoryManager.FindRoomMatches(room, requested);
            }

            if (matches.Count == 0)
                return MessageHandler.System($"No '{requested}' found in this room.");

            if (matches.Count == 1)
            {
                // Single match - transfer immediately using the InventoryManager
                return InventoryManager.TransferToPlayer(_world, _dbConnection, player, matches[0]);
            }

            // Multiple matches: check if they are identical (same TemplateID)
            bool allSameTemplate = matches.All(i => i.TemplateID == matches[0].TemplateID);

            if (allSameTemplate)
            {
                // Ask how many to pick up
                player.PendingAction = new PendingAction
                {
                    Command = "GetItem",
                    Step = "ChooseQuantity",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"There are {matches.Count} of '{matches[0].Name}'. How many do you want to get?");
            }
            else
            {
                // Different kinds - ask which one (show indices)
                var listText = string.Join("\n", matches.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})"));
                player.PendingAction = new PendingAction
                {
                    Command = "GetItem",
                    Step = "SelectIndex",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"Multiple items match '{requested}'. Choose which one:\n{listText}\nType the number of the item to take.");
            }
        }

        // -------------------------------
        // DROP
        // -------------------------------
        private Message? DropItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Prefer ItemParser for inventory resolution; still preserve multi-match UX.
            var parseRes = ItemParser.ResolveForInventoryCommand(player, requested);
            List<Item> matches;

            if (parseRes.Winner != null)
            {
                // collect all carried items that match the resolved winner (template/name/id)
                matches = player.Inventory
                    .Where(i => i != null && (
                        i.ItemID == parseRes.Winner.ItemID ||
                        (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parseRes.Winner.TemplateID) && i.TemplateID == parseRes.Winner.TemplateID) ||
                        (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parseRes.Winner.Name) && string.Equals(i.Name, parseRes.Winner.Name, StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                if (matches.Count == 0)
                {
                    // unexpected - fallback to naive name/template matching
                    matches = player.Inventory
                        .Where(i => i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                                 || i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count == 0)
                    {
                        matches = player.Inventory
                            .Where(i => i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                }
            }
            else
            {
                // Parser did not determine a winner -> use the existing behavior
                matches = player.Inventory
                    .Where(i => i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                             || i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    // try partial
                    matches = player.Inventory
                        .Where(i => i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }

            if (matches.Count == 0)
                return MessageHandler.System($"You are not carrying '{requested}'.");

            // Use runtime flags for worn/held detection (do NOT treat template HandLoc as held)
            var droppable = matches.Where(m => !IsRuntimeWorn(m) && !IsRuntimeHeld(m)).ToList();

            // If there are no droppable matches but there are matches that are worn/held, instruct player
            if (droppable.Count == 0)
            {
                // Decide whether to instruct unwear or unhold (or both) based on matched items
                bool anyWorn = matches.Any(m => IsRuntimeWorn(m));
                bool anyHeld = matches.Any(m => IsRuntimeHeld(m));

                if (anyWorn && anyHeld)
                    return MessageHandler.System($"Those items are currently worn or held. Please unwear or unhold them before dropping.");
                if (anyWorn)
                    return MessageHandler.System($"That item is currently worn. Please unwear it before dropping.");
                if (anyHeld)
                    return MessageHandler.System($"That item is currently held. Please unhold it before dropping.");

                // fallback (shouldn't hit)
                return MessageHandler.System($"You cannot drop that item while it is equipped.");
            }

            // Use the droppable list for all subsequent logic (single / multiple / quantity selection)
            matches = droppable;

            if (matches.Count == 1)
            {
                return InventoryManager.TransferToRoom(_world, _dbConnection, player, matches[0]);
            }

            bool allSameTemplate = matches.All(i => i.TemplateID == matches[0].TemplateID);

            if (allSameTemplate)
            {
                player.PendingAction = new PendingAction
                {
                    Command = "DropItem",
                    Step = "ChooseQuantity",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"You have {matches.Count} of '{matches[0].Name}'. How many do you want to drop?");
            }
            else
            {
                var listText = string.Join("\n", matches.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})"));
                player.PendingAction = new PendingAction
                {
                    Command = "DropItem",
                    Step = "SelectIndex",
                    Data = new GetPendingData { Matches = matches }
                };

                return MessageHandler.System($"Multiple carried items match '{requested}'. Choose which one to drop:\n{listText}\nType the number of the item to drop.");
            }
        }

        // Called when we have a single item to transfer to player
        private Message? TransferItemToPlayer(Player player, Item item)
        {
            // Player/NPC owned items have no fixed area
            if (item != null)
                item.OwnerAreaID = 0;

            return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item);
        }

        // Called when player drops a single item into current room
        private Message? TransferItemToRoom(Player player, Item item)
        {
            var room = player.CurrentRoom;
            if (room != null && item != null)
                item.OwnerAreaID = room.AreaID;

            return InventoryManager.TransferToRoom(_world, _dbConnection, player, item);
        }

        // -------------------------------
        // Pending Actions (extended)
        // -------------------------------
        private Message? CheckPendingAction(Player player, string rawCommand)
        {
            if (player.PendingAction == null) return null;

            // Delegate auth flow to PlayerAuthentication
            var authHandled = _auth.HandleAuthPending(player, rawCommand);
            if (authHandled != null) return authHandled;

            // The auth handler may have completed the flow and cleared PendingAction (e.g. successful login).
            // Stop processing further pending-action logic if PendingAction is now null.
            if (player.PendingAction == null) return null;

            string input = ExtractCommandText(rawCommand).Trim();
            var pa = player.PendingAction!;

            // existing pending action handling (GetItem, DropItem, EditRoom, CreateArea, CreateRoom, etc.)
            if (pa.Command == "GetItem")
            {
                var room = player.CurrentRoom;
                var data = pa.Data as GetPendingData;
                if (data == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to take.");

                    var chosen = data.Matches[idx - 1];
                    player.PendingAction = null!;
                    return TransferItemToPlayer(player, chosen);
                }

                if (pa.Step == "ChooseQuantity")
                {
                    if (!int.TryParse(input, out int quantity) || quantity < 1)
                        return MessageHandler.System("Please enter a positive number.");

                    int available = data.Matches.Count;
                    if (quantity > available)
                        return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                    // Transfer 'quantity' items (they are identical instances)
                    var toTransfer = data.Matches.Take(quantity).ToList();
                    player.PendingAction = null!;

                    foreach (var it in toTransfer)
                    {
                        // reuse TransferItemToPlayer which persists
                        TransferItemToPlayer(player, it);
                    }

                    return MessageHandler.System($"You picked up {quantity} x {toTransfer.First().Name}.");
                }
            }

            // Handle DropItem multi-step interaction (mirror GetItem)
            if (pa.Command == "DropItem")
            {
                var room = player.CurrentRoom;
                var data = pa.Data as GetPendingData;
                if (data == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to drop.");

                    var chosen = data.Matches[idx - 1];
                    player.PendingAction = null!;
                    return TransferItemToRoom(player, chosen);
                }

                if (pa.Step == "ChooseQuantity")
                {
                    if (!int.TryParse(input, out int quantity) || quantity < 1)
                        return MessageHandler.System("Please enter a positive number.");

                    int available = data.Matches.Count;
                    if (quantity > available)
                        return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                    var toTransfer = data.Matches.Take(quantity).ToList();
                    player.PendingAction = null!;

                    foreach (var it in toTransfer)
                    {
                        TransferItemToRoom(player, it);
                    }

                    return MessageHandler.System($"You dropped {quantity} x {toTransfer.First().Name}.");
                }
            }

            // EDIT ROOM
            if (pa.Command == "EditRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Room editing ended.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System("Enter new name for this room:");
                    }
                    if (input == "2" || input.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditDescription";
                        return MessageHandler.System("Enter new description for this room:");
                    }

                    return MessageHandler.System("Invalid option. Type Name, Description, 1, 2, or EndEdit.");
                }

                if (pa.Step == "EditName")
                {
                    room.Name = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null!;
                    return MessageHandler.System($"Room name updated to '{room.Name}'.");
                }

                if (pa.Step == "EditDescription")
                {
                    room.Description = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room description updated.");
                }
            }

            // CREATE AREA
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;

                var newArea = new Area(input, _world.Areas.Count + 1);
                var newRoom = new Room($"{input} - Undefined Room", "Starting room for the area")
                {
                    AreaID = newArea.AreaID
                };

                int dbID = _world.CreateRoomInDB(newRoom, _dbConnection);
                if (dbID == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Failed to create room in database.");
                }

                newArea.Rooms.Add(newRoom);
                _world.Areas.Add(newArea);

                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null!;

                // 4) Replace CreateArea/CreateRoom return Room calls to use names only (no LastAction formatting)
                return MessageHandler.Room(
                    player.CurrentRoom,
                    player.CurrentRoom.Npcs.Select(n => n.DisplayName).ToList(),
                    player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                    _world);
            }

            // CREATE ROOM
            if (pa.Command == "CreateRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;

                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Current room is not in an area.");
                }

                var newRoom = new Room(input, "Newly created room")
                {
                    AreaID = currentArea.AreaID
                };

                int dbID = _world.CreateRoomInDB(newRoom, _dbConnection);
                if (dbID == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Failed to create room in database.");
                }

                currentArea.Rooms.Add(newRoom);

                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null!;

                // 4) Replace CreateArea/CreateRoom return Room calls to use names only (no LastAction formatting)
                return MessageHandler.Room(
                    player.CurrentRoom,
                    player.CurrentRoom.Npcs.Select(n => n.DisplayName).ToList(),
                    player.CurrentRoom.Players.Where(p => p != player).Select(p => p.Name).ToList(),
                    _world);
            }

            return null;
        }

        // Helper class for GetItem / DropItem pending data
        private class GetPendingData
        {
            public List<Item> Matches { get; set; } = new List<Item>();
        }

        // -------------------------------
        // Admin Commands
        // -------------------------------
        private Message? HandleCreateArea(Player player, string command)
        {
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateArea <Direction>");

            string dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            if (player.CurrentRoom.Exits.Any(e =>
                    e.Direction.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            {
                return MessageHandler.System($"Cannot create area. An exit already exists in direction {dir}.");
            }

            player.PendingAction = new PendingAction
            {
                Command = "CreateArea",
                Direction = dir
            };

            return MessageHandler.System("Enter name for the new area:");
        }

        private Message? HandleCreateRoom(Player player, string command)
        {
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateRoom <Direction>");

            string dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            if (player.CurrentRoom.Exits.Any(e =>
                    e.Direction.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            {
                return MessageHandler.System($"Cannot create room. An exit already exists in direction {dir}.");
            }

            player.PendingAction = new PendingAction
            {
                Command = "CreateRoom",
                Direction = dir
            };

            return MessageHandler.System("Enter name for the new room:");
        }

        private Message? HandleEditRoom(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null)
                return MessageHandler.System("You are not inside a valid room.");

            player.PendingAction = new PendingAction
            {
                Command = "EditRoom",
                Step = "SelectField"
            };

            return MessageHandler.System(
                $"Editing room: {room.Name}\n" +
                "Choose a field to edit:\n" +
                "1) Name\n" +
                "2) Description\n" +
                "Type the option name/number, or type EndEdit to finish."
            );
        }

        private Message? HandleCreateNpc(Player player, string templateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            try
            {
                // Create in-memory NPC via NpcFactory
                var newNpc = _world.NpcFactory.CreateNpc(templateId, room);

                // Persist the NPC and assign NpcID
                int newId = MudPersistence.NpcPersistence.InsertNpc(_dbConnection, newNpc);
                newNpc.NpcID = newId;

                // Ensure world list contains it
                if (!_world.Npcs.Any(n => n.NpcID == newNpc.NpcID))
                {
                    _world.Npcs.Add(newNpc);
                    Console.WriteLine($"[WORLD][TRACE] NPC created and added: NpcID={newNpc.NpcID} Name='{newNpc.Name}' RoomID={room.RoomID}");
                }
                else
                {
                    //DEPRECATED// world.Npcs already contains NPC; skipping add.
                    Console.WriteLine($"[WORLD][TRACE] NPC already in world list: NpcID={newNpc.NpcID}");
                }

                // If template specifies equipment, instantiate items and assign to NPC
                try
                {
                    var equipTemplates = newNpc.Template?.EquipmentTemplateIds;
                    if (equipTemplates != null && equipTemplates.Count > 0 && _world.ItemFactory != null)
                    {
                        foreach (var equipTemplateId in equipTemplates)
                        {
                            try
                            {
                                // Create item owned by NPC (OwnerType "NPC", OwnerID = newNpc.NpcID), RoomID set to current room
                                var item = _world.ItemFactory.CreateItem(equipTemplateId, "NPC", newNpc.NpcID, newNpc.CurrentRoom?.RoomID ?? 0);
                                // Persist item (assigns ItemID)
                                MudPersistence.ItemPersistence.SaveItem(_dbConnection, item);
                                // Add to NPC inventory
                                newNpc.Inventory.Add(item);
                            }
                            catch (Exception iex)
                            {
                                Console.WriteLine($"[NPC] Failed to create/equip item '{equipTemplateId}' for NPC {newNpc.Name}: {iex.Message}");
                            }
                        }
                    }
                }
                catch (Exception exEquip)
                {
                    Console.WriteLine($"[NPC] Equipment assignment error for NPC {newNpc.Name}: {exEquip.Message}");
                }

                // Broadcast updated room so clients immediately see the NPC (and any equipment effect)
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => n.DisplayName).ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                        _world));
                }

                // send NPC actions to log (separate system messages, no name+action in badges)
                foreach (var n in room.Npcs)
                {
                    if (!string.IsNullOrWhiteSpace(n.LastAction))
                    {
                        foreach (var recipient in room.Players)
                            MessageHandler.SendSystem(recipient, n.LastAction);
                    }
                }

                return MessageHandler.System($"NPC '{newNpc.Name}' created (ID {newId}).");
            }
            catch (KeyNotFoundException)
            {
                return MessageHandler.System($"NPC template not found: {templateId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPC] Failed to create NPC from template {templateId}: {ex}");
                return MessageHandler.System($"Failed to create NPC: {ex.Message}");
            }
        }

        private Message? WearItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");
            if (player.Inventory == null || player.Inventory.Count == 0)
                return MessageHandler.System("You are carrying nothing to wear.");

            // Use ItemParser to resolve which inventory item the player meant
            var parseResult = ItemParser.ResolveForInventoryCommand(player, requested);
            if (parseResult.Winner == null)
            {
                // No single winner - if there are candidates show them to the player, otherwise return a failure.
                if (parseResult.Candidates.Count > 0)
                {
                    var list = string.Join(", ", parseResult.Candidates.Select(i => i.Name ?? $"Item#{i.ItemID}"));
                    return MessageHandler.System($"Multiple matches: {list}");
                }
                return MessageHandler.System($"You do not carry '{requested}'.");
            }

            var item = parseResult.Winner;

            // Validate armor category and WearLoc presence (same as before)
            if (!string.Equals(item.ItemCategory, "Armor", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System($"'{item.Name}' is not armor.");
            if (string.IsNullOrWhiteSpace(item.WearLoc))
                return MessageHandler.System($"'{item.Name}' cannot be worn (missing WearLoc).");

            var wearLoc = item.WearLoc.Trim();
            if (!AllowedWearLocs.Contains(wearLoc))
            {
                var allowed = string.Join(", ", AllowedWearLocs.OrderBy(s => s));
                return MessageHandler.System(
                    $"'{item.Name}' has unknown WearLoc '{wearLoc}'. Allowed locations: {allowed}.");
            }

            // Remove 'Worn' from any item already in that slot (use Flags setter so FlagsJson stays in sync)
            var currentlyWorn = player.Inventory.Where(i =>
                i != item &&
                string.Equals(i.ItemCategory, "Armor", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.WearLoc, wearLoc, StringComparison.OrdinalIgnoreCase) &&
                (i.Flags?.Contains("Worn") ?? (i.FlagsJson?.Contains("\"Worn\"") ?? false))
            ).ToList();

            foreach (var old in currentlyWorn)
            {
                var oldFlags = old.Flags ?? new List<string>();
                oldFlags.RemoveAll(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase));
                old.Flags = oldFlags;
                try { MudPersistence.ItemPersistence.SaveItem(_dbConnection, old); } catch { }
            }

            // Add Worn flag
            var flags = item.Flags ?? new List<string>();
            if (!flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase)))
                flags.Add("Worn");
            item.Flags = flags;

            try { MudPersistence.ItemPersistence.SaveItem(_dbConnection, item); } catch (Exception ex) { Console.WriteLine($"[WEAR] SaveItem failed: {ex.Message}"); }

            // Recompute effective stats and broadcast updated room state
            MudServer.Combat.UpdateEffectiveStats(player);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }

            // Send authoritative player update so client header/inventory reflect change immediately
            try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }

            return MessageHandler.System($"You wear {item.Name} on {wearLoc}.");
        }

        private Message? UnwearItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");
            if (player.Inventory == null || player.Inventory.Count == 0)
                return MessageHandler.System("You are carrying nothing to unwear.");

            // Normalize helper - treats runtime "Worn" flag (preferred) or FlagsJson fallback.
            bool IsWorn(Item i) =>
                (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrEmpty(i.FlagsJson) && i.FlagsJson.IndexOf("\"Worn\"", StringComparison.OrdinalIgnoreCase) >= 0);

            // 1) Try ItemParser over equipped items (best-effort deterministic resolution)
            var parse = ItemParser.ResolveForEquipped(player, requested);
            Item? target = null;
            if (parse.Winner != null)
            {
                target = player.Inventory.FirstOrDefault(i =>
                    i != null && (ReferenceEquals(i, parse.Winner) || i.ItemID == parse.Winner.ItemID));
                // If parser predicted an equipped item but we couldn't find the exact instance,
                // try a looser match by template/name as fallback below.
            }

            // 2) If parser did not provide an instance, allow WearLoc slot lookup (like "head", "body")
            if (target == null && AllowedWearLocs.Contains(requested))
            {
                target = player.Inventory.FirstOrDefault(i =>
                    string.Equals(i.ItemCategory, "Armor", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(i.WearLoc) &&
                    string.Equals(i.WearLoc, requested, StringComparison.OrdinalIgnoreCase) &&
                    IsWorn(i));
                if (target == null)
                    return MessageHandler.System($"No item is currently worn in {requested}.");
            }

            // 3) If still not found, fallback to name/template matching among carried armor items,
            //    preferring worn items (preserve existing UX).
            if (target == null)
            {
                var candidates = player.Inventory.Where(i =>
                    string.Equals(i.ItemCategory, "Armor", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(i.Name, requested, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, requested, StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase)))
                ).ToList();

                if (candidates.Count == 0)
                    return MessageHandler.System($"No worn armor matching '{requested}' was found.");

                // Prefer a worn item among matches
                target = candidates.FirstOrDefault(IsWorn) ?? candidates[0];
            }

            if (target == null)
                return MessageHandler.System($"No worn armor matching '{requested}' was found.");

            if (!IsWorn(target))
                return MessageHandler.System($"'{target.Name}' is not currently worn.");

            // Remove 'Worn' flag using the Flags setter (keeps FlagsJson in sync)
            var flags = target.Flags ?? new List<string>();
            flags.RemoveAll(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase));
            target.Flags = flags;

            // Diagnostic: log before saving
            try
            {
                Console.WriteLine($"[UNWEAR] Attempting unwear: ItemID={target.ItemID}, Name='{target.Name}', FlagsJson(before)='{target.FlagsJson}', Flags=[{string.Join(", ", target.Flags ?? new List<string>())}]");
                MudPersistence.ItemPersistence.SaveItem(_dbConnection, target);
                Console.WriteLine($"[UNWEAR] SaveItem returned for ItemID={target.ItemID}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNWEAR][ERROR] SaveItem failed for ItemID={target.ItemID}: {ex.Message}");
            }

            // Verify persisted DB value for Flags column
            try
            {
                using var q = _dbConnection.CreateCommand();
                q.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id";
                q.Parameters.AddWithValue("@id", target.ItemID);
                var dbv = q.ExecuteScalar();
                Console.WriteLine($"[UNWEAR] DB Flags for ItemID={target.ItemID}: '{(dbv == null ? "NULL" : dbv.ToString())}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNWEAR][WARN] DB verification failed for ItemID={target.ItemID}: {ex.Message}");
            }

            // Recompute effective stats (worn armor no longer contributes)
            MudServer.Combat.UpdateEffectiveStats(player);

            // Broadcast updated room payload so other clients see inventory/appearance change
            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }

            // Send authoritative player update so client header/inventory reflect change immediately
            try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }

            var locText = string.IsNullOrWhiteSpace(target.WearLoc) ? "slot" : target.WearLoc;
            return MessageHandler.System($"You remove {target.Name} from {locText}.");
        }

        // Runtime helpers (prefer runtime flags; do NOT treat template HandLoc/WearLoc as runtime state)
        private static bool IsRuntimeWorn(Item i)
        {
            if (i == null) return false;
            if (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase))) return true;
            if (!string.IsNullOrEmpty(i.FlagsJson) && i.FlagsJson.IndexOf("\"Worn\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsRuntimeHeld(Item i)
        {
            if (i == null) return false;
            if (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase))) return true;
            if (!string.IsNullOrEmpty(i.FlagsJson) && i.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Effective hand requirement: prefer template HandLoc when present (1 or 2), fallback to 1
        private static int EffectiveHandLoc(Item i)
        {
            if (i == null) return 1;
            if (i.HandLoc == 2) return 2;
            if (i.HandLoc == 1) return 1;
            // default to 1 for handhelds/weapons without explicit HandLoc
            return 1;
        }

        // Whether an item is holdable: category "Weapon" or "Handheld" (case-insensitive)
        private static bool CanBeHeld(Item i)
        {
            if (i == null) return false;
            var cat = i.ItemCategory ?? string.Empty;
            return string.Equals(cat, "Weapon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cat, "Handheld", StringComparison.OrdinalIgnoreCase);
        }

        // HoldItem: resolve inventory item, ensure holdable, auto-unhold to free hands, persist flags.
        private Message? HoldItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");
            if (player.Inventory == null || player.Inventory.Count == 0)
                return MessageHandler.System("You are carrying nothing to hold.");

            // Resolve intended inventory item
            var parse = ItemParser.ResolveForInventoryCommand(player, requested);
            if (parse.Winner == null)
            {
                if (parse.Candidates.Count > 0)
                {
                    var list = string.Join(", ", parse.Candidates.Select(i => i.Name ?? $"Item#{i.ItemID}"));
                    return MessageHandler.System($"Multiple matches: {list}");
                }
                return MessageHandler.System($"You do not carry '{requested}'.");
            }

            var item = player.Inventory.FirstOrDefault(i => i != null && (ReferenceEquals(i, parse.Winner) || i.ItemID == parse.Winner.ItemID));
            if (item == null)
            {
                // defensive fallback
                item = player.Inventory.FirstOrDefault(i =>
                    (!string.IsNullOrWhiteSpace(i.Name) && string.Equals(i.Name, requested, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, requested, StringComparison.OrdinalIgnoreCase)));
                if (item == null) return MessageHandler.System($"You do not carry '{requested}'.");
            }

            if (!CanBeHeld(item))
                return MessageHandler.System($"'{item.Name}' cannot be held (not a Weapon or Handheld item).");

            int needHands = EffectiveHandLoc(item);

            if (IsRuntimeHeld(item))
                return MessageHandler.System($"You are already holding '{item.Name}'.");

            // Count occupied hands
            var heldItems = player.Inventory.Where(i => IsRuntimeHeld(i)).ToList();
            int occupiedHands = heldItems.Sum(i => EffectiveHandLoc(i));
            int availableHands = Math.Max(0, 2 - occupiedHands);

            var autoUnheld = new List<Item>();

            if (needHands > availableHands)
            {
                // Unhold one-handed items first
                var candidatesToUnhold = heldItems
                    .OrderBy(i => EffectiveHandLoc(i)) // 1-handed first
                    .ThenBy(i => i.ItemID)
                    .ToList();

                int freed = 0;
                foreach (var c in candidatesToUnhold)
                {
                    if (availableHands + freed >= needHands) break;

                    var flags = c.Flags ?? new List<string>();
                    flags.RemoveAll(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase));
                    c.Flags = flags;
                    try { MudPersistence.ItemPersistence.SaveItem(_dbConnection, c); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HOLD] Failed to save unhold for ItemID={c.ItemID}: {ex.Message}");
                    }

                    autoUnheld.Add(c);
                    freed += EffectiveHandLoc(c);
                }

                occupiedHands = player.Inventory.Where(i => IsRuntimeHeld(i)).Sum(i => EffectiveHandLoc(i));
                availableHands = Math.Max(0, 2 - occupiedHands);

                if (needHands > availableHands)
                {
                    var names = autoUnheld.Select(x => x.Name ?? $"Item#{x.ItemID}");
                    return MessageHandler.System($"Not enough hands available to hold '{item.Name}'. Unheld: {string.Join(", ", names)}. Try again after unholding more items.");
                }
            }

            // Hold requested item
            var newFlags = item.Flags ?? new List<string>();
            if (!newFlags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase)))
                newFlags.Add("Held");
            item.Flags = newFlags;

            try { MudPersistence.ItemPersistence.SaveItem(_dbConnection, item); }
            catch (Exception ex)
            {
                Console.WriteLine($"[HOLD] SaveItem failed for ItemID={item.ItemID}: {ex.Message}");
                return MessageHandler.System("Failed to hold item due to a server error.");
            }

            MudServer.Combat.UpdateEffectiveStats(player);

            // Broadcast room and enqueue player update
            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }
            try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }

            if (autoUnheld.Count > 0)
            {
                var names = autoUnheld.Select(x => x.Name ?? $"Item#{x.ItemID}");
                return MessageHandler.System($"You unhold {string.Join(", ", names)} and hold {item.Name}.");
            }

            return MessageHandler.System($"You hold {item.Name}.");
        }

        // Unhold: resolve held item and remove 'Held' flag
        private Message? UnholdItem(Player player, string requested)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");
            if (player.Inventory == null || player.Inventory.Count == 0)
                return MessageHandler.System("You are carrying nothing to unhold.");

            // Try parser (equipped/held)
            var parse = ItemParser.ResolveForEquipped(player, requested);
            Item? target = null;
            if (parse.Winner != null)
                target = player.Inventory.FirstOrDefault(i => i != null && (ReferenceEquals(i, parse.Winner) || i.ItemID == parse.Winner.ItemID));

            if (target == null)
            {
                var candidates = player.Inventory.Where(i =>
                    IsRuntimeHeld(i) &&
                    ((!string.IsNullOrWhiteSpace(i.Name) && string.Equals(i.Name, requested, StringComparison.OrdinalIgnoreCase))
                     || (!string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, requested, StringComparison.OrdinalIgnoreCase))
                     || (!string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                if (candidates.Count == 0) return MessageHandler.System($"You are not holding '{requested}'.");
                target = candidates.FirstOrDefault(i => string.Equals(i.Name, requested, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
            }

            if (target == null) return MessageHandler.System($"You are not holding '{requested}'.");
            if (!IsRuntimeHeld(target)) return MessageHandler.System($"'{target.Name}' is not currently held.");

            var flags = target.Flags ?? new List<string>();
            flags.RemoveAll(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase));
            target.Flags = flags;

            // Diagnostic: log before saving
            try
            {
                Console.WriteLine($"[UNHOLD] Attempting unhold: ItemID={target.ItemID}, Name='{target.Name}', FlagsJson(before)='{target.FlagsJson}', Flags=[{string.Join(", ", target.Flags ?? new List<string>())}]");
                MudPersistence.ItemPersistence.SaveItem(_dbConnection, target);
                Console.WriteLine($"[UNHOLD] SaveItem returned for ItemID={target.ItemID}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNHOLD][ERROR] SaveItem failed for ItemID={target.ItemID}: {ex.Message}");
            }

            // Verify persisted DB value for Flags column
            try
            {
                using var q = _dbConnection.CreateCommand();
                q.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id";
                q.Parameters.AddWithValue("@id", target.ItemID);
                var dbv = q.ExecuteScalar();
                Console.WriteLine($"[UNHOLD] DB Flags for ItemID={target.ItemID}: '{(dbv == null ? "NULL" : dbv.ToString())}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNHOLD][WARN] DB verification failed for ItemID={target.ItemID}: {ex.Message}");
            }

            MudServer.Combat.UpdateEffectiveStats(player);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }
            try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }

            return MessageHandler.System($"You unhold {target.Name}.");
        }

        private bool IsValidDirection(string dir)
        {
            string[] valid = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "UP", "DOWN" };
            return valid.Any(v => v.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }

        private string ExtractCommandText(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand)) return string.Empty;
            var s = rawCommand.Trim();

            // If client sent a JSON payload (e.g. {"type":"command","text":"s"}), extract the text field.
            if (s.StartsWith("{") || s.StartsWith("[") || s.StartsWith("\""))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.String)
                        return root.GetString() ?? string.Empty;

                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("text", out var prop) && prop.ValueKind == JsonValueKind.String)
                            return prop.GetString() ?? string.Empty;

                        if (root.TryGetProperty("command", out prop) && prop.ValueKind == JsonValueKind.String)
                            return prop.GetString() ?? string.Empty;

                        // nested payloads: data.text
                        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                            data.TryGetProperty("text", out var dataText) && dataText.ValueKind == JsonValueKind.String)
                        {
                            return dataText.GetString() ?? string.Empty;
                        }
                    }
                }
                catch
                {
                    // fall through to return trimmed raw string if JSON parsing fails
                }
            }

            return s.Trim(); // ensure final trimmed text
        }

        private string OppositeDirection(string dir) => dir.ToUpper() switch
        {
            "N" => "S",
            "S" => "N",
            "E" => "W",
            "W" => "E",
            "NE" => "SW",
            "SW" => "NE",
            "NW" => "SE",
            "SE" => "NW",
            "UP" => "DOWN",
            "DOWN" => "UP",
            _ => ""
        };

        // -------------------------------

        // Admin helpers: add/remove/list items in current room
        private Message? AddItem(Player player, string itemTemplateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (_world.ItemFactory == null)
                return MessageHandler.System("Item factory not available.");

            try
            {
                Console.WriteLine($"[AddItem][TRACE] Creating item from template='{itemTemplateId}' in RoomID={room.RoomID}, AreaID={room.AreaID}.");
                var item = _world.ItemFactory.CreateItem(itemTemplateId, "ROOM", 0, room.RoomID);

                // Normalize ownership fields consistently
                item.OwnerType = "Room";
                item.OwnerID = room.RoomID;
                item.RoomID = room.RoomID;
                item.OwnerAreaID = room.AreaID;

                Console.WriteLine($"[AddItem][TRACE] Pre-SaveItem: ItemID={item.ItemID}, TemplateID='{item.TemplateID}', Name='{item.Name}', OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}, FlagsJson='{item.FlagsJson}'.");

                MudPersistence.ItemPersistence.SaveItem(_dbConnection, item);

                using var readCmd = _dbConnection.CreateCommand();
                readCmd.CommandText = "SELECT ItemID, TemplateID, OwnerType, OwnerID, RoomID, OwnerAreaID FROM Item WHERE ItemID=@id";
                readCmd.Parameters.AddWithValue("@id", item.ItemID);
                using var r = readCmd.ExecuteReader();
                if (r.Read())
                {
                    Console.WriteLine($"[AddItem][VERIFY] Row: id={r.GetInt32(0)} tid='{r.GetString(1)}' otype='{r.GetString(2)}' oid={r.GetInt32(3)} rid={r.GetInt32(4)} oarea={r.GetInt32(5)}");
                }
                else
                {
                    Console.WriteLine($"[AddItem][ERROR] Just-inserted ItemID={item.ItemID} not found via SELECT.");
                }

                Console.WriteLine($"[AddItem][TRACE] Post-SaveItem: ItemID={item.ItemID}, OwnerType='{item.OwnerType}', OwnerID={item.OwnerID}, RoomID={item.RoomID}, OwnerAreaID={item.OwnerAreaID}.");

                using var verifyCmd = _dbConnection.CreateCommand();
                verifyCmd.CommandText = "SELECT COUNT(*) FROM Item WHERE OwnerType='Room' AND OwnerID=@rid";
                verifyCmd.Parameters.AddWithValue("@rid", room.RoomID);
                var countObj = verifyCmd.ExecuteScalar();
                long count = countObj is long l ? l : Convert.ToInt64(countObj);
                Console.WriteLine($"[AddItem][TRACE] DB verify: Room {room.RoomID} now has {count} item(s) persisted as OwnerType='Room'.");

                room.Inventory.Add(item);
                room.IsDirty = true;

                // Verify DB persistence for this room
                using var checkCmd = _dbConnection.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM Item WHERE OwnerType='Room' AND OwnerID=@rid";
                checkCmd.Parameters.AddWithValue("@rid", room.RoomID);
                var itemCount = (long)(checkCmd.ExecuteScalar() ?? 0);
                Console.WriteLine($"[AddItem][TRACE] Verify after add: Room {room.RoomID} has {itemCount} items in DB.");

                _world.SaveDirtyRooms(_dbConnection);

                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => n.DisplayName).ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                        _world));
                }

                foreach (var n in room.Npcs)
                {
                    if (!string.IsNullOrWhiteSpace(n.LastAction))
                    {
                        foreach (var recipient in room.Players)
                            MessageHandler.SendSystem(recipient, n.LastAction);
                    }
                }

                return MessageHandler.System($"Item '{item.Name}' added to the room.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AddItem][ERROR] AddItem failed: {ex}");
                return MessageHandler.System($"Failed to add item: {ex.Message}");
            }
        }

        private Message? RemoveItem(Player player, string identifier)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // allow admin only removal
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var target = room.Inventory.FirstOrDefault(i => i.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                                                   || i.TemplateID.Equals(identifier, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                return MessageHandler.System($"No item '{identifier}' found in the room.");

            // Remove from DB and room
            target.OwnerType = "ROOM";
            target.OwnerID = 0;
            MudPersistence.ItemPersistence.SaveItem(_dbConnection, target);

            room.Inventory.Remove(target);
            room.IsDirty = true;
            _world.SaveDirtyRooms(_dbConnection);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }

            foreach (var n in room.Npcs)
            {
                if (!string.IsNullOrWhiteSpace(n.LastAction))
                {
                    foreach (var recipient in room.Players)
                        MessageHandler.SendSystem(recipient, n.LastAction);
                }
            }

            return MessageHandler.System($"Item '{target.Name}' removed from the room.");
        }

        private Message? ListItems(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (room.Inventory == null || room.Inventory.Count == 0)
                return MessageHandler.System("This room contains no items.");

            var lines = room.Inventory.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})");
            return MessageHandler.System("Items in room:\n" + string.Join("\n", lines));
        }

        private static string NormalizeCommandAlias(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token;
            switch (token.Trim().ToLowerInvariant())
            {
                case "u": return "up";
                case "d": return "down";
                default:  return token;
            }
        }
    }
}
