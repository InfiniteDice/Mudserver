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
        private readonly AdminService _adminService; // new
        private readonly MudServer.MudCore.Actions.DoorService _doorService; // door helper

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
            _adminService = new AdminService(world, dbConnection); // initialize admin service
            _doorService = new MudServer.MudCore.Actions.DoorService(world, dbConnection); // initialize door service
        }

        public Message? ProcessCommand(Player player, string rawCommand)
        {
            var pendingMsg = CheckPendingAction(player, rawCommand);
            if (pendingMsg != null) return pendingMsg;

            string command = ExtractCommandText(rawCommand).Trim();

            // --- OPEN / CLOSE door commands ---
            if (command.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
            {
                var arg = command.Substring(5).Trim();
                if (string.IsNullOrWhiteSpace(arg)) return MessageHandler.System("Usage: open <direction>");
                try
                {
                    return _doorService.OpenDoor(player, arg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD] Open command failed: {ex}");
                    return MessageHandler.System("Failed to open the door due to a server error.");
                }
            }

            if (command.StartsWith("close ", StringComparison.OrdinalIgnoreCase))
            {
                var arg = command.Substring(6).Trim();
                if (string.IsNullOrWhiteSpace(arg)) return MessageHandler.System("Usage: close <direction>");
                try
                {
                    return _doorService.CloseDoor(player, arg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD] Close command failed: {ex}");
                    return MessageHandler.System("Failed to close the door due to a server error.");
                }
            }

            // EAT command - supports "eat <item>" and "eat <item> from <container>"
            if (command.StartsWith("eat", StringComparison.OrdinalIgnoreCase))
            {
                var partsEat = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (partsEat.Length < 2)
                    return MessageHandler.System("Usage: eat <food item> or eat <item> from <container>");
                string foodName = partsEat[1].Trim();
                return EatItem(player, foodName, false);
            }

            // DRINK command - supports "drink <liquid>" and "drink <liquid> from <container>"
            if (command.StartsWith("drink", StringComparison.OrdinalIgnoreCase))
            {
                var partsDrink = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (partsDrink.Length < 2)
                    return MessageHandler.System("Usage: drink <liquid> or drink <liquid> from <container>");
                string drinkName = partsDrink[1].Trim();
                return EatItem(player, drinkName, true);
            }

            // LOCK command: accept "lock" (usage) or "lock <container> [with <key>]"
            if (command.StartsWith("lock", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return MessageHandler.System("Usage: lock <container> [with <key>]");
                var payload = parts[1].Trim();
                int idxWith = payload.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                string containerName = idxWith >= 0 ? payload.Substring(0, idxWith).Trim() : payload;
                string keyName = idxWith >= 0 ? payload.Substring(idxWith + " with ".Length).Trim() : "";

                if (string.IsNullOrWhiteSpace(containerName))
                    return MessageHandler.System("Usage: lock <container> [with <key>]");

                // Resolve container with ItemParser first (search player inventory + room). Conservative options for containers.
                Item? container = null;
                try
                {
                    var optsContainer = new ItemParserOptions
                    {
                        IncludeDescriptorTokens = false,
                        IncludeMaterialTokens = false,
                        AllowStartsWith = true
                    };
                    var pres = ItemParser.ResolveForAny(player, containerName, optsContainer);
                    if (pres?.Winner != null) container = pres.Winner;
                }
                catch { /* best-effort; fall through to legacy lookup */ }

                // Fallback to legacy lookup
                if (container == null) container = InventoryManager.FindContainer(player, containerName);
                if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                if (!string.IsNullOrWhiteSpace(keyName))
                {
                    // find key in player's inventory
                    var key = player.Inventory?.FirstOrDefault(i =>
                        i != null && (string.Equals(i.Name, keyName, StringComparison.OrdinalIgnoreCase)
                                      || (!string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, keyName, StringComparison.OrdinalIgnoreCase))))
                        ?? null;

                    if (key == null) return MessageHandler.System($"You are not carrying a key named '{keyName}'.");

                    // Start pending flow to prompt for label (CheckPendingAction already handles "LockKey" NameKey)
                    player.PendingAction = new PendingAction
                    {
                        Command = "LockKey",
                        Step = "NameKey",
                        Data = new KeyLocks.LockPendingData { Container = container, Key = key }
                    };
                    return MessageHandler.System($"Enter label for the key to lock {container.Name}:");
                }
                else
                {
                    // If no key specified, prompt for key name (or you could prompt to pick a carried key)
                    player.PendingAction = new PendingAction
                    {
                        Command = "LockKey",
                        Step = "NameKey",
                        Data = new KeyLocks.LockPendingData { Container = container, Key = null! } // LockPendingData.Key may be filled by pending step handling
                    };
                    return MessageHandler.System($"Which key do you want to use to lock {container.Name}? Type the key name, or 'cancel'.");
                }
            }//End LOCK


            // UNLOCK command: accept "unlock" (usage) or "unlock <container> with <key>"
            if (command.StartsWith("unlock", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return MessageHandler.System("Usage: unlock <container> with <key>");
                var payload = parts[1].Trim();
                int idxWith = payload.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (idxWith < 0) return MessageHandler.System("Usage: unlock <container> with <key>");

                var containerName = payload.Substring(0, idxWith).Trim();
                var keyName = payload.Substring(idxWith + " with ".Length).Trim();

                if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(keyName))
                    return MessageHandler.System("Usage: unlock <container> with <key>");

                // Resolve container using ItemParser first (player inventory + room)
                Item? container = null;
                try
                {
                    var optsContainer = new ItemParserOptions
                    {
                        IncludeDescriptorTokens = false,
                        IncludeMaterialTokens = false,
                        AllowStartsWith = true
                    };
                    var pres = ItemParser.ResolveForAny(player, containerName, optsContainer);
                    if (pres?.Winner != null) container = pres.Winner;
                }
                catch { /* best-effort */ }

                if (container == null) container = InventoryManager.FindContainer(player, containerName);
                if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                var key = player.Inventory?.FirstOrDefault(i =>
                        i != null && (string.Equals(i.Name, keyName, StringComparison.OrdinalIgnoreCase)
                                      || (!string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, keyName, StringComparison.OrdinalIgnoreCase))))
                        ?? null;

                if (key == null) return MessageHandler.System($"You are not carrying a key named '{keyName}'.");

                // Attempt unlock immediately
                return KeyLocks.UnlockContainer(_dbConnection, player, container, key);
            }//End UNLOCK

            // Inspect container (accept "inspect" usage or "inspect <container>")
            if (command.StartsWith("inspect", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return MessageHandler.System("Usage: inspect <container>");
                var containerName = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(containerName)) return MessageHandler.System("Usage: inspect <container>");

                // NEW BEHAVIOR:
                // - Resolve across ALL scopes first (room + inventory + equipped).
                // - If parser returns a single Winner, use it regardless of location.
                // - If multiple candidates (tie), prefer a single inventory-held candidate when present.
                // - If still ambiguous present the candidate list to the player.
                var optsAny = new ItemParserOptions
                {
                    IncludeDescriptorTokens = true,
                    IncludeMaterialTokens = true,
                    AllowStartsWith = true
                };

                Item? container = null;
                ItemResolutionResult presAny;
                try
                {
                    presAny = ItemParser.ResolveForAny(player, containerName, optsAny);
                }
                catch
                {
                    presAny = new ItemResolutionResult { Reason = "Parser failure." };
                }

                // If parser produced a clear winner from any scope, use it.
                if (presAny?.Winner != null)
                {
                    container = presAny.Winner;
                }
                else if (presAny != null && presAny.Candidates != null && presAny.Candidates.Count > 0)
                {
                    // Disambiguate tie: prefer an inventory match if exactly one inventory candidate exists.
                    var invMatches = new List<Item>();
                    if (player?.Inventory != null)
                    {
                        foreach (var c in presAny.Candidates)
                        {
                            if (player.Inventory.Any(inv => ReferenceEquals(inv, c) || inv.ItemID == c.ItemID))
                                invMatches.Add(c);
                        }
                    }

                    if (invMatches.Count == 1)
                    {
                        container = invMatches[0];
                    }
                    else
                    {
                        // No single inventory preference -> present ambiguous list (preserve current UX)
                        var list = string.Join(", ", presAny.Candidates.Select(i => i.Name));
                        return MessageHandler.System($"Multiple items match: {list}");
                    }
                }

                // If still not resolved, report not found
                if (container == null)
                {
                    return MessageHandler.System($"You see no '{containerName}' here.");
                }

                // Enforce lock check (works for both room and inventory containers)
                var lockMsg = KeyLocks.EnsureAccessible(_dbConnection, player, container);
                if (lockMsg != null) return MessageHandler.System(lockMsg);

                try
                {
                    // Load items whose OwnerType='Item' and OwnerID == container.ItemID
                    var contents = ItemPersistence.LoadItemsByOwner(_dbConnection, "Item", container.ItemID) ?? new List<Item>();

                    // Compute current used weight in container
                    double usedWeight = 0.0;
                    try
                    {
                        using var cmd = _dbConnection.CreateCommand();
                        cmd.CommandText = @"SELECT SUM(COALESCE(Weight,0.0) * COALESCE(Quantity,1)) FROM Item
            WHERE OwnerType = 'Item' AND OwnerID = @cid;";
                        cmd.Parameters.AddWithValue("@cid", container.ItemID);
                        var obj = cmd.ExecuteScalar();
                        if (!(obj == null || obj is DBNull))
                            usedWeight = Convert.ToDouble(obj);
                    }
                    catch
                    {
                        usedWeight = 0.0;
                    }

                    // Format capacity and used weight for presentation (two decimal places)
                    var cap = InventoryManager.ParseCapacity(container);
                    string capText = cap.HasValue ? $"{cap.Value:F2}" : "unlimited";

                    if (contents.Count == 0)
                    {
                        string emptyMsg = $"{container.Name} is empty. Capacity: {capText}. Used: {usedWeight:F2}.";
                        return MessageHandler.System(emptyMsg);
                    }

                    var lines = new List<string>();
                    for (int i = 0; i < contents.Count; i++)
                    {
                        var it = contents[i];
                        var qtyPart = it.Quantity > 1 ? $" (x{it.Quantity})" : "";
                        var totalWeight = it.Weight * Math.Max(1, it.Quantity);
                        // Present weight rounded to two decimals to avoid long floating-point artifacts
                        lines.Add($"{i + 1}) {it.Name}{qtyPart} — wt {totalWeight:F2}");
                    }

                    var header = $"{container.Name} contents (Used: {usedWeight:F2} / Capacity: {capText}):";
                    return MessageHandler.System(header + "\n" + string.Join("\n", lines));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[INSPECT] Failed to inspect container '{containerName}': {ex}");
                    return MessageHandler.System("Failed to inspect container due to a server error.");
                }
            }//END INSPECT

            // --- admin: debugrooms <areaid?> ---
            if (command.StartsWith("debugrooms", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var arg = parts.Length > 1 ? parts[1].Trim() : "";
                try
                {
                    return _adminService.DebugRooms(player, arg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][DEBUGROOMS] {ex}");
                    return MessageHandler.System("Failed to run debugrooms due to a server error.");
                }
            }

            // --- COOK command: cook <container> (delegates logic to Cooking service) ---
            if (command.StartsWith("cook ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return MessageHandler.System("Usage: cook <container>");
                var payload = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: cook <container>");

                // Support explicit operator forms:
                //  - "cook with <container>"  -> treat everything after "with " as container (allow descriptors)
                //  - "cook <container>"       -> legacy behavior (conservative container lookup)
                bool usedWithOperator = false;
                string containerName = payload;

                if (payload.StartsWith("with ", StringComparison.OrdinalIgnoreCase))
                {
                    usedWithOperator = true;
                    containerName = payload.Substring("with ".Length).Trim();
                }
                try
                {
                    // Conservative parser options for normal forms (avoid descriptor collisions)
                    var optsContainer = new ItemParserOptions
                    {
                        IncludeDescriptorTokens = false,
                        IncludeMaterialTokens = false,
                        AllowStartsWith = true
                    };

                    // If user used explicit "with", be more permissive and allow descriptor tokens
                    if (usedWithOperator)
                    {
                        optsContainer.IncludeDescriptorTokens = true;
                        optsContainer.IncludeMaterialTokens = true;
                    }

                    string resolvedName = containerName;
                    Item? container = null;
                    try
                    {
                        var pres = ItemParser.ResolveForAny(player, containerName, optsContainer);
                        if (pres?.Winner != null)
                        {
                            container = pres.Winner;
                            resolvedName = !string.IsNullOrWhiteSpace(pres.Winner.Name) ? pres.Winner.Name : (pres.Winner.TemplateID ?? containerName);
                        }
                    }
                    catch { /* best-effort; fall back to provided containerName */ }

                    // fallback legacy FindContainer if parser didn't find one
                    if (container == null) container = InventoryManager.FindContainer(player, containerName);
                    if (container == null)
                    {
                        return MessageHandler.System($"No container matching '{containerName}' found.");
                    }

                    return MudServer.MudCore.Actions.Cooking.TryStartCooking(player, resolvedName, _world, _dbConnection);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][COOK] StartCooking failed: {ex}");
                    return MessageHandler.System("Failed to start cooking due to a server error.");
                }
            }

            // NEW ADDITION: support "fill <container> with water" as a shortcut that sources water from room flags.
            // This is additive and does not replace existing 'fill ... from ... to ...' behavior.
            if (command.StartsWith("fill ", StringComparison.OrdinalIgnoreCase))
            {
                var payloadShort = command.Substring(5).Trim(); // after "fill "
                int idxWith = payloadShort.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (idxWith > 0)
                {
                    var containerName = payloadShort.Substring(0, idxWith).Trim();
                    var src = payloadShort.Substring(idxWith + " with ".Length).Trim();

                    if (!string.IsNullOrWhiteSpace(containerName) && !string.IsNullOrWhiteSpace(src))
                    {
                        // only handle the explicit "water" shortcut here; leave all other "fill" forms untouched
                        if (string.Equals(src, "water", StringComparison.OrdinalIgnoreCase) || string.Equals(src, "water", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                return InventoryManager.FillContainerFromRoomWithWater(_world, _dbConnection, player, containerName);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CMD][FILL-WATER] Failed: {ex}");
                                return MessageHandler.System("Failed to fill container due to a server error.");
                            }
                        }
                    }
                }
            }

            if (command.StartsWith("fill ", StringComparison.OrdinalIgnoreCase))
            {
                // Expected syntax: fill <liquid> from <sourceContainer> to <targetContainer>
                // Example: "fill gruel from pot to bowl" or "fill gruel_hare from pot to bowl"
                var payload = command.Substring(5).Trim(); // after "fill "
                                                           // find " from " and " to " (case-insensitive)
                int idxFrom = payload.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
                int idxTo = payload.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);

                if (idxFrom < 0 || idxTo < 0 || idxTo <= idxFrom)
                    return MessageHandler.System("Usage: fill <liquid> from <container> to <container>");

                var liquidExpr = payload.Substring(0, idxFrom).Trim();
                var fromName = payload.Substring(idxFrom + " from ".Length, idxTo - (idxFrom + " from ".Length)).Trim();
                var toName = payload.Substring(idxTo + " to ".Length).Trim();

                if (string.IsNullOrWhiteSpace(liquidExpr) || string.IsNullOrWhiteSpace(fromName) || string.IsNullOrWhiteSpace(toName))
                    return MessageHandler.System("Usage: fill <liquid> from <container> to <container>");

                return InventoryManager.TransferLiquidBetweenContainers(_world, _dbConnection, player, liquidExpr, fromName, toName);
            }

            // --- GET / TAKE command handling: delegate to helper ---
            if (command.StartsWith("get ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("take ", StringComparison.OrdinalIgnoreCase))
            {
                return HandleGetCommand(player, command);
            }

            // --- PUT command delegated to helper ---
            if (command.StartsWith("put ", StringComparison.OrdinalIgnoreCase))
            {
                return HandlePutCommand(player, command);
            }

            // Handle 'drop' command
            if (command.StartsWith("drop ", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count < 2) return MessageHandler.System("Usage: drop <item> or drop <n|all> <item>");

                var (quantity, explicitQuantity, itemExpr, containerName, dropErr) = ParseQuantityItemContainer(tokens, 1);
                if (dropErr != null) return MessageHandler.System(dropErr);

                int forwardQty = explicitQuantity ? quantity : 0;
                return DropItem(player, itemExpr, forwardQty);
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

            // Handle room inventory commands and other admin flows...
            if (command.StartsWith("additem ", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin)
                    return MessageHandler.System("Permission denied. Admins only.");

                var rest = command.Substring(8).Trim();
                if (string.IsNullOrWhiteSpace(rest))
                    return MessageHandler.System("Usage: additem <template> [quantity]");

                // If admin used the "to" form (e.g. "additem water to cup"), pass the whole rest to AdminService
                if (rest.IndexOf(" to ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // AdminService will parse "item to target" form.
                    return _adminService.AddItem(player, rest, 1);
                }

                var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string template = tokens[0];
                int qty = 1;
                if (tokens.Length > 1)
                {
                    if (!int.TryParse(tokens[1], out qty) || qty < 1)
                        return MessageHandler.System("Invalid quantity. Provide a positive integer.");
                    qty = Math.Min(qty, 10000);
                }

                return _adminService.AddItem(player, template, qty);
            }

            //if (command.StartsWith("exchange ", StringComparison.OrdinalIgnoreCase))
            //{
            //    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            //    return _adminService.HandleExchange(player, command);
            //}

            if (command.StartsWith("removeitem ", StringComparison.OrdinalIgnoreCase))
            {
                string item = command.Substring(11).Trim();
                return _adminService.RemoveItem(player, item);
            }

            if (command.Equals("listitems", StringComparison.OrdinalIgnoreCase))
            {
                return _adminService.ListItems(player);
            }

            switch (command.Split(' ')[0].ToLower())
            {
                case "look":
                    return HandleLookCommand(player, command);

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
                            // prompt player to pick a numeric index from the displayed list
                            var list = string.Join("\n", matches.Select((m, idx) => $"{idx + 1}) {m.DisplayName}"));
                            player.PendingAction = new PendingAction
                            {
                                Command = "AttackSelect",
                                Step = "SelectIndex",
                                Data = new NpcPendingData { Matches = matches }
                            };
                            return MessageHandler.System($"Multiple targets match '{targetExpr}'. Choose one to attack:\n{list}\nType the number of the target to attack.");
                        }

                        return CombatManager.StartOrJoin(player, matches[0], _world, _dbConnection);
                    }

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
                    return _adminService.HandleCreateArea(player, command);

                case string s when s.StartsWith("createroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return _adminService.HandleCreateRoom(player, command);

                case string s when s.StartsWith("editroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return _adminService.HandleEditRoom(player);

                case string s when s.StartsWith("editexit", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return _adminService.HandleEditExit(player, command);

                case string s when s.StartsWith("createnpc", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    var parts = command.Split(' ', 2);
                    if (parts.Length < 2) return MessageHandler.System("Usage: createnpc <templateId>");
                    string templateId = parts[1].Trim();
                    return _adminService.HandleCreateNpc(player, templateId);

                case string s when s.Equals("inventory", StringComparison.OrdinalIgnoreCase) ||
                                  s.Equals("inv", StringComparison.OrdinalIgnoreCase):
                    {
                        var invItems = player.Inventory?.Select(i =>
                        {
                            bool isWorn = (i.Flags != null && i.Flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase)))
                                          || (!string.IsNullOrEmpty(i.FlagsJson) && i.FlagsJson.IndexOf("\"Worn\"", StringComparison.OrdinalIgnoreCase) >= 0);

                            bool isHeld = IsRuntimeHeld(i);

                            double baseTotalWeight = i.Weight * Math.Max(1, i.Quantity);

                            double containedWeight = 0.0;
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(i.ItemCategory) && string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase))
                                {
                                    using var cmd = _dbConnection.CreateCommand();
                                    cmd.CommandText = @"SELECT SUM(COALESCE(Weight,0.0) * COALESCE(Quantity,1)) FROM Item
                WHERE OwnerType = 'Item' AND OwnerID = @cid;";
                                    cmd.Parameters.AddWithValue("@cid", i.ItemID);
                                    var obj = cmd.ExecuteScalar();
                                    if (!(obj == null || obj is DBNull))
                                    {
                                        containedWeight = Convert.ToDouble(obj);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[INVENTORY] Failed to compute contained weight for ItemID={i?.ItemID}: {ex.Message}");
                                containedWeight = 0.0;
                            }

                            double totalWeight = Math.Round(baseTotalWeight + containedWeight, 2);

                            return new MudNetworking.InventoryItem
                            {
                                Name = i.Name ?? "",
                                Weight = totalWeight,
                                Condition = i.Condition,
                                ItemCategory = i.ItemCategory ?? "",
                                ShortDesc = i.ShortDesc ?? "",
                                WearLoc = i.WearLoc ?? "",
                                IsWorn = isWorn,
                                IsHeld = isHeld,
                                HandLoc = isHeld ? i.HandLoc : 0,
                                Quantity = i.Quantity > 0 ? i.Quantity : 1,
                                // New fields for client inventory panel
                                Descriptor = i.Descriptor ?? "",
                                Material = i.Material ?? ""
                            };
                        }).ToList() ?? new List<MudNetworking.InventoryItem>();

                        return MudNetworking.MessageHandler.Inventory(invItems);
                    }

                case string s when s.Equals("stats", StringComparison.OrdinalIgnoreCase):
                    {
                        if (player == null) return MessageHandler.System("Invalid player.");

                        try
                        {
                            Combat.InitializeSizeDefaults(player);
                            Combat.UpdateEffectiveStats(player);
                        }
                        catch { }

                        var flags = player.Flags ?? new List<string>();
                        var buffs = player.Buffs ?? new List<string>();
                        var debuffs = player.Debuffs ?? new List<string>();

                        var lines = new List<string>
    {
        $"Stats for {player.Name}:",
        $"Level: {player.Level}",
        $"Health: {player.Health}/{player.MaxHealth}",
        $"Size (SizeEnum): {player.SizeEnum}",
        $"Effective Mass: {player.EffMass:F2}",
        $"Effective Combat Skill: {player.EffCombatSkill:F2}",
        $"Effective Armor Class: {player.EffArmorClass:F2}",
        $"Effective Damage Dice: {player.EffDmgDice:F2}",
        $"Flags: {(flags.Count == 0 ? "(none)" : string.Join(", ", flags))}",
        $"Buffs: {(buffs.Count == 0 ? "(none)" : string.Join(", ", buffs))}",
        $"Debuffs: {(debuffs.Count == 0 ? "(none)" : string.Join(", ", debuffs))}"
    };

                        return MessageHandler.System(string.Join("\n", lines));
                    }

                case string s when s.StartsWith("smelt", StringComparison.OrdinalIgnoreCase):
                    {
                        var idx = command.IndexOf(' ');
                        var args = idx >= 0 ? command.Substring(idx + 1).Trim() : string.Empty;
                        try
                        {
                            return MudServer.MudCore.Actions.Crafting.Smelt(player, args, _world, _dbConnection);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD] Smelt command failed: {ex.Message}");
                            return MessageHandler.System("Smelting failed due to a server error.");
                        }
                    }

                case string s when s.StartsWith("wear", StringComparison.OrdinalIgnoreCase):
                    {
                        var trimmed = command.Trim();
                        var arg = trimmed.Length > 4 ? trimmed.Substring(4).Trim() : "";
                        if (string.IsNullOrWhiteSpace(arg))
                            return MessageHandler.System("Usage: wear <item name>");
                        return WearItem(player, arg);
                    }

                case string s when s.StartsWith("unwear", StringComparison.OrdinalIgnoreCase):
                    {
                        var partsUnwear = command.Split(' ', 2);
                        if (partsUnwear.Length < 2) return MessageHandler.System("Usage: unwear <item name or wearloc>");
                        var arg = partsUnwear[1].Trim();
                        return UnwearItem(player, arg);
                    }

                case string s when s.StartsWith("hold", StringComparison.OrdinalIgnoreCase):
                    {
                        var trimmed = command.Trim();
                        var arg = trimmed.Length > 4 ? trimmed.Substring(4).Trim() : "";
                        if (string.IsNullOrWhiteSpace(arg))
                            return MessageHandler.System("Usage: hold <item name>");
                        return HoldItem(player, arg);
                    }

                case string s when s.StartsWith("unhold", StringComparison.OrdinalIgnoreCase):
                    {
                        var partsUnhold = command.Split(' ', 2);
                        if (partsUnhold.Length < 2) return MessageHandler.System("Usage: unhold <item name or hand>");
                        var arg = partsUnhold[1].Trim();
                        return UnholdItem(player, arg);
                    }

                case "assist":
                    {
                        var partsAssist = command.Split(' ', 2);
                        if (partsAssist.Length < 2) return MessageHandler.System("Usage: assist <player|npc>");
                        var targetExpr = partsAssist[1].Trim();
                        if (player.CurrentRoom == null) return MessageHandler.System("You are not in a valid room.");
                        return CombatManager.Assist(player, targetExpr, _world, _dbConnection);
                    }

                // Admin anchor command (delegated to helper to keep ProcessCommand compact)
                case string s when s.StartsWith("anchor", StringComparison.OrdinalIgnoreCase):
                    {
                        return HandleAnchorCommand(player, command);
                    }

                case "debugitems":
                    {
                        return _adminService.DebugItems(player);
                    }

                case string s when s.StartsWith("kill", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    try
                    {
                        return _adminService.HandleKill(player, command);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CMD][KILL] {ex}");
                        return MessageHandler.System("Failed to run kill due to a server error.");
                    }

                default:
                    return MessageHandler.System($"Unknown command: {command}");
            }
        } // end ProcessCommand



        private Message? HandleLookCommand(Player player, string command)
        {
            var lookParts = command.Split(' ', 2); if (lookParts.Length == 1) return Look(player);
            var targetExpr = lookParts[1].Trim();
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are lost in the void.");

            var dirToken = NormalizeCommandAlias(targetExpr).ToUpperInvariant();
            if (IsValidDirection(dirToken))
            {
                var exit = room.Exits.FirstOrDefault(x => string.Equals(x.Direction, dirToken, StringComparison.OrdinalIgnoreCase));
                if (exit == null)
                    exit = room.Exits.FirstOrDefault(x => string.Equals(NormalizeCommandAlias(x.Direction), NormalizeCommandAlias(targetExpr), StringComparison.OrdinalIgnoreCase));

                if (exit == null)
                    return MessageHandler.System("You don't see an exit in that direction.");

                var targetRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);

                string distantText;
                if (targetRoom != null && !string.IsNullOrWhiteSpace(targetRoom.DistantDescription))
                    distantText = targetRoom.DistantDescription;
                else if (targetRoom != null)
                    distantText = $"In the distance you can see {targetRoom.Name}.";
                else
                    distantText = "You can see a distant landscape in that direction.";

                string movementHint = "";
                try
                {
                    int distantCount = 0;
                    if (targetRoom != null)
                        distantCount = (targetRoom.Npcs?.Count ?? 0) + (targetRoom.Players?.Count ?? 0);

                    if (distantCount == 1)
                        movementHint = " Something can be seen moving in the distance.";
                    else if (distantCount == 2)
                        movementHint = " A couple of shapes can be seen moving in the distance.";
                    else if (distantCount >= 3 && distantCount <= 4)
                        movementHint = " Several figures can be seen moving in the distance.";
                    else if (distantCount >= 5)
                        movementHint = " Many things can be seen moving in the distance.";
                }
                catch { }

                return MessageHandler.System(distantText + movementHint);
            }

            var npcMatches = NpcTargeting.FindMatches(room, targetExpr);
            if (npcMatches.Count > 0)
            {
                if (npcMatches.Count > 1)
                {
                    var list = string.Join(", ", npcMatches.Select(m => m.DisplayName));
                    return MessageHandler.System($"Multiple matches: {list}");
                }

                var npc = npcMatches[0];

                // Prefer instance description; fall back to template description if instance is empty.
                string desc = !string.IsNullOrWhiteSpace(npc.Description) ? npc.Description
                              : (npc.Template != null && !string.IsNullOrWhiteSpace(npc.Template.Description)
                                 ? npc.Template.Description
                                 : "No description.");

                // Present health as plain text to avoid sending raw HTML tags to clients.
                var healthSegment = $" ({npc.Health}/{npc.HealthMax})";
                var text = $"{npc.DisplayName}: {desc}{healthSegment}";
                return MessageHandler.System(text);
            }

            // Support "look all <query>" to return descriptions for every matching item in the room.
            // Example: "look all iron" -> items whose material/descriptor/name/token matches "iron".
            try
            {
                if (targetExpr.StartsWith("all ", StringComparison.OrdinalIgnoreCase))
                {
                    var query = targetExpr.Substring(4).Trim();
                    if (string.IsNullOrWhiteSpace(query))
                        return MessageHandler.System("Usage: look all <item>");

                    string qLower = query.ToLowerInvariant();

                    // local tokenizer: returns lowercased alphanumeric tokens
                    IEnumerable<string> TokenizeLocal(string? s)
                    {
                        if (string.IsNullOrWhiteSpace(s)) yield break;
                        int i = 0, n = s.Length;
                        while (i < n)
                        {
                            while (i < n && !char.IsLetterOrDigit(s[i])) i++;
                            int start = i;
                            while (i < n && char.IsLetterOrDigit(s[i])) i++;
                            if (i > start) yield return s.Substring(start, i - start).ToLowerInvariant();
                        }
                    }

                    bool ItemMatchesQuery(Item it, string q)
                    {
                        if (it == null || string.IsNullOrWhiteSpace(q)) return false;
                        // TemplateID exact
                        if (!string.IsNullOrWhiteSpace(it.TemplateID) && string.Equals(it.TemplateID, q, StringComparison.OrdinalIgnoreCase)) return true;

                        // Name exact / starts-with / contains token
                        if (!string.IsNullOrWhiteSpace(it.Name))
                        {
                            if (it.Name.Equals(q, StringComparison.OrdinalIgnoreCase) || it.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return true;
                            var nameTokens = TokenizeLocal(it.Name).ToList();
                            if (nameTokens.Any(t => t.Equals(q) || t.StartsWith(q))) return true;
                            if (it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }

                        // Descriptor match
                        if (!string.IsNullOrWhiteSpace(it.Descriptor) && it.Descriptor.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

                        // Material match
                        if (!string.IsNullOrWhiteSpace(it.Material) && it.Material.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

                        return false;
                    }

                    var matches = room.Inventory?
                        .Where(i => i != null && ItemMatchesQuery(i, qLower))
                        .ToList() ?? new List<Item>();

                    if (matches.Count == 0)
                        return MessageHandler.System($"You see no '{query}' here.");

                    // Build combined descriptions, one after another separated by blank line
                    var pieces = new List<string>();
                    foreach (var it in matches)
                    {
                        var getItemDesc = !string.IsNullOrWhiteSpace(it.LongDesc) ? it.LongDesc :
                                       !string.IsNullOrWhiteSpace(it.ShortDesc) ? it.ShortDesc : "No description.";

                        // Build metadata: "Key words: [name] [descriptor] [material]"
                        string getMeta = "";
                        try
                        {
                            var metaParts = new List<string>();
                            if (!string.IsNullOrWhiteSpace(it.Name)) metaParts.Add(it.Name.Trim());
                            if (!string.IsNullOrWhiteSpace(it.Descriptor)) metaParts.Add(it.Descriptor.Trim());
                            if (!string.IsNullOrWhiteSpace(it.Material)) metaParts.Add(it.Material.Trim());

                            if (metaParts.Count > 0)
                            {
                                var joined = string.Join(" ", metaParts.Where(s => !string.IsNullOrWhiteSpace(s)));
                                const int maxMetaLen = 120;
                                if (joined.Length > maxMetaLen) joined = joined.Substring(0, maxMetaLen) + "...";
                                getMeta = $" Key words: {joined}";
                            }
                        }
                        catch { /* best-effort */ }

                        pieces.Add($"{it.Name}: {getItemDesc}{getMeta}");
                    }

                    var header = matches.Count == 1 ? $"You see {matches.Count} matching item:" : $"You see {matches.Count} matching items:";
                    var result = header + "\n\n" + string.Join("\n\n", pieces);
                    return MessageHandler.System(result);
                }
            }
            catch { /* non-fatal; fall through to single-item lookup below */ }

            // Use ItemParser to resolve room items (supports descriptor/material tokens)
            var opts = new ItemParserOptions { IncludeDescriptorTokens = true, IncludeMaterialTokens = true, AllowStartsWith = true };
            var pres = ItemParser.ResolveForGet(player, targetExpr, opts);

            if (pres.Winner == null)
            {
                if (pres.Candidates.Count == 0)
                    return MessageHandler.System($"You see no '{targetExpr}' here.");
                var list = string.Join(", ", pres.Candidates.Select(i => i.Name));
                return MessageHandler.System($"Multiple items match: {list}");
            }

            var item = pres.Winner;
            var itemDesc = !string.IsNullOrWhiteSpace(item.LongDesc) ? item.LongDesc :
                           !string.IsNullOrWhiteSpace(item.ShortDesc) ? item.ShortDesc : "No description.";

            // Build metadata: "Key words: [name] [descriptor] [material]"
            string meta = "";
            try
            {
                var metaParts = new List<string>();
                // include name as requested (may duplicate the leading Name in the output)
                if (!string.IsNullOrWhiteSpace(item.Name)) metaParts.Add(item.Name.Trim());
                if (!string.IsNullOrWhiteSpace(item.Descriptor)) metaParts.Add(item.Descriptor.Trim());
                if (!string.IsNullOrWhiteSpace(item.Material)) metaParts.Add(item.Material.Trim());

                if (metaParts.Count > 0)
                {
                    var joined = string.Join(" ", metaParts.Where(s => !string.IsNullOrWhiteSpace(s)));
                    // limit metadata length for UX (avoid huge lines)
                    const int maxMetaLen = 120;
                    if (joined.Length > maxMetaLen) joined = joined.Substring(0, maxMetaLen) + "...";
                    meta = $" Key words: {joined}";
                }
            }
            catch { /* best-effort - do not break look */ }

            return MessageHandler.System($"{item.Name}: {itemDesc}{meta}");
        } // end HandleLookCommand

        private Message? HandleGetCommand(Player player, string command)
        {
            var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(); if (tokens.Count < 2) return MessageHandler.System("Usage: get <item> [container] OR get <n|all> <item> <container>");
            var (quantity, explicitQuantity, itemExpr, containerName, getErr) = ParseQuantityItemContainer(tokens, 1);
            if (getErr != null) return MessageHandler.System(getErr);

            // If a container is specified, operate against its contents.
            if (!string.IsNullOrWhiteSpace(containerName))
            {
                var container = InventoryManager.FindContainer(player, containerName);
                if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                // enforce lock check before attempting to read container contents
                var lockMsg = KeyLocks.EnsureAccessible(_dbConnection, player, container);
                if (lockMsg != null) return MessageHandler.System(lockMsg);

                // Load container contents (DB authoritative)
                List<Item> containerItems;
                try { containerItems = ItemPersistence.LoadItemsByOwner(_dbConnection, "Item", container.ItemID) ?? new List<Item>(); }
                catch { containerItems = new List<Item>(); }

                // Prefer parser-based resolution first (allow descriptors/materials for general GET)
                var opts = new ItemParserOptions { IncludeDescriptorTokens = true, IncludeMaterialTokens = true, AllowStartsWith = true };
                var pres = ItemParser.ResolveCandidates(containerItems, player, itemExpr, opts);

                // Helper for logical equality (prefer TemplateID)
                bool SameLogical(Item a, Item b)
                {
                    if (!string.IsNullOrWhiteSpace(a.TemplateID) && !string.IsNullOrWhiteSpace(b.TemplateID))
                        return string.Equals(a.TemplateID, b.TemplateID, StringComparison.OrdinalIgnoreCase);
                    return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                }

                // If parser produced a winner, use it (and honor explicit quantity logic)
                if (pres.Winner != null)
                {
                    var representative = pres.Winner;
                    var matchingRows = containerItems.Where(c => SameLogical(representative, c)).OrderByDescending(r => r.Quantity).ToList();
                    int available = matchingRows.Sum(r => r.Quantity);

                    if (explicitQuantity)
                    {
                        if (quantity < 0) // 'all'
                        {
                            if (available == 0) return MessageHandler.System($"No '{representative.Name}' remain in {container.Name}.");
                            foreach (var row in matchingRows)
                                InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, row, row.Quantity);
                            return MessageHandler.System($"You took {available} x {representative.Name} from {container.Name}.");
                        }

                        if (available < quantity)
                            return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                        int remaining = quantity;
                        int taken = 0;
                        foreach (var row in matchingRows)
                        {
                            if (remaining <= 0) break;
                            int take = Math.Min(remaining, row.Quantity);
                            InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, row, take);
                            taken += take;
                            remaining -= take;
                        }

                        return MessageHandler.System($"You took {taken} x {representative.Name} from {container.Name}.");
                    }

                    // No explicit quantity: take one from the best matching row
                    var getCandidate = matchingRows.OrderByDescending(m => !string.IsNullOrWhiteSpace(m.TemplateID) && string.Equals(m.TemplateID, itemExpr, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                                                .ThenByDescending(m => m.Quantity).FirstOrDefault();
                    if (getCandidate == null) return MessageHandler.System($"No '{itemExpr}' found inside '{container.Name}'.");

                    bool isLiquid = (getCandidate.Flags != null && getCandidate.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                    || (!string.IsNullOrEmpty(getCandidate.FlagsJson) && getCandidate.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (isLiquid)
                        return MessageHandler.System($"That's a liquid. Use: fill <liquid> from {container.Name} to <target container>.");

                    return InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, getCandidate, 1);
                }

                // Parser returned nothing useful — fall back to legacy heuristics to preserve behavior.
                // Tokenize helper (kept from previous logic)
                IEnumerable<string> Tokenize(string? s)
                {
                    if (string.IsNullOrWhiteSpace(s)) yield break;
                    int i = 0, n = s.Length;
                    while (i < n)
                    {
                        while (i < n && !char.IsLetterOrDigit(s[i])) i++;
                        int start = i;
                        while (i < n && char.IsLetterOrDigit(s[i])) i++;
                        if (i > start) yield return s.Substring(start, i - start).ToLowerInvariant();
                    }
                }

                // Matching heuristics (legacy fallback)
                var matches = containerItems
                    .Where(i => !string.IsNullOrEmpty(i.Name) &&
                                (i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)
                                 || (!string.IsNullOrEmpty(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                if (matches.Count == 0)
                {
                    var reqTokens = Tokenize(itemExpr).ToList();
                    if (reqTokens.Count > 0)
                    {
                        matches = containerItems.Where(it =>
                        {
                            var nameTokens = Tokenize(it?.Name).ToList();
                            if (reqTokens.Any(rt => nameTokens.Any(nt => string.Equals(nt, rt, StringComparison.OrdinalIgnoreCase)))) return true;
                            if (reqTokens.All(rt => nameTokens.Any(nt => string.Equals(nt, rt, StringComparison.OrdinalIgnoreCase)))) return true;
                            return false;
                        }).ToList();
                    }
                }

                if (matches.Count == 0)
                {
                    matches = containerItems
                        .Where(i => !string.IsNullOrEmpty(i.Name) && i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                if (matches.Count == 0)
                {
                    var reqTokens2 = Tokenize(itemExpr).ToList();
                    if (reqTokens2.Count > 0)
                    {
                        matches = containerItems.Where(it =>
                        {
                            var nameTokens = Tokenize(it?.Name).ToList();
                            return nameTokens.Any(nt => reqTokens2.Any(rt => nt.StartsWith(rt, StringComparison.OrdinalIgnoreCase)));
                        }).ToList();
                    }
                }

                if (matches.Count == 0)
                    return MessageHandler.System($"No '{itemExpr}' found inside '{container.Name}'.");

                // If user specified a quantity, fulfill it by distributing across matching rows in the container.
                if (explicitQuantity)
                {
                    // logical matching: prefer TemplateID match, otherwise Name equality
                    bool SameLogicalFallback(Item a, Item b)
                    {
                        if (!string.IsNullOrWhiteSpace(a.TemplateID) && !string.IsNullOrWhiteSpace(b.TemplateID))
                            return string.Equals(a.TemplateID, b.TemplateID, StringComparison.OrdinalIgnoreCase);
                        return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    }

                    var representative = matches.First();

                    var matchingRows = containerItems.Where(c => SameLogicalFallback(representative, c)).OrderByDescending(r => r.Quantity).ToList();
                    int available = matchingRows.Sum(r => r.Quantity);

                    if (quantity < 0) // 'all'
                    {
                        if (available == 0) return MessageHandler.System($"No '{representative.Name}' remain in {container.Name}.");
                        foreach (var row in matchingRows)
                            InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, row, row.Quantity);
                        return MessageHandler.System($"You took {available} x {representative.Name} from {container.Name}.");
                    }

                    if (available < quantity)
                        return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                    int remaining = quantity;
                    int taken = 0;
                    foreach (var row in matchingRows)
                    {
                        if (remaining <= 0) break;
                        int take = Math.Min(remaining, row.Quantity);
                        InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, row, take);
                        taken += take;
                        remaining -= take;
                    }

                    return MessageHandler.System($"You took {taken} x {representative.Name} from {container.Name}.");
                }

                // No explicit quantity: transfer a single unit from the first match (preserve simple UX, no menu).
                var candidate = matches.OrderByDescending(m => !string.IsNullOrWhiteSpace(m.TemplateID) && string.Equals(m.TemplateID, itemExpr, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                                       .ThenByDescending(m => m.Quantity).First();

                bool isLiquid2 = (candidate.Flags != null && candidate.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                || (!string.IsNullOrEmpty(candidate.FlagsJson) && candidate.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isLiquid2)
                    return MessageHandler.System($"That's a liquid. Use: fill <liquid> from {container.Name} to <target container>.");

                return InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, candidate, 1);
            }
            else
            {
                // No container: delegate to room-based GetItem which follows the new explicit-only policy.
                int forwardQty = explicitQuantity ? quantity : 0;
                return GetItem(player, itemExpr, forwardQty);
            }
        } // end HandleGetCommand

        private Message? HandlePutCommand(Player player, string command)
        {
            var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(); if (tokens.Count < 2) return MessageHandler.System("Usage: put <item> <container> OR put <n> <item> <container>");
            var (quantity, explicitQuantity, itemExpr, containerName, putErr) = ParseQuantityItemContainer(tokens, 1);
            if (putErr != null) return MessageHandler.System("Usage: put <n?> <item> in <container> (recommended).");

            Console.WriteLine($"[PUT] parsed: qty={quantity}, itemExpr='{itemExpr}', container='{containerName}'");

            if (string.IsNullOrWhiteSpace(containerName))
                return MessageHandler.System("Usage: put <n?> <item> in <container> (use 'in' or 'into' for multi-word containers).");

            // Resolve container using parser first (search player's inventory + room). Fallback to InventoryManager.FindContainer.
            Item? container = null;
            try
            {
                var optsContainer = new ItemParserOptions
                {
                    IncludeDescriptorTokens = false, // be conservative for container lookup
                    IncludeMaterialTokens = false,
                    AllowStartsWith = true
                };
                var pres = ItemParser.ResolveForAny(player, containerName, optsContainer);
                if (pres.Winner != null) container = pres.Winner;
            }
            catch { /* best-effort */ }

            if (container == null) container = InventoryManager.FindContainer(player, containerName);

            if (container == null)
            {
                Console.WriteLine($"[PUT] No container matching '{containerName}' found. Player inventory: {string.Join(", ", player.Inventory.Select(i => i.Name ?? $"Item#{i.ItemID}"))}");
                return MessageHandler.System($"No container matching '{containerName}' found.");
            }

            try
            {
                Console.WriteLine("[PUT] Player inventory dump:");
                foreach (var it in player.Inventory)
                {
                    Console.WriteLine($"[PUT][INV] ItemID={it.ItemID} Name='{it.Name}' Template='{it.TemplateID}' OwnerType='{it.OwnerType}' OwnerID={it.OwnerID} Qty={it.Quantity}");
                }
            }
            catch { }

            // If an explicit quantity was specified, fulfill it by distributing across matching carried items.
            if (explicitQuantity)
            {
                // Build a list of candidate carried items that match the requested expression.
                // Prefer parser-based template matching when available to avoid ambiguous name matches.
                var parse = ItemParser.ResolveForInventoryCommand(player, itemExpr);

                List<Item> candidates = new List<Item>();

                if (parse.Winner != null)
                {
                    // collect all carried items that match the resolved winner (template/name/id)
                    candidates = player.Inventory
                        .Where(i => i != null && (
                            i.ItemID == parse.Winner.ItemID ||
                            (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parse.Winner.TemplateID) && i.TemplateID == parse.Winner.TemplateID) ||
                            (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parse.Winner.Name) && string.Equals(i.Name, parse.Winner.Name, StringComparison.OrdinalIgnoreCase))
                        ))
                        .Where(i => !IsRuntimeWorn(i) && !IsRuntimeHeld(i))
                        .ToList();

                    // If none non-equipped, broaden search to any carried matching instances (but still prefer non-equipped earlier)
                    if (candidates.Count == 0)
                    {
                        candidates = player.Inventory
                            .Where(i => i != null && (
                                (!string.IsNullOrWhiteSpace(i.Name) && i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)) ||
                                i.ItemID == parse.Winner.ItemID
                            ))
                            .Where(i => !IsRuntimeWorn(i) && !IsRuntimeHeld(i))
                            .ToList();
                    }
                }
                else
                {
                    // No deterministic parser winner - try non-equipped matches first
                    candidates = FindNonEquippedMatches(player, itemExpr);
                    if (candidates.Count == 0)
                    {
                        // Fallback: broader match across inventory
                        var firstTok = itemExpr.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? itemExpr;
                        candidates = player.Inventory
                            .Where(i => i != null && !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                                        (!string.IsNullOrWhiteSpace(i.Name) && (i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)
                                         || i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase)
                                         || i.Name.IndexOf(firstTok, StringComparison.OrdinalIgnoreCase) >= 0)
                                        || (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase))))
                            .ToList();
                    }
                }

                if (candidates.Count == 0)
                {
                    return MessageHandler.System($"You are not carrying '{itemExpr}' (or it's equipped).");
                }

                // Combine available quantity across candidates
                int available = candidates.Sum(c => c.Quantity);

                if (quantity < 0) // 'all'
                {
                    if (available == 0) return MessageHandler.System($"You have no '{itemExpr}' to put into {container.Name}.");

                    int moved = 0;
                    foreach (var row in candidates.OrderByDescending(c => c.Quantity).ToList())
                    {
                        int take = row.Quantity;
                        var result = InventoryManager.TransferToContainer(_world, _dbConnection, player, row, container, take);
                        // propagate failure immediately
                        if (result == null) continue;
                        var text = result.Text ?? "";
                        if (text.StartsWith("You put", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("Partial:", StringComparison.OrdinalIgnoreCase) ||
                            text.IndexOf("already inside", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // successful-ish
                            moved += take;
                        }
                        else
                        {
                            // return the failure message to the player immediately
                            return result;
                        }
                    }

                    return MessageHandler.System($"You put {moved} x {candidates.First().Name} into {container.Name}.");
                }

                // numeric requested
                if (available < quantity)
                    return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                int remaining = quantity;
                int movedTotal = 0;
                // Prefer larger stacks first to minimize number of DB ops
                foreach (var row in candidates.OrderByDescending(c => c.Quantity).ToList())
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(remaining, row.Quantity);
                    var result = InventoryManager.TransferToContainer(_world, _dbConnection, player, row, container, take);
                    if (result == null)
                    {
                        // treat as failure and stop (defensive)
                        return MessageHandler.System("Failed to put the item due to a server error.");
                    }
                    var text = result.Text ?? "";
                    if (text.StartsWith("You put", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("Partial:", StringComparison.OrdinalIgnoreCase) ||
                        text.IndexOf("already inside", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        movedTotal += take;
                        remaining -= take;
                    }
                    else
                    {
                        // Propagate the failure message immediately (do not claim success)
                        return result;
                    }
                }

                return MessageHandler.System($"You put {movedTotal} x {candidates.First().Name} into {container.Name}.");
            }

            // No explicit quantity: resolve a single item and put one unit.
            var parseSingle = ItemParser.ResolveForInventoryCommand(player, itemExpr);
            Item? item = null;
            if (parseSingle.Winner != null)
            {
                item = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == parseSingle.Winner.ItemID);

                if (item != null && (IsRuntimeWorn(item) || IsRuntimeHeld(item)))
                {
                    var alt = FindNonEquippedMatches(player, itemExpr);
                    if (alt.Count > 0)
                    {
                        item = alt[0];
                        Console.WriteLine($"[PUT] Parser winner was equipped; using non-equipped alternative: ItemID={item.ItemID} Name='{item.Name}'");
                    }
                    else
                    {
                        Console.WriteLine($"[PUT] Parser winner is equipped and no non-equipped alternative found.");
                    }
                }
            }

            if (item == null)
            {
                var nonEquippedExact = FindNonEquippedMatches(player, itemExpr);
                if (nonEquippedExact.Count > 0)
                    item = nonEquippedExact[0];

                if (item == null)
                {
                    item = player.Inventory.FirstOrDefault(i =>
                        !string.IsNullOrWhiteSpace(i.Name) &&
                        (i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)
                         || i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase)
                         || (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)))
                    );
                }
            }

            if (item == null)
            {
                var firstTok = itemExpr.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? itemExpr;
                item = player.Inventory.FirstOrDefault(i =>
                    !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                    !string.IsNullOrWhiteSpace(i.Name) &&
                    i.Name.IndexOf(firstTok, StringComparison.OrdinalIgnoreCase) >= 0);

                if (item == null)
                {
                    item = player.Inventory.FirstOrDefault(i =>
                      !string.IsNullOrWhiteSpace(i.Name) &&
                      i.Name.IndexOf(firstTok, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }

            if (item == null)
            {
                Console.WriteLine($"[PUT] Could not resolve item '{itemExpr}' in player inventory. Inventory names: {string.Join(", ", player.Inventory.Select(i => i.Name))}");
                return MessageHandler.System($"You are not carrying '{itemExpr}'.");
            }

            var roomRef = player.CurrentRoom;
            bool containerAccessible = (player.Inventory?.Contains(container) ?? false) || (roomRef != null && roomRef.Inventory.Contains(container));
            Console.WriteLine($"[PUT] Container resolved: ItemID={container.ItemID} Name='{container.Name}' Accessible={containerAccessible}");

            if (!containerAccessible)
                return MessageHandler.System($"Container '{container.Name}' not accessible.");

            // Transfer a single unit
            return InventoryManager.TransferToContainer(_world, _dbConnection, player, item, container, 1);
        } // end HandlePutCommand

        // Helper: perform admin 'anchor' behavior. Kept private and focused so ProcessCommand stays compact.
        // - Adds 'Anchored' to Flags (deterministically via Flags property) and persists via ItemPersistence.
        // - Only room-owned items may be anchored. Admin only.
        // - Notifies room players via MessageHandler.SendSystem.
        // Returns a system Message for the caller (ProcessCommand) to return to the user.
        private Message? HandleAnchorCommand(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // extract argument after 'anchor '
            var arg = command.Length > 7 ? command.Substring(7).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(arg)) return MessageHandler.System("Usage: anchor <item>");

            // Prefer ItemParser to resolve the intended room item (supports descriptor/material tokens for look-like matches)
            var opts = new ItemParserOptions
            {
                IncludeDescriptorTokens = true,
                IncludeMaterialTokens = true,
                AllowStartsWith = true
            };

            ItemResolutionResult pres;
            try
            {
                pres = ItemParser.ResolveForGet(player, arg, opts);
            }
            catch
            {
                pres = new ItemResolutionResult { Reason = "Parser failure." };
            }

            if (pres.Winner == null)
            {
                if (pres.Candidates.Count == 0)
                    return MessageHandler.System($"No '{arg}' found in this room.");
                var list = string.Join(", ", pres.Candidates.Select(i => i.Name));
                return MessageHandler.System($"Multiple items match: {list}");
            }

            var target = pres.Winner;

            // Ensure the resolved instance is actually a room-owned item present in this room.
            if (!string.Equals(target.OwnerType ?? "", "Room", StringComparison.OrdinalIgnoreCase) ||
                target.OwnerID != room.RoomID)
            {
                // Attempt to locate a matching instance in the current room inventory by ItemID or Name
                var found = room.Inventory.FirstOrDefault(i => i != null && (i.ItemID == target.ItemID || string.Equals(i.Name, target.Name, StringComparison.OrdinalIgnoreCase)));
                if (found == null)
                    return MessageHandler.System($"No '{arg}' found in this room.");
                target = found;
            }

            try
            {
                var flags = target.Flags ?? new List<string>();
                if (!flags.Any(f => string.Equals(f, "Anchored", StringComparison.OrdinalIgnoreCase)))
                    flags.Add("Anchored");
                target.Flags = flags;
                MudPersistence.ItemPersistence.SaveItem(_dbConnection, target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN] Anchor failed: {ex.Message}");
                return MessageHandler.System("Failed to anchor item due to server error.");
            }

            // Notify room players
            try
            {
                foreach (var p in room.Players)
                    MessageHandler.SendSystem(p, $"{target.Name} has been anchored by an admin.");
            }
            catch { }

            return MessageHandler.System($"{target.Name} is now anchored.");
        }

        // -------------------------------
        // GET / TAKE (modified to accept requestedQuantity and prompt for stacks)
        // -------------------------------
        private Message? GetItem(Player player, string requested, int requestedQuantity = 0)
        {
            // requestedQuantity meanings:
            //   0  => no explicit quantity provided (transfer one)
            //  >0  => explicit numeric requested
            //  -1  => 'all' requested (take entire stack(s) or as much as available)

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Prefer ItemParser resolution but preserve matching heuristics.
            var parseRes = ItemParser.ResolveForGet(player, requested);
            List<Item> matches;

            if (parseRes.Winner != null)
            {
                matches = room.Inventory
                    .Where(i => i != null && (
                        i.ItemID == parseRes.Winner.ItemID ||
                        (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parseRes.Winner.TemplateID) && i.TemplateID == parseRes.Winner.TemplateID) ||
                        (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parseRes.Winner.Name) && string.Equals(i.Name, parseRes.Winner.Name, StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                if (matches.Count == 0)
                    matches = InventoryManager.FindRoomMatches(room, requested);
            }
            else
            {
                matches = InventoryManager.FindRoomMatches(room, requested);
            }

            if (matches == null || matches.Count == 0)
                return MessageHandler.System($"No '{requested}' found in this room.");

            // If explicit quantity requested, distribute across matching rows (no menu).
            if (requestedQuantity != 0)
            {
                // logical match helper
                bool SameLogical(Item a, Item b)
                {
                    if (!string.IsNullOrWhiteSpace(a.TemplateID) && !string.IsNullOrWhiteSpace(b.TemplateID))
                        return string.Equals(a.TemplateID, b.TemplateID, StringComparison.OrdinalIgnoreCase);
                    return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                }

                var representative = matches.First();
                var matchingRows = room.Inventory.Where(c => SameLogical(representative, c)).OrderByDescending(r => r.Quantity).ToList();
                int available = matchingRows.Sum(r => r.Quantity);

                if (requestedQuantity < 0) // 'all'
                {
                    if (available == 0) return MessageHandler.System($"No '{representative.Name}' remain here.");
                    foreach (var row in matchingRows)
                        InventoryManager.TransferToPlayer(_world, _dbConnection, player, row, row.Quantity);
                    return MessageHandler.System($"You picked up {available} x {representative.Name}.");
                }

                if (available < requestedQuantity)
                    return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                int remaining = requestedQuantity;
                int taken = 0;
                foreach (var row in matchingRows)
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(remaining, row.Quantity);
                    InventoryManager.TransferToPlayer(_world, _dbConnection, player, row, take);
                    taken += take;
                    remaining -= take;
                }

                return MessageHandler.System($"You picked up {taken} x {representative.Name}.");
            }

            // No explicit quantity: transfer 1 from the best candidate (no menu)
            var candidate = matches.OrderByDescending(m => !string.IsNullOrWhiteSpace(m.TemplateID) && string.Equals(m.TemplateID, requested, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                                   .ThenByDescending(m => m.Quantity)
                                   .First();

            if (candidate.Quantity > 1)
            {
                // treat "get item" with no qty as "take one"
                return InventoryManager.TransferToPlayer(_world, _dbConnection, player, candidate, 1);
            }

            return InventoryManager.TransferToPlayer(_world, _dbConnection, player, candidate, 1);
        } // end GetItem

        // New eat logic
        private Message? EatItem(Player player, string requested, bool isDrink = false)
        {
            // requested: "item" or "item from <container>"
            if (player == null) return MessageHandler.System("Invalid player.");
            if (string.IsNullOrWhiteSpace(requested)) return MessageHandler.System("Usage: eat <food> OR drink <liquid> OR <item> from <container>");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Helper tokenizer (same as used elsewhere)
            IEnumerable<string> Tokenize(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) yield break;
                int i = 0, n = s.Length;
                while (i < n)
                {
                    while (i < n && !char.IsLetterOrDigit(s[i])) i++;
                    int start = i;
                    while (i < n && char.IsLetterOrDigit(s[i])) i++;
                    if (i > start) yield return s.Substring(start, i - start).ToLowerInvariant();
                }
            }

            // Parse container hint: "X from Y"
            string itemExpr = requested;
            string? containerName = null;
            int idxFrom = requested.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
            if (idxFrom >= 0)
            {
                itemExpr = requested.Substring(0, idxFrom).Trim();
                containerName = requested.Substring(idxFrom + " from ".Length).Trim();
            }
            else
            {
                // SUPPORT: shorthand "drink water cup" (no 'from') -> treat last token as container if it matches an accessible container.
                // This preserves prior behavior when the last token is not a container.
                try
                {
                    var toks = requested.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (toks.Length >= 2)
                    {
                        var lastTok = toks[^1].Trim();
                        if (!string.IsNullOrWhiteSpace(lastTok))
                        {
                            // Use ItemParser first for robust descriptor/material-aware container matching
                            Item? maybeContainer = null;
                            try
                            {
                                var optsTry = new ItemParserOptions
                                {
                                    IncludeDescriptorTokens = true,
                                    IncludeMaterialTokens = true,
                                    AllowStartsWith = true
                                };
                                var presTry = ItemParser.ResolveForAny(player, lastTok, optsTry);
                                if (presTry?.Winner != null) maybeContainer = presTry.Winner;
                            }
                            catch { /* best-effort */ }

                            // Fallback to legacy lookup if parser didn't find anything
                            if (maybeContainer == null)
                            {
                                maybeContainer = InventoryManager.FindContainer(player, lastTok);
                            }

                            if (maybeContainer != null)
                            {
                                containerName = lastTok;
                                itemExpr = string.Join(" ", toks.Take(toks.Length - 1)).Trim();
                                Console.WriteLine($"[EAT] Interpreting shorthand: item='{itemExpr}' container='{containerName}' (resolved via parser/legacy)");
                            }
                        }
                    }
                }
                catch { /* best-effort; fall back to original parsing */ }
            }

            Item? toConsume = null;

            // Keep references so we can report accurate "remaining" when consuming from a container
            Item? sourceContainer = null;
            Item? chosenFromContainer = null;

            try
            {
                // If container specified, attempt to take one unit from it into player's inventory then consume
                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    // Prefer ItemParser first (descriptor/material aware). Be permissive for eat/drink flows.
                    Item? container = null;
                    try
                    {
                        var optsContainer = new ItemParserOptions
                        {
                            IncludeDescriptorTokens = true,
                            IncludeMaterialTokens = true,
                            AllowStartsWith = true
                        };
                        var pres = ItemParser.ResolveForAny(player, containerName, optsContainer);
                        if (pres?.Winner != null) container = pres.Winner;
                    }
                    catch { /* best-effort */ }

                    // Fallback to legacy lookup
                    if (container == null) container = InventoryManager.FindContainer(player, containerName);

                    if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                    sourceContainer = container;

                    // Ensure container accessible and unlocked
                    var lockMsg = KeyLocks.EnsureAccessible(_dbConnection, player, container);
                    if (lockMsg != null) return MessageHandler.System(lockMsg);

                    // Load items inside container
                    var contents = ItemPersistence.LoadItemsByOwner(_dbConnection, "Item", container.ItemID) ?? new List<Item>();
                    if (contents.Count == 0) return MessageHandler.System($"{container.Name} is empty.");

                    // Use parser but disable descriptor/material for food/liquid flows (they are not relevant for Eat/Drink)
                    var opts = new ItemParserOptions { IncludeDescriptorTokens = false, IncludeMaterialTokens = false, AllowStartsWith = true };
                    var presContents = ItemParser.ResolveCandidates(contents, player, itemExpr, opts);

                    if (presContents.Winner != null)
                    {
                        var chosen = presContents.Winner;
                        chosenFromContainer = chosen;

                        // If chosen is liquid and user typed 'eat' but not 'drink', we'll allow and show a "drink" text.
                        bool chosenIsLiquid = (chosen.Flags != null && chosen.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                              || (!string.IsNullOrEmpty(chosen.FlagsJson) && chosen.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                        // Transfer one unit from container into player's inventory
                        var takeMsg = InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, chosen, 1);
                        if (takeMsg == null) return MessageHandler.System("Failed to take item from container.");

                        var txt = takeMsg.Text ?? "";
                        if (txt.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (txt.IndexOf("inside", StringComparison.OrdinalIgnoreCase) >= 0 || txt.IndexOf("accessible", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            return takeMsg;
                        }

                        // Locate the transferred item in player's inventory. Prefer template match + flags.
                        Item? transferred = null;
                        if (!string.IsNullOrWhiteSpace(chosen.TemplateID))
                        {
                            transferred = player.Inventory?.FirstOrDefault(i =>
                                !string.IsNullOrWhiteSpace(i.TemplateID) &&
                                string.Equals(i.TemplateID, chosen.TemplateID, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(i.FlagsJson ?? "[]", chosen.FlagsJson ?? "[]", StringComparison.OrdinalIgnoreCase));
                        }

                        // fallback by name
                        if (transferred == null)
                        {
                            transferred = player.Inventory?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) && string.Equals(i.Name, chosen.Name, StringComparison.OrdinalIgnoreCase));
                        }

                        // Final fallback: any item with same template or name tokens
                        if (transferred == null && !string.IsNullOrWhiteSpace(chosen.Name))
                        {
                            var toks = Tokenize(chosen.Name).ToList();
                            if (toks.Count > 0 && player.Inventory != null)
                            {
                                transferred = player.Inventory.FirstOrDefault(it =>
                                {
                                    var its = Tokenize(it.Name).ToList();
                                    return toks.All(t => its.Any(x => x.StartsWith(t)));
                                });
                            }
                        }

                        if (transferred == null)
                        {
                            // Strange: item transfer succeeded but we couldn't find instance - inform user
                            return MessageHandler.System($"You took {chosen.Name}, but cannot locate it to consume. Try again.");
                        }

                        toConsume = transferred;
                        // If it was liquid and user executed 'drink' we respect wording; if user typed 'eat' and it is liquid, show 'drink' text.
                        isDrink = isDrink || chosenIsLiquid;
                    }
                    else
                    {
                        // Fallback to legacy matching when parser didn't return a winner
                        // find matches using tokenization and heuristics (prefer template match)
                        List<Item> matches = new List<Item>();

                        // exact template or name
                        matches = contents.Where(it =>
                            (!string.IsNullOrWhiteSpace(it.TemplateID) && string.Equals(it.TemplateID, itemExpr, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrWhiteSpace(it.Name) && string.Equals(it.Name, itemExpr, StringComparison.OrdinalIgnoreCase)))
                            .ToList();

                        // token-equals
                        if (matches.Count == 0)
                        {
                            var reqToks = Tokenize(itemExpr).ToList();
                            if (reqToks.Count > 0)
                            {
                                matches = contents.Where(it =>
                                {
                                    var nameTokens = Tokenize(it?.Name).ToList();
                                    if (reqToks.Any(rt => nameTokens.Any(nt => string.Equals(nt, rt, StringComparison.OrdinalIgnoreCase)))) return true;
                                    if (reqToks.All(rt => nameTokens.Any(nt => string.Equals(nt, rt, StringComparison.OrdinalIgnoreCase)))) return true;
                                    return false;
                                }).ToList();
                            }
                        }

                        // starts-with fallback
                        if (matches.Count == 0)
                        {
                            matches = contents.Where(it => !string.IsNullOrWhiteSpace(it.Name) && it.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase)).ToList();
                        }

                        if (matches.Count == 0)
                        {
                            // token start fallback
                            var reqToks2 = Tokenize(itemExpr).ToList();
                            if (reqToks2.Count > 0)
                            {
                                matches = contents.Where(it =>
                                {
                                    var nameTokens = Tokenize(it?.Name).ToList();
                                    return nameTokens.Any(nt => reqToks2.Any(rt => nt.StartsWith(rt, StringComparison.OrdinalIgnoreCase)));
                                }).ToList();
                            }
                        }

                        if (matches.Count == 0)
                            return MessageHandler.System($"No '{itemExpr}' found inside '{container.Name}'.");

                        // Prefer template id matches first
                        var chosen = matches.OrderByDescending(m => string.Equals(m.TemplateID, itemExpr, StringComparison.OrdinalIgnoreCase) ? 1 : 0).First();
                        chosenFromContainer = chosen;

                        // transfer same as above
                        bool chosenIsLiquid = (chosen.Flags != null && chosen.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                              || (!string.IsNullOrEmpty(chosen.FlagsJson) && chosen.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                        var takeMsg = InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, chosen, 1);
                        if (takeMsg == null) return MessageHandler.System("Failed to take item from container.");
                        var txt = takeMsg.Text ?? "";
                        if (txt.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (txt.IndexOf("inside", StringComparison.OrdinalIgnoreCase) >= 0 || txt.IndexOf("accessible", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            return takeMsg;
                        }

                        Item? transferred = null;
                        if (!string.IsNullOrWhiteSpace(chosen.TemplateID))
                        {
                            transferred = player.Inventory?.FirstOrDefault(i =>
                                !string.IsNullOrWhiteSpace(i.TemplateID) &&
                                string.Equals(i.TemplateID, chosen.TemplateID, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(i.FlagsJson ?? "[]", chosen.FlagsJson ?? "[]", StringComparison.OrdinalIgnoreCase));
                        }
                        if (transferred == null)
                        {
                            transferred = player.Inventory?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) && string.Equals(i.Name, chosen.Name, StringComparison.OrdinalIgnoreCase));
                        }
                        if (transferred == null && !string.IsNullOrWhiteSpace(chosen.Name))
                        {
                            var toks = Tokenize(chosen.Name).ToList();
                            if (toks.Count > 0 && player.Inventory != null)
                            {
                                transferred = player.Inventory.FirstOrDefault(it =>
                                {
                                    var its = Tokenize(it.Name).ToList();
                                    return toks.All(t => its.Any(x => x.StartsWith(t)));
                                });
                            }
                        }

                        if (transferred == null) return MessageHandler.System($"You took {chosen.Name}, but cannot locate it to consume. Try again.");
                        toConsume = transferred;
                        isDrink = isDrink || chosenIsLiquid;
                    }
                }
                else
                {
                    // No container - consume from player's inventory (existing behavior)
                    var optsInv = new ItemParserOptions { IncludeDescriptorTokens = false, IncludeMaterialTokens = false, AllowStartsWith = true };
                    var parse = ItemParser.ResolveForInventoryCommand(player, itemExpr, optsInv);
                    Item? item = null;

                    if (parse.Winner != null)
                    {
                        item = player.Inventory.FirstOrDefault(i =>
                            i != null && (i.ItemID == parse.Winner.ItemID ||
                                          (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parse.Winner.TemplateID) && i.TemplateID == parse.Winner.TemplateID) ||
                                          (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parse.Winner.Name) && string.Equals(i.Name, parse.Winner.Name, StringComparison.OrdinalIgnoreCase)))
                        );

                        // Reject attempts to eat/drink container items directly
                        if (item != null && string.Equals(item.ItemCategory ?? "", "Container", StringComparison.OrdinalIgnoreCase))
                        {
                            return MessageHandler.System($"You cannot {(isDrink ? "drink" : "eat")} a container. To consume its contents use: 'drink <liquid> from {item.Name}' or 'drink <liquid> <container>'.");
                        }
                    }

                    if (item == null)
                    {
                        var candidates = player.Inventory
                            .Where(i => i.ItemCategory != null && i.ItemCategory.Equals("Food", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        // If requested explicitly matches a carried item irrespective of category, allow it (preserve UX)
                        if (candidates.Count == 0)
                        {
                            // fallback attempts: exact name, starts with
                            item = player.Inventory.FirstOrDefault(i =>
                                (!string.IsNullOrWhiteSpace(i.Name) && i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)));

                            if (item == null)
                                item = player.Inventory.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase));

                            if (item == null)
                                return MessageHandler.System($"No food item matches '{itemExpr}'.");

                            // If a direct name/template match resolved to a container, reject
                            if (item != null && string.Equals(item.ItemCategory ?? "", "Container", StringComparison.OrdinalIgnoreCase))
                            {
                                return MessageHandler.System($"You cannot {(isDrink ? "drink" : "eat")} a container. To consume its contents use: 'drink <liquid> from {item.Name}' or 'drink <liquid> <container>'.");
                            }
                        }
                        else
                        {
                            // try exact/template match first among food items
                            item = candidates.FirstOrDefault(i =>
                                (!string.IsNullOrWhiteSpace(i.Name) && i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)));

                            if (item == null)
                                item = candidates.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase));

                            if (item == null)
                                return MessageHandler.System($"No food item matches '{itemExpr}'.");
                        }
                    }

                    toConsume = item;

                    // If the item is liquid and the player used 'eat' but not 'drink', consider this a drink operation
                    bool instIsLiquid = (toConsume.Flags != null && toConsume.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                        || (!string.IsNullOrEmpty(toConsume.FlagsJson) && toConsume.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (instIsLiquid) isDrink = true;
                }

                // Now toConsume is a player-carried item instance to consume
                if (toConsume == null) return MessageHandler.System("Nothing to eat or drink.");

                // Prevent consuming containers in any unexpected path
                if (string.Equals(toConsume.ItemCategory ?? "", "Container", StringComparison.OrdinalIgnoreCase))
                {
                    return MessageHandler.System($"You cannot {(isDrink ? "drink" : "eat")} a container. Use 'drink <liquid> from <container>' to consume the contents.");
                }

                // Determine if item provides healing (Heal flag)
                bool canHeal = (toConsume.Flags != null && toConsume.Flags.Any(f => f.Equals("Heal", StringComparison.OrdinalIgnoreCase)))
                               || (!string.IsNullOrEmpty(toConsume.FlagsJson) && toConsume.FlagsJson.IndexOf("\"Heal\"", StringComparison.OrdinalIgnoreCase) >= 0);

                int proposedHeal = 0;
                int appliedHeal = 0;
                if (canHeal)
                {
                    try
                    {
                        proposedHeal = Effects.ComputeHealAmount(toConsume, Effects.HealSource.Food);
                        appliedHeal = Effects.ComputeAppliedHeal(player, proposedHeal);
                    }
                    catch { /* best-effort */ }

                    // DEBUG LOG: show expected heal calculation and item weight/flags
                    try
                    {
                        Console.WriteLine($"[EAT][DEBUG] Player='{player?.Name}' Consuming ItemID={toConsume.ItemID} Template='{toConsume.TemplateID}' Name='{toConsume.Name}' Weight={toConsume.Weight:F3} FlagsJson='{toConsume.FlagsJson ?? "[]"}' ProposedHeal={proposedHeal} AppliedHeal={appliedHeal} PlayerHPBefore={player.Health}/{player.MaxHealth}");
                    }
                    catch { /*best-effort*/ }

                    if (appliedHeal > 0)
                    {
                        player.Health = Math.Min(player.MaxHealth, player.Health + appliedHeal);
                    }
                }

                // Consume one unit from the item (respect stacking)
                try
                {
                    if (toConsume.Quantity > 1)
                    {
                        toConsume.Quantity = Math.Max(0, toConsume.Quantity - 1);
                        try { ItemPersistence.SaveItem(_dbConnection, toConsume); } catch (Exception ex) { Console.WriteLine($"[EAT] Failed to save item quantity for ItemID={toConsume.ItemID}: {ex.Message}"); }

                        if (toConsume.Quantity <= 0)
                        {
                            if (player.Inventory.Contains(toConsume)) player.Inventory.Remove(toConsume);
                            try { ItemPersistence.DeleteItem(_dbConnection, toConsume.ItemID); } catch (Exception ex) { Console.WriteLine($"[EAT] Failed to delete item {toConsume.ItemID}: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        if (player.Inventory.Contains(toConsume)) player.Inventory.Remove(toConsume);
                        try { ItemPersistence.DeleteItem(_dbConnection, toConsume.ItemID); } catch (Exception ex) { Console.WriteLine($"[EAT] Failed to delete item {toConsume.ItemID}: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EAT] Consumption error for ItemID={toConsume?.ItemID}: {ex.Message}");
                }

                // Persist player health
                PersistPlayerHealth(player);

                // If consumption was from a container, compute remaining in the container (DB authoritative)
                int containerRemaining = -1;
                if (sourceContainer != null && chosenFromContainer != null)
                {
                    try
                    {
                        using var cmd = _dbConnection.CreateCommand();
                        if (!string.IsNullOrWhiteSpace(chosenFromContainer.TemplateID))
                        {
                            cmd.CommandText = @"SELECT SUM(COALESCE(Quantity,1)) FROM Item
                                WHERE OwnerType = 'Item' AND OwnerID = @cid AND TemplateID = @template AND Flags = @flags;";
                            cmd.Parameters.AddWithValue("@cid", sourceContainer.ItemID);
                            cmd.Parameters.AddWithValue("@template", chosenFromContainer.TemplateID);
                            cmd.Parameters.AddWithValue("@flags", chosenFromContainer.FlagsJson ?? "[]");
                        }
                        else
                        {
                            cmd.CommandText = @"SELECT SUM(COALESCE(Quantity,1)) FROM Item
                                WHERE OwnerType = 'Item' AND OwnerID = @cid AND Name = @name;";
                            cmd.Parameters.AddWithValue("@cid", sourceContainer.ItemID);
                            cmd.Parameters.AddWithValue("@name", chosenFromContainer.Name ?? "");
                        }

                        var obj = cmd.ExecuteScalar();
                        if (obj == null || obj is DBNull) containerRemaining = 0;
                        else
                        {
                            if (obj is long l) containerRemaining = Convert.ToInt32(l);
                            else if (obj is int iv) containerRemaining = iv;
                            else containerRemaining = Convert.ToInt32(obj);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[EAT][DEBUG] Failed to compute container remaining for ItemTemplate='{chosenFromContainer.TemplateID}' in ContainerID={sourceContainer.ItemID}: {ex.Message}");
                        containerRemaining = -1;
                    }
                }

                // Build response text
                string verb = isDrink ? "drink" : "eat";
                string prettyName = toConsume.Name ?? toConsume.TemplateID ?? "something";
                string msg = $"You {verb} {prettyName}.";
                if (canHeal && appliedHeal > 0) msg += $" You feel better (+{appliedHeal} HP).";

                // Show remaining: prefer container remaining when consumption originated from a container.
                if (sourceContainer != null)
                {
                    if (containerRemaining > 0) msg += $" Remaining in {sourceContainer.Name}: {containerRemaining}.";
                    // else container empty -> no remaining message
                }
                else
                {
                    if (toConsume.Quantity > 0) msg += $" Remaining: {toConsume.Quantity}.";
                }

                // Additional debug: log applied heal and remaining
                if (canHeal)
                {
                    try
                    {
                        Console.WriteLine($"[EAT][DEBUG] AppliedHeal={appliedHeal} PlayerHPAfter={player.Health}/{player.MaxHealth} ContainerRemaining={containerRemaining} ToConsumeQty={toConsume.Quantity}");
                    }
                    catch { }
                }

                return MessageHandler.System(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EAT] Unexpected error: {ex}");
                return MessageHandler.System("Failed to eat/drink due to a server error.");
            }
        } // end EatItem

        // Persist player's current Health to the database.
        // Centralized so future health updates (spells, regen, effects) reuse the same logic and logging.
        private void PersistPlayerHealth(Player player)
        {
            if (player == null) return;

            // Primary save path: delegate to PlayerPersistence which already routes writes
            // through the single-writer DbWriteQueue when available.
            try
            {
                PlayerPersistence.SavePlayer(_dbConnection, player);
                // Success — early return to avoid additional fallback attempts.
                try { PlayerPersistence.LoadExtendedCombat(_dbConnection, player); } catch { }
                try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }
                return;
            }
            catch (Exception ex)
            {
                // Log and attempt queued/fallback persists; do not throw.
                Console.WriteLine($"[PLAYER] SavePlayer via PlayerPersistence failed: {ex.Message}");
            }

            // Secondary: attempt to enqueue a small writer job to persist the minimal health state.
            // This serializes with other writer activity and avoids ad-hoc direct writes from
            // the networking/command layers.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    if (player.DbPlayerID != 0)
                    {
                        cmd.CommandText = "UPDATE Player SET Health = @h WHERE PlayerID = @id";
                        cmd.Parameters.AddWithValue("@h", player.Health);
                        cmd.Parameters.AddWithValue("@id", player.DbPlayerID);
                    }
                    else
                    {
                        // Fallback to name-based update when numeric id is not available.
                        cmd.CommandText = "UPDATE Player SET Health = @h WHERE Name = @name";
                        cmd.Parameters.AddWithValue("@h", player.Health);
                        cmd.Parameters.AddWithValue("@name", player.Name ?? "");
                    }
                    cmd.ExecuteNonQuery();
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));

                // Refresh extended combat fields and notify client (best-effort).
                try { PlayerPersistence.LoadExtendedCombat(_dbConnection, player); } catch { }
                try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct legacy fallback below.
            }
            catch (Exception exQueue)
            {
                Console.WriteLine($"[PLAYER] Queued health persist failed: {exQueue.Message}");
            }

            // Final conservative fallback: direct write on provided connection (preserve prior behavior).
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
                Console.WriteLine($"[PLAYER][FALLBACK] Direct health persist failed: {ex.Message}");
            }

            // Refresh extended combat fields and notify client (best-effort).
            try { PlayerPersistence.LoadExtendedCombat(_dbConnection, player); } catch { }
            try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }
        }

        // Add this helper method inside the CommandHandler class (near other private helpers)
        private (int quantity, bool explicitQuantity, string itemExpr, string containerName, string? error) ParseQuantityItemContainer(List<string> tokens, int startIdx)
        {
            // tokens: full token list; startIdx = index of first token after command word (e.g. 1 for "put"/"get")
            // Enforce strict-separator policy: only treat a container when an explicit separator
            // (in/into/from/inside) is present. This avoids misinterpreting descriptors/adjectives
            // as container names when the user supplies multi-word item expressions like "pot polished".
            if (tokens == null || tokens.Count <= startIdx)
                return (1, false, "", "", "Usage: <command> <item> [container] or <n|all> <item> <container>");

            int quantity = 1;
            bool explicitQuantity = false;
            int idx = startIdx;

            // optional numeric prefix or keyword 'all'
            if (tokens.Count > idx)
            {
                var tok = tokens[idx].Trim().ToLowerInvariant();
                if (int.TryParse(tokens[idx], out var parsedQty))
                {
                    quantity = Math.Max(1, parsedQty);
                    explicitQuantity = true;
                    idx++;
                }
                else if (tok == "all" || tok == "everything" || tok == "entire")
                {
                    // -1 denotes "all"
                    quantity = -1;
                    explicitQuantity = true;
                    idx++;
                }
            }

            if (tokens.Count <= idx) return (quantity, explicitQuantity, "", "", "Usage: <command> <item> [container] or <n|all> <item> <container>");

            // Look for separator token that indicates container follows
            // Accept: in, into, from, inside
            var separators = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "in", "into", "from", "inside" };
            int sepIndex = -1;
            for (int i = idx; i < tokens.Count; i++)
            {
                if (separators.Contains(tokens[i]))
                {
                    sepIndex = i;
                    break;
                }
            }

            string itemExpr;
            string containerName = "";

            if (sepIndex >= 0)
            {
                // everything before separator is item, everything after is container (support multi-word container)
                if (sepIndex == idx) return (quantity, explicitQuantity, "", "", "Usage: missing item before container separator.");
                itemExpr = string.Join(" ", tokens.Skip(idx).Take(sepIndex - idx));
                if (sepIndex + 1 >= tokens.Count) return (quantity, explicitQuantity, "", "", "Usage: missing container name after separator.");
                containerName = string.Join(" ", tokens.Skip(sepIndex + 1));
            }
            else
            {
                // No separator: treat the entire remaining text as the item expression.
                // This is the strict behavior you requested: both-object commands MUST use a separator.
                itemExpr = string.Join(" ", tokens.Skip(idx));
                containerName = "";
            }

            itemExpr = itemExpr?.Trim() ?? "";
            containerName = containerName?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(itemExpr)) return (quantity, explicitQuantity, "", "", "Usage: missing item name.");
            return (quantity, explicitQuantity, itemExpr, containerName, null);
        } // end ParseQuantityItemContainer

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

            // Mark this room message as an explicit look so the client displays "You look at ..." text.
            return MessageHandler.Room(room, npcs, players, _world, null, explicitLook: true);
        } // end Look

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
        // DROP
        // -------------------------------

        private Message? DropItem(Player player, string requested, int requestedQuantity = 0)
        {
            // requestedQuantity meanings:
            //   0  => no explicit quantity provided (drop one)
            //  >0  => explicit numeric requested
            //  -1  => 'all' requested (drop entire stack(s) or as much as available)

            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Prefer ItemParser for inventory resolution; still preserve matching heuristics.
            var parseRes = ItemParser.ResolveForInventoryCommand(player, requested);
            List<Item> matches;

            if (parseRes.Winner != null)
            {
                matches = player.Inventory
                    .Where(i => i != null && (
                        i.ItemID == parseRes.Winner.ItemID ||
                        (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(parseRes.Winner.TemplateID) && i.TemplateID == parseRes.Winner.TemplateID) ||
                        (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(parseRes.Winner.Name) && string.Equals(i.Name, parseRes.Winner.Name, StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                // If parser returned a winner but matches include an equipped instance, try to find non-equipped alternatives.
                var nonEquipped = matches.Where(m => !IsRuntimeWorn(m) && !IsRuntimeHeld(m)).ToList();
                if (nonEquipped.Count > 0)
                {
                    matches = nonEquipped;
                }
                else
                {
                    var alt = FindNonEquippedMatches(player, requested);
                    if (alt.Count > 0)
                        matches = alt;
                }

                if (matches.Count == 0)
                {
                    matches = player.Inventory
                        .Where(i => (i?.Name != null && i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
                                 || (!string.IsNullOrWhiteSpace(i?.TemplateID) && i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
            }
            else
            {
                matches = player.Inventory
                    .Where(i => !string.IsNullOrWhiteSpace(i.Name) &&
                                (i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                if (matches.Count == 0)
                {
                    matches = FindNonEquippedMatches(player, requested);
                    if (matches.Count == 0)
                    {
                        matches = player.Inventory
                            .Where(i => !string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                }
            }

            if (matches.Count == 0)
                return MessageHandler.System($"You are not carrying '{requested}'.");

            // Use runtime flags for worn/held detection
            var droppable = matches.Where(m => !IsRuntimeWorn(m) && !IsRuntimeHeld(m)).ToList();

            if (droppable.Count == 0)
            {
                bool anyWorn = matches.Any(m => IsRuntimeWorn(m));
                bool anyHeld = matches.Any(m => IsRuntimeHeld(m));

                if (anyWorn && anyHeld)
                    return MessageHandler.System($"Those items are currently worn or held. Please unwear or unhold them before dropping.");
                if (anyWorn)
                    return MessageHandler.System($"That item is currently worn. Please unwear it before dropping.");
                if (anyHeld)
                    return MessageHandler.System($"That item is currently held. Please unhold it before dropping.");

                return MessageHandler.System($"You cannot drop that item while it is equipped.");
            }

            // Now operate only on droppable items
            matches = droppable;

            // Helper to determine logical equality (prefer TemplateID)
            bool SameLogical(Item a, Item b)
            {
                if (!string.IsNullOrWhiteSpace(a.TemplateID) && !string.IsNullOrWhiteSpace(b.TemplateID))
                    return string.Equals(a.TemplateID, b.TemplateID, StringComparison.OrdinalIgnoreCase);
                return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            // If explicit quantity requested, fulfill it immediately (no menu)
            if (requestedQuantity != 0)
            {
                // Representative for matching identity
                var representative = matches.First();

                var matchingRows = matches.Where(c => SameLogical(representative, c)).OrderByDescending(r => r.Quantity).ToList();
                int available = matchingRows.Sum(r => r.Quantity);

                if (requestedQuantity < 0) // all
                {
                    if (available == 0) return MessageHandler.System($"You have no '{representative.Name}' to drop.");
                    int dropped = 0;
                    foreach (var row in matchingRows)
                    {
                        try
                        {
                            InventoryManager.TransferToRoom(_world, _dbConnection, player, row, row.Quantity);
                            dropped += row.Quantity;
                        }
                        catch { /* best-effort */ }
                    }
                    return MessageHandler.System($"You dropped {dropped} x {representative.Name}.");
                }

                if (available < requestedQuantity)
                    return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                int remaining = requestedQuantity;
                int droppedTotal = 0;
                foreach (var row in matchingRows)
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(remaining, row.Quantity);
                    try
                    {
                        InventoryManager.TransferToRoom(_world, _dbConnection, player, row, take);
                        droppedTotal += take;
                        remaining -= take;
                    }
                    catch { /* continue best-effort */ }
                }

                return MessageHandler.System($"You dropped {droppedTotal} x {representative.Name}.");
            }

            // No explicit quantity: drop a single unit from the best candidate (prefer larger stacks)
            var candidate = matches.OrderByDescending(m => m.Quantity).First();

            if (candidate.Quantity > 1)
            {
                // treat "drop item" as dropping one unit
                return InventoryManager.TransferToRoom(_world, _dbConnection, player, candidate, 1);
            }

            // single-qty item
            return InventoryManager.TransferToRoom(_world, _dbConnection, player, candidate, 1);
        } // end DropItem

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

            string input = ExtractCommandText(rawCommand).Trim();
            var pa = player.PendingAction!;

            bool TryHandleCancel()
            {
                if (string.Equals(input, "cancel", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }
                    return true;
                }
                return false;
            }

            // Handle LockKey pending action (unchanged)
            if (pa.Command == "LockKey")
            {
                var data = pa.Data as KeyLocks.LockPendingData;
                if (data == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "NameKey")
                {
                    string label = input;
                    if (string.IsNullOrWhiteSpace(label) || label.Length > 100)
                        return MessageHandler.System("Invalid label. Enter a non-empty label up to 100 characters.");

                    player.PendingAction = null!;
                    return KeyLocks.LockContainer(_dbConnection, player, data.Container, data.Key, label.Trim());
                }
            }

            // Delegate auth pending
            var authHandled = _auth.HandleAuthPending(player, rawCommand);
            if (authHandled != null) return authHandled;

            // Admin pending
            try
            {
                var adminHandled = _adminService.HandlePendingAction(player, pa, input);
                if (adminHandled != null) return adminHandled;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PENDING][ADMIN] HandlePendingAction failed: {ex}");
                player.PendingAction = null!;
                return MessageHandler.System("Server error while processing pending admin action.");
            }

            // AttackSelect pending
            if (pa.Command == "AttackSelect")
            {
                var data = pa.Data as NpcPendingData;
                if (data == null || data.Matches == null || data.Matches.Count == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (!int.TryParse(input, out int idx) || idx < 1 || idx > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the target to attack.");

                    var chosen = data.Matches[idx - 1];

                    var room = player.CurrentRoom;
                    if (room == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("You are not in a valid room.");
                    }

                    var live = room.Npcs.FirstOrDefault(n => n != null && n.NpcID == chosen.NpcID);
                    if (live == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("That target is no longer here.");
                    }

                    player.PendingAction = null!;
                    return CombatManager.StartOrJoin(player, live, _world, _dbConnection);
                }
            }

            // --- Pending: GetFromContainer ---
            if (pa.Command == "GetFromContainer")
            {
                var data = pa.Data as GetPendingData;
                if (data == null || data.Matches == null || data.Matches.Count == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (TryHandleCancel()) return MessageHandler.System("Selection cancelled.");

                    if (!int.TryParse(input, out int sel) || sel < 1 || sel > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to take.");

                    var chosen = data.Matches[sel - 1];

                    var room = player.CurrentRoom;
                    if (room == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("You are not in a valid room.");
                    }

                    Item? container = null;
                    if (data.ContainerItemId.HasValue)
                    {
                        container = (player.Inventory ?? new List<Item>()).FirstOrDefault(i => i.ItemID == data.ContainerItemId.Value)
                                    ?? room.Inventory.FirstOrDefault(i => i.ItemID == data.ContainerItemId.Value);
                    }

                    if (container == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("The container is no longer accessible.");
                    }

                    // Reload container contents (DB authoritative)
                    var contents = ItemPersistence.LoadItemsByOwner(_dbConnection, "Item", container.ItemID) ?? new List<Item>();
                    var live = contents.FirstOrDefault(c => c.ItemID == chosen.ItemID);
                    if (live == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("That item is no longer in the container.");
                    }

                    // If explicit quantity was provided, distribute across matching rows in the container
                    if (data.ExplicitQuantity)
                    {
                        // helper to match same logical item: prefer TemplateID when present, otherwise Name equality
                        bool SameLogical(Item a, Item b)
                        {
                            if (!string.IsNullOrWhiteSpace(a.TemplateID) && !string.IsNullOrWhiteSpace(b.TemplateID))
                                return string.Equals(a.TemplateID, b.TemplateID, StringComparison.OrdinalIgnoreCase);
                            return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        }

                        // 'all' requested: transfer all matching rows
                        if (data.RequestedQuantity < 0)
                        {
                            var matchingRows = contents.Where(c => SameLogical(live, c)).ToList();
                            int total = matchingRows.Sum(r => r.Quantity);
                            if (total == 0)
                            {
                                player.PendingAction = null!;
                                return MessageHandler.System($"No '{live.Name}' remain in {container.Name}.");
                            }

                            foreach (var row in matchingRows.OrderByDescending(r => r.Quantity).ToList())
                            {
                                try { InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, row, row.Quantity); }
                                catch { /* best-effort per-row */ }
                            }

                            player.PendingAction = null!;
                            return MessageHandler.System($"You took {total} x {live.Name} from {container.Name}.");
                        }

                        // explicit numeric request: fulfill across rows starting with selected row
                        int remaining = data.RequestedQuantity;
                        // gather matching rows, with chosen row first
                        var matchingOrdered = contents
                            .Where(c => SameLogical(live, c))
                            .OrderByDescending(c => c.ItemID == live.ItemID ? 1 : 0) // chosen first
                            .ThenByDescending(c => c.Quantity) // larger stacks earlier
                            .ToList();

                        int available = matchingOrdered.Sum(r => r.Quantity);
                        if (available == 0)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System($"No '{live.Name}' remain in {container.Name}.");
                        }
                        if (available < remaining)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");
                        }

                        int takenTotal = 0;
                        foreach (var row in matchingOrdered)
                        {
                            if (remaining <= 0) break;
                            int take = Math.Min(remaining, row.Quantity);
                            try
                            {
                                InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, row, take);
                                takenTotal += take;
                                remaining -= take;
                            }
                            catch
                            {
                                // ignore per-row failure and continue best-effort
                            }
                        }

                        player.PendingAction = null!;
                        return MessageHandler.System($"You took {takenTotal} x {live.Name} from {container.Name}.");
                    }

                    // No explicit quantity — if chosen row is stacked, prompt for how many; otherwise transfer one.
                    if (live.Quantity > 1)
                    {
                        player.PendingAction = new PendingAction
                        {
                            Command = "GetStack",
                            Step = "ChooseQuantity",
                            Data = new GetPendingData { Matches = new List<Item> { live }, ContainerItemId = container.ItemID }
                        };
                        return MessageHandler.System($"There are {live.Quantity} of '{live.Name}' in {container.Name}. How many do you want to take? (type a number or 'all')");
                    }

                    player.PendingAction = null!;
                    return InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, live, 1);
                }
            }

            // --- Pending: GetStack ---
            if (pa.Command == "GetStack")
            {
                var data = pa.Data as GetPendingData;
                if (data == null || data.Matches == null || data.Matches.Count == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "ChooseQuantity")
                {
                    if (TryHandleCancel()) return MessageHandler.System("Selection cancelled.");

                    var item = data.Matches[0];

                    // Determine source: room vs container
                    Item? live = null;
                    if (data.ContainerItemId.HasValue)
                    {
                        var contents = ItemPersistence.LoadItemsByOwner(_dbConnection, "Item", data.ContainerItemId.Value) ?? new List<Item>();
                        live = contents.FirstOrDefault(i => i.ItemID == item.ItemID);
                    }
                    else
                    {
                        var room = player.CurrentRoom;
                        if (room != null)
                        {
                            live = room.Inventory.FirstOrDefault(i => i.ItemID == item.ItemID) ?? ItemPersistence.LoadItemsByOwner(_dbConnection, "Room", room.RoomID)?.FirstOrDefault(i => i.ItemID == item.ItemID);
                        }
                    }

                    if (live == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("That item is no longer available.");
                    }

                    if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        if (data.ContainerItemId.HasValue)
                        {
                            var container = (player.Inventory ?? new List<Item>()).FirstOrDefault(i => i.ItemID == data.ContainerItemId.Value)
                                            ?? player.CurrentRoom?.Inventory.FirstOrDefault(i => i.ItemID == data.ContainerItemId.Value);
                            return InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, live, live.Quantity);
                        }
                        return InventoryManager.TransferToPlayer(_world, _dbConnection, player, live, live.Quantity);
                    }

                    if (!int.TryParse(input, out int qty) || qty < 1)
                        return MessageHandler.System("Invalid number. Enter a positive integer or 'all'.");

                    qty = Math.Min(qty, live.Quantity);
                    player.PendingAction = null!;
                    if (data.ContainerItemId.HasValue)
                    {
                        var container = (player.Inventory ?? new List<Item>()).FirstOrDefault(i => i.ItemID == data.ContainerItemId.Value)
                                        ?? player.CurrentRoom?.Inventory.FirstOrDefault(i => i.ItemID == data.ContainerItemId.Value);
                        return InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, live, qty);
                    }
                    else
                    {
                        return InventoryManager.TransferToPlayer(_world, _dbConnection, player, live, qty);
                    }
                }
            }

            // --- Pending: GetItem ---
            if (pa.Command == "GetItem")
            {
                var data = pa.Data as GetPendingData;
                if (data == null || data.Matches == null || data.Matches.Count == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "SelectIndex")
                {
                    if (TryHandleCancel()) return MessageHandler.System("Selection cancelled.");

                    if (!int.TryParse(input, out int sel) || sel < 1 || sel > data.Matches.Count)
                        return MessageHandler.System("Invalid selection. Type the number of the item to take.");

                    var chosen = data.Matches[sel - 1];

                    // If explicit quantity was provided earlier, honor by distributing across room rows (similar to container flow)
                    if (data.ExplicitQuantity)
                    {
                        var room = player.CurrentRoom;
                        if (room == null)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System("You are not in a valid room.");
                        }

                        // Reload room contents
                        var contents = room.Inventory.ToList();
                        // helper: match by TemplateID or Name
                        bool SameLogical(Item a, Item b)
                        {
                            if (!string.IsNullOrWhiteSpace(a.TemplateID) && !string.IsNullOrWhiteSpace(b.TemplateID))
                                return string.Equals(a.TemplateID, b.TemplateID, StringComparison.OrdinalIgnoreCase);
                            return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        }

                        var liveChosen = contents.FirstOrDefault(i => i.ItemID == chosen.ItemID)
                                        ?? ItemPersistence.LoadItemsByOwner(_dbConnection, "Room", room.RoomID)?.FirstOrDefault(i => i.ItemID == chosen.ItemID);

                        if (liveChosen == null)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System("That item is no longer here.");
                        }

                        if (data.RequestedQuantity < 0)
                        {
                            var matchingRows = contents.Where(c => SameLogical(liveChosen, c)).ToList();
                            int total = matchingRows.Sum(r => r.Quantity);
                            foreach (var row in matchingRows.OrderByDescending(r => r.Quantity).ToList())
                                try { InventoryManager.TransferToPlayer(_world, _dbConnection, player, row, row.Quantity); } catch { }
                            player.PendingAction = null!;
                            return MessageHandler.System($"You picked up {total} x {liveChosen.Name}.");
                        }

                        int remaining = data.RequestedQuantity;
                        var matchingOrdered = contents
                            .Where(c => SameLogical(liveChosen, c))
                            .OrderByDescending(c => c.ItemID == liveChosen.ItemID ? 1 : 0)
                            .ThenByDescending(c => c.Quantity)
                            .ToList();

                        int available = matchingOrdered.Sum(r => r.Quantity);
                        if (available < remaining)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");
                        }

                        int taken = 0;
                        foreach (var row in matchingOrdered)
                        {
                            if (remaining <= 0) break;
                            int take = Math.Min(remaining, row.Quantity);
                            try
                            {
                                InventoryManager.TransferToPlayer(_world, _dbConnection, player, row, take);
                                taken += take;
                                remaining -= take;
                            }
                            catch { }
                        }

                        player.PendingAction = null!;
                        return MessageHandler.System($"You picked up {taken} x {liveChosen.Name}.");
                    }

                    // If chosen is a stack, prompt for quantity
                    if (chosen.Quantity > 1)
                    {
                        player.PendingAction = new PendingAction
                        {
                            Command = "GetStack",
                            Step = "ChooseQuantity",
                            Data = new GetPendingData { Matches = new List<Item> { chosen } }
                        };
                        return MessageHandler.System($"There are {chosen.Quantity} of '{chosen.Name}'. How many do you want to get? (type a number or 'all')");
                    }

                    // Single quantity - transfer
                    var room2 = player.CurrentRoom;
                    if (room2 == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("You are not in a valid room.");
                    }

                    var liveSingle = room2.Inventory.FirstOrDefault(i => i.ItemID == chosen.ItemID)
                                     ?? ItemPersistence.LoadItemsByOwner(_dbConnection, "Room", room2.RoomID)?.FirstOrDefault(i => i.ItemID == chosen.ItemID);

                    if (liveSingle == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("That item is no longer here.");
                    }

                    player.PendingAction = null!;
                    return InventoryManager.TransferToPlayer(_world, _dbConnection, player, liveSingle, 1);
                }

                // ChooseQuantity for GetItem (unchanged behavior)
                if (pa.Step == "ChooseQuantity")
                {
                    if (TryHandleCancel()) return MessageHandler.System("Selection cancelled.");

                    if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        int totalAvailable = data.Matches.Sum(m => m.Quantity);
                        int remaining = totalAvailable;
                        foreach (var it in data.Matches.OrderByDescending(m => m.Quantity).ToList())
                        {
                            int take = Math.Min(remaining, it.Quantity);
                            InventoryManager.TransferToPlayer(_world, _dbConnection, player, it, take);
                            remaining -= take;
                            if (remaining <= 0) break;
                        }

                        player.PendingAction = null!;
                        return MessageHandler.System($"You picked up {totalAvailable} x {data.Matches[0].Name}.");
                    }

                    if (!int.TryParse(input, out int want) || want < 1)
                        return MessageHandler.System("Invalid number. Enter a positive integer or 'all'.");

                    int combined = data.Matches.Sum(m => m.Quantity);
                    if (want > combined)
                        return MessageHandler.System($"Only {combined} available. Enter a number up to {combined}.");

                    int remainingWanted = want;
                    foreach (var it in data.Matches.OrderByDescending(m => m.Quantity).ToList())
                    {
                        int take = Math.Min(remainingWanted, it.Quantity);
                        InventoryManager.TransferToPlayer(_world, _dbConnection, player, it, take);
                        remainingWanted -= take;
                        if (remainingWanted <= 0) break;
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"You picked up {want} x {data.Matches[0].Name}.");
                }
            }

            return null;
        } // end CheckPendingAction

        // Helper class for GetItem / DropItem pending data
        private class GetPendingData
        {
            public List<Item> Matches { get; set; } = new List<Item>();

            // Optional: when selection comes from a container, store the container ItemID so completion can resolve it.
            public int? ContainerItemId { get; set; } = null;

            // Preserve original request quantity (and whether it was explicit) so selection-step can honor it.
            // RequestedQuantity: >0 explicit numeric, -1 means 'all', 0 means none supplied.
            public int RequestedQuantity { get; set; } = 0;
            public bool ExplicitQuantity { get; set; } = false;
        } // end GetPendingData

        // NEW: pending data DTO for NPC selection prompts
        private class NpcPendingData
        {
            public List<Npc> Matches { get; set; } = new List<Npc>();
        } // end NpcPendingData

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

        // --- Insert this helper inside the CommandHandler class (near other private helpers) ---
        private List<Item> FindNonEquippedMatches(Player player, string itemExpr)
        {
            if (player == null || string.IsNullOrWhiteSpace(itemExpr) || player.Inventory == null) return new List<Item>();

            // Exact name/template first (non-equipped)
            var exact = player.Inventory
                .Where(i => i != null &&
                       !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                       (
                           (!string.IsNullOrWhiteSpace(i.Name) && i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase)) ||
                           (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase))
                       ))
                .ToList();
            if (exact.Count > 0) return exact;

            // Starts-with match
            var starts = player.Inventory
                .Where(i => i != null &&
                       !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                       (!string.IsNullOrWhiteSpace(i.Name) && i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (starts.Count > 0) return starts;

            // Fallback: first-token or contains
            var firstTok = itemExpr.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? itemExpr;
            var contains = player.Inventory
                .Where(i => i != null &&
                       !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                       (!string.IsNullOrWhiteSpace(i.Name) && i.Name.IndexOf(firstTok, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            return contains;
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

        private static string NormalizeCommandAlias(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token;
            switch (token.Trim().ToLowerInvariant())
            {
                case "u": return "up";
                case "d": return "down";
                default: return token;
            }
        }
    }
}