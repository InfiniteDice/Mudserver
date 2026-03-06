using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using Microsoft.Data.Sqlite;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudUtils; // <- added
using MudCore.Actions;


namespace MudServer
{
    public class CommandHandler
    {
        private readonly World _world;
        private readonly SqliteConnection _dbConnection;
        private readonly PlayerAuthentication _auth;
        private readonly AdminService _adminService; // new
        private readonly MudCore.Actions.DoorService _doorService; // door helper

        private static readonly HashSet<string> AllowedWearLocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Head", "Neck", "Body",
            "Leftleg", "Rightleg",
            "Leftarm", "Rightarm",
            "Lefthand", "Righthand",
            "Leftfoot", "Rightfoot", "Deco_Back", "Deco_Waist", "Deco_Finger", "Deco_Wrist" //deco slots are not armourable but allows for commands like "wear ring1 on finger" or "wear necklace on neck"
        };

        public CommandHandler(World world, SqliteConnection dbConnection)
        {
            _world = world;
            _dbConnection = dbConnection;
            _auth = new PlayerAuthentication(world, dbConnection);
            _adminService = new AdminService(world, dbConnection); // initialize admin service
            _doorService = new DoorService(world, dbConnection); // initialize door service
        }

        public Message? ProcessCommand(Player player, string rawCommand)
        {
            // NEW: Never process auth pending actions as commands
            if (player.PendingAction != null && player.PendingAction.Command == "Auth")
            {
                var authMsg = _auth.HandleAuthPending(player, rawCommand);
                if (authMsg != null) player.PendingMessageQueue.Add(authMsg);
                // Important: return null so password is NOT treated as an unknown command
                return null;
            }

            var pendingMsg = CheckPendingAction(player, rawCommand);
            if (pendingMsg != null) return pendingMsg;

            // NEW: Check if player has a spell choice pending
            if (player.PendingAction != null &&
                string.Equals(player.PendingAction.Step, "AwaitingChoice", StringComparison.OrdinalIgnoreCase))
            {
                return HandleChoiceResponse(player, rawCommand);
            }

            // NEW: Refresh session token on each command (extends timeout)
            if (player.IsAuthenticated && !string.IsNullOrWhiteSpace(player.SessionToken))
            {
                try
                {
                    MudNetworking.SessionManager.Instance.RefreshSession(player.SessionToken, 30);
                }
                catch { /* best-effort */ }
            }

            string command = ExtractCommandText(rawCommand).Trim();

            // --- NEW: If player has an outstanding TraderBuy pending, offer guidance for confirm (autopay disabled)
            try
            {
                if (player?.PendingAction != null && string.Equals(player.PendingAction.Command, "TraderBuy", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract pending data
                    if (player.PendingAction.Data is System.Collections.Generic.Dictionary<string, object> pd)
                    {
                        // Resolve the trader npc instance if possible
                        if (pd.TryGetValue("NpcId", out var nidObj) && int.TryParse(nidObj.ToString(), out var pendingNpcId))
                        {
                            var room = player?.CurrentRoom;
                            var npc = room?.Npcs.FirstOrDefault(n => n != null && n.NpcID == pendingNpcId)
                                      ?? _world.Npcs.FirstOrDefault(n => n != null && n.NpcID == pendingNpcId);

                            if (npc != null && MudCore.AI.ItemTraderManager.TryGet(npc, out var trader))
                            {
                                // 'confirm' no longer performs autopay; delegate to trader.HandleConfirm which instructs the player.
                                if (string.Equals(command, "confirm", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        return trader.HandleConfirm(player, _dbConnection);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[CMD][TRADER-CONFIRM] Confirm handling failed: {ex.Message}");
                                        return MessageHandler.System("Confirm handling failed due to server error.");
                                    }
                                }

                                // pay <amount> -> treat as explicit pay (still supported)
                                if (command.StartsWith("pay ", StringComparison.OrdinalIgnoreCase))
                                {
                                    var amtArg = command.Substring(4).Trim();
                                    if (string.IsNullOrWhiteSpace(amtArg)) return MessageHandler.System("Usage: pay <amount> (e.g. pay 1g20s5c or pay 250c)");
                                    try
                                    {
                                        return trader.HandlePaymentAttempt(player, amtArg, _dbConnection);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[CMD][TRADER-PAY] Pay handling failed: {ex.Message}");
                                        return MessageHandler.System("Payment failed due to server error.");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CMD][TRADER-PENDING] Early pending handling failed: {ex.Message}");
            }
            // --- NEW: Interpret "give <what> to <npc>" specially when target is a trader with a pending buy --
            try
            {
                var lcCmd = command.Length > 0 ? command.ToLowerInvariant() : "";
                if (lcCmd.StartsWith("give "))
                {
                    var payload = command.Substring(5).Trim();

                    // First try explicit " to " form
                    var idxTo = payload.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
                    string whatExpr = payload;
                    string targetExpr = "";

                    if (idxTo >= 0)
                    {
                        whatExpr = payload.Substring(0, idxTo).Trim();
                        targetExpr = payload.Substring(idxTo + 4).Trim();
                    }
                    else
                    {
                        // permissive fallback: if last token matches an NPC in room, treat it as target (allows "give 11 copper seleas")
                        var toks = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (toks.Length >= 2)
                        {
                            var possibleTarget = toks[^1];
                            var possibleWhat = string.Join(" ", toks.Take(toks.Length - 1));
                            var room = player?.CurrentRoom;
                            if (room != null)
                            {
                                var matches = NpcTargeting.FindMatches(room, possibleTarget);
                                if (matches.Count == 1)
                                {
                                    whatExpr = possibleWhat;
                                    targetExpr = possibleTarget;
                                }
                                // If multiple matches, prefer explicit "to" to avoid ambiguity -> fall through to normal give path
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(targetExpr))
                    {
                        if (string.IsNullOrWhiteSpace(whatExpr) || string.IsNullOrWhiteSpace(targetExpr))
                            return MessageHandler.System("Usage: give <#> <item> to <npc>");

                        var room = player?.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        var npc = matches[0];

                        // Barkeep: if player has a BarkeepBuy pending for this npc, delegate 'give ... to <npc>' as payment to the BarTender
                        if (MudCore.AI.BarTenderManager.TryGet(npc, out var bt)
                            && player.PendingAction != null
                            && string.Equals(player.PendingAction.Command, "BarkeepBuy", StringComparison.OrdinalIgnoreCase)
                            && player.PendingAction.Data is System.Collections.Generic.Dictionary<string, object> pd2
                            && pd2.TryGetValue("NpcId", out var nidObj2)
                            && int.TryParse(nidObj2.ToString(), out var nid2)
                            && nid2 == npc.NpcID)
                        {
                            long copper2 = 0;

                            // 1) Direct currency syntax (1g20s5c or 250c)
                            if (whatExpr.IndexOf('g', StringComparison.OrdinalIgnoreCase) >= 0
                                || whatExpr.IndexOf('s', StringComparison.OrdinalIgnoreCase) >= 0
                                || whatExpr.IndexOf('c', StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { copper2 = MudCore.AI.ItemTrader.ParseCurrencyExpressionToCopper(whatExpr); } catch { copper2 = 0; }
                            }
                            else
                            {
                                // 2) Quantity + token: "2 coin_gold" or "2 gold"
                                var toks = whatExpr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                                if (toks.Length == 2 && int.TryParse(toks[0], out var q2) && q2 > 0)
                                {
                                    var token = toks[1].Trim().ToLowerInvariant();
                                    if (token.Contains("gold") || token.Contains("coin_gold"))
                                        copper2 = (long)q2 * Currency.CopperPerGold;
                                    else if (token.Contains("silver") || token.Contains("coin_silver"))
                                        copper2 = (long)q2 * Currency.CopperPerSilver;
                                    else if (token.Contains("copper") || token.Contains("coin_copper"))
                                        copper2 = q2;
                                    else
                                    {
                                        var found = player.Inventory?.FirstOrDefault(i =>
                                            i != null && (string.Equals(i.TemplateID, toks[1], StringComparison.OrdinalIgnoreCase)
                                                          || (!string.IsNullOrWhiteSpace(i.Name) && i.Name.IndexOf(toks[1], StringComparison.OrdinalIgnoreCase) >= 0)));
                                        if (found != null)
                                        {
                                            if (string.Equals(found.TemplateID, "coin_gold", StringComparison.OrdinalIgnoreCase))
                                                copper2 = (long)q2 * Currency.CopperPerGold;
                                            else if (string.Equals(found.TemplateID, "coin_silver", StringComparison.OrdinalIgnoreCase))
                                                copper2 = (long)q2 * Currency.CopperPerSilver;
                                            else if (string.Equals(found.TemplateID, "coin_copper", StringComparison.OrdinalIgnoreCase))
                                                copper2 = q2;
                                        }
                                    }
                                }
                                else
                                {
                                    // 3) Single token that may match coin stacks in inventory: sum them
                                    var foundAny = player.Inventory?.Where(i => i != null && (
                                        string.Equals(i.TemplateID, whatExpr, StringComparison.OrdinalIgnoreCase)
                                        || (!string.IsNullOrWhiteSpace(i.Name) && i.Name.IndexOf(whatExpr, StringComparison.OrdinalIgnoreCase) >= 0)))
                                        .ToList();

                                    if (foundAny != null && foundAny.Count > 0)
                                    {
                                        foreach (var it in foundAny)
                                        {
                                            if (it == null) continue;
                                            if (string.Equals(it.TemplateID, "coin_gold", StringComparison.OrdinalIgnoreCase))
                                                copper2 += (long)it.Quantity * Currency.CopperPerGold;
                                            else if (string.Equals(it.TemplateID, "coin_silver", StringComparison.OrdinalIgnoreCase))
                                                copper2 += (long)it.Quantity * Currency.CopperPerSilver;
                                            else if (string.Equals(it.TemplateID, "coin_copper", StringComparison.OrdinalIgnoreCase))
                                                copper2 += it.Quantity;
                                        }
                                    }
                                }
                            }

                            if (copper2 > 0)
                            {
                                var cur2 = Currency.FromCopper(copper2);
                                var sb2 = new System.Text.StringBuilder();
                                if (cur2.Gold > 0) sb2.Append($"{cur2.Gold}g");
                                if (cur2.Silver > 0) sb2.Append($"{cur2.Silver}s");
                                if (cur2.Copper > 0) sb2.Append($"{cur2.Copper}c");
                                if (sb2.Length == 0) sb2.Append("0c");

                                try
                                {
                                    return bt.HandlePaymentAttempt(player, sb2.ToString(), _dbConnection);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[CMD][GIVE->BARKEEP] Payment delegation failed: {ex.Message}");
                                    return MessageHandler.System("Payment failed due to server error.");
                                }
                            }

                            // if we couldn't interpret as coins, fall through to the standard give flow below
                        }

                        // If NPC is a trader and the player has a TraderBuy pending for that npc, treat this as payment
                        if (MudCore.AI.ItemTraderManager.TryGet(npc, out var trader)
                            && player.PendingAction != null
                            && string.Equals(player.PendingAction.Command, "TraderBuy", StringComparison.OrdinalIgnoreCase)
                            && player.PendingAction.Data is System.Collections.Generic.Dictionary<string, object> pd
                            && pd.TryGetValue("NpcId", out var nidObj)
                            && int.TryParse(nidObj.ToString(), out var nid)
                            && nid == npc.NpcID)
                        {
                            // Attempt to interpret 'whatExpr' as coins and delegate to trader's payment flow.
                            long copper = 0;

                            // 1) Direct currency syntax (1g20s5c or 250c)
                            if (whatExpr.IndexOf('g', StringComparison.OrdinalIgnoreCase) >= 0
                                || whatExpr.IndexOf('s', StringComparison.OrdinalIgnoreCase) >= 0
                                || whatExpr.IndexOf('c', StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                copper = MudCore.AI.ItemTrader.ParseCurrencyExpressionToCopper(whatExpr);
                            }
                            else
                            {
                                // 2) Quantity + token: "2 coin_gold" or "2 gold"
                                var toks = whatExpr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                                if (toks.Length == 2 && int.TryParse(toks[0], out var q) && q > 0)
                                {
                                    var token = toks[1].Trim().ToLowerInvariant();
                                    if (token.Contains("gold") || token.Contains("coin_gold"))
                                        copper = (long)q * Currency.CopperPerGold;
                                    else if (token.Contains("silver") || token.Contains("coin_silver"))
                                        copper = (long)q * Currency.CopperPerSilver;
                                    else if (token.Contains("copper") || token.Contains("coin_copper"))
                                        copper = q;
                                    else
                                    {
                                        // attempt to resolve a coin item in player's inventory by template or name
                                        var found = player.Inventory?.FirstOrDefault(i =>
                                            i != null
                                            && (string.Equals(i.TemplateID, toks[1], StringComparison.OrdinalIgnoreCase)
                                                || (!string.IsNullOrWhiteSpace(i.Name) && i.Name.IndexOf(toks[1], StringComparison.OrdinalIgnoreCase) >= 0)));
                                        if (found != null)
                                        {
                                            if (string.Equals(found.TemplateID, "coin_gold", StringComparison.OrdinalIgnoreCase))
                                                copper = (long)q * Currency.CopperPerGold;
                                            else if (string.Equals(found.TemplateID, "coin_silver", StringComparison.OrdinalIgnoreCase))
                                                copper = (long)q * Currency.CopperPerSilver;
                                            else if (string.Equals(found.TemplateID, "coin_copper", StringComparison.OrdinalIgnoreCase))
                                                copper = q;
                                        }
                                    }
                                }
                                else
                                {
                                    // 3) Single token that may match a coin stack in inventory: "coin_gold" or "gold"
                                    var foundAny = player.Inventory?.Where(i => i != null && (
                                        string.Equals(i.TemplateID, whatExpr, StringComparison.OrdinalIgnoreCase)
                                        || (!string.IsNullOrWhiteSpace(i.Name) && i.Name.IndexOf(whatExpr, StringComparison.OrdinalIgnoreCase) >= 0)))
                                        .ToList();

                                    if (foundAny != null && foundAny.Count > 0)
                                    {
                                        // Sum matching coin stacks' copper value
                                        foreach (var it in foundAny)
                                        {
                                            if (it == null) continue;
                                            if (string.Equals(it.TemplateID, "coin_gold", StringComparison.OrdinalIgnoreCase))
                                                copper += (long)it.Quantity * Currency.CopperPerGold;
                                            else if (string.Equals(it.TemplateID, "coin_silver", StringComparison.OrdinalIgnoreCase))
                                                copper += (long)it.Quantity * Currency.CopperPerSilver;
                                            else if (string.Equals(it.TemplateID, "coin_copper", StringComparison.OrdinalIgnoreCase))
                                                copper += it.Quantity;
                                        }
                                    }
                                }
                            }

                            if (copper <= 0)
                            {
                                // Could not interpret input as coins — fall back to normal give.
                                // Continue to standard give path below.
                            }
                            else
                            {
                                // Format copper into compact amount string like "1g20s5c" (no spaces).
                                var cur = Currency.FromCopper(copper);
                                var sb = new System.Text.StringBuilder();
                                if (cur.Gold > 0) sb.Append($"{cur.Gold}g");
                                if (cur.Silver > 0) sb.Append($"{cur.Silver}s");
                                if (cur.Copper > 0) sb.Append($"{cur.Copper}c");
                                if (sb.Length == 0) sb.Append("0c");

                                // Delegate to trader payment attempt (this will remove coins from player transactionally)
                                try
                                {
                                    var payMsg = trader.HandlePaymentAttempt(player, sb.ToString(), _dbConnection);
                                    return payMsg;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[CMD][GIVE->TRADER] Payment delegation failed: {ex.Message}");
                                    return MessageHandler.System("Payment failed due to server error.");
                                }
                            }
                        }

                        // Not a trader-payment scenario (or we couldn't parse coins) -> perform standard give:
                        // Basic give: move item from player -> npc (single quantity or specified qty).
                        try
                        {
                            int giveQty = 1;
                            string itemQuery = whatExpr;
                            // If expression starts with a number, use as qty
                            var wToks = whatExpr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                            if (wToks.Length == 2 && int.TryParse(wToks[0], out var n) && n > 0)
                            {
                                giveQty = n;
                                itemQuery = wToks[1];
                            }

                            // Resolve item in player's non-equipped inventory
                            var matchesItems = FindNonEquippedMatches(player, itemQuery);
                            if (matchesItems == null || matchesItems.Count == 0)
                                return MessageHandler.System("You are not carrying that item.");

                            var item = matchesItems.First();
                            if (item == null) return MessageHandler.System("You are not carrying that item.");

                            int moveQty = Math.Min(giveQty, Math.Max(1, item.Quantity));

                            // Use writer queue to persist transfer
                            try
                            {
                                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                                {
                                    try
                                    {
                                        // If stackable and moving partial qty, reduce player's item and possibly create new item row for NPC
                                        if (moveQty < item.Quantity)
                                        {
                                            // Reduce player's stack
                                            int remaining = item.Quantity - moveQty;
                                            item.Quantity = remaining;
                                            ItemPersistence.SaveItem(wConn, item, wTx);

                                            // Create NPC-owned item stack of same template/qty
                                            var created = _world.ItemFactory.CreateItem(item.TemplateID ?? item.Name ?? "", "NPC", npc.NpcID, 0, moveQty);
                                            // copy runtime useful fields
                                            created.Name = item.Name;
                                            created.ItemCategory = item.ItemCategory;
                                            ItemPersistence.SaveItem(wConn, created, wTx);

                                            // update in-memory lists
                                            player.Inventory.RemoveAll(i => i.ItemID == 0 && i.Quantity == remaining && string.IsNullOrWhiteSpace(i.TemplateID) == false); // best-effort trim
                                            npc.Inventory.Add(created);
                                        }
                                        else
                                        {
                                            // Move full item: set owner fields and save
                                            item.OwnerType = "NPC";
                                            item.OwnerID = npc.NpcID;
                                            item.RoomID = 0;
                                            ItemPersistence.SaveItem(wConn, item, wTx);

                                            // Update in-memory lists
                                            try { player.Inventory.Remove(item); } catch { }
                                            npc.Inventory.Add(item);
                                        }
                                    }
                                    catch (Exception exW)
                                    {
                                        Console.WriteLine($"[CMD][GIVE] Writer tx failed: {exW.Message}");
                                    }
                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(8));
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CMD][GIVE] Enqueue writer failed: {ex.Message}");
                                return MessageHandler.System("Failed to give item due to server error.");
                            }

                            // Notify both parties
                            try
                            {
                                MessageHandler.SendSystem(player, $"You give {(moveQty > 1 ? $"{moveQty} {item.Name}" : item.Name)} to {npc.DisplayName}.");
                                // NPC reaction (simple)
                                MessageHandler.SendSystem(player, $"{npc.DisplayName}: Thank you.");
                            }
                            catch { /* best-effort */ }

                            return MessageHandler.System($"You give {(moveQty > 1 ? $"{moveQty} {item.Name}" : item.Name)} to {npc.DisplayName}.");
                        }
                        catch (Exception exGive)
                        {
                            Console.WriteLine($"[CMD][GIVE] Give flow failed: {exGive.Message}");
                            return MessageHandler.System("Give failed due to server error.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CMD][GIVE] Unexpected error: {ex}");
                // fall through to regular processing
            }
            // --- END give handling ---

            
            // --- NEW: ItemTrader / Barkeep command routing ---
            // Support:
            //  - trade <npc> [list|buy <id>|sell <item>|convert <amount>]
            //  - buy <npc> <templateId>
            //  - sell <npc> <item>
            //  - convert <npc> <amount>
            try
            {
                var lc = command.Length > 0 ? command.ToLowerInvariant() : "";

                // MENU command: "menu <npc>" - show barkeep/trader menu
                if (lc == "menu" || lc.StartsWith("menu "))
                {
                    var payload = command.Length > 5 ? command.Substring(5).Trim() : "";
                    if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: menu <npc>");

                    var room = player?.CurrentRoom;
                    if (room == null) return MessageHandler.System("You are not in a valid room.");

                    var matches = NpcTargeting.FindMatches(room, payload);
                    if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{payload}' here.");
                    if (matches.Count > 1)
                    {
                        var list = string.Join(", ", matches.Select(m => m.DisplayName));
                        return MessageHandler.System($"Multiple matches: {list}");
                    }

                    var npc = matches[0];

                    // Prefer BarTender behavior (barkeep)
                    try
                    {
                        if (MudCore.AI.BarTenderManager.TryGet(npc, out var bt))
                        {
                            return bt.HandleCommand(player, "menu", _dbConnection);
                        }

                        return MessageHandler.System($"{npc.DisplayName} has nothing to sell.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CMD][MENU] Error showing menu for '{npc?.DisplayName}': {ex.Message}");
                        return MessageHandler.System("Failed to show menu due to a server error.");
                    }
                }

                if (lc == "trade" || lc.StartsWith("trade "))
                {
                    var payload = command.Length > 5 ? command.Substring(5).Trim() : "";
                    if (string.IsNullOrWhiteSpace(payload))
                        return MessageHandler.System("Usage: trade <npc> [list|buy <id>|sell <item>|convert <amount>]");

                    var parts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var targetExpr = parts[0];
                    var sub = parts.Length > 1 ? parts[1] : "";

                    var room = player?.CurrentRoom;
                    if (room == null) return MessageHandler.System("You are not in a valid room.");

                    var matches = NpcTargeting.FindMatches(room, targetExpr);
                    if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                    if (matches.Count > 1)
                    {
                        var list = string.Join(", ", matches.Select(m => m.DisplayName));
                        return MessageHandler.System($"Multiple matches: {list}");
                    }

                    var npc = matches[0];
                    if (!MudCore.AI.ItemTraderManager.TryGet(npc, out var trader))
                        return MessageHandler.System($"{npc.DisplayName} does not trade.");

                    var args = string.IsNullOrWhiteSpace(sub) ? "trade" : sub;
                    return trader.HandleCommand(player, args, _dbConnection);
                }

                if (lc.StartsWith("buy "))
                {
                    var payload = command.Substring(4).Trim();

                    // Support both forms:
                    //  - buy <npc> <item>         (legacy)
                    //  - buy <item> from <npc>    (natural / consistent with other commands)
                    string targetExpr;
                    string itemArg;

                    int idxFrom = payload.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
                    if (idxFrom >= 0)
                    {
                        itemArg = payload.Substring(0, idxFrom).Trim();
                        targetExpr = payload.Substring(idxFrom + " from ".Length).Trim();
                        if (string.IsNullOrWhiteSpace(itemArg) || string.IsNullOrWhiteSpace(targetExpr))
                            return MessageHandler.System("Usage: buy <npc> <item> OR buy <item> from <npc>");
                    }
                    else
                    {
                        var parts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: buy <npc> <item> OR buy <item> from <npc>");
                        targetExpr = parts[0];
                        itemArg = parts[1];
                    }

                    var room = player?.CurrentRoom;
                    if (room == null) return MessageHandler.System("You are not in a valid room.");

                    var matches = NpcTargeting.FindMatches(room, targetExpr);
                    if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                    if (matches.Count > 1)
                    {
                        var list = string.Join(", ", matches.Select(m => m.DisplayName));
                        return MessageHandler.System($"Multiple matches: {list}");
                    }

                    var npc = matches[0];

                    // Prefer BarTender (barkeep) behaviour first
                    try
                    {
                        if (MudCore.AI.BarTenderManager.TryGet(npc, out var bt))
                        {
                            return bt.HandleCommand(player, $"buy {itemArg}", _dbConnection);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CMD][BUY] BarTender.HandleCommand failed: {ex.Message}");
                        // fall through to trader fallback
                    }

                    if (!MudCore.AI.ItemTraderManager.TryGet(npc, out var trader))
                        return MessageHandler.System($"{npc.DisplayName} does not trade.");

                    // Delegate to trader; ItemTrader.HandleBuy will use ItemParser to resolve item keywords.
                    return trader.HandleCommand(player, $"buy {itemArg}", _dbConnection);
                }

                if (lc.StartsWith("sell "))
                {
                    var payload = command.Substring(5).Trim();

                    // Support both:
                    //  - sell <npc> <item>   (legacy)
                    //  - sell <item> to <npc> (natural)
                    string targetExpr;
                    string itemArg;

                    int idxTo = payload.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
                    if (idxTo >= 0)
                    {
                        itemArg = payload.Substring(0, idxTo).Trim();
                        targetExpr = payload.Substring(idxTo + 4).Trim();
                        if (string.IsNullOrWhiteSpace(itemArg) || string.IsNullOrWhiteSpace(targetExpr))
                            return MessageHandler.System("Usage: sell <npc> <item> OR sell <item> to <npc>");
                    }
                    else
                    {
                        var parts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: sell <npc> <item> OR sell <item> to <npc>");
                        targetExpr = parts[0];
                        itemArg = parts[1];
                    }

                    var room = player?.CurrentRoom;
                    if (room == null) return MessageHandler.System("You are not in a valid room.");

                    var matches = NpcTargeting.FindMatches(room, targetExpr);
                    if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                    if (matches.Count > 1)
                    {
                        var list = string.Join(", ", matches.Select(m => m.DisplayName));
                        return MessageHandler.System($"Multiple matches: {list}");
                    }

                    var npc = matches[0];
                    if (!MudCore.AI.ItemTraderManager.TryGet(npc, out var trader))
                        return MessageHandler.System($"{npc.DisplayName} does not trade.");

                    // Delegate to trader; ItemTrader uses ItemParser to resolve the actual inventory item.
                    return trader.HandleCommand(player, $"sell {itemArg}", _dbConnection);
                }

                if (lc.StartsWith("convert "))
                {
                    var payload = command.Substring(8).Trim();
                    var parts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return MessageHandler.System("Usage: convert <npc> <amount>");
                    var targetExpr = parts[0];
                    var amtArg = parts[1];

                    var room = player?.CurrentRoom;
                    if (room == null) return MessageHandler.System("You are not in a valid room.");

                    var matches = NpcTargeting.FindMatches(room, targetExpr);
                    if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                    if (matches.Count > 1)
                    {
                        var list = string.Join(", ", matches.Select(m => m.DisplayName));
                        return MessageHandler.System($"Multiple matches: {list}");
                    }

                    var npc = matches[0];
                    if (!MudCore.AI.ItemTraderManager.TryGet(npc, out var trader))
                        return MessageHandler.System($"{npc.DisplayName} does not trade.");

                    return trader.HandleCommand(player, $"convert {amtArg}", _dbConnection);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CMD][TRADER ROUTE] Error routing trader command: {ex.Message}");
                // fall through to normal command processing if routing fails unexpectedly
            }
            // --- END ItemTrader routing ---

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



            // NEW: MILK command - "milk <target> with <container>"
            // - Container must be in player's inventory (no longer required to be held).
            // - Container matching uses descriptor/name/material-aware ItemParser over player's inventory.
            // - Target must be an NPC in the current room with a MaxMilk{n} flag (e.g. "MaxMilk40").
            // - Target must have at least one "milk_raw" in its inventory (sum of quantities).
            // - Attempt to transfer one unit of milk into the specified container per command invocation.
            // - Uses the writer queue to adjust NPC inventory and create milk-in-container row atomically.
            // - Respects container capacity (Capacity flag, parsed by InventoryManager.ParseCapacity).
            if (command.StartsWith("milk ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return MessageHandler.System("Usage: milk <target> with <container>");
                var payload = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: milk <target> with <container>");

                // Expect form: "<target> with <container>"
                int idxWith = payload.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (idxWith < 0) return MessageHandler.System("Usage: milk <target> with <container>");

                var targetExpr = payload.Substring(0, idxWith).Trim();
                var containerExpr = payload.Substring(idxWith + " with ".Length).Trim();

                if (string.IsNullOrWhiteSpace(targetExpr) || string.IsNullOrWhiteSpace(containerExpr))
                    return MessageHandler.System("Usage: milk <target> with <container>");

                var room = player.CurrentRoom;
                if (room == null) return MessageHandler.System("You are not in a valid room.");

                // Resolve the container from the player's inventory using ItemParser (descriptor/material/name aware)
                Item? heldContainer = null;
                try
                {
                    var opts = new ItemParserOptions
                    {
                        IncludeDescriptorTokens = true,
                        IncludeMaterialTokens = true,
                        AllowStartsWith = true
                    };
                    var pres = ItemParser.ResolveForInventoryCommand(player, containerExpr, opts);
                    if (pres?.Winner != null) heldContainer = pres.Winner;
                }
                catch { /* best-effort */ }

                // Container must be in player's inventory and be a Container that holds liquids
                bool okContainer = false;
                if (heldContainer != null)
                {
                    bool isContainerCat = string.Equals(heldContainer.ItemCategory ?? "", "Container", StringComparison.OrdinalIgnoreCase);
                    bool holdsLiquid = (heldContainer.Flags != null && heldContainer.Flags.Any(f => string.Equals(f, "HoldsLiquid", StringComparison.OrdinalIgnoreCase)))
                                       || (!string.IsNullOrEmpty(heldContainer.FlagsJson) && heldContainer.FlagsJson.IndexOf("\"HoldsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                    // Removed held requirement: only require the item is a container and supports liquids and is in the player's inventory.
                    if (isContainerCat && holdsLiquid && (player.Inventory?.Any(i => i != null && i.ItemID == heldContainer.ItemID) ?? false))
                        okContainer = true;
                }

                if (!okContainer)
                    return MessageHandler.System("You must have a container for liquid to milk!");

                // Resolve NPC target in the room (reuse NpcTargeting)
                var npcMatches = NpcTargeting.FindMatches(room, targetExpr);
                if (npcMatches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                if (npcMatches.Count > 1)
                {
                    var list = string.Join(", ", npcMatches.Select(m => m.DisplayName));
                    return MessageHandler.System($"Multiple matches: {list}");
                }
                var targetNpc = npcMatches[0];

                // Target must have MaxMilk{n} flag (required). Check runtime flags, FlagsJson, instance template flags, then world template manager.
                int? maxMilk = null;
                try
                {
                    // 1) Runtime flags on the instance
                    if (targetNpc.Flags != null)
                    {
                        foreach (var f in targetNpc.Flags)
                        {
                            if (string.IsNullOrWhiteSpace(f)) continue;
                            if (f.StartsWith("MaxMilk", StringComparison.OrdinalIgnoreCase))
                            {
                                var tail = f.Substring("MaxMilk".Length).Trim();
                                if (int.TryParse(tail, out var v) && v >= 0) { maxMilk = v; break; }
                            }
                        }
                    }

                    // 2) FlagsJson on the instance (best-effort parse)
                    if (!maxMilk.HasValue && !string.IsNullOrEmpty(targetNpc.FlagsJson))
                    {
                        var idx = targetNpc.FlagsJson.IndexOf("MaxMilk", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            int start = idx + "MaxMilk".Length;
                            int end = start;
                            while (end < targetNpc.FlagsJson.Length && char.IsDigit(targetNpc.FlagsJson[end])) end++;
                            if (end > start)
                            {
                                var numStr = targetNpc.FlagsJson.Substring(start, end - start);
                                if (int.TryParse(numStr, out var v2) && v2 >= 0) maxMilk = v2;
                            }
                        }
                    }

                    // 3) Instance's Template flags (designer-set)
                    if (!maxMilk.HasValue && targetNpc.Template?.Flags != null)
                    {
                        foreach (var f in targetNpc.Template.Flags)
                        {
                            if (string.IsNullOrWhiteSpace(f)) continue;
                            if (f.StartsWith("MaxMilk", StringComparison.OrdinalIgnoreCase))
                            {
                                var tail = f.Substring("MaxMilk".Length).Trim();
                                if (int.TryParse(tail, out var v) && v >= 0) { maxMilk = v; break; }
                            }
                        }
                    }

                    // 4) World TemplateManager lookup (covers case where npc.Template is null but template exists in manager)
                    if (!maxMilk.HasValue && !string.IsNullOrWhiteSpace(targetNpc.TemplateID) && _world?.TemplateManager != null)
                    {
                        try
                        {
                            if (_world.TemplateManager.TryGet(targetNpc.TemplateID, out var tmplFromMgr) && tmplFromMgr?.Flags != null)
                            {
                                foreach (var f in tmplFromMgr.Flags)
                                {
                                    if (string.IsNullOrWhiteSpace(f)) continue;
                                    if (f.StartsWith("MaxMilk", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var tail = f.Substring("MaxMilk".Length).Trim();
                                        if (int.TryParse(tail, out var v3) && v3 >= 0) { maxMilk = v3; break; }
                                    }
                                }
                            }
                        }
                        catch { /* best-effort */ }
                    }
                }
                catch { maxMilk = null; }

                if (!maxMilk.HasValue)
                    return MessageHandler.System($"{targetNpc.DisplayName} cannot be milked.");

                // Count milk_raw in NPC inventory
                int npcMilkCount = 0;
                try
                {
                    npcMilkCount = targetNpc.Inventory?.Where(i => i != null && string.Equals(i.TemplateID, "milk_raw", StringComparison.OrdinalIgnoreCase))
                                                      .Sum(i => Math.Max(1, i.Quantity)) ?? 0;
                }
                catch { npcMilkCount = 0; }

                if (npcMilkCount <= 0) return MessageHandler.System($"{targetNpc.DisplayName} has no milk to give.");
                if (npcMilkCount >= maxMilk.Value) return MessageHandler.System($"{targetNpc.DisplayName} is not producing more milk right now.");

                // Check container capacity (weight-based) before attempting transfer
                double? cap = InventoryManager.ParseCapacity(heldContainer);
                if (cap.HasValue)
                {
                    double usedWeight = 0.0;
                    try
                    {
                        using var cmd = _dbConnection.CreateCommand();
                        cmd.CommandText = @"SELECT SUM(COALESCE(Weight,0.0) * COALESCE(Quantity,1)) FROM Item
                    WHERE OwnerType = 'Item' AND OwnerID = @cid;";
                        cmd.Parameters.AddWithValue("@cid", heldContainer.ItemID);
                        var obj = cmd.ExecuteScalar();
                        if (!(obj == null || obj is DBNull))
                            usedWeight = Convert.ToDouble(obj);
                    }
                    catch { usedWeight = 0.0; }

                    double milkWeight = 0.0;
                    try
                    {
                        if (_world?.ItemFactory != null)
                        {
                            var proto = _world.ItemFactory.CreateItem("milk_raw", "Room", room.RoomID, room.RoomID, 1);
                            if (proto != null) milkWeight = proto.Weight;
                        }
                    }
                    catch { milkWeight = 0.0; }

                    if (usedWeight + milkWeight > cap.Value)
                        return MessageHandler.System($"{heldContainer.Name} is too full to hold more milk.");
                }

                // Perform DB transaction: decrement one milk from NPC inventory and create milk item inside container.
                try
                {
                    var result = DbWriteQueue.Instance.RunWriteAndWait<Message>((wConn, wTx) =>
                    {
                        // Find an NPC-owned milk row in DB
                        using var sel = wConn.CreateCommand();
                        sel.Transaction = wTx;
                        sel.CommandText = @"SELECT ItemID, Quantity FROM Item
                    WHERE OwnerType = 'NPC' AND OwnerID = @nid AND TemplateID = @tmpl
                    ORDER BY ItemID LIMIT 1;";
                        sel.Parameters.AddWithValue("@nid", targetNpc.NpcID);
                        sel.Parameters.AddWithValue("@tmpl", "milk_raw");

                        int? sourceItemId = null;
                        int sourceQty = 0;
                        using var rdr = sel.ExecuteReader();
                        if (rdr.Read())
                        {
                            sourceItemId = rdr.IsDBNull(0) ? (int?)null : rdr.GetInt32(0);
                            sourceQty = rdr.IsDBNull(1) ? 1 : rdr.GetInt32(1);
                        }
                        rdr.Close();

                        if (!sourceItemId.HasValue)
                            return MessageHandler.System($"{targetNpc.DisplayName} has no milk to give.");

                        if (sourceQty > 1)
                        {
                            using var upd = wConn.CreateCommand();
                            upd.Transaction = wTx;
                            upd.CommandText = "UPDATE Item SET Quantity = @q WHERE ItemID = @id;";
                            upd.Parameters.AddWithValue("@q", sourceQty - 1);
                            upd.Parameters.AddWithValue("@id", sourceItemId.Value);
                            upd.ExecuteNonQuery();
                        }
                        else
                        {
                            using var del = wConn.CreateCommand();
                            del.Transaction = wTx;
                            del.CommandText = "DELETE FROM Item WHERE ItemID = @id;";
                            del.Parameters.AddWithValue("@id", sourceItemId.Value);
                            del.ExecuteNonQuery();
                        }

                        if (_world?.ItemFactory == null)
                            return MessageHandler.System("Server cannot create items right now.");

                        var newMilk = _world.ItemFactory.CreateItem("milk_raw", "Item", heldContainer.ItemID, room.RoomID, 1);
                        newMilk.OwnerType = "Item";
                        newMilk.OwnerID = heldContainer.ItemID;
                        newMilk.RoomID = room.RoomID;

                        ItemPersistence.SaveItem(wConn, newMilk, wTx);

                        // Update in-memory NPC inventory (best-effort)
                        try
                        {
                            var inMemSrc = targetNpc.Inventory?.FirstOrDefault(i => i != null && i.ItemID == sourceItemId.Value)
                                          ?? targetNpc.Inventory?.FirstOrDefault(i => i != null && string.Equals(i.TemplateID, "milk_raw", StringComparison.OrdinalIgnoreCase));
                            if (inMemSrc != null)
                            {
                                if (inMemSrc.Quantity > 1) inMemSrc.Quantity -= 1;
                                else targetNpc.Inventory.Remove(inMemSrc);
                            }
                        }
                        catch { /* best-effort */ }

                        // Best-effort: notify player and update room payload
                        try
                        {
                            if (room != null)
                            {
                                room.IsDirty = true;
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world));
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        return MessageHandler.System($"You milk {targetNpc.DisplayName} and collect some milk into {heldContainer.Name}.");
                    }, timeout: TimeSpan.FromSeconds(10));

                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MILK] Failed to milk {targetExpr}: {ex}");
                    return MessageHandler.System("Failed to milk due to a server error.");
                }
            }//END MILK

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
                    AllowStartsWith = true,
                    PreferInventoryOnTie = false,
                    PreferEquippedOnTie = false
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
                    // No automatic preference: if exactly one candidate exists (across room+inventory), choose it.
                    if (presAny.Candidates.Count == 1)
                    {
                        container = presAny.Candidates[0];
                    }
                    else
                    {
                        // Ambiguous: present the candidate list; do NOT attempt a legacy fallback.
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

            // NEW: timechange admin command
            if (command.StartsWith("timechange", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                try
                {
                    return _adminService.HandleTimeChange(player, command);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][TIMECHANGE] {ex}");
                    return MessageHandler.System("Failed to change time due to a server error.");
                }
            }

            // NEW: npccounts admin command
            if (command.StartsWith("npccounts", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                try
                {
                    return _adminService.HandleNpcCounts(player, command);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][NPCCOUNTS] {ex}");
                    return MessageHandler.System("Failed to run npccounts due to a server error.");
                }
            }

            // NEW: time admin command
            if (command.Equals("time", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                try
                {
                    return _adminService.HandleTime(player);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][TIME] {ex}");
                    return MessageHandler.System("Failed to run time command due to a server error.");
                }
            }


            // NEW: dig / dig with <tool> requiring a target: "dig <item> with <tool>"
            if (command.StartsWith("dig", StringComparison.OrdinalIgnoreCase))
            {
                var idxWith = command.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (idxWith < 0)
                    return MessageHandler.System("Usage: dig <item> with <tool>");

                var before = command.Substring(3, idxWith - 3).Trim(); // after 'dig' up to ' with '
                var tool = command.Substring(idxWith + " with ".Length).Trim();

                if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(tool))
                    return MessageHandler.System("Usage: dig <item> with <tool>");

                try
                {
                    return MudCore.Actions.DigManager.StartDigging(player, "dig", before, tool, _world, _dbConnection);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][DIG] {ex}");
                    return MessageHandler.System("Failed to start digging due to a server error.");
                }
            }

            // NEW: mine / mine with <tool> requiring a target: "mine <item> with <tool>"
            if (command.StartsWith("mine", StringComparison.OrdinalIgnoreCase))
            {
                var idxWith = command.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (idxWith < 0)
                    return MessageHandler.System("Usage: mine <item> with <tool>");

                var before = command.Substring(4, idxWith - 4).Trim(); // after 'mine' up to ' with '
                var tool = command.Substring(idxWith + " with ".Length).Trim();

                if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(tool))
                    return MessageHandler.System("Usage: mine <item> with <tool>");

                try
                {
                    return MudCore.Actions.DigManager.StartDigging(player, "mine", before, tool, _world, _dbConnection);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][MINE] {ex}");
                    return MessageHandler.System("Failed to start mining due to a server error.");
                }
            }

            // NEW: prospect / prospect with <tool> (15s, non-blocking)
            if (command.StartsWith("prospect", StringComparison.OrdinalIgnoreCase))
            {
                string tool = "";
                var idxWith = command.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                if (idxWith >= 0)
                {
                    tool = command.Substring(idxWith + " with ".Length).Trim();
                }
                else
                {
                    var toks = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (toks.Length > 1) tool = toks[1].Trim();
                }

                try
                {
                    return MudCore.Actions.ProspectManager.StartProspecting(player, tool, _world, _dbConnection);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CMD][PROSPECT] {ex}");
                    return MessageHandler.System("Failed to run prospect due to a server error.");
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

                    return MudCore.Actions.Cooking.TryStartCooking(player, resolvedName, _world, _dbConnection);
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

            // Sky landing commands (aliases for "down")
            if (command.Equals("land", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("descend", StringComparison.OrdinalIgnoreCase))
            {
                return Move(player, "down");
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

            if (command.StartsWith("emote ", StringComparison.OrdinalIgnoreCase))
            {
                var args = command.Substring(6).Trim();
                if (string.IsNullOrWhiteSpace(args))
                    return MessageHandler.System("Usage: emote <action>");

                var room = player.CurrentRoom;
                if (room == null) return MessageHandler.System("You are not in a valid room.");

                // Send to self
                MessageHandler.SendSystem(player, $"You {args}");

                // Send to others
                foreach (var p in room.Players.Where(p => p != player))
                {
                    MessageHandler.SendSystem(p, $"{player.Name} {args}");
                }

                return null;
            }

            // Handle admin flows...
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


            // Admin toggle invisibility command
            if (normalized == "togglevisible" || normalized == "togglevis")
            {
                return _adminService.HandleToggleVisible(player);
            }

            // NEW: togglefly command
            if (command.StartsWith("togglefly", StringComparison.OrdinalIgnoreCase))
            {
                return _adminService.HandleToggleFly(player);
            }

            // NEW: debugareas command
            if (command.StartsWith("debugareas", StringComparison.OrdinalIgnoreCase))
            {
                return _adminService.HandleDebugAreas(player);
            }

            // In ProcessCommand, add with other admin commands:
            if (command.StartsWith("createsky", StringComparison.OrdinalIgnoreCase))
            {
                return _adminService.HandleCreateSky(player);
            }

            // Add after createsky command (around line 1100-1150):

            if (command.StartsWith("linksky", StringComparison.OrdinalIgnoreCase))
            {
                return _adminService.HandleLinkSky(player, command);
            }

            // NEW: debugnpcs command
            if (command.StartsWith("debugnpcs", StringComparison.OrdinalIgnoreCase))
            {
                var payload = command.Trim();
                if (payload.StartsWith("debugnpcs", StringComparison.OrdinalIgnoreCase))
                    payload = payload.Substring("debugnpcs".Length).Trim();
                return _adminService.HandleDebugNpcs(player, payload);
            }

            // Admin teleport command - handles admin or admin forced player teleports
            if (normalized == "teleport" || command.StartsWith("teleport ", StringComparison.OrdinalIgnoreCase))
            {
                return _adminService.HandleTeleport(player, command);
            }

            // Add after teleport command (around line 565)

            if (normalized == "shutdown" || command.StartsWith("shutdown ", StringComparison.OrdinalIgnoreCase))
            {
                // Pass shutdown callback that stops the game loop
                return _adminService.HandleShutdown(player, command, () =>
                {
                    Console.WriteLine("[SHUTDOWN] Signaling Program.cs to stop...");
                    // Signal the main program to stop gracefully
                    Program.RequestShutdown();
                });
            }

            if (command.StartsWith("removeitem ", StringComparison.OrdinalIgnoreCase))
            {
                string item = command.Substring(11).Trim();
                return _adminService.RemoveItem(player, item);
            }

            if (command.StartsWith("listitems", StringComparison.OrdinalIgnoreCase))
            {
                var payload = command.Trim();
                if (payload.StartsWith("listitems", StringComparison.OrdinalIgnoreCase))
                    payload = payload.Substring("listitems".Length).Trim();

                // If payload provided, treat as NpcID
                if (!string.IsNullOrWhiteSpace(payload))
                    return _adminService.ListItemsForNpc(player, payload);

                // No payload: list room items
                return _adminService.ListItems(player);
            }

            switch (command.Split(' ')[0].ToLower())
            {
                case "look":
                    return HandleLookCommand(player, command);

                case "move": return Move(player, command);

                case "attack":
                 case "kill":
                case "fight":
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
                            var list = string.Join("\n", matches.Select((m, idx) => $"{idx + 1}) {m.DisplayName}"));
                            player.PendingAction = new PendingAction
                            {
                                Command = "AttackSelect",
                                Step = "SelectIndex",
                                Data = new NpcPendingData { Matches = matches }
                            };
                            return MessageHandler.System($"Multiple targets match '{targetExpr}'. Choose one to attack:\n{list}\nType the number of the target to attack.");
                        }

                        var target = matches[0];

                        // NEW: Check NoAttack flag
                        if (MudCore.Actions.Combat.HasNoAttackFlag(target))
                            return MessageHandler.System("This action would be unwise!");

                        return CombatManager.StartOrJoin(player, target, _world, _dbConnection);
                    }

                case "block":
                    {
                        var blockParts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (blockParts.Length < 2)
                            return MessageHandler.System("Usage: block <opponent|all>");

                        var arg = blockParts[1].Trim();
                        return MudCore.Actions.CombatBlockManager.HandleBlockCommand(player, arg, _world, _dbConnection);
                    }

                //Below are the lead/release/hitch/unhitch commands

                case "lead":
                    {
                        var partsLead = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (partsLead.Length < 2) return MessageHandler.System("Usage: lead <npc>");
                        var targetExpr = partsLead[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        try
                        {
                            LeadService.LeadNpc(player, matches[0], _world, _dbConnection);
                            return null; // LeadService sends messages directly
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][LEAD] Failed: {ex.Message}");
                            return MessageHandler.System("Failed to lead due to server error.");
                        }
                    }

                case "release":
                    {
                        var partsRelease = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (partsRelease.Length < 2) return MessageHandler.System("Usage: release <npc>");
                        var targetExpr = partsRelease[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        try
                        {
                            LeadService.ReleaseNpc(player, matches[0], _world, _dbConnection);
                            return null; // LeadService sends messages directly
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][RELEASE] Failed: {ex.Message}");
                            return MessageHandler.System("Failed to release due to server error.");
                        }
                    }

                case "hitch":
                    {
                        var partsHitch = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (partsHitch.Length < 2) return MessageHandler.System("Usage: hitch <npc>");
                        var targetExpr = partsHitch[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        try
                        {
                            LeadService.HitchNpc(player, matches[0], _world, _dbConnection);
                            return null; // LeadService sends messages directly
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][HITCH] Failed: {ex.Message}");
                            return MessageHandler.System("Failed to hitch due to server error.");
                        }
                    }

                case "unhitch":
                    {
                        var partsUnhitch = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (partsUnhitch.Length < 2) return MessageHandler.System("Usage: unhitch <npc>");
                        var targetExpr = partsUnhitch[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        try
                        {
                            LeadService.UnhitchNpc(player, matches[0], _world, _dbConnection);
                            return null; // LeadService sends messages directly
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][UNHITCH] Failed: {ex.Message}");
                            return MessageHandler.System("Failed to unhitch due to server error.");
                        }
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

                case "listhitched":
                    {
                        try
                        {
                            return LeadService.ListHitched(player, _world);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][LISTHITCHED] Failed: {ex.Message}");
                            return MessageHandler.System("Failed to list hitched creatures due to server error.");
                        }
                    }


                case "talk":
                    {
                        // Minimal 'talk' command: "talk <npcnamekeyword>"
                        var partsTalk = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (partsTalk.Length < 2) return MessageHandler.System("Usage: talk <npc>");
                        var targetExpr = partsTalk[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null) return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0) return MessageHandler.System($"No NPC matching '{targetExpr}' here.");
                        if (matches.Count > 1)
                        {
                            // Prompt numeric selection (pending)
                            var list = string.Join("\n", matches.Select((m, idx) => $"{idx + 1}) {m.DisplayName}"));
                            player.PendingAction = new PendingAction
                            {
                                Command = "TalkSelect",
                                Step = "SelectIndex",
                                Data = new NpcPendingData { Matches = matches }
                            };
                            return MessageHandler.System($"Multiple targets match '{targetExpr}'. Choose one to talk to:\n{list}\nType the number of the NPC to talk to.");
                        }

                        // Single match: start talk session with world and db connection
                        try
                        {
                            return MudCore.Actions.NpcTalk.BeginTalkSession(player, matches[0], _world, _dbConnection);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][TALK] BeginTalkSession failed: {ex}");
                            return MessageHandler.System("Failed to start conversation due to a server error.");
                        }
                    }

                case "read":
                    {
                        string cleanCommand = ExtractCommandText(rawCommand);
                        var partsRead = cleanCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (partsRead.Length < 2)
                            return MessageHandler.System("Usage: read <book> or read <book> <page#>");

                        // Everything after "read" is the book name, unless the last token is a number
                        int? pageNum = null;
                        string bookName;

                        // Check if last part is a page number
                        if (partsRead.Length >= 3 && int.TryParse(partsRead[partsRead.Length - 1], out int pNum))
                        {
                            pageNum = pNum;
                            bookName = string.Join(" ", partsRead.Skip(1).Take(partsRead.Length - 2)).Trim();
                        }
                        else
                        {
                            bookName = string.Join(" ", partsRead.Skip(1)).Trim();
                        }

                        var book = InventoryManager.FindContainer(player, bookName);

                        if (book == null)
                            return MessageHandler.System($"No book matching '{bookName}' found.");

                        if (!string.Equals(book.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase))
                            return MessageHandler.System("That is not a book.");

                        // If page number specified, view that page
                        if (pageNum.HasValue)
                        {
                            return BooksManager.ViewPageInBook(player, book, pageNum.Value, _world, _dbConnection);
                        }

                        // Otherwise, display book contents
                        return BooksManager.DisplayBook(player, book, _dbConnection);
                    }

                case "scribe":
                    {
                        string cleanCommand = ExtractCommandText(rawCommand);
                        var partsScribe = cleanCommand.Split(new[] { " to " }, StringSplitOptions.None);
                        if (partsScribe.Length < 2)
                            return MessageHandler.System("Usage: scribe [spellId] to [page]");

                        string spellIdRaw = partsScribe[0].Substring("scribe".Length).Trim();
                        string pageExpr = partsScribe[1].Trim();

                        if (string.IsNullOrWhiteSpace(spellIdRaw))
                            return MessageHandler.System("Usage: scribe [spellId] to [page]");

                        // Convert user input to proper spell ID format (e.g., "lightning" -> "Lightning")
                        string spellIdLower = spellIdRaw.ToLowerInvariant();
                        string spellIdProper = char.ToUpperInvariant(spellIdLower[0]) + spellIdLower.Substring(1);

                        // Find the target page in inventory (Page is an item, not a container)
                        var targetPage = InventoryManager.FindNonEquippedMatches(player, pageExpr)
                            .FirstOrDefault(i => i != null && string.Equals(i.ItemCategory, "Page", StringComparison.OrdinalIgnoreCase));

                        if (targetPage == null)
                            return MessageHandler.System($"You do not have a blank page matching '{pageExpr}'.");

                        var room = player.CurrentRoom;
                        if (room == null)
                            return MessageHandler.System("You are not in a valid room.");

                        return SpellScribingManager.ScribeSpellToPage(player, spellIdProper, targetPage, room, _world, _dbConnection);
                    }

                case "listskills":
                    {
                        if (player.DbPlayerID <= 0)
                            return MessageHandler.System("Your account is not yet saved.");

                        try
                        {
                            var skillLines = new System.Text.StringBuilder();
                            skillLines.AppendLine("=== Your Skills ===");

                            using var cmd = _dbConnection.CreateCommand();
                            cmd.CommandText = @"
                                SELECT SkillID, SkillLevel, SkillHasLevels
                                FROM PlayerSkills
                                WHERE PlayerID = @pid
                                ORDER BY SkillID ASC;";
                            cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);

                            bool any = false;
                            using var rdr = cmd.ExecuteReader();
                            while (rdr.Read())
                            {
                                any = true;
                                string skillId = rdr.IsDBNull(0) ? "?" : rdr.GetString(0);
                                int level = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                                int hasLevels = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);

                                if (hasLevels == 1)
                                    skillLines.AppendLine($"  {skillId} — Level {level}");
                                else
                                    skillLines.AppendLine($"  {skillId} — Known");
                            }

                            if (!any)
                                skillLines.AppendLine("  You have no skills yet.");

                            return MessageHandler.System(skillLines.ToString());
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][LISTSKILLS] Failed for {player.Name}: {ex.Message}");
                            return MessageHandler.System("Failed to retrieve skills.");
                        }
                    }

                case "cast":
                    {
                        string cleanCommand = ExtractCommandText(rawCommand);

                        // Strip the "cast" keyword to get the remainder
                        var castRemainder = cleanCommand.Length > 4 ? cleanCommand.Substring(4).Trim() : "";
                        if (string.IsNullOrWhiteSpace(castRemainder))
                            return MessageHandler.System("Usage: cast [spellId] or cast [spellId] at/on [target]");

                        // Parse optional target — split on " at " or " on "
                        string spellIdRaw;
                        string? targetExpr = null;

                        int idxAt = castRemainder.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
                        int idxOn = castRemainder.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);

                        int splitIdx = -1;
                        int splitLen = 4; // " at " or " on " both length 4
                        if (idxAt >= 0 && (idxOn < 0 || idxAt <= idxOn)) { splitIdx = idxAt; }
                        else if (idxOn >= 0) { splitIdx = idxOn; }

                        if (splitIdx >= 0)
                        {
                            spellIdRaw = castRemainder.Substring(0, splitIdx).Trim();
                            targetExpr = castRemainder.Substring(splitIdx + splitLen).Trim();
                            if (string.IsNullOrWhiteSpace(targetExpr)) targetExpr = null;
                        }
                        else
                        {
                            spellIdRaw = castRemainder.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(spellIdRaw))
                            return MessageHandler.System("Usage: cast [spellId] or cast [spellId] at/on [target]");

                        // Normalize spell ID: e.g. "lightning" -> "Lightning"
                        string spellIdLower = spellIdRaw.ToLowerInvariant();
                        string spellIdProper = char.ToUpperInvariant(spellIdLower[0]) + spellIdLower.Substring(1);

                        var castRoom = player.CurrentRoom;
                        if (castRoom == null)
                            return MessageHandler.System("You are not in a valid room.");

                        try
                        {
                            return SpellCastingManager.StartCasting(
                                player, spellIdProper, targetExpr, castRoom, _world, _dbConnection);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD][CAST] StartCasting failed: {ex}");
                            return MessageHandler.System("Failed to cast spell due to a server error.");
                        }
                    }

                case "memorize":
                    {
                        string cleanCommand = ExtractCommandText(rawCommand);
                        var partsMem = cleanCommand.Split(new[] { " from " }, StringSplitOptions.None);
                        if (partsMem.Length < 2)
                            return MessageHandler.System("Usage: memorize [spellId] from [page/book]");

                        string spellIdRaw = partsMem[0].Substring("memorize".Length).Trim();

                        if (string.IsNullOrWhiteSpace(spellIdRaw))
                            return MessageHandler.System("Usage: memorize [spellId] from [page/book]");

                        // Convert user input to proper spell ID format (e.g., "lightning" -> "Lightning")
                        // This will be used to search for spell_lightning.json
                        string spellIdLower = spellIdRaw.ToLowerInvariant();
                        string spellIdProper = char.ToUpperInvariant(spellIdLower[0]) + spellIdLower.Substring(1);

                        var room = player.CurrentRoom;
                        if (room == null)
                            return MessageHandler.System("You are not in a valid room.");

                        return MemorizationManager.StartMemorizing(player, spellIdProper, room, _world, _dbConnection);
                    }

                case "memlist":
                    return MemorizationManager.GetMemoredSpells(player, _dbConnection);

                case "forget":
                    {
                        string spellId = ExtractCommandText(rawCommand).Substring("forget".Length).Trim();
                        if (string.IsNullOrWhiteSpace(spellId))
                            return MessageHandler.System("Usage: forget [spellId]");
                        return MemorizationManager.ForgetSpell(player, spellId, _dbConnection);
                    }

                case "add":
                    {
                        // add [page] to [book]
                        string cleanCommand = ExtractCommandText(rawCommand);
                        var partsAdd = cleanCommand.Split(new[] { " to " }, StringSplitOptions.None);
                        if (partsAdd.Length < 2)
                            return MessageHandler.System("Usage: add [page] to [book]");

                        string pageExpr = partsAdd[0].Substring("add".Length).Trim();
                        string bookExpr = partsAdd[1].Trim();

                        var pageMatches = InventoryManager.FindNonEquippedMatches(player, pageExpr);
                        if (pageMatches.Count == 0)
                            return MessageHandler.System($"No page matching '{pageExpr}' found.");

                        if (pageMatches.Count > 1)
                            return MessageHandler.System("Multiple pages match. Be more specific.");

                        var book = InventoryManager.FindContainer(player, bookExpr);
                        if (book == null)
                            return MessageHandler.System($"No book matching '{bookExpr}' found.");

                        var page = pageMatches[0];

                        return BooksManager.AddPageToBook(player, page, book, null, _dbConnection);
                    }

                case "remove":
                    {
                        // remove [page] from [book]
                        string cleanCommand = ExtractCommandText(rawCommand);
                        var partsRemove = cleanCommand.Split(new[] { " from " }, StringSplitOptions.None);
                        if (partsRemove.Length < 2)
                            return MessageHandler.System("Usage: remove [page] from [book]");

                        string pageExpr = partsRemove[0].Substring("remove".Length).Trim();
                        string bookExpr = partsRemove[1].Trim();

                        var book = InventoryManager.FindContainer(player, bookExpr);
                        if (book == null)
                            return MessageHandler.System($"No book matching '{bookExpr}' found.");

                        var bookPages = BooksManager.LoadPagesInBook(book, _dbConnection);
                        var pageMatches = bookPages.Where(p => p.Name.StartsWith(pageExpr, StringComparison.OrdinalIgnoreCase)).ToList();

                        if (pageMatches.Count == 0)
                            return MessageHandler.System($"No page matching '{pageExpr}' found in that book.");

                        if (pageMatches.Count > 1)
                            return MessageHandler.System("Multiple pages match. Be more specific.");

                        return BooksManager.RemovePageFromBook(player, pageMatches[0], book, _dbConnection);
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

                // NEW: editnpc admin command - starts pending edit flow (single-match -> menu, multi-match -> select)

                case string s when s.StartsWith("editnpc", StringComparison.OrdinalIgnoreCase):
                    {
                        try
                        {
                            return _adminService.HandleEditNpc(player, command);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD] EditNpc routing failed: {ex.Message}");
                            return MessageHandler.System("Failed to start NPC edit due to a server error.");
                        }
                    }

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
                                Condition = (float)Math.Round(i.Condition, 2),  // Round to 2 decimal places
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

                case string s when s.StartsWith("autoflee", StringComparison.OrdinalIgnoreCase):
                    {
                        var parts2 = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts2.Length < 2)
                            return MessageHandler.System("Usage: autoflee <health_threshold> (0 to disable, 1-100 for threshold)");

                        var arg = parts2[1].Trim();
                        if (!int.TryParse(arg, out int threshold) || threshold < 0 || threshold > 100)
                            return MessageHandler.System("Invalid threshold. Enter a number between 0-100 (0 = disabled).");

                        player.AutoFlee = threshold;

                        try
                        {
                            MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                            {
                                using var cmd = wConn.CreateCommand();
                                cmd.Transaction = wTx;
                                cmd.CommandText = "UPDATE Player SET AutoFlee = @threshold WHERE PlayerID = @pid";
                                cmd.Parameters.AddWithValue("@threshold", threshold);
                                cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                                cmd.ExecuteNonQuery();
                                return (object?)null;
                            }, timeout: TimeSpan.FromSeconds(5));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AUTOFLEE] Failed to persist: {ex.Message}");
                            return MessageHandler.System("Failed to save autoflee setting due to server error.");
                        }

                        if (threshold == 0)
                            return MessageHandler.System("Autoflee disabled.");

                        return MessageHandler.System($"Autoflee enabled: you will automatically flee when health drops to {threshold} or below.");
                    }

                case string s when s.StartsWith("consider", StringComparison.OrdinalIgnoreCase):
                    {
                        var parts3 = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts3.Length < 2)
                            return MessageHandler.System("Usage: consider <target>");

                        var targetExpr = parts3[1].Trim();
                        var room = player.CurrentRoom;
                        if (room == null)
                            return MessageHandler.System("You are not in a valid room.");

                        var matches = NpcTargeting.FindMatches(room, targetExpr);
                        if (matches.Count == 0)
                        {
                            // Q3B: Also try to match players for assessment
                            var playerMatches = room.Players.Where(p => p != player &&
                                (!string.IsNullOrWhiteSpace(p.Name) &&
                                 p.Name.StartsWith(targetExpr, StringComparison.OrdinalIgnoreCase))).ToList();

                            if (playerMatches.Count == 0)
                                return MessageHandler.System($"You don't see '{targetExpr}' here to assess.");

                            if (playerMatches.Count > 1)
                            {
                                var list = string.Join(", ", playerMatches.Select(m => m.Name));
                                return MessageHandler.System($"Multiple matches: {list}");
                            }

                            return MudCore.Actions.Combat.EvaluateWinProbability(player, playerMatches[0], _world);
                        }

                        if (matches.Count > 1)
                        {
                            var list = string.Join(", ", matches.Select(m => m.DisplayName));
                            return MessageHandler.System($"Multiple matches: {list}");
                        }

                        return MudCore.Actions.Combat.EvaluateWinProbability(player, matches[0], _world);
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

                        // NEW: Refresh encumbrance before displaying stats
                        try
                        {
                            Encumbrance.UpdateEffMass(player);
                        }
                        catch { /* best-effort */ }

                        // NEW: Refresh stamina from DB to ensure we're showing current value
                        // (async writes from DecreaseStamina/IncreaseStamina may not have completed yet)
                        try
                        {
                            using var cmd = _dbConnection.CreateCommand();
                            cmd.CommandText = "SELECT Stamina FROM Player WHERE PlayerID = @pid";
                            cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                            var obj = cmd.ExecuteScalar();
                            if (obj != null && !(obj is DBNull))
                            {
                                player.Stamina = Convert.ToInt32(obj);
                            }
                        }
                        catch { /* best-effort; use in-memory value */ }

                        var flags = player.Flags ?? new List<string>();
                        var buffs = player.Buffs ?? new List<string>();
                        var debuffs = player.Debuffs ?? new List<string>();

                        // Compute XP to next level using AdvancementManager; avoid throwing if manager missing.
                        long xpToNext = 0;
                        try
                        {
                            xpToNext = MudCore.Services.AdvancementManager.GetXpToNext(player.Experience);
                        }
                        catch { xpToNext = 0; }

                        var lines = new List<string>
    {
        $"Stats for {player.Name}:",
        $"Level: {player.Level}",
        // Alignment line: resolved enum name and numeric value (two decimals)
        $"Alignment: {player.Alignment}({player.AlignmentValue:F2})",
        $"Xp to Next Level: {(xpToNext > 0 ? xpToNext.ToString() : "(max level)")}",
        $"Unspent Skill Points: {player.SkillPoints}",
        $"Health: {player.Health}/{player.MaxHealth}",
        $"Stamina: {player.Stamina}/{player.MaxStamina}",
        // Power line: only shown if player has a mystic knowledge skill (MaxPower > 0)
                        player.MaxPower > 0
                            ? $"Power: {player.Power}/{player.MaxPower}"
                            : $"Power: none (requires a knowledge skill)",
        $"Size (SizeEnum): {player.SizeEnum}",
        GetEncumbranceStatLine(player),
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
                            return MudCore.Actions.Crafting.Smelt(player, args, _world, _dbConnection);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD] Smelt command failed: {ex.Message}");
                            return MessageHandler.System("Smelting failed due to a server error.");
                        }
                    }

                case string s when s.StartsWith("mill", StringComparison.OrdinalIgnoreCase):
                    {
                        var idx = command.IndexOf(' ');
                        var args = idx >= 0 ? command.Substring(idx + 1).Trim() : string.Empty;
                        try
                        {
                            return MudCore.Actions.Crafting.Mill(player, args, _world, _dbConnection);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD] Mill command failed: {ex.Message}");
                            return MessageHandler.System("Milling failed due to a server error.");
                        }
                    }

                case string s when s.StartsWith("npcstats", StringComparison.OrdinalIgnoreCase):
                    {
                        if (!player.IsAdmin) return MessageHandler.System("Unknown command.");
                        var arg = command.Length > 8 ? command.Substring(8).Trim() : string.Empty;
                        return _adminService.GetNpcStats(arg, player);
                    }

                case string s when s.StartsWith("repair", StringComparison.OrdinalIgnoreCase):
                    {
                        var idx = command.IndexOf(' ');
                        var args = idx >= 0 ? command.Substring(idx + 1).Trim() : string.Empty;
                        try
                        {
                            return MudCore.Actions.Crafting.Repair(player, args, _world, _dbConnection);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CMD] Repair command failed: {ex.Message}");
                            return MessageHandler.System("Repair failed due to a server error.");
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

                case "blockip":
                    {
                        return _adminService.HandleBlockIP(player, command);
                    }

                case "unblockip":
                    {
                        return _adminService.HandleUnblockIP(player, command);
                    }

                case string s when s.StartsWith("editarea", StringComparison.OrdinalIgnoreCase):
                    if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
                    return _adminService.HandleEditArea(player, command);

                case string s when s.Equals("adminhelp", StringComparison.OrdinalIgnoreCase):
                    {
                        if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

                        var adminCommands = new List<string>
        {
            "time - Display current time",
            "timechange - Adjust game time",
            "npccounts - Show NPC counts",
            "debugrooms - List all rooms",
            "debugitems - Print all items",
            "kill <npc> - Remove NPCs",
            "createnpc - Spawn an NPC",
            "editnpc - Edit an NPC",
            "createarea - Create new area",
            "createroom - Create new room",
            "createsky - Create sky room for area",
            "linksky - Link sky rooms",
            "editroom - Edit current room",
            "editexit - Edit exit properties",
            "additem - Add item to room",
            "removeitem - Remove item",
            "listitems - List items in room",
            "anchor - Prevents item pickup",
            "teleport - Move to room",
            "togglevis - Toggle invisibility",
            "shutdown - Shutdown server"
        };

                        var header = "=== Admin Commands ===";
                        var footer = "========================";
                        var body = string.Join("\n", adminCommands);

                        return MessageHandler.System($"{header}\n{body}\n{footer}");
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
            var lookParts = command.Split(' ', 2);
            if (lookParts.Length == 1) return Look(player);

            var targetExpr = lookParts[1].Trim();
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are lost in the void.");

            // NEW: Check if it's too dark to see distant (hours 21-24 and 0-5 are dark)
            try
            {
                if (_world?.GameTime != null)
                {
                    int hour = _world.GameTime.Hour;
                    if ((hour >= 21 && hour <= 23) || (hour >= 0 && hour <= 5))
                    {
                        return MessageHandler.System("It is too dark to see that far!");
                    }
                }
            }
            catch { /* best-effort; continue to normal logic */ }

            // Check if player is in a sky room first (before direction logic)
            bool isInSky = room.Flags != null &&
                           room.Flags.Any(f => string.Equals(f, "IsSkyRoom", StringComparison.OrdinalIgnoreCase));

            // NEW: "look down" in sky rooms shows landing menu
            if (isInSky && targetExpr.Equals("down", StringComparison.OrdinalIgnoreCase))
            {
                var area = _world.Areas?.FirstOrDefault(a => a?.AreaID == room.AreaID);
                if (area == null)
                    return MessageHandler.System("Area not found.");

                var landingOptions = _world.Rooms?.Where(r =>
                    r != null &&
                    r.AreaID == area.AreaID &&
                    (r.Flags == null || !r.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase))) &&
                    (r.Flags == null || !r.Flags.Any(f => string.Equals(f, "IsSkyRoom", StringComparison.OrdinalIgnoreCase)))
                ).ToList() ?? new List<Room>();

                if (landingOptions.Count == 0)
                    return MessageHandler.System("You gaze down but see no clear landing spots.");

                // Build numbered list
                var menu = new System.Text.StringBuilder();
                menu.AppendLine("Looking down, you can see these areas below:");
                menu.AppendLine();
                for (int i = 0; i < landingOptions.Count; i++)
                {
                    menu.AppendLine($"{i + 1}) {landingOptions[i].Name}");
                }
                menu.AppendLine();
                menu.AppendLine("Type 'look <number>' to view a location's distant description, or 'land' to descend.");

                return MessageHandler.System(menu.ToString());
            }

            // NEW: "look <number>" in sky rooms shows distant description
            if (isInSky && int.TryParse(targetExpr, out int lookIdx))
            {
                var area = _world.Areas?.FirstOrDefault(a => a?.AreaID == room.AreaID);
                if (area == null)
                    return MessageHandler.System("Area not found.");

                var landingOptions = _world.Rooms?.Where(r =>
                    r != null &&
                    r.AreaID == area.AreaID &&
                    (r.Flags == null || !r.Flags.Any(f => string.Equals(f, "Indoor", StringComparison.OrdinalIgnoreCase))) &&
                    (r.Flags == null || !r.Flags.Any(f => string.Equals(f, "IsSkyRoom", StringComparison.OrdinalIgnoreCase)))
                ).ToList() ?? new List<Room>();

                if (lookIdx < 1 || lookIdx > landingOptions.Count)
                    return MessageHandler.System($"Invalid number. Choose 1-{landingOptions.Count} or use 'look down' to see the list.");

                var targetRoom = landingOptions[lookIdx - 1];

                // Show distant description (or fallback to name if distant is empty)
                string distant = string.IsNullOrWhiteSpace(targetRoom.DistantDescription)
                    ? $"You see {targetRoom.Name} far below."
                    : targetRoom.DistantDescription;

                var response = new System.Text.StringBuilder();
                response.AppendLine($"Looking down at {targetRoom.Name}:");
                response.AppendLine();
                response.AppendLine(distant);

                return MessageHandler.System(response.ToString());
            }

            // EXISTING: Direction-based look (preserved exactly as before)
            var dirToken = NormalizeCommandAlias(targetExpr).ToUpperInvariant();
            if (IsValidDirection(dirToken))
            {
                // NEW: Handle UP direction with sky visibility or standard exit logic
                if (dirToken.Equals("UP", StringComparison.OrdinalIgnoreCase) || dirToken.Equals("U", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleLookUp(player);
                }

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

            // EXISTING: NPC look (preserved exactly)
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

            // EXISTING: "look all <query>" functionality (preserved exactly)
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

            // EXISTING: Single item look with ItemParser (preserved exactly)
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
                // Resolve container using ItemParser first (search player inventory + room) to match 'inspect' behavior.
                Item? container = null;
                try
                {
                    var optsContainer = new ItemParserOptions
                    {
                        IncludeDescriptorTokens = true,
                        IncludeMaterialTokens = true,
                        AllowStartsWith = true
                    };
                    var pres2 = ItemParser.ResolveForAny(player, containerName, optsContainer);
                    if (pres2?.Winner != null) container = pres2.Winner;
                }
                catch { /* best-effort; fall through to legacy lookup */ }

                // Fallback to legacy lookup
                if (container == null) container = InventoryManager.FindContainer(player, containerName);
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
                    {
                        return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");
                    }

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

        // NEW: Handle choice responses for spells with hasChoicePrompt
        // Pattern: Player responds to "grow 1" or "grow 2" when spell is awaiting choice.
        // This is a reusable handler for ANY spell that uses the choice system.
        // Add new choice-based spells by:
        // 1) Adding hasChoicePrompt=true to spell template SpellEffect
        // 2) Adding choiceOptions=[1,2] and choiceFieldName="SizeChoice" etc
        // 3) The choice will be stored in PendingAction.Data and passed to your DeliverXxx() method
        private Message? HandleChoiceResponse(Player player, string rawCommand)
        {
            if (player?.PendingAction == null)
                return MessageHandler.System("No spell is waiting for your choice.");

            if (!string.Equals(player.PendingAction.Step, "AwaitingChoice", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System("No choice is pending.");

            var data = player.PendingAction.Data as Dictionary<string, object>;
            if (data == null)
                return MessageHandler.System("Choice data lost.");

            // Extract choice field name (e.g., "SizeChoice") and valid options
            string? choiceFieldName = data.TryGetValue("ChoiceFieldName", out var cfn) ? cfn?.ToString() : null;
            var choiceOptions = data.TryGetValue("ChoiceOptions", out var opts) ? opts as List<int> : null;

            if (string.IsNullOrWhiteSpace(choiceFieldName) || choiceOptions == null || choiceOptions.Count == 0)
                return MessageHandler.System("Choice configuration missing.");

            // Parse player's input as integer
            // rawCommand might be JSON {"type":"command","text":"2"} or plain "2"
            string commandText = rawCommand.Trim();

            // If it looks like JSON, extract the "text" field
            if (commandText.StartsWith("{") && commandText.EndsWith("}"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(commandText);
                    if (doc.RootElement.TryGetProperty("text", out var textProp))
                    {
                        commandText = textProp.GetString() ?? commandText;
                    }
                }
                catch { /* not valid JSON, use as-is */ }
            }

            if (!int.TryParse(commandText.Trim(), out int playerChoice))
                return MessageHandler.System($"Invalid choice. Expected number, got '{rawCommand}'.");

            // Validate choice is in allowed options
            if (!choiceOptions.Contains(playerChoice))
                return MessageHandler.System($"Invalid choice '{playerChoice}'. Valid options: {string.Join(", ", choiceOptions)}");

            // Store choice in pending action data
            data[choiceFieldName] = playerChoice;

            // Transition from "AwaitingChoice" to "Casting"
            player.PendingAction.Step = "Casting";

            // CRITICAL FIX: Ensure TargetIsSelf is set to true for choice-based spells
            // (choice spells are typically self-cast like Grow)
            data["TargetIsSelf"] = true;

            // Fire the spell immediately since we already waited for the choice
            try
            {
                return MudCore.Actions.SpellCastingManager.CompleteCast(player, _dbConnection, _world);
            }
            catch (Exception exComplete)
            {
                Console.WriteLine($"[CHOICE] CompleteCast failed: {exComplete.Message}");
                player.PendingAction = null;
                return MessageHandler.System("Spell cast failed due to an error.");
            }
        }

        private Message? HandlePutCommand(Player player, string command)
        {
            var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(); if (tokens.Count < 2) return MessageHandler.System("Usage: put <item> <container> OR put <n> <item> <container>");
            var (quantity, explicitQuantity, itemExpr, containerName, putErr) = ParseQuantityItemContainer(tokens, 1);
            if (putErr != null) return MessageHandler.System("Usage: put <n?> <item> in <container> (recommended).");

            Console.WriteLine($"[PUT] parsed: qty={quantity}, itemExpr='{itemExpr}', container='{containerName}'");

            if (string.IsNullOrWhiteSpace(containerName))
                return MessageHandler.System("Usage: put <n?> <item> in <container> (use 'in' or 'into' for multi-word containers).");

            // Resolve container using parser first (search player's inventory + room).
            // Strategy:
            //  1) Try conservative parser (no descriptor tokens) to avoid accidental ambiguous matches.
            //  2) If nothing found, try permissive parser (descriptor/material tokens enabled) to match cases like "embroidered".
            //  3) Fallback to legacy InventoryManager.FindContainer.
            Item? container = null;
            try
            {
                var optsConservative = new ItemParserOptions
                {
                    IncludeDescriptorTokens = false,
                    IncludeMaterialTokens = false,
                    AllowStartsWith = true
                };
                var pres = ItemParser.ResolveForAny(player, containerName, optsConservative);
                if (pres?.Winner != null) container = pres.Winner;
            }
            catch { /* best-effort */ }

            // Permissive fallback: match descriptor/material tokens (addresses cases like "embroidered")
            if (container == null)
            {
                try
                {
                    var optsPermissive = new ItemParserOptions
                    {
                        IncludeDescriptorTokens = true,
                        IncludeMaterialTokens = true,
                        AllowStartsWith = true,
                        PreferInventoryOnTie = true
                    };
                    var pres2 = ItemParser.ResolveForAny(player, containerName, optsPermissive);
                    if (pres2?.Winner != null)
                    {
                        container = pres2.Winner;
                        Console.WriteLine($"[PUT][DIAG] Permissive parser matched container '{container?.Name}' (descriptor/material tokens).");
                    }
                }
                catch { /* best-effort */ }
            }

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

                // NEW: Apply timed effects from item flags (e.g. "Flying:1")
                try
                {
                    if (toConsume.Flags != null)
                    {
                        foreach (var f in toConsume.Flags)
                        {
                            if (string.IsNullOrWhiteSpace(f)) continue;
                            var parts = f.Split(':', 2);
                            if (parts.Length == 2 && int.TryParse(parts[1], out int minutes))
                            {
                                Effects.ApplyTimedEffect(player, parts[0], minutes, _dbConnection, _world);
                            }
                        }
                    }
                }
                catch (Exception exEff) { Console.WriteLine($"[EAT] Timed effect application failed: {exEff.Message}"); }

                // NEW: Use centralized healing method to apply item healing and persist to DB
                int appliedHeal = player.ApplyItemHealing(toConsume);

                // NEW: Apply refresh (stamina) if item has Refresh flag
                int appliedRefresh = 0;
                try
                {
                    appliedRefresh = player.ApplyItemRefresh(toConsume);
                }
                catch (Exception exRefresh)
                {
                    Console.WriteLine($"[EAT] ApplyItemRefresh failed for player {player.Name}: {exRefresh.Message}");
                    appliedRefresh = 0; // fallback to no refresh
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
                if (appliedHeal > 0) msg += $" You feel better (+{appliedHeal} HP).";
                if (appliedRefresh > 0) msg += $" You feel energized (+{appliedRefresh} Stamina).";

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

                return MessageHandler.System(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EAT] Unexpected error: {ex}");
                return MessageHandler.System("Failed to eat/drink due to a server error.");
            }
        } // end EatItem
        
        
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
                var raw = rawCommand.Trim();

                // Handle land/descend specially - bypass NormalizeCommandAlias
                if (raw.Equals("land", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("descend", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("down", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("d", StringComparison.OrdinalIgnoreCase))
                {
                    dir = "DOWN";
                }
                else
                {
                    dir = NormalizeCommandAlias(raw).ToUpper();
                }
            }

            // Add debug logging
            Console.WriteLine($"[MOVE] Sending to AttemptMove: dir='{dir}'");

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

            // --- NEW: TalkSession pending handling ---
            // If player has an active TalkSession pending, route short commands here (say / ask / end).
            if (string.Equals(pa.Command, "TalkSession", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pa.Step, "Active", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Delegate to the session manager (will handle 'end'/'cancel' and clear player.PendingAction when appropriate)
                    return MudCore.Actions.NpcTalk.HandleTalkCommand(player, input, _world, _dbConnection);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PENDING][TALK] TalkSession handling failed: {ex}");
                    player.PendingAction = null!;
                    return MessageHandler.System("Conversation failed due to server error.");
                }
            }

            // --- NEW: TraderSession pending handling ---
            // If player opened a trade session using 'trade <npc>', short commands like 'list', 'buy <item>',
            // 'sell <item> to <trader>', 'confirm', 'cancel', 'quit' are routed here without re-specifying the NPC.
            if (string.Equals(pa.Command, "TraderSession", StringComparison.OrdinalIgnoreCase) && string.Equals(pa.Step, "Active", StringComparison.OrdinalIgnoreCase))
            {
                // Quick cancellation / quit
                if (string.Equals(input, "cancel", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "end", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Trade session ended.");
                }

                // Resolve NpcId from pending data
                try
                {
                    if (!(pa.Data is System.Collections.Generic.IDictionary<string, object> dict) || !dict.TryGetValue("NpcId", out var nidObj))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Trade session invalid.");
                    }

                    int nid = Convert.ToInt32(nidObj);
                    var npc = _world.Npcs.FirstOrDefault(n => n != null && n.NpcID == nid);
                    if (npc == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Trader not found.");
                    }

                    if (!MudCore.AI.ItemTraderManager.TryGet(npc, out var trader))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System($"{npc.DisplayName} does not trade.");
                    }

                    // PASS-THROUGH WHITELIST:
                    // Allow inventory-related commands to fall through to regular handlers while a trade session is active.
                    // This enables workflows like: inspect bag -> get 1 egg from bag -> sell 1 egg
                    var passthrough = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "inventory", "inv", "inspect", "get", "take", "put", "look", "fill"
                    };

                    var firstTok = (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (passthrough.Contains(firstTok))
                    {
                        // Return null so ProcessCommand continues normal command handling (inspect/get/put/etc).
                        return null;
                    }

                    // Delegate the user's typed short command to the trader instance
                    var msg = trader.HandleCommand(player, input, _dbConnection);

                    // If the player asked to quit or cancel via trader command, ensure session cleared
                    if (string.Equals(input, "cancel", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(input, "end", StringComparison.OrdinalIgnoreCase))
                    {
                        try { player.PendingAction = null!; } catch { }
                    }

                    return msg;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PENDING][TRADER] TraderSession handling failed: {ex.Message}");
                    player.PendingAction = null!;
                    return MessageHandler.System("Trade session failed due to server error.");
                }
            }
            // --- END TraderSession handling ---

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

            // For IsFlying pending, expect the player to type "1" or "2" to choose normal exit or sky exit, or "cancel" to abort.
            if (pa.Command == "ChooseUp")
            {
                // Extract the actual command text from JSON payload
                var cInput = ExtractCommandText(rawCommand);

                if (cInput.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Cancelled.");
                }

                var choice = cInput.Trim();
                if (choice != "1" && choice != "2")
                {
                    return MessageHandler.System("Invalid choice. Type 1 or 2, or 'cancel'.");
                }

                var data = pa.Data as Dictionary<string, object>;
                if (data == null)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Pending data lost. Try again.");
                }

                int targetRoomId = choice == "1"
                    ? Convert.ToInt32(data["NormalExitRoomId"])
                    : Convert.ToInt32(data["SkyRoomId"]);

                player.PendingAction = null;

                // Find the target room
                var targetRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == targetRoomId);
                if (targetRoom == null)
                {
                    return MessageHandler.System("Target room not found.");
                }

                var currentRoom = player.CurrentRoom;
                if (currentRoom == null)
                {
                    return MessageHandler.System("You are not in a valid room.");
                }

                // Remove from current room
                currentRoom.Players.Remove(player);

                // Add to target room
                player.CurrentRoom = targetRoom;
                targetRoom.Players.Add(player);

                // Persist
                try
                {
                    PlayerPersistence.SavePlayer(_dbConnection, player);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MOVE-UP] Failed to persist: {ex.Message}");
                }

                // Broadcast messages
                string verb = choice == "2" ? "ascends into the sky" : "climbs upward";
                foreach (var p in currentRoom.Players)
                {
                    MessageHandler.SendSystem(p, $"{player.Name} {verb}!");
                }

                foreach (var p in targetRoom.Players.Where(p => p != player))
                {
                    MessageHandler.SendSystem(p, $"{player.Name} arrives from below!");
                }

                // Send room update
                player.PendingMessageQueue.Add(MessageHandler.Room(
                    targetRoom,
                    targetRoom.Npcs?.Select(n => n.DisplayName).ToList() ?? new List<string>(),
                    targetRoom.Players.Where(pl => pl != player).Select(pl => MessageHandler.GetPlayerDisplayName(pl)).ToList(),
                    _world, _dbConnection));

                // Broadcast room updates
                foreach (var p in currentRoom.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        currentRoom,
                        currentRoom.Npcs?.Select(n => n.DisplayName).ToList() ?? new List<string>(),
                        currentRoom.Players.Where(pl => pl != p).Select(pl => MessageHandler.GetPlayerDisplayName(pl)).ToList(),
                        _world, _dbConnection));
                }

                foreach (var p in targetRoom.Players.Where(p => p != player))
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        targetRoom,
                        targetRoom.Npcs?.Select(n => n.DisplayName).ToList() ?? new List<string>(),
                        targetRoom.Players.Where(pl => pl != p).Select(pl => MessageHandler.GetPlayerDisplayName(pl)).ToList(),
                        _world, _dbConnection));
                }

