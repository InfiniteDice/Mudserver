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

            // Inspect container (new): inspect <containername>
            // Inspect container (new): inspect <containername>
            if (command.StartsWith("inspect ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return MessageHandler.System("Usage: inspect <container>");
                var containerName = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(containerName)) return MessageHandler.System("Usage: inspect <container>");

                var container = InventoryManager.FindContainer(player, containerName);
                if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                // NEW: enforce lock check
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

                    var cap = InventoryManager.ParseCapacity(container);
                    string capText = cap.HasValue ? $"{cap.Value}" : "unlimited";

                    if (contents.Count == 0)
                    {
                        string emptyMsg = $"{container.Name} is empty. Capacity: {capText}. Used: {usedWeight}.";
                        return MessageHandler.System(emptyMsg);
                    }

                    var lines = new List<string>();
                    for (int i = 0; i < contents.Count; i++)
                    {
                        var it = contents[i];
                        var qtyPart = it.Quantity > 1 ? $" (x{it.Quantity})" : "";
                        var totalWeight = it.Weight * Math.Max(1, it.Quantity);
                        lines.Add($"{i + 1}) {it.Name}{qtyPart} — wt {totalWeight}");
                    }

                    var header = $"{container.Name} contents (Used: {usedWeight} / Capacity: {capText}):";
                    return MessageHandler.System(header + "\n" + string.Join("\n", lines));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[INSPECT] Failed to inspect container '{containerName}': {ex}");
                    return MessageHandler.System("Failed to inspect container due to a server error.");
                }
            }

            // --- GET / TAKE command handling: added container lock check & quantity support ---
            if (command.StartsWith("get ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("take ", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count < 2) return MessageHandler.System("Usage: get <item> [container] OR get <n|all> <item> <container>");

                // Replace the existing "get" branch body in ProcessCommand with this block
                // inside ProcessCommand GET/TAKE branch - replace existing ParseQuantityItemContainer unpacking
                var (quantity, explicitQuantity, itemExpr, containerName, getErr) = ParseQuantityItemContainer(tokens, 1);
                if (getErr != null) return MessageHandler.System(getErr);

                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    var container = InventoryManager.FindContainer(player, containerName);
                    if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                    // NEW: enforce lock check before attempting to read container contents
                    var lockMsg = KeyLocks.EnsureAccessible(_dbConnection, player, container);
                    if (lockMsg != null) return MessageHandler.System(lockMsg);

                    // Load and resolve inside container (unchanged)
                    List<Item> containerItems = ItemPersistence.LoadItemsByOwner(_dbConnection, "Item", container.ItemID);
                    var matches = containerItems
                        .Where(i => !string.IsNullOrEmpty(i.Name) &&
                                    (i.Name.Equals(itemExpr, StringComparison.OrdinalIgnoreCase) ||
                                     (!string.IsNullOrEmpty(i.TemplateID) && i.TemplateID.Equals(itemExpr, StringComparison.OrdinalIgnoreCase))))
                        .ToList();

                    if (matches.Count == 0)
                    {
                        matches = containerItems.Where(i => !string.IsNullOrEmpty(i.Name) && i.Name.StartsWith(itemExpr, StringComparison.OrdinalIgnoreCase)).ToList();
                    }

                    if (matches.Count == 0)
                        return MessageHandler.System($"No '{itemExpr}' found inside '{container.Name}'.");

                    if (matches.Count == 1)
                    {
                        // If player explicitly requested a quantity use that, otherwise default to single-unit for container-get
                        int qtyToTake = explicitQuantity ? (quantity < 0 ? matches[0].Quantity : quantity) : 1;
                        return InventoryManager.TransferFromContainer(_world, _dbConnection, player, container, matches[0], qtyToTake);
                    }
                    else
                    {
                        var listText = string.Join("\n", matches.Select((it, idx2) => $"{idx2 + 1}) {it.Name} (Qty {it.Quantity})"));
                        player.PendingAction = new PendingAction
                        {
                            Command = "GetFromContainer",
                            Step = "SelectIndex",
                            Data = new GetPendingData { Matches = matches }
                        };

                        return MessageHandler.System($"Multiple items match '{itemExpr}' inside '{container.Name}'. Choose which one:\n{listText}\nType the number of the item to take.");
                    }
                }
                else
                {
                    // No container specified -> room-level get; pass parsed quantity through
                    // If the player explicitly specified a number/all forward it; otherwise signal 'unspecified' via 0 so GetItem will prompt for stacks.
                    int forwardQty = explicitQuantity ? quantity : 0;
                    return GetItem(player, itemExpr, forwardQty);
                }
            }

            // --- PUT command already delegates to InventoryManager.TransferToContainer which now enforces lock ---
            // (existing put branch remains; TransferToContainer will call KeyLocks.EnsureAccessible)

            // --- NEW: lock/unlock commands ---
            if (command.StartsWith("lock ", StringComparison.OrdinalIgnoreCase))
            {
                // Syntax: lock <container> with <key>
                var rest = command.Substring(4).Trim();
                var parts = rest.Split(new string[] { " with " }, 2, StringSplitOptions.None);
                if (parts.Length < 2) return MessageHandler.System("Usage: lock <container> with <key>");

                var containerName = parts[0].Trim();
                var keyExpr = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(keyExpr))
                    return MessageHandler.System("Usage: lock <container> with <key>");

                var container = InventoryManager.FindContainer(player, containerName);
                if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                if (!KeyLocks.IsLockable(container))
                    return MessageHandler.System("You cannot lock that.");

                // Resolve key from player's inventory
                var parse = ItemParser.ResolveForInventoryCommand(player, keyExpr);
                Item? key = null;
                if (parse.Winner != null)
                    key = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == parse.Winner.ItemID);

                if (key == null)
                {
                    // fallback simple match
                    key = player.Inventory.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) &&
                        (i.Name.Equals(keyExpr, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(keyExpr, StringComparison.OrdinalIgnoreCase))));
                }

                if (key == null)
                    return MessageHandler.System($"You do not carry '{keyExpr}'.");

                if (!string.Equals(key.ItemCategory ?? "", "Key", StringComparison.OrdinalIgnoreCase))
                    return MessageHandler.System($"'{key.Name}' is not a key.");

                // Prevent self-lock: key inside the container was handled earlier; but check again
                if (string.Equals(key.OwnerType, "Item", StringComparison.OrdinalIgnoreCase) && key.OwnerID == container.ItemID)
                    return MessageHandler.System("You cannot lock a container with a key that is inside it.");

                // Prompt for key label (pending action)
                player.PendingAction = new PendingAction
                {
                    Command = "LockKey",
                    Step = "NameKey",
                    Data = new KeyLocks.LockPendingData { Container = container, Key = key }
                };

                return MessageHandler.System("Enter a label for this key (single line):");
            }

            if (command.StartsWith("unlock ", StringComparison.OrdinalIgnoreCase))
            {
                // Syntax: unlock <container> with <key>
                var rest = command.Substring(6).Trim();
                var parts = rest.Split(new string[] { " with " }, 2, StringSplitOptions.None);
                if (parts.Length < 2) return MessageHandler.System("Usage: unlock <container> with <key>");

                var containerName = parts[0].Trim();
                var keyExpr = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(keyExpr))
                    return MessageHandler.System("Usage: unlock <container> with <key>");

                var container = InventoryManager.FindContainer(player, containerName);
                if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");

                // Resolve key from player's inventory
                var parse = ItemParser.ResolveForInventoryCommand(player, keyExpr);
                Item? key = null;
                if (parse.Winner != null)
                    key = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == parse.Winner.ItemID);

                if (key == null)
                {
                    key = player.Inventory.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Name) &&
                        (i.Name.Equals(keyExpr, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(i.TemplateID) && i.TemplateID.Equals(keyExpr, StringComparison.OrdinalIgnoreCase))));
                }

                if (key == null) return MessageHandler.System($"You do not carry '{keyExpr}'.");

                // Attempt unlock
                return KeyLocks.UnlockContainer(_dbConnection, player, container, key);
            }


            // Handle 'put' command: put <item> <container>  or put <n> <item> <container>
            // Replace the existing "put" branch body in ProcessCommand with this block
            if (command.StartsWith("put ", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count < 2) return MessageHandler.System("Usage: put <item> <container> OR put <n> <item> <container>");

                var (quantity, explicitQuantity, itemExpr, containerName, putErr) = ParseQuantityItemContainer(tokens, 1);
                if (putErr != null) return MessageHandler.System("Usage: put <n?> <item> in <container> (recommended).");

                Console.WriteLine($"[PUT] parsed: qty={quantity}, itemExpr='{itemExpr}', container='{containerName}'");

                // Require explicit separator (more reliable for multi-word container names)
                if (string.IsNullOrWhiteSpace(containerName))
                    return MessageHandler.System("Usage: put <n?> <item> in <container> (use 'in' or 'into' for multi-word containers).");

                // Resolve container (player inventory first then room)
                var container = InventoryManager.FindContainer(player, containerName);
                if (container == null)
                {
                    Console.WriteLine($"[PUT] No container matching '{containerName}' found. Player inventory: {string.Join(", ", player.Inventory.Select(i => i.Name ?? $"Item#{i.ItemID}"))}");
                    return MessageHandler.System($"No container matching '{containerName}' found.");
                }

                // Diagnostic: list player's inventory with IDs and quantities
                try
                {
                    Console.WriteLine("[PUT] Player inventory dump:");
                    foreach (var it in player.Inventory)
                    {
                        Console.WriteLine($"[PUT][INV] ItemID={it.ItemID} Name='{it.Name}' Template='{it.TemplateID}' OwnerType='{it.OwnerType}' OwnerID={it.OwnerID} Qty={it.Quantity}");
                    }
                }
                catch { }

                // Resolve item in player's inventory (prefer ItemParser)
                // --- Replace the resolution block inside the 'put' branch (where item is resolved) with this block ---

                // Resolve item in player's inventory (prefer ItemParser)
                var parse = ItemParser.ResolveForInventoryCommand(player, itemExpr);
                Console.WriteLine($"[PUT] ItemParser: winner={(parse.Winner != null ? $"{parse.Winner.Name} (ID={parse.Winner.ItemID})" : "(null)")} candidates={string.Join(", ", parse.Candidates.Select(c => c.Name ?? $"#{c.ItemID}"))}");

                Item? item = null;
                if (parse.Winner != null)
                {
                    // Try mapping parser winner -> actual carried instance
                    item = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == parse.Winner.ItemID);

                    // If parser selected an equipped instance, try to find a non-equipped duplicate to use instead
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
                            // No non-equipped alternative — keep the equipped reference so later TransferToContainer can instruct unwear/unhold if needed.
                            Console.WriteLine($"[PUT] Parser winner is equipped and no non-equipped alternative found.");
                        }
                    }
                }

                if (item == null)
                {
                    // Stronger fallback: exact template / exact name / starts-with name (non-equipped preferred)
                    var nonEquippedExact = FindNonEquippedMatches(player, itemExpr);
                    if (nonEquippedExact.Count > 0)
                        item = nonEquippedExact[0];

                    // If still null, fall back to including equipped items (original behavior)
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

                // Additional fallback: token or plural-insensitive first-token match (prefer non-equipped)
                if (item == null)
                {
                    var firstTok = itemExpr.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? itemExpr;
                    item = player.Inventory.FirstOrDefault(i =>
                        !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                        !string.IsNullOrWhiteSpace(i.Name) &&
                        i.Name.IndexOf(firstTok, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (item == null)
                    {
                        // allow equipped fallback for ambiguous cases to preserve UX
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
                // Log container accessibility check before transfer
                var roomRef = player.CurrentRoom;
                bool containerAccessible = (player.Inventory?.Contains(container) ?? false) || (roomRef != null && roomRef.Inventory.Contains(container));
                Console.WriteLine($"[PUT] Container resolved: ItemID={container.ItemID} Name='{container.Name}' Accessible={containerAccessible}");

                if (!containerAccessible)
                    return MessageHandler.System($"Container '{container.Name}' not accessible.");

                // perform transfer
                return InventoryManager.TransferToContainer(_world, _dbConnection, player, item, container, quantity);
            }

            // Handle 'drop' command
            if (command.StartsWith("drop ", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count < 2) return MessageHandler.System("Usage: drop <item> or drop <n|all> <item>");

                var (quantity, explicitQuantity, itemExpr, containerName, dropErr) = ParseQuantityItemContainer(tokens, 1);
                if (dropErr != null) return MessageHandler.System(dropErr);

                // We don't support dropping directly into a container with this form; ignore containerName if present.
                int forwardQty = explicitQuantity ? quantity : 0; // 0 => unspecified => prompt for stacks when appropriate
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

            // Handle room inventory commands
            if (command.StartsWith("additem ", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin)
                    return MessageHandler.System("Permission denied. Admins only.");

                // Parse optional quantity: additem <template> [qty]
                var rest = command.Substring(8).Trim();
                if (string.IsNullOrWhiteSpace(rest))
                    return MessageHandler.System("Usage: additem <template> [quantity]");

                var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string template = tokens[0];
                int qty = 1;
                if (tokens.Length > 1)
                {
                    if (!int.TryParse(tokens[1], out qty) || qty < 1)
                        return MessageHandler.System("Invalid quantity. Provide a positive integer.");
                    qty = Math.Min(qty, 10000);
                }

                // Delegated to new AdminService
                return _adminService.AddItem(player, template, qty);
            }

            if (command.StartsWith("exchange ", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                return _adminService.HandleExchange(player, command);
            }

            if (command.StartsWith("removeitem ", StringComparison.OrdinalIgnoreCase))
            {
                string item = command.Substring(11).Trim();
                // Delegated to AdminService
                return _adminService.RemoveItem(player, item);
            }

            if (command.Equals("listitems", StringComparison.OrdinalIgnoreCase))
            {
                // Delegated to AdminService
                return _adminService.ListItems(player);
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

                        // 1) If the token looks like a direction/exit, handle distant look first.
                        // Accept abbreviations ("n","e","s","w","ne","sw", "up","down") and full words ("north","east", etc.)
                        var dirToken = NormalizeCommandAlias(targetExpr).ToUpperInvariant();
                        if (IsValidDirection(dirToken))
                        {
                            // Find matching exit in current room (case-insensitive direction match)
                            var exit = room.Exits.FirstOrDefault(x => string.Equals(x.Direction, dirToken, StringComparison.OrdinalIgnoreCase));
                            if (exit == null)
                            {
                                // Allow friendly alias matching (e.g. "north" vs "N"), try normalized comparison
                                exit = room.Exits.FirstOrDefault(x => string.Equals(NormalizeCommandAlias(x.Direction), NormalizeCommandAlias(targetExpr), StringComparison.OrdinalIgnoreCase));
                            }

                            if (exit == null)
                                return MessageHandler.System("You don't see an exit in that direction.");

                            // Resolve target room
                            var targetRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);

                            string distantText;
                            if (targetRoom != null && !string.IsNullOrWhiteSpace(targetRoom.DistantDescription))
                            {
                                distantText = targetRoom.DistantDescription;
                            }
                            else if (targetRoom != null)
                            {
                                // Fallback: use room name if no DistantDescription
                                distantText = $"In the distance you can see {targetRoom.Name}.";
                            }
                            else
                            {
                                distantText = "You can see a distant landscape in that direction.";
                            }

                            // Movement hint based on presence of NPCs/players in distant room
                            string movementHint = "";
                            try
                            {
                                int distantCount = 0;
                                if (targetRoom != null)
                                {
                                    distantCount = (targetRoom.Npcs?.Count ?? 0) + (targetRoom.Players?.Count ?? 0);
                                }

                                if (distantCount == 1)
                                    movementHint = " Something can be seen moving in the distance.";
                                else if (distantCount == 2)
                                    movementHint = " A couple of shapes can be seen moving in the distance.";
                                else if (distantCount >= 3 && distantCount <= 4)
                                    movementHint = " Several figures can be seen moving in the distance.";
                                else if (distantCount >= 5)
                                    movementHint = " Many things can be seen moving in the distance.";
                            }
                            catch { /* best-effort only */ }

                            return MessageHandler.System(distantText + movementHint);
                        }

                        // 2) Try NPC lookup in current room
                        var npcMatches = NpcTargeting.FindMatches(room, targetExpr);
                        Console.WriteLine($"[LOOK] target='{targetExpr}', room='{room.Name}', npcMatches={npcMatches.Count}");

                        if (npcMatches.Count > 0)
                        {
                            if (npcMatches.Count > 1)
                            {
                                var list = string.Join(", ", npcMatches.Select(m => m.DisplayName));
                                return MessageHandler.System($"Multiple matches: {list}");
                            }

                            var npc = npcMatches[0];
                            var desc = string.IsNullOrWhiteSpace(npc.Description) ? "No description." : npc.Description;

                            // Append health segment; current in <span class='hp-current'> for client styling.
                            var healthSegment = $" (<span class='hp-current'>{npc.Health}</span>/{npc.HealthMax})";
                            var text = $"{npc.DisplayName}: {desc}{healthSegment}";
                            Console.WriteLine($"[LOOK] match='{npc.DisplayName}', text='{text}'");

                            return MessageHandler.System(text);
                        }

                        // 3) Try item lookup in this room
                        var itemMatches = InventoryManager.FindRoomMatches(room, targetExpr);
                        if (itemMatches == null || itemMatches.Count == 0)
                        {
                            // No NPC or item or exit matched
                            return MessageHandler.System($"You see no '{targetExpr}' here.");
                        }

                        if (itemMatches.Count > 1)
                        {
                            // Present a simple disambiguation list (preserve existing UX)
                            var list = string.Join(", ", itemMatches.Select(i => i.Name));
                            return MessageHandler.System($"Multiple items match: {list}");
                        }

                        var item = itemMatches[0];
                        var itemDesc = !string.IsNullOrWhiteSpace(item.LongDesc) ? item.LongDesc :
                                       !string.IsNullOrWhiteSpace(item.ShortDesc) ? item.ShortDesc : "No description.";
                        return MessageHandler.System($"{item.Name}: {itemDesc}");
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
                    return _adminService.HandleCreateArea(player, command);

                case string s when s.StartsWith("createroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return _adminService.HandleCreateRoom(player, command);

                case string s when s.StartsWith("editroom", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return _adminService.HandleEditRoom(player);

                case string s when s.StartsWith("createnpc", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    var parts = command.Split(' ', 2);
                    if (parts.Length < 2) return MessageHandler.System("Usage: createnpc <templateId>");
                    string templateId = parts[1].Trim();
                    return _adminService.HandleCreateNpc(player, templateId);

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

                            // Compute base total weight for stacked items (unit weight * quantity)
                            double baseTotalWeight = i.Weight * Math.Max(1, i.Quantity);

                            // If this item is a container, add weight of contained items (OwnerType='Item' OwnerID = container.ItemID)
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
                                        // obj may be long/double/decimal - convert safely
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
                                // NEW fields:
                                WearLoc = i.WearLoc ?? "",
                                IsWorn = isWorn,
                                // NEW: expose runtime held state and send handLoc only when runtime-held
                                IsHeld = isHeld,
                                HandLoc = isHeld ? i.HandLoc : 0,
                                // NEW: quantity
                                Quantity = i.Quantity > 0 ? i.Quantity : 1
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
                        // Delegate to AdminService (copied implementation lives in AdminService)
                        return _adminService.DebugItems(player);
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

                // Fallback starts-with
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

        // Add this helper method inside the CommandHandler class (near other private helpers)
        private (int quantity, bool explicitQuantity, string itemExpr, string containerName, string? error) ParseQuantityItemContainer(List<string> tokens, int startIdx)
        {
            // tokens: full token list; startIdx = index of first token after command word (e.g. 1 for "put"/"get")
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
                // fallback: assume last token is container if there are at least two tokens remaining
                if (tokens.Count - idx >= 2)
                {
                    containerName = tokens.Last();
                    itemExpr = string.Join(" ", tokens.Skip(idx).Take(tokens.Count - idx - 1));
                }
                else
                {
                    // only an item expression present
                    itemExpr = string.Join(" ", tokens.Skip(idx));
                    containerName = "";
                }
            }

            itemExpr = itemExpr?.Trim() ?? "";
            containerName = containerName?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(itemExpr)) return (quantity, explicitQuantity, "", "", "Usage: missing item name.");
            return (quantity, explicitQuantity, itemExpr, containerName, null);
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
        // GET / TAKE (modified to accept requestedQuantity and prompt for stacks)
        // -------------------------------
        private Message? GetItem(Player player, string requested, int requestedQuantity = 0)
        {
            // requestedQuantity meanings:
            //   0  => no explicit quantity provided (prompt for stacks)
            //  >0  => explicit numeric requested
            //  -1  => 'all' requested (take entire stack or as much as available)

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
                var item = matches[0];

                // Stack handling
                if (item.Quantity > 1)
                {
                    if (requestedQuantity == 0)
                    {
                        // Prompt for how many (player didn't specify)
                        player.PendingAction = new PendingAction
                        {
                            Command = "GetStack",
                            Step = "ChooseQuantity",
                            Data = new GetPendingData { Matches = new List<Item> { item } }
                        };

                        return MessageHandler.System($"There are {item.Quantity} of '{item.Name}'. How many do you want to get? (type a number or 'all')");
                    }
                    else if (requestedQuantity < 0)
                    {
                        // 'all' requested: transfer entire quantity
                        return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item, item.Quantity);
                    }
                    else
                    {
                        int take = Math.Min(requestedQuantity, item.Quantity);
                        return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item, take);
                    }
                }
                else
                {
                    // Non-stackable / single quantity: honor explicit quantity if >0 (will be treated as 1)
                    int take = (requestedQuantity > 0) ? requestedQuantity : 1;
                    return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item, take);
                }
            }

            // Multiple matches: check if they are identical (same TemplateID)
            bool allSameTemplate = matches.All(i => i.TemplateID == matches[0].TemplateID);

            if (allSameTemplate)
            {
                // If player specified an explicit numeric quantity, take that many rows
                if (requestedQuantity > 0)
                {
                    int want = Math.Min(requestedQuantity, matches.Count);
                    var toTransfer = matches.Take(want).ToList();
                    foreach (var it in toTransfer)
                        InventoryManager.TransferToPlayer(_world, _dbConnection, player, it, 1);
                    return MessageHandler.System($"You picked up {want} x {toTransfer.First().Name}.");
                }

                // Ask how many to pick up (preserve existing UX)
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
        
private Message? DropItem(Player player, string requested, int requestedQuantity = 0)
        {
            // requestedQuantity meanings:
            //   0  => no explicit quantity provided (prompt for stacks)
            //  >0  => explicit numeric requested
            //  -1  => 'all' requested (drop entire stack or as much as available)

            if (player == null) return MessageHandler.System("Invalid player.");
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Prefer ItemParser for inventory resolution; still preserve multi-match UX.
            var parseRes = ItemParser.ResolveForInventoryCommand(player, requested);
            List<Item> matches;

            // --- Replace the early-matches section in DropItem (after parser resolution) with this updated logic ---

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

                // If parser returned a winner but matches include an equipped instance, try to find non-equipped alternatives.
                var nonEquipped = matches.Where(m => !IsRuntimeWorn(m) && !IsRuntimeHeld(m)).ToList();
                if (nonEquipped.Count > 0)
                {
                    matches = nonEquipped;
                }
                else
                {
                    // Try broader search for non-equipped matching items (template/name/startswith)
                    var alt = FindNonEquippedMatches(player, requested);
                    if (alt.Count > 0)
                        matches = alt;
                }

                // If still no matches, keep the original (likely equipped) matches so later logic can instruct to unwear/unhold.
                if (matches.Count == 0)
                {
                    // fallback to conservative behavior (use winner-based filtering but nothing matched)
                    matches = player.Inventory
                        .Where(i => (i?.Name != null && i.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
                                 || (!string.IsNullOrWhiteSpace(i?.TemplateID) && i.TemplateID.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
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
                    // try partial (prefer non-equipped)
                    matches = FindNonEquippedMatches(player, requested);
                    if (matches.Count == 0)
                    {
                        matches = player.Inventory
                            .Where(i => i.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                }
            }

            if (matches.Count == 0)
                return MessageHandler.System($"You are not carrying '{requested}'.");

            // Use runtime flags for worn/held detection (do NOT treat template HandLoc/WearLoc as runtime state)
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
                var item = matches[0];

                // If it's a stackable item (Quantity > 1) handle explicit vs unspecified quantity
                if (item.Quantity > 1)
                {
                    if (requestedQuantity == 0)
                    {
                        // unspecified -> prompt for how many
                        player.PendingAction = new PendingAction
                        {
                            Command = "DropStack",
                            Step = "ChooseQuantity",
                            Data = new GetPendingData { Matches = new List<Item> { item } }
                        };

                        return MessageHandler.System($"You have {item.Quantity} of '{item.Name}'. How many do you want to drop? (type a number or 'all')");
                    }

                    // explicit request
                    if (requestedQuantity < 0)
                    {
                        // 'all'
                        return InventoryManager.TransferToRoom(_world, _dbConnection, player, item, item.Quantity);
                    }

                    int dropQty = Math.Min(requestedQuantity, item.Quantity);
                    return InventoryManager.TransferToRoom(_world, _dbConnection, player, item, dropQty);
                }

                // non-stackable or single-qty
                return InventoryManager.TransferToRoom(_world, _dbConnection, player, item, 1);
            }

            bool allSameTemplate = matches.All(i => i.TemplateID == matches[0].TemplateID);

            if (allSameTemplate)
            {
                // NEW: if the player explicitly requested a numeric quantity, fulfill it automatically
                // prefer a single stack that can satisfy the request; otherwise consume across stacks.
                if (requestedQuantity > 0)
                {
                    // prefer single stack with enough quantity
                    var single = matches.FirstOrDefault(m => m.Quantity >= requestedQuantity);
                    if (single != null)
                    {
                        return InventoryManager.TransferToRoom(_world, _dbConnection, player, single, Math.Min(requestedQuantity, single.Quantity));
                    }

                    // if combined quantity across matches satisfies request, consume in descending-size order
                    int combined = matches.Sum(m => m.Quantity);
                    if (combined >= requestedQuantity)
                    {
                        int remaining = requestedQuantity;
                        foreach (var it in matches.OrderByDescending(m => m.Quantity).ToList())
                        {
                            int take = Math.Min(remaining, it.Quantity);
                            InventoryManager.TransferToRoom(_world, _dbConnection, player, it, take);
                            remaining -= take;
                            if (remaining <= 0) break;
                        }

                        return MessageHandler.System($"You dropped {requestedQuantity} x {matches.First().Name}.");
                    }

                    // not enough total quantity -> inform player
                    return MessageHandler.System($"Only {combined} available. Enter a number up to {combined}.");
                }

                // preserve existing UX (prompt) when no explicit quantity supplied
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

            // First, allow immediate handling of the LockKey pending flow so the
            // label entered by the player is consumed correctly (was being treated
            // as a normal command previously).
            string input = ExtractCommandText(rawCommand).Trim();
            var pa = player.PendingAction!;

            // Handle LockKey pending action (name the key)
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

                // If there are other LockKey steps in future, handle them here...
            }

            // Delegate auth flow to PlayerAuthentication (unchanged behavior for auth pending)
            var authHandled = _auth.HandleAuthPending(player, rawCommand);
            if (authHandled != null) return authHandled;

            // The rest of the existing pending action handling follows (GetItem, DropItem, EditRoom, CreateArea, CreateRoom, etc.)
            // (Preserved existing logic below.)

            // NEW: GetStack pending action (single stack prompt)
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
                    var item = data.Matches[0];
                    if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "everything", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item, item.Quantity);
                    }

                    if (!int.TryParse(input, out int qty) || qty < 1)
                        return MessageHandler.System("Please enter a positive number or 'all'.");

                    int take = Math.Min(qty, item.Quantity);
                    player.PendingAction = null!;
                    return InventoryManager.TransferToPlayer(_world, _dbConnection, player, item, take);
                }
            }

            // NEW: DropStack pending action (single stack prompt)
            if (pa.Command == "DropStack")
            {
                var data = pa.Data as GetPendingData;
                if (data == null || data.Matches == null || data.Matches.Count == 0)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Pending data missing.");
                }

                if (pa.Step == "ChooseQuantity")
                {
                    var item = data.Matches[0];
                    if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "everything", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return InventoryManager.TransferToRoom(_world, _dbConnection, player, item, item.Quantity);
                    }

                    if (!int.TryParse(input, out int qty) || qty < 1)
                        return MessageHandler.System("Please enter a positive number or 'all'.");

                    int drop = Math.Min(qty, item.Quantity);
                    player.PendingAction = null!;
                    return InventoryManager.TransferToRoom(_world, _dbConnection, player, item, drop);
                }
            }

            // The GetItem pending action
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

                    var toTransfer = data.Matches.Take(quantity).ToList();
                    player.PendingAction = null!;

                    foreach (var it in toTransfer)
                    {
                        TransferItemToPlayer(player, it);
                    }

                    return MessageHandler.System($"You picked up {quantity} x {toTransfer.First().Name}.");
                }
            }

            // DropItem pending action
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

            // EditRoom pending action (partial flow preserved)
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
                    // NEW: allow editing distant description via in-game editor
                    if (input == "3" || input.Equals("DistantDescription", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditDistantDescription";
                        return MessageHandler.System("Enter new distant description for this room:");
                    }

                    return MessageHandler.System("Invalid option. Type Name, Description, DistantDescription, 1, 2, 3, or EndEdit.");
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
                // NEW: handle distant description update
                if (pa.Step == "EditDistantDescription")
                {
                    room.DistantDescription = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_dbConnection);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room distant description updated.");
                }
            }

            // CreateArea pending action
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var newArea = new Area(input, 0);

                try
                {
                    using var ins = _dbConnection.CreateCommand();
                    ins.CommandText = @"
                        INSERT INTO Area (Name, Description, WorldID, Theme, LevelRangeMin, LevelRangeMax, IsPvPAllowed, Owner)
                        VALUES (@name, @desc, @wid, @theme, @min, @max, @pvp, @owner);
                        SELECT last_insert_rowid();";
                    ins.Parameters.AddWithValue("@name", newArea.Name ?? "");
                    ins.Parameters.AddWithValue("@desc", newArea.Description ?? "");
                    ins.Parameters.AddWithValue("@wid", _world.WorldID);
                    ins.Parameters.AddWithValue("@theme", newArea.Theme ?? "Generic");
                    ins.Parameters.AddWithValue("@min", newArea.LevelRangeMin);
                    ins.Parameters.AddWithValue("@max", newArea.LevelRangeMax);
                    ins.Parameters.AddWithValue("@pvp", newArea.IsPvPAllowed ? 1 : 0);
                    ins.Parameters.AddWithValue("@owner", newArea.Owner ?? "System");

                    long newAreaId = (long)ins.ExecuteScalar();
                    newArea.AreaID = (int)newAreaId;
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEAREA][ERROR] Failed to insert Area '{input}': {ex.Message}");
                    return MessageHandler.System($"Error creating area {input}: {ex.Message}");
                }

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

                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea != null)
                    currentArea.Rooms.Add(newRoom);
                player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, newRoom.RoomID));
                newRoom.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                player.CurrentRoom.IsDirty = true;
                newRoom.IsDirty = true;
                _world.SaveDirtyRooms(_dbConnection);

                player.PendingAction = null!;
                return MessageHandler.System("Area created.");
            }

            // CreateRoom pending action
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
                return MessageHandler.System("Room created.");
            }

            // If no pending action matched, return null so normal command processing continues.
            return null;
        }

        // Helper class for GetItem / DropItem pending data
        private class GetPendingData
        {
            public List<Item> Matches { get; set; } = new List<Item>();
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