                return MessageHandler.System($"You {(choice == "2" ? "ascend into the sky" : "climb upward")}.");
            }



            if (pa.Command == "SkyDescend")
            {
                // Extract the actual command text from JSON payload
                var cInput = ExtractCommandText(rawCommand);

                if (cInput.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null;
                    return MessageHandler.System("You continue hovering in the sky.");
                }

                // Extract data
                var data = pa.Data as Dictionary<string, object>;
                if (data == null || !data.TryGetValue("AvailableRooms", out var roomIdsObj))
                {
                    player.PendingAction = null;
                    return MessageHandler.System("Landing data lost. Try again.");
                }

                var availableRoomIds = (roomIdsObj as List<int>) ?? new List<int>();
                if (availableRoomIds.Count == 0)
                {
                    player.PendingAction = null;
                    return MessageHandler.System("No landing options available.");
                }

                Room targetRoom = null;

                // Try parse as number (1-based index)
                if (int.TryParse(cInput.Trim(), out int idx) && idx >= 1 && idx <= availableRoomIds.Count)
                {
                    int targetRoomId = availableRoomIds[idx - 1];
                    targetRoom = _world.Rooms?.FirstOrDefault(r => r != null && r.RoomID == targetRoomId);
                }
                else
                {
                    // Try match by room name (partial, case-insensitive)
                    var searchTerm = cInput.Trim().ToLowerInvariant();
                    var matchingRooms = _world.Rooms?.Where(r =>
                        r != null &&
                        availableRoomIds.Contains(r.RoomID) &&
                        !string.IsNullOrWhiteSpace(r.Name) &&
                        r.Name.ToLowerInvariant().Contains(searchTerm)
                    ).ToList();

                    if (matchingRooms?.Count == 1)
                    {
                        targetRoom = matchingRooms[0];
                    }
                    else if (matchingRooms?.Count > 1)
                    {
                        player.PendingAction = null;
                        return MessageHandler.System($"Multiple rooms match '{cInput}'. Please use the number instead.");
                    }
                }

                if (targetRoom == null)
                {
                    return MessageHandler.System("Invalid choice. Type a number or room name, or 'cancel'.");
                }

                // Execute landing
                var skyRoom = player.CurrentRoom;

                // Remove from sky
                skyRoom.Players.Remove(player);

                // Add to target room
                player.CurrentRoom = targetRoom;
                targetRoom.Players.Add(player);

                // Persist
                try
                {
                    PlayerPersistence.SavePlayer(_dbConnection, player);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DESCEND] Failed to persist: {ex.Message}");
                }

                // Broadcast departure from sky
                foreach (var p in skyRoom.Players)
                {
                    MessageHandler.SendSystem(p, $"{player.Name} descends and vanishes below!");
                }

                // Broadcast arrival to target room
                foreach (var p in targetRoom.Players.Where(p => p != player))
                {
                    MessageHandler.SendSystem(p, $"{player.Name} descends from above and lands gracefully!");
                }

                // Send room update to landing player
                player.PendingMessageQueue.Add(MessageHandler.Room(
                    targetRoom,
                    targetRoom.Npcs?.Select(n => n.DisplayName).ToList() ?? new List<string>(),
                    targetRoom.Players.Where(pl => pl != player).Select(pl => MessageHandler.GetPlayerDisplayName(pl)).ToList(),
                    _world, _dbConnection));

                // Broadcast room updates to both rooms
                foreach (var p in skyRoom.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        skyRoom,
                        skyRoom.Npcs?.Select(n => n.DisplayName).ToList() ?? new List<string>(),
                        skyRoom.Players.Where(pl => pl != p).Select(pl => MessageHandler.GetPlayerDisplayName(pl)).ToList(),
                        _world, _dbConnection));
                }

                foreach (var p in targetRoom.Players.Where(p => p != player))
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        targetRoom,
                        targetRoom.Npcs?.Select(n => n.DisplayName).ToList() ?? new List<string>(),
                        targetRoom.Players.Where(pl => pl != p).Select(pl => MessageHandler.GetPlayerDisplayName(pl)).ToList(),
                        _world, _dbConnection));
                }

                player.PendingAction = null;
                return null; // Message already sent
            }//END SkyDescend pending

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

            // --- Pending: TalkSelect (numeric selection when multiple NPC matches were shown) ---
            if (pa.Command == "TalkSelect")
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
                        return MessageHandler.System("Invalid selection. Type the number of the NPC to talk to.");

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
                        return MessageHandler.System("That NPC is no longer here.");
                    }

                    player.PendingAction = null!;
                    try
                    {
                        return MudCore.Actions.NpcTalk.BeginTalkSession(player, live, _world, _dbConnection);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PENDING][TALK] BeginTalkSession failed after selection: {ex}");
                        return MessageHandler.System("Failed to start conversation due to a server error.");
                    }
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
            MudCore.Actions.Combat.UpdateEffectiveStats(player);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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
            Combat.UpdateEffectiveStats(player);

            // Broadcast updated room payload so other clients see inventory/appearance change
            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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

        // Delegate to InventoryManager to centralize matching behavior across commands.
        private List<Item> FindNonEquippedMatches(Player player, string itemExpr)
        {
            // Added when trying to resolve Trader - Sell command issues.
            try
            {
                return InventoryManager.FindNonEquippedMatches(player, itemExpr) ?? new List<Item>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CMD] FindNonEquippedMatches delegation failed: {ex.Message}");
                return new List<Item>();
            }
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

            Combat.UpdateEffectiveStats(player);

            // Broadcast room and enqueue player update
            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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

            Combat.UpdateEffectiveStats(player);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
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

        /// <summary>
        /// Helper: Build a descriptive message about sky creatures visible from ground level.
        /// Visibility and detail depend on observer size (larger = better perception).
        /// Returns null if no sky creatures or no skyroom in associated area.
        /// 
        /// Size-based visibility:
        /// - Size 3 or less: "You can faintly make out something flying overhead!"
        /// - Size 4-7: Shows humanoid vs creature count
        /// - Size 8+: Shows accurate names and counts
        /// </summary>
        private string GetSkyCreaturesViewMessage(Player observer)
        {
            if (observer?.CurrentRoom == null || observer.CurrentRoom.Flags == null)
                return null;

            // Skip if player is already in sky
            bool inSky = observer.CurrentRoom.Flags.Any(f =>
                string.Equals(f, "IsSkyRoom", StringComparison.OrdinalIgnoreCase));
            if (inSky) return null;

            try
            {
                // Find associated skyroom for this area
                var area = _world?.Areas?.FirstOrDefault(a => a?.AreaID == observer.CurrentRoom.AreaID);
                if (area == null || !area.SkyRoomID.HasValue)
                    return null;

                var skyRoom = _world?.Rooms?.FirstOrDefault(r => r?.RoomID == area.SkyRoomID.Value);
                if (skyRoom == null)
                    return null;

                // NEW: Collect both flying NPCs and players in skyroom
                var flyingEntities = new List<(string name, bool isHumanoid, bool isNpc)>();

                // Add flying NPCs
                if (skyRoom.Npcs != null)
                {
                    var flyingNpcs = skyRoom.Npcs
                        .Where(n => n != null && n.Health > 0 && (n.IsFlying || n.Flags?.Any(f =>
                            string.Equals(f, "IsFlying", StringComparison.OrdinalIgnoreCase)) == true))
                        .ToList();

                    foreach (var npc in flyingNpcs)
                    {
                        flyingEntities.Add((npc.DisplayName ?? npc.Name ?? "Unknown", npc.IsHumanoid, true));
                    }
                }

                // NEW: Add all players in skyroom (they're flying/in sky)
                if (skyRoom.Players != null)
                {
                    var playersInSky = skyRoom.Players
                        .Where(p => p != null && p.Health > 0 && p != observer) // Don't see yourself
                        .ToList();

                    foreach (var player in playersInSky)
                    {
                        // Players are always humanoid
                        flyingEntities.Add((player.Name, true, false));
                    }
                }

                if (flyingEntities.Count == 0)
                    return null;

                int observerSize = observer.SizeEnum;

                // Size 3 or less: vague description
                if (observerSize <= 3)
                {
                    return "You can faintly make out something flying overhead!";
                }

                // Size 4-7: categorize by humanoid vs creature
                if (observerSize <= 7)
                {
                    var humanoidCount = flyingEntities.Count(e => e.isHumanoid);
                    var creatureCount = flyingEntities.Count(e => !e.isHumanoid);

                    var parts = new List<string>();

                    if (humanoidCount > 0)
                    {
                        string humanDesc = humanoidCount == 1
                            ? "a humanoid"
                            : $"{NumberToWord(humanoidCount)} humanoids";
                        parts.Add(humanDesc);
                    }

                    if (creatureCount > 0)
                    {
                        string creatureDesc = creatureCount == 1
                            ? "a creature"
                            : $"{NumberToWord(creatureCount)} creatures";
                        parts.Add(creatureDesc);
                    }

                    if (parts.Count == 0)
                        return null;

                    string combined = string.Join(", and ", parts);
                    return $"You can make out {combined} flying overhead!";
                }

                // Size 8+: show accurate names and counts
                var groupedByName = flyingEntities
                    .GroupBy(e => e.name)
                    .OrderBy(g => g.Key)
                    .ToList();

                var descriptions = new List<string>();

                foreach (var group in groupedByName)
                {
                    string name = group.Key;
                    int count = group.Count();

                    if (count == 1)
                    {
                        descriptions.Add($"a {name}");
                    }
                    else
                    {
                        descriptions.Add($"{NumberToWord(count)} {Pluralize(name)}");
                    }
                }

                if (descriptions.Count == 0)
                    return null;

                string finalDesc = JoinDescriptions(descriptions);
                return $"You can see {finalDesc} flying overhead!";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommandHandler] GetSkyCreaturesViewMessage failed: {ex.Message}");
                return null;
            }
        }//end GetSkyCreaturesViewMessage

        /// <summary>
        /// Helper: Convert number (1-100) to written form.
        /// Supports up to 100 by tapping into base numbers for compound values.
        /// </summary>
        public string NumberToWord(int n)
        {
            // Handle single digits and teens
            if (n <= 20)
            {
                return n switch
                {
                    1 => "one",
                    2 => "two",
                    3 => "three",
                    4 => "four",
                    5 => "five",
                    6 => "six",
                    7 => "seven",
                    8 => "eight",
                    9 => "nine",
                    10 => "ten",
                    11 => "eleven",
                    12 => "twelve",
                    13 => "thirteen",
                    14 => "fourteen",
                    15 => "fifteen",
                    16 => "sixteen",
                    17 => "seventeen",
                    18 => "eighteen",
                    19 => "nineteen",
                    20 => "twenty",
                    _ => n.ToString()
                };
            }

            // Handle 21-99 by breaking into tens and ones
            if (n < 100)
            {
                int tens = n / 10;
                int ones = n % 10;

                string tenWord = tens switch
                {
                    2 => "twenty",
                    3 => "thirty",
                    4 => "forty",
                    5 => "fifty",
                    6 => "sixty",
                    7 => "seventy",
                    8 => "eighty",
                    9 => "ninety",
                    _ => n.ToString()
                };

                if (ones == 0)
                    return tenWord;

                string oneWord = NumberToWord(ones);
                return $"{tenWord}-{oneWord}";
            }

            // For 100+, just return the number
            return n.ToString();
        }//end NumberToWord

        /// <summary>
        /// Helper: Basic English pluralization - just adds 's'.
        /// </summary>
        private string Pluralize(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return word;

            return word + "s";
        }

        /// <summary>
        /// Helper: Join descriptions with proper grammar (e.g., "a dragon, two eagles, and a gryphon").
        /// </summary>
        private string JoinDescriptions(List<string> descriptions)
        {
            if (descriptions == null || descriptions.Count == 0)
                return "";

            if (descriptions.Count == 1)
                return descriptions[0];

            if (descriptions.Count == 2)
                return $"{descriptions[0]} and {descriptions[1]}";

            // More than 2: use Oxford comma
            var all = string.Join(", ", descriptions.Take(descriptions.Count - 1));
            return $"{all}, and {descriptions.Last()}";
        }

        /// <summary>
        /// Call this in the HandleLookCommand when player looks UP with no valid up exit.
        /// Returns sky visibility message if applicable, otherwise returns standard "no exit" message.
        /// </summary>
        private Message HandleLookUp(Player player)
        {
            if (player?.CurrentRoom == null)
                return MessageHandler.System("You cannot look up from here.");

            // First try: check if there's an actual UP exit
            var upExit = player.CurrentRoom.Exits?.FirstOrDefault(e =>
                string.Equals(Movement.NormalizeDirection(e.Direction), "UP", StringComparison.OrdinalIgnoreCase));

            if (upExit != null)
            {
                var targetRoom = _world.Rooms?.FirstOrDefault(r => r?.RoomID == upExit.ToRoomID);
                if (targetRoom != null)
                {
                    // Standard look distant room behavior
                    return MessageHandler.System($"Looking up, you see {targetRoom.Name}.");
                }
            }

            // No up exit: try sky creatures message
            var skyMessage = GetSkyCreaturesViewMessage(player);
            if (!string.IsNullOrWhiteSpace(skyMessage))
            {
                return MessageHandler.System(skyMessage);
            }

            // NEW: No UP exit and no sky creatures, but area has a skyroom - show default sky view
            try
            {
                var area = _world?.Areas?.FirstOrDefault(a => a?.AreaID == player.CurrentRoom.AreaID);
                if (area != null && area.SkyRoomID.HasValue)
                {
                    return MessageHandler.System("You look up and see the distant sky.");
                }
            }
            catch { /* best-effort */ }

            // Fall back to standard "no exit" message
            return MessageHandler.System("There is no valid direction up.");
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

        // Helper: build encumbrance stat line safely
        private string GetEncumbranceStatLine(Player player)
        {
            try
            {
                double inventoryMass = Encumbrance.GetInventoryMass(player);
                string loadDesc = Encumbrance.GetLoadDescription(player);
                return $"Carried Weight: {inventoryMass:F1} kg ({loadDesc})";
            }
            catch
            {
                return "Carried Weight: <error>";
            }
        }

        private List<int> ConvertToIntList(object obj)
        {
            if (obj == null) return new List<int>();

            // Try direct cast first
            if (obj is List<int> list) return list;

            // Try IEnumerable<object> (common when deserialized from JSON)
            if (obj is System.Collections.IEnumerable enumerable)
            {
                var result = new List<int>();
                foreach (var item in enumerable)
                {
                    if (item is int i) result.Add(i);
                    else if (int.TryParse(item?.ToString(), out var parsed)) result.Add(parsed);
                }
                return result;
            }

            return new List<int>();
        }
    }
}