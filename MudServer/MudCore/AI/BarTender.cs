// REPLACE FROM: // BarTender.cs
// REPLACE TO:   // BarTender.cs
// BarTender.cs
// Barkeep AI behavior implementation (Step 2 partial).
// - Parses AiBehavior config from Npc.Template.AiBehaviorsRaw when present.
// - Lightweight runtime state persisted into Npc.Flags as BarKeepState:{json}.
// - Public API: CreateIfConfigured, HandleCommand (basic menu/buy reservation), OnPlayerSpoke (rumor collection).
// - Payment/finalize flows will follow ItemTrader patterns in later steps; this step focuses on registration + keyword collection.
// NOTE: Additive file; do not remove existing comments elsewhere.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;

namespace MudCore.AI
{
    public sealed class BarKeepConfig
    {
        public string Behavior { get; set; } = "Barkeep";
        public List<string>? ServePeriod { get; set; } = new List<string>(); // e.g. ["Evening","Night"]
        public int BarRoom { get; set; } = 0; // room id where barkeep stays while serving
        public bool SellDrinks { get; set; } = false;
        public bool SellFood { get; set; } = false;
        public List<string>? DrinkMenu { get; set; } = new List<string>(); // entries "template:priceCopper"
        public List<string>? FoodMenu { get; set; } = new List<string>();  // entries "template:priceCopper"
        public bool InformationCollector { get; set; } = false;
        public List<string>? TriggerCollectKeywords { get; set; } = new List<string>();
        public List<string>? RequiredAlignment { get; set; } = new List<string>();
        public int RequiredLevel { get; set; } = 0;
        public int RequiredQuestId { get; set; } = 0; // reserved, not enforced here
        public string? FillContainerTemplateId { get; set; } = null; // e.g. "glass_pint"
    }

    public sealed class BarKeepState
    {
        public DateTime? LastServeUtc { get; set; }
        public DateTime? LastCollectUtc { get; set; }
        public Dictionary<string, int>? SalesCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }


    // REPLACE FROM: class BarTender
    // REPLACE TO:   public sealed class BarTender
    public sealed class BarTender
    {
        private readonly Npc _npc;
        private readonly World.World _world;
        private readonly BarKeepConfig _cfg;
        private BarKeepState _state;
        private readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private const string BarKeepStateFlagPrefix = "BarKeepState:";
        public bool debugLogEnabled = false;

        // Reservation map for pre-created sell items (ItemID -> PlayerID)
        // This allows us to reserve a specific NPC-owned item row when a player initiates a buy.
        private readonly object _reserveLock = new object();
        private readonly System.Collections.Generic.Dictionary<int, string> _reservedItems = new System.Collections.Generic.Dictionary<int, string>();

        private BarTender(Npc npc, World.World world, BarKeepConfig cfg)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _cfg = cfg ?? new BarKeepConfig();
            _state = new BarKeepState { SalesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) };
            LoadStateFromNpc();
        }

        // Factory: create a BarTender from Npc.Template.AiBehaviorsRaw when a Barkeep behavior object exists.
        // Returns null when not configured.

        public static BarTender? CreateIfConfigured(Npc npc, World.World world)
        {
            try
            {
                if (npc?.Template == null) return null;



                // 1) Prefer legacy/raw JSON AiBehaviors when present (preserves custom fields like DrinkMenu/FoodMenu)
                if (npc.Template.AiBehaviorsRaw != null && npc.Template.AiBehaviorsRaw.Count > 0)
                {
                    foreach (var el in npc.Template.AiBehaviorsRaw)
                    {
                        try
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;



                            if (el.TryGetProperty("Behavior", out var be) && string.Equals(be.GetString() ?? "", "Barkeep", StringComparison.OrdinalIgnoreCase))
                            {
                                var cfg = JsonSerializer.Deserialize<BarKeepConfig>(el.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (cfg == null) cfg = new BarKeepConfig();
                                Console.WriteLine($"[BarTender][DEBUG] Parsed BarKeepConfig from AiBehaviorsRaw for Npc '{npc?.Name}' - registering BarTender.");
                                return new BarTender(npc, world, cfg);
                            }
                        }
                        catch (Exception exEl)
                        {
                            //Console.WriteLine($"[BarTender][WARN] Error examining AiBehaviorsRaw element for Npc '{npc?.Name}': {exEl.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] CreateIfConfigured failed for Npc '{npc?.Name}': {ex.Message}");
            }
            return null;
        }

        private void LoadStateFromNpc()
        {
            try
            {
                if (_npc.Flags != null)
                {
                    foreach (var f in _npc.Flags)
                    {
                        if (string.IsNullOrWhiteSpace(f)) continue;
                        if (f.StartsWith(BarKeepStateFlagPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var json = f.Substring(BarKeepStateFlagPrefix.Length).Trim();
                            if (!string.IsNullOrWhiteSpace(json))
                            {
                                try
                                {
                                    var s = JsonSerializer.Deserialize<BarKeepState>(json, _jsonOpts);
                                    if (s != null)
                                    {
                                        _state = s;
                                        if (_state.SalesCounts == null) _state.SalesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                        return;
                                    }
                                }
                                catch { /* best-effort parse */ }
                            }
                        }
                    }
                }

                // Seed defaults when not present
                _state = new BarKeepState { SalesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] LoadStateFromNpc error for NpcID={_npc?.NpcID}: {ex.Message}");
                _state = new BarKeepState { SalesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) };
            }
        }

        public void PersistState(SqliteConnection? conn = null)
        {
            try
            {
                var json = JsonSerializer.Serialize(_state, _jsonOpts);
                var flags = _npc.Flags ?? new List<string>();
                flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(BarKeepStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                flags.Add($"{BarKeepStateFlagPrefix}{json}");
                _npc.Flags = flags;
                _npc.FlagsJson = JsonSerializer.Serialize(flags);

                try
                {
                    if (conn != null && _npc.NpcID > 0)
                    {
                        MudPersistence.NpcPersistence.UpdateNpc(conn, _npc);
                    }
                    else
                    {
                        try
                        {
                            MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                            {
                                try
                                {
                                    if (wConn != null && _npc.NpcID > 0)
                                    {
                                        using var cmd = wConn.CreateCommand();
                                        if (wTx != null) cmd.Transaction = wTx;
                                        cmd.CommandText = "UPDATE Npc SET Flags = @flags WHERE NpcID = @id;";
                                        cmd.Parameters.AddWithValue("@flags", _npc.FlagsJson ?? "[]");
                                        cmd.Parameters.AddWithValue("@id", _npc.NpcID);
                                        cmd.ExecuteNonQuery();
                                    }
                                }
                                catch { /* best-effort */ }
                                return (object?)null;
                            }, timeout: TimeSpan.FromSeconds(5));
                        }
                        catch { /* ignore writer enqueue issues */ }
                    }
                }
                catch { /*best-effort*/ }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] PersistState failed for NpcID={_npc?.NpcID}: {ex.Message}");
            }
        }

        // Check if Barkeep is serving now (based on ServePeriod). Returns friendly text when not.
        public bool IsServingNow(out string friendly)
        {
            friendly = "";
            try
            {
                if (_cfg.ServePeriod == null || _cfg.ServePeriod.Count == 0) return true;
                foreach (var p in _cfg.ServePeriod)
                {
                    if (_world?.GameTime != null && _world.GameTime.IsInPeriod(p)) return true;
                }
                friendly = string.Join(" or ", _cfg.ServePeriod);
                return false;
            }
            catch
            {
                friendly = _cfg.ServePeriod != null && _cfg.ServePeriod.Count > 0 ? string.Join(",", _cfg.ServePeriod) : "serving hours";
                return false;
            }
        }

        // Enforce RequiredLevel and RequiredAlignment (simple)
        public (bool CanBuy, string? Reason) CanBuy(Player player)
        {
            if (player == null) return (false, "Invalid player.");
            try
            {
                if (_cfg.RequiredLevel > 0 && player.Level < _cfg.RequiredLevel)
                    return (false, "You are not experienced enough to buy from me.");
            }
            catch { /* best-effort */ }

            try
            {
                if (_cfg.RequiredAlignment != null && _cfg.RequiredAlignment.Count > 0)
                {
                    // Support template shorthand "Any" to allow all alignments.
                    if (_cfg.RequiredAlignment.Any(a => string.Equals(a?.Trim(), "Any", StringComparison.OrdinalIgnoreCase)))
                        return (true, null);

                    string palign = player.Alignment.ToString();
                    bool ok = false;
                    foreach (var want in _cfg.RequiredAlignment)
                    {
                        if (string.Equals(want, palign, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                    }
                    if (!ok) return (false, "I will not serve you due to your alignment.");
                }
            }
            catch { /* best-effort */ }

            // RequiredQuestId intentionally not enforced here (reserved)
            return (true, null);
        }

        // Basic command handler: "bar"/"menu" -> show menu; "buy <item>" -> create pending purchase awaiting 'give' payment.
        public Message HandleCommand(Player player, string rawArgs, SqliteConnection? conn = null)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            try
            {
                if (!IsServingNow(out var when)) return MessageHandler.System($"I'm not serving right now. Come back in {when}.");

                var cb = CanBuy(player);
                if (!cb.CanBuy) return MessageHandler.System(cb.Reason ?? "You cannot be served.");

                var args = (rawArgs ?? "").Trim();
                if (string.IsNullOrWhiteSpace(args) || string.Equals(args, "bar", StringComparison.OrdinalIgnoreCase) || string.Equals(args, "menu", StringComparison.OrdinalIgnoreCase))
                {
                    // Trigger a background restock check for visible menu items (best-effort)
                    try
                    {
                        if (_cfg.DrinkMenu != null)
                        {
                            foreach (var e in _cfg.DrinkMenu)
                            {
                                var (_, tmpl) = ParseMenuEntry(e);
                                if (!string.IsNullOrWhiteSpace(tmpl)) RestockIfNeeded(tmpl, 2);
                            }
                        }
                        if (_cfg.FoodMenu != null)
                        {
                            foreach (var e in _cfg.FoodMenu)
                            {
                                var (_, tmpl) = ParseMenuEntry(e);
                                if (!string.IsNullOrWhiteSpace(tmpl)) RestockIfNeeded(tmpl, 2);
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    return MessageHandler.System(BuildMenuText());
                }

                var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var verb = parts[0].ToLowerInvariant();
                var payload = parts.Length > 1 ? parts[1].Trim() : "";

                if (verb == "buy")
                {
                    if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: buy <drink|food token>");
                    var (price, template) = FindMenuEntry(payload);
                    if (price <= 0 || string.IsNullOrWhiteSpace(template)) return MessageHandler.System("I don't sell that.");

                    // Determine whether this token is a drink
                    bool isDrink = false;
                    try
                    {
                        if (_cfg.SellDrinks && _cfg.DrinkMenu != null)
                        {
                            foreach (var e in _cfg.DrinkMenu)
                            {
                                var (_, t) = ParseMenuEntry(e);
                                if (!string.IsNullOrWhiteSpace(t) && string.Equals(t, template, StringComparison.OrdinalIgnoreCase)) { isDrink = true; break; }
                            }
                        }
                    }
                    catch { isDrink = false; }

                    Item? candidateContainer = null;
                    Item? candidateDrink = null;

                    try
                    {
                        var inv = _npc.Inventory ?? Enumerable.Empty<Item>();

                        if (isDrink)
                        {
                            // For drinks: we must find an empty container (pre-spawned) and a separate NPC-owned drink item (not placed into the container).
                            var containerTemplate = !string.IsNullOrWhiteSpace(_cfg.FillContainerTemplateId) ? _cfg.FillContainerTemplateId : "glass_pint";

                            // Find any unreserved container
                            foreach (var it in inv)
                            {
                                if (it == null) continue;
                                if (string.IsNullOrWhiteSpace(it.TemplateID)) continue;
                                if (!string.Equals(it.TemplateID, containerTemplate, StringComparison.OrdinalIgnoreCase)) continue;
                                lock (_reserveLock)
                                {
                                    if (!_reservedItems.ContainsKey(it.ItemID))
                                    {
                                        candidateContainer = it;
                                        _reservedItems[it.ItemID] = player.PlayerID ?? "";
                                        break;
                                    }
                                }
                            }

                            // Find an NPC-owned drink item matching template that is not reserved
                            foreach (var it in inv)
                            {
                                if (it == null) continue;
                                if (string.IsNullOrWhiteSpace(it.TemplateID)) continue;
                                if (!string.Equals(it.TemplateID, template, StringComparison.OrdinalIgnoreCase)) continue;
                                lock (_reserveLock)
                                {
                                    if (!_reservedItems.ContainsKey(it.ItemID))
                                    {
                                        candidateDrink = it;
                                        _reservedItems[it.ItemID] = player.PlayerID ?? "";
                                        break;
                                    }
                                }
                            }

                            // If we reserved a container but not a drink, release container reservation
                            if (candidateContainer != null && candidateDrink == null)
                            {
                                try { ReleaseReservationForItem(candidateContainer.ItemID); } catch { }
                                candidateContainer = null;
                            }

                            // If we reserved a drink but not a container, release drink reservation
                            if (candidateDrink != null && candidateContainer == null)
                            {
                                try { ReleaseReservationForItem(candidateDrink.ItemID); } catch { }
                                candidateDrink = null;
                            }
                        }
                        else
                        {
                            // Non-drink: find a direct NPC-owned item row matching the template and reserve it
                            foreach (var it in inv)
                            {
                                if (it == null) continue;
                                if (string.IsNullOrWhiteSpace(it.TemplateID)) continue;
                                if (!string.Equals(it.TemplateID, template, StringComparison.OrdinalIgnoreCase)) continue;
                                lock (_reserveLock)
                                {
                                    if (!_reservedItems.ContainsKey(it.ItemID))
                                    {
                                        candidateContainer = it;
                                        _reservedItems[it.ItemID] = player.PlayerID ?? "";
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { candidateContainer = null; candidateDrink = null; }

                    if ((isDrink && (candidateContainer == null || candidateDrink == null)) || (!isDrink && candidateContainer == null))
                    {
                        // Out of stock in current NPC inventory — trigger an async restock request and inform player.
                        try { RestockIfNeeded(template, 2); } catch { }
                        return MessageHandler.System($"{_npc.DisplayName}: Sorry, I'm out of stock right now. Try again shortly.");
                    }

                    // Build pending that references the reserved item(s)
                    var pending = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["NpcId"] = _npc.NpcID,
                        ["TemplateId"] = template,
                        ["ItemName"] = payload,
                        ["Price"] = (long)price,
                        ["Paid"] = 0L
                    };

                    if (isDrink)
                    {
                        pending["ReservedContainerId"] = candidateContainer!.ItemID;
                        pending["ReservedDrinkId"] = candidateDrink!.ItemID;
                    }
                    else
                    {
                        pending["ReservedItemId"] = candidateContainer!.ItemID;
                    }

                    player.PendingAction = new PendingAction { Command = "BarkeepBuy", Step = "AwaitPayment", Data = pending };
                    try { _npc.IsTradeLocked = true; _npc.AddInterrupt(Npc.InterruptType.Trade, owner: player.PlayerID, note: "BarkeepBuy", ttl: null); } catch { }

                    return MessageHandler.System($"{_npc.DisplayName}: I have reserved {payload} for you. Total cost is {price}c. To pay, type: give <amount> to {_npc.DisplayName}");
                }

                return MessageHandler.System("I don't understand. Try: menu, buy <item>.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] HandleCommand error for NpcID={_npc?.NpcID}: {ex.Message}");
                return MessageHandler.System("The barkeep fails to process your request.");
            }
        }//end HandleCommand

        private string BuildMenuText()
        {
            var lines = new List<string> { $"{_npc.DisplayName}: Menu" };
            if (_cfg.SellDrinks && _cfg.DrinkMenu != null && _cfg.DrinkMenu.Count > 0)
            {
                lines.Add("Drinks:");
                foreach (var e in _cfg.DrinkMenu)
                {
                    var (price, template) = ParseMenuEntry(e);
                    var name = template ?? e;
                    lines.Add($" - {name} : {price}c");
                }
            }
            if (_cfg.SellFood && _cfg.FoodMenu != null && _cfg.FoodMenu.Count > 0)
            {
                lines.Add("Food:");
                foreach (var e in _cfg.FoodMenu)
                {
                    var (price, template) = ParseMenuEntry(e);
                    var name = template ?? e;
                    lines.Add($" - {name} : {price}c");
                }
            }
            return string.Join("\n", lines);
        }

        private (long price, string template) FindMenuEntry(string token)
        {
            try
            {
                token = token.Trim().ToLowerInvariant();
                IEnumerable<string> menus = new List<string>();
                if (_cfg.SellDrinks && _cfg.DrinkMenu != null) menus = menus.Concat(_cfg.DrinkMenu);
                if (_cfg.SellFood && _cfg.FoodMenu != null) menus = menus.Concat(_cfg.FoodMenu);

                foreach (var e in menus)
                {
                    var (price, template) = ParseMenuEntry(e);
                    if (string.Equals(template?.ToLowerInvariant(), token, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(template) && template.ToLowerInvariant().Contains(token)))
                        return (price, template ?? "");
                }
            }
            catch { }
            return (0L, "");
        }

        private (long price, string template) ParseMenuEntry(string entry)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(entry)) return (0L, "");
                var p = entry.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                var tmpl = p[0].Trim();
                long price = 0;
                if (p.Length > 1) long.TryParse(p[1].Trim(), out price);
                return (price, tmpl);
            }
            catch { return (0L, ""); }
        }

        // Payment attempt handler: player issues "give <amount> to <npc>" (command wiring must route here for pending BarkeepBuy)

        public Message HandlePaymentAttempt(Player player, string amountExpr, SqliteConnection? conn = null)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            try
            {
                if (player.PendingAction == null || !string.Equals(player.PendingAction.Command, "BarkeepBuy", StringComparison.OrdinalIgnoreCase))
                    return MessageHandler.System("You have no pending purchase.");

                if (!(player.PendingAction.Data is Dictionary<string, object> pendingDict))
                    return MessageHandler.System("Invalid pending purchase data.");

                if (!pendingDict.TryGetValue("Price", out var priceObj) || !long.TryParse(priceObj?.ToString() ?? "0", out var priceCopper) || priceCopper <= 0)
                    return MessageHandler.System("Invalid purchase data.");

                // Parse amount expression using ItemTrader helper (supports 1g20s5c etc)
                long attemptedCopper = 0;
                try { attemptedCopper = MudCore.AI.ItemTrader.ParseCurrencyExpressionToCopper(amountExpr); } catch { attemptedCopper = 0; }
                if (attemptedCopper <= 0) return MessageHandler.System("Please specify an amount to give (e.g. 'give 5c to Jonah' or 'give 1s to Jonah').");

                long amountPaidNow = 0;
                long paidSoFar = 0;
                long changeCopper = 0;

                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                int qBefore = MudServer.DbWriteQueue.Instance.GetQueueLength();
                int wps = MudServer.DbWriteQueue.Instance.GetWritesPerSecond();
                Console.WriteLine($"[BarTender][PAY] Enqueueing writer job. QueueLength={qBefore}, writes/sec={wps}, Player='{player?.Name}', AmountRequested={attemptedCopper}c");

                // IMMEDIATE ACK: if the amount the player is attempting plus already-paid meets or exceeds the price,
                // notify the player immediately so they don't wait blind while the writer work completes.
                try
                {
                    long currPaid = 0;
                    try { currPaid = pendingDict.ContainsKey("Paid") ? Convert.ToInt64(pendingDict["Paid"]) : 0L; } catch { currPaid = 0L; }
                    if (currPaid + attemptedCopper >= priceCopper)
                    {
                        try { MessageHandler.SendSystem(player, $"{_npc.DisplayName}: Ok, give me a few moments!"); } catch { }
                    }
                }
                catch { /* best-effort - don't fail payment path for messaging */ }

                try
                {

                    // Writer transaction: remove the requested coins and update pending (DO NOT refund on partial here)
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        // 1) Remove coins for the explicitly parsed amount (may consume higher denominations per ItemTrader behavior)
                        amountPaidNow = RemoveCoinsFromPlayerInTx(player, attemptedCopper, wConn, wTx);

                        // 2) Update in-memory pending Paid accumulator
                        try
                        {
                            paidSoFar = pendingDict.ContainsKey("Paid") ? Convert.ToInt64(pendingDict["Paid"]) : 0L;
                        }
                        catch { paidSoFar = 0L; }
                        paidSoFar += amountPaidNow;
                        pendingDict["Paid"] = paidSoFar;

                        // 3) If fully paid now, finalize purchase and give change
                        if (paidSoFar >= priceCopper)
                        {
                            changeCopper = paidSoFar - priceCopper;

                            try
                            {
                                CommitPurchaseFinalizeInTx(player, pendingDict, priceCopper, amountPaidNow, changeCopper, wConn, wTx);
                            }
                            catch (Exception ex)
                            {
                                // On finalize failure, refund the amountPaidNow and rethrow to be handled outside
                                try { RefundPaidToPlayerInTx(player, amountPaidNow, wConn, wTx); } catch { }
                                throw new Exception($"Finalize failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Partial payment: keep pending intact so player can give more later.
                        }

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(30));

                }
                catch (Exception exRun)
                {
                    Console.WriteLine($"[BarTender] Payment transaction failed: {exRun.Message}");
                    // If we removed some coins but the writer failed, attempt best-effort refund
                    try
                    {
                        if (amountPaidNow > 0)
                        {
                            MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                            {
                                RefundPaidToPlayerInTx(player, amountPaidNow, wConn, wTx);
                                return (object?)null;
                            }, timeout: TimeSpan.FromSeconds(10));
                        }
                    }
                    catch { }
                    return MessageHandler.System("Payment failed due to a server error. Your money has not been taken.");
                }

                // After successful writer tx, decide response based on accumulated paid
                try
                {
                    paidSoFar = pendingDict.ContainsKey("Paid") ? Convert.ToInt64(pendingDict["Paid"]) : 0L;

                    if (paidSoFar < priceCopper)
                    {
                        long stillOwe = priceCopper - paidSoFar;
                        // Keep pending action active and keep NPC locked
                        return MessageHandler.System($"{_npc.DisplayName}: You paid {FormatCopper(amountPaidNow)}. You still owe {FormatCopper(stillOwe)}.");
                    }
                    else
                    {
                        // Fully paid: clear pending and unlock NPC interrupt (CommitPurchaseFinalizeInTx handled DB-side transfers and change)
                        try { player.PendingAction = null; } catch { }
                        try { _npc.RemoveInterrupt(Npc.InterruptType.Trade, owner: player.PlayerID); } catch { }
                        _npc.IsTradeLocked = false;

                        // Thank-you message; CommitPurchaseFinalizeInTx already gave change and item to player (via DB + in-memory)
                        sw.Stop();
                        int qAfter = MudServer.DbWriteQueue.Instance.GetQueueLength();
                        Console.WriteLine($"[BarTender][PAY] Writer job finished in {sw.ElapsedMilliseconds}ms. QueueBefore={qBefore}, QueueAfter={qAfter}, Removed={amountPaidNow}c");

                        // Provide the ordered item name in the thank-you message (best-effort)
                        string itemOrdered = "";
                        try
                        {
                            if (pendingDict != null)
                            {
                                if (pendingDict.TryGetValue("ItemName", out var inObj) && inObj != null)
                                    itemOrdered = inObj.ToString() ?? "";
                                if (string.IsNullOrWhiteSpace(itemOrdered) && pendingDict.TryGetValue("TemplateId", out var tObj) && tObj != null)
                                    itemOrdered = tObj.ToString() ?? "";
                            }
                        }
                        catch { itemOrdered = ""; }

                        if (string.IsNullOrWhiteSpace(itemOrdered)) itemOrdered = "item";

                        return MessageHandler.System($"{_npc.DisplayName}: Thank you — here is your {itemOrdered}!");

                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BarTender] Post-payment handling error: {ex.Message}");
                    return MessageHandler.System("Payment completed but post-processing failed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] HandlePaymentAttempt error for NpcID={_npc?.NpcID}: {ex.Message}");
                return MessageHandler.System("Payment processing failed.");
            }
        }

        // Remove up to copperNeeded from player's coin stacks inside the provided writer transaction.
        // Returns the total copper actually removed (may be 0).

        private long RemoveCoinsFromPlayerInTx(Player player, long copperNeeded, SqliteConnection wConn, SqliteTransaction? wTx)
        {
            long remaining = Math.Max(0, copperNeeded);
            long removed = 0;
            if (player.Inventory == null || remaining <= 0) return 0;

            // Preferred order: gold, silver, copper
            var coinOrder = new[] { ("coin_gold", Currency.CopperPerGold), ("coin_silver", Currency.CopperPerSilver), ("coin_copper", 1L) };

            foreach (var (templateId, unitValue) in coinOrder)
            {
                if (remaining <= 0) break;

                var matches = player.Inventory.Where(it => it != null && string.Equals(it.TemplateID, templateId, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var it in matches)
                {
                    if (it == null) continue;
                    long qty = Math.Max(0, it.Quantity);
                    if (qty <= 0) continue;
                    long maxCopperHere = qty * unitValue;
                    long needHere = Math.Min(remaining, maxCopperHere);
                    long unitsToRemove = (long)(needHere / unitValue);
                    if (unitsToRemove <= 0 && needHere > 0)
                    {
                        // if unitValue > needHere but at least one coin needed, remove one coin
                        unitsToRemove = 1;
                    }
                    unitsToRemove = Math.Min(unitsToRemove, qty);

                    // Update item quantity
                    it.Quantity = (int)(qty - unitsToRemove);
                    long copperRemovedHere = unitsToRemove * unitValue;
                    remaining -= copperRemovedHere;
                    removed += copperRemovedHere;

                    // Persist change: if quantity > 0 save, else delete
                    try
                    {
                        if (it.Quantity > 0)
                        {
                            MudPersistence.ItemPersistence.SaveItem(wConn, it, wTx);
                        }
                        else
                        {
                            // Delete item row
                            try
                            {
                                MudPersistence.ItemPersistence.DeleteItem(wConn, it.ItemID);
                            }
                            catch
                            {
                                // fallback: attempt explicit delete via command (best-effort)
                                try
                                {
                                    using var cmd = wConn.CreateCommand();
                                    if (wTx != null) cmd.Transaction = wTx;
                                    cmd.CommandText = "DELETE FROM Item WHERE ItemID = @id;";
                                    cmd.Parameters.AddWithValue("@id", it.ItemID);
                                    cmd.ExecuteNonQuery();
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BarTender] Failed to persist coin removal for ItemID={it.ItemID}: {ex.Message}");
                    }

                    // Update player's in-memory inventory list: remove items with qty 0
                    if (it.Quantity == 0)
                    {
                        try { player.Inventory.Remove(it); } catch { }
                    }

                    if (remaining <= 0) break;
                }
            }

            // Safety check: removed should never exceed requested; if it does, log for diagnosis.
            if (removed > copperNeeded)
            {
                Console.WriteLine($"[BarTender][WARN] Removed more copper ({removed}) than requested ({copperNeeded}) for player '{player?.Name}'.");
            }

            return removed;
        }

        // Refund a given copper amount to player inside writer tx by creating coin items back to player.
        private void RefundPaidToPlayerInTx(Player player, long paidCopper, SqliteConnection wConn, SqliteTransaction? wTx)
        {
            if (player == null || paidCopper <= 0) return;
            try
            {
                // Break into gold/silver/copper
                long gc = paidCopper;
                long gold = gc / MudCore.Entities.Currency.CopperPerGold;
                gc -= gold * MudCore.Entities.Currency.CopperPerGold;
                long silver = gc / MudCore.Entities.Currency.CopperPerSilver;
                gc -= silver * MudCore.Entities.Currency.CopperPerSilver;
                long copper = gc;

                // Helper that attempts to merge into an existing player stack first, otherwise creates a new item row.
                void GiveCoinMerged(string tmpl, long qty)
                {
                    if (qty <= 0) return;
                    try
                    {
                        // Try merge using InventoryManager helper inside the writer tx context
                        try
                        {
                            var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                wConn, wTx,
                                "Player", player.DbPlayerID,
                                player.CurrentRoom?.RoomID ?? 0,
                                tmpl, "[]", (int)qty);

                            if (merged.mergedId != 0)
                            {
                                // Update in-memory if present
                                try
                                {
                                    var mem = player.Inventory?.FirstOrDefault(i => i != null && i.ItemID == merged.mergedId);
                                    if (mem != null) mem.Quantity = merged.newQty;
                                }
                                catch { }
                                return;
                            }
                        }
                        catch
                        {
                            // If merge helper fails for any reason, fall back to create-new path below.
                        }

                        // Create new coin item owned by player
                        var created = _world.ItemFactory.CreateItem(tmpl, "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, (int)qty);
                        created.Name = created.Name ?? tmpl;
                        MudPersistence.ItemPersistence.SaveItem(wConn, created, wTx);
                        try
                        {
                            if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>();
                            player.Inventory.Add(created);
                        }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BarTender] RefundPaidToPlayerInTx GiveCoinMerged failed for {tmpl}: {ex.Message}");
                    }
                }

                GiveCoinMerged("coin_gold", gold);
                GiveCoinMerged("coin_silver", silver);
                GiveCoinMerged("coin_copper", copper);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] RefundPaidToPlayerInTx failed: {ex.Message}");
            }
        }

        // Finalize purchase inside writer transaction: create container owned by player, create inner items, give change.
        private void CommitPurchaseFinalizeInTx(Player player, Dictionary<string, object> pending, long priceCopper, long paidNow, long changeCopper, SqliteConnection wConn, SqliteTransaction? wTx)
        {
            if (player == null || pending == null) throw new ArgumentNullException(nameof(player));
            if (!pending.TryGetValue("TemplateId", out var tmplObj) || tmplObj == null) throw new ArgumentException("Missing TemplateId");
            var itemTemplate = tmplObj.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(itemTemplate)) throw new ArgumentException("Invalid TemplateId");

            bool IsDrinkTemplate(string tmpl)
            {
                try
                {
                    if (!_cfg.SellDrinks || _cfg.DrinkMenu == null) return false;
                    foreach (var e in _cfg.DrinkMenu)
                    {
                        var (p, t) = ParseMenuEntry(e);
                        if (!string.IsNullOrWhiteSpace(t) && string.Equals(t, tmpl, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
                return false;
            }

            bool isDrink = IsDrinkTemplate(itemTemplate);

            // If pending references a reserved container+drink (drink sale), transfer container to player then attach the drink to that container.
            if (isDrink && pending.TryGetValue("ReservedContainerId", out var rcObj) && int.TryParse(rcObj?.ToString() ?? "0", out var containerId) && containerId > 0
                && pending.TryGetValue("ReservedDrinkId", out var rdObj) && int.TryParse(rdObj?.ToString() ?? "0", out var drinkId) && drinkId > 0)
            {
                try
                {
                    // Load NPC rows authoritative
                    var npcItems = MudPersistence.ItemPersistence.LoadItemsByOwner(wConn, "NPC", _npc.NpcID, wTx) ?? new List<MudCore.Entities.Item>();
                    var containerRow = npcItems.FirstOrDefault(i => i != null && i.ItemID == containerId);
                    var drinkRow = npcItems.FirstOrDefault(i => i != null && i.ItemID == drinkId);

                    if (containerRow == null || drinkRow == null)
                    {
                        // reserved rows missing; refund
                        RefundPaidToPlayerInTx(player, paidNow, wConn, wTx);
                        throw new Exception("Reserved container or drink not available.");
                    }

                    // Transfer container to player (handle stacks vs single)
                    int contAvail = Math.Max(1, containerRow.Quantity);
                    int playerContainerItemId = 0;
                    if (contAvail > 1)
                    {
                        // decrement NPC stack and create a new player-owned container
                        containerRow.Quantity = contAvail - 1;
                        MudPersistence.ItemPersistence.SaveItem(wConn, containerRow, wTx);

                        var createdContainer = _world.ItemFactory.CreateItem(containerRow.TemplateID ?? (_cfg.FillContainerTemplateId ?? "glass_pint"), "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, 1);
                        createdContainer.Name = createdContainer.Name ?? containerRow.Name;
                        MudPersistence.ItemPersistence.SaveItem(wConn, createdContainer, wTx);
                        playerContainerItemId = createdContainer.ItemID;
                        try { if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>(); player.Inventory.Add(createdContainer); } catch { }
                    }
                    else
                    {
                        // full-row transfer of container
                        containerRow.OwnerType = "Player";
                        containerRow.OwnerID = player.DbPlayerID;
                        containerRow.RoomID = player.CurrentRoom?.RoomID ?? 0;
                        MudPersistence.ItemPersistence.SaveItem(wConn, containerRow, wTx);
                        playerContainerItemId = containerRow.ItemID;
                        try { _npc.Inventory?.RemoveAll(i => i != null && i.ItemID == containerRow.ItemID); } catch { }
                        try { if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>(); player.Inventory.Add(containerRow); } catch { }
                    }

                    // Attach drink to the player's container by changing drink row ownership to OwnerType='Item' and OwnerID=playerContainerItemId.
                    int drinkAvail = Math.Max(1, drinkRow.Quantity);
                    if (drinkAvail > 1)
                    {
                        // decrement NPC stack and create a new drink item owned by the container
                        drinkRow.Quantity = drinkAvail - 1;
                        MudPersistence.ItemPersistence.SaveItem(wConn, drinkRow, wTx);

                        var createdDrink = _world.ItemFactory.CreateItem(itemTemplate, "Item", playerContainerItemId, player.CurrentRoom?.RoomID ?? 0, 1);
                        createdDrink.Name = createdDrink.Name ?? itemTemplate;
                        MudPersistence.ItemPersistence.SaveItem(wConn, createdDrink, wTx);
                    }
                    else
                    {
                        // transfer existing drink row into the container
                        drinkRow.OwnerType = "Item";
                        drinkRow.OwnerID = playerContainerItemId;
                        // keep RoomID set to container's room
                        drinkRow.RoomID = player.CurrentRoom?.RoomID ?? 0;
                        MudPersistence.ItemPersistence.SaveItem(wConn, drinkRow, wTx);
                        try { _npc.Inventory?.RemoveAll(i => i != null && i.ItemID == drinkRow.ItemID); } catch { }
                    }

                    // Release reservations
                    try { ReleaseReservationForItem(containerId); } catch { }
                    try { ReleaseReservationForItem(drinkId); } catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BarTender] Failed to transfer reserved container+drink: {ex.Message}");
                    throw;
                }
            }
            else if (pending.TryGetValue("ReservedItemId", out var reservedObj) && int.TryParse(reservedObj?.ToString() ?? "0", out var reservedId) && reservedId > 0)
            {
                // Non-drink (existing behavior)
                try
                {
                    var npcItems = MudPersistence.ItemPersistence.LoadItemsByOwner(wConn, "NPC", _npc.NpcID, wTx) ?? new List<MudCore.Entities.Item>();
                    var row = npcItems.FirstOrDefault(i => i != null && i.ItemID == reservedId);
                    if (row == null)
                    {
                        RefundPaidToPlayerInTx(player, paidNow, wConn, wTx);
                        throw new Exception("Reserved item not available.");
                    }

                    int giveQty = 1;
                    int avail = Math.Max(1, row.Quantity);
                    if (avail > giveQty)
                    {
                        row.Quantity = avail - giveQty;
                        MudPersistence.ItemPersistence.SaveItem(wConn, row, wTx);

                        bool merged = false;
                        if (!string.IsNullOrWhiteSpace(row.TemplateID))
                        {
                            try
                            {
                                var mergedInfo = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                    wConn, wTx, "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0,
                                    row.TemplateID ?? "", row.FlagsJson ?? "[]", giveQty);

                                if (mergedInfo.mergedId != 0)
                                {
                                    try
                                    {
                                        var mem = player.Inventory?.FirstOrDefault(i => i != null && i.ItemID == mergedInfo.mergedId);
                                        if (mem != null) mem.Quantity = mergedInfo.newQty;
                                    }
                                    catch { }
                                    merged = true;
                                }
                            }
                            catch { merged = false; }
                        }

                        if (!merged)
                        {
                            var created = _world.ItemFactory.CreateItem(row.TemplateID ?? row.Name ?? "", "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, giveQty);
                            created.Name = row.Name;
                            created.ItemCategory = row.ItemCategory;
                            MudPersistence.ItemPersistence.SaveItem(wConn, created, wTx);
                            try { if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>(); player.Inventory.Add(created); } catch { }
                        }

                        try { ReleaseReservationForItem(row.ItemID); } catch { }
                    }
                    else
                    {
                        row.OwnerType = "Player";
                        row.OwnerID = player.DbPlayerID;
                        row.RoomID = player.CurrentRoom?.RoomID ?? 0;
                        MudPersistence.ItemPersistence.SaveItem(wConn, row, wTx);

                        try { _npc.Inventory?.RemoveAll(i => i != null && i.ItemID == row.ItemID); } catch { }
                        try { if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>(); player.Inventory.Add(row); } catch { }

                        try { ReleaseReservationForItem(row.ItemID); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BarTender] Failed to transfer reserved item: {ex.Message}");
                    throw;
                }
            }
            else
            {
                // Fallback creation (unchanged) — create container+inside for drinks, or a player item for non-drinks
                try
                {
                    if (isDrink)
                    {
                        var containerTemplate = !string.IsNullOrWhiteSpace(_cfg.FillContainerTemplateId) ? _cfg.FillContainerTemplateId : "glass_pint";
                        var container = _world.ItemFactory.CreateItem(containerTemplate, "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, 1);
                        container.Name = container.Name ?? containerTemplate;
                        MudPersistence.ItemPersistence.SaveItem(wConn, container, wTx);

                        var inside = _world.ItemFactory.CreateItem(itemTemplate, "Item", container.ItemID, player.CurrentRoom?.RoomID ?? 0, 1);
                        inside.Name = inside.Name ?? itemTemplate;
                        MudPersistence.ItemPersistence.SaveItem(wConn, inside, wTx);
                    }
                    else
                    {
                        var created = _world.ItemFactory.CreateItem(itemTemplate, "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, 1);
                        created.Name = created.Name ?? itemTemplate;
                        MudPersistence.ItemPersistence.SaveItem(wConn, created, wTx);
                        try
                        {
                            if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>();
                            player.Inventory.Add(created);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BarTender] Fallback create items failed: {ex.Message}");
                    throw;
                }
            }

            // Give change if any (merge into existing stacks when possible)
            try
            {
                if (changeCopper > 0)
                {
                    long gc = changeCopper;
                    long gold = gc / MudCore.Entities.Currency.CopperPerGold;
                    gc -= gold * MudCore.Entities.Currency.CopperPerGold;
                    long silver = gc / MudCore.Entities.Currency.CopperPerSilver;
                    gc -= silver * MudCore.Entities.Currency.CopperPerSilver;
                    long copper = gc;

                    void GiveOrMerge(string tmpl, int units)
                    {
                        if (units <= 0) return;
                        try
                        {
                            try
                            {
                                var merged = MudCore.Entities.InventoryManager.TryMergeIntoExistingStack(
                                    wConn, wTx, "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0,
                                    tmpl, "[]", units);

                                if (merged.mergedId != 0)
                                {
                                    try
                                    {
                                        var mem = player.Inventory?.FirstOrDefault(i => i != null && i.ItemID == merged.mergedId);
                                        if (mem != null) mem.Quantity = merged.newQty;
                                    }
                                    catch { }
                                    return;
                                }
                            }
                            catch { }

                            var created = _world.ItemFactory.CreateItem(tmpl, "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, units);
                            created.Name = created.Name ?? tmpl;
                            MudPersistence.ItemPersistence.SaveItem(wConn, created, wTx);
                            try
                            {
                                if (player.Inventory == null) player.Inventory = new List<MudCore.Entities.Item>();
                                player.Inventory.Add(created);
                            }
                            catch { }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[BarTender] GiveOrMerge failed for {tmpl}: {ex.Message}");
                            throw;
                        }
                    }

                    GiveOrMerge("coin_gold", (int)gold);
                    GiveOrMerge("coin_silver", (int)silver);
                    GiveOrMerge("coin_copper", (int)copper);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTender] Failed giving change: {ex.Message}");
                throw;
            }

            // Update sales counts
            try
            {
                if (_state.SalesCounts == null) _state.SalesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (!_state.SalesCounts.ContainsKey(itemTemplate)) _state.SalesCounts[itemTemplate] = 0;
                _state.SalesCounts[itemTemplate] += 1;
                _state.LastServeUtc = DateTime.UtcNow;
                PersistState(wConn);
            }
            catch { /*best-effort*/ }
        }

        // Helpers: formatting and parsing currency
        private static string FormatCopper(long copper)
        {
            if (copper < 0) copper = 0;
            var cur = Currency.FromCopper(copper);
            var parts = new List<string>();
            if (cur.Gold > 0) parts.Add($"{cur.Gold}g");
            if (cur.Silver > 0) parts.Add($"{cur.Silver}s");
            if (cur.Copper > 0) parts.Add($"{cur.Copper}c");
            if (parts.Count == 0) return "0c";
            return string.Join(" ", parts);
        }

        // Called by chat handling to collect rumors when InformationCollector=true and keywords match.
        // This method is best-effort; it will insert a Rumor into EventCollector when a trigger keyword is found.
        public void OnPlayerSpoke(Player player, string text, World.World? world = null, SqliteConnection? conn = null)
        {
            if (player == null || string.IsNullOrWhiteSpace(text)) return;
            if (!_cfg.InformationCollector) return;
            try
            {
                // Only collect while serving or when in bar room if configured
                if (!IsServingNow(out _))
                {
                    // allow collection only if in same bar room (if configured)
                    if (_cfg.BarRoom > 0 && player.CurrentRoom != null && player.CurrentRoom.RoomID != _cfg.BarRoom)
                        return;
                }
                // single-match across keywords -> capture full string once
                if (_cfg.TriggerCollectKeywords == null || _cfg.TriggerCollectKeywords.Count == 0) return;
                var lower = text.ToLowerInvariant();
                bool matched = false;
                foreach (var kw in _cfg.TriggerCollectKeywords)
                {
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    if (lower.IndexOf(kw.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) >= 0) { matched = true; break; }
                }
                if (!matched) return;

                // Groom rumor text
                string captured = text.Trim();
                string rumor = $"It was overheard that {player.Name} was talking about {captured}.";

                int? areaId = null;
                try { areaId = player.CurrentRoom?.AreaID; } catch { areaId = null; }

                // metadata
                var meta = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NpcID"] = _npc.NpcID,
                    ["PlayerID"] = player.PlayerID ?? ""
                };

                try
                {
                    EventCollector.InsertRumor(areaId, rumor, false, conn, expiresAt: null, metadata: meta);
                    _state.LastCollectUtc = DateTime.UtcNow;
                    PersistState(conn);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BarTender] Failed to insert rumor: {ex.Message}");
                }
            }
            catch { /*best-effort*/ }
        }

        

        private void ReleaseReservationForItem(int itemId)
        {
            if (itemId <= 0) return;
            lock (_reserveLock)
            {
                _reservedItems.Remove(itemId);
            }
        }

        private void RestockIfNeeded(string templateId, int targetCount)
        {
            if (string.IsNullOrWhiteSpace(templateId) || targetCount <= 0) return;

            try
            {
                MudServer.DbWriteQueue.Instance.EnqueueAsync(async (wConn, wTx) =>
                {
                    try
                    {
                        bool isDrink = false;
                        try
                        {
                            if (_cfg.SellDrinks && _cfg.DrinkMenu != null)
                            {
                                foreach (var e in _cfg.DrinkMenu)
                                {
                                    var (_, t) = ParseMenuEntry(e);
                                    if (!string.IsNullOrWhiteSpace(t) && string.Equals(t, templateId, StringComparison.OrdinalIgnoreCase)) { isDrink = true; break; }
                                }
                            }
                        }
                        catch { isDrink = false; }

                        var current = MudPersistence.ItemPersistence.LoadItemsByOwner(wConn, "NPC", _npc.NpcID, wTx) ?? new List<MudCore.Entities.Item>();

                        if (isDrink)
                        {
                            var containerTemplate = !string.IsNullOrWhiteSpace(_cfg.FillContainerTemplateId) ? _cfg.FillContainerTemplateId : "glass_pint";
                            int haveContainers = current.Where(i => i != null && string.Equals(i.TemplateID, containerTemplate, StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Max(1, i.Quantity));
                            int haveDrinks = current.Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Max(1, i.Quantity));

                            int needContainers = Math.Max(0, targetCount - haveContainers);
                            int needDrinks = Math.Max(0, targetCount - haveDrinks);

                            // Use explicit configured bar room id (per your request)
                            int barRoomId = _cfg.BarRoom;

                            for (int i = 0; i < needContainers; i++)
                            {
                                var container = _world.ItemFactory.CreateItem(containerTemplate, "NPC", _npc.NpcID, 0, 1);
                                container.Name = container.Name ?? containerTemplate;

                                // Attach DropEmpty flag: DropEmpty:<npcExemptID>:<barRoomID>
                                try
                                {
                                    var flag = $"DropEmpty:{_npc.NpcID}:{barRoomId}";
                                    container.Flags = container.Flags ?? new List<string>();
                                    if (!container.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)))
                                        container.Flags.Add(flag);
                                    container.FlagsJson = JsonSerializer.Serialize(container.Flags);
                                }
                                catch { /* best-effort */ }

                                // Persist container
                                MudPersistence.ItemPersistence.SaveItem(wConn, container, wTx);

                                // Defensive: ensure the DropEmpty flag actually ends up persisted even if SaveItem merged with an existing row.
                                try
                                {
                                    // 1) Read back the current Flags value for this ItemID (inside same writer tx)
                                    string currentFlagsJson = "[]";
                                    try
                                    {
                                        using var sel = wConn.CreateCommand();
                                        if (wTx != null) sel.Transaction = wTx;
                                        sel.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id;";
                                        sel.Parameters.AddWithValue("@id", container.ItemID);
                                        var obj = sel.ExecuteScalar();
                                        currentFlagsJson = obj == null ? "[]" : obj.ToString() ?? "[]";
                                    }
                                    catch (Exception exSel)
                                    {
                                        Console.WriteLine($"[BarTender] Failed to SELECT Flags for ItemID={container.ItemID}: {exSel.Message}");
                                        currentFlagsJson = "[]";
                                    }

                                    // 2) Deserialize, merge our intended flags (avoid duplicates), then write back only if changed
                                    try
                                    {
                                        var dbFlagsList = new List<string>();
                                        try
                                        {
                                            dbFlagsList = JsonSerializer.Deserialize<List<string>>(currentFlagsJson) ?? new List<string>();
                                        }
                                        catch { dbFlagsList = new List<string>(); }

                                        // ensure container.Flags is present as well (in-memory)
                                        container.Flags = container.Flags ?? new List<string>();

                                        // Build intended DropEmpty flag string
                                        var flag = $"DropEmpty:{_npc.NpcID}:{(_cfg.BarRoom)}";
                                        // Add to in-memory list for consistency
                                        if (!container.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)))
                                            container.Flags.Add(flag);

                                        // If dbFlagsList lacks our DropEmpty flag, add and persist
                                        bool changed = false;
                                        if (!dbFlagsList.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            dbFlagsList.Add(flag);
                                            changed = true;
                                        }

                                        // Also ensure any other flags from container.Flags are present in DB list (merge)
                                        foreach (var f in container.Flags)
                                        {
                                            if (string.IsNullOrWhiteSpace(f)) continue;
                                            if (!dbFlagsList.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)))
                                            {
                                                dbFlagsList.Add(f);
                                                changed = true;
                                            }
                                        }

                                        if (changed)
                                        {
                                            var newFlagsJson = JsonSerializer.Serialize(dbFlagsList);
                                            using var upd = wConn.CreateCommand();
                                            if (wTx != null) upd.Transaction = wTx;
                                            upd.CommandText = "UPDATE Item SET Flags = @flags WHERE ItemID = @id;";
                                            upd.Parameters.AddWithValue("@flags", newFlagsJson);
                                            upd.Parameters.AddWithValue("@id", container.ItemID);
                                            upd.ExecuteNonQuery();

                                            Console.WriteLine($"[BarTender] Ensured Flags for ItemID={container.ItemID}: {newFlagsJson}");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"[BarTender] Flags already present for ItemID={container.ItemID}: {currentFlagsJson}");
                                        }
                                    }
                                    catch (Exception exMerge)
                                    {
                                        Console.WriteLine($"[BarTender] Failed to merge/persist Flags for ItemID={container.ItemID}: {exMerge.Message}");
                                    }
                                }
                                catch (Exception exUpdOuter)
                                {
                                    Console.WriteLine($"[BarTender] Flags persistence check failed for ItemID={container.ItemID}: {exUpdOuter.Message}");
                                }

                                try
                                {
                                    if (_npc.Inventory == null) _npc.Inventory = new List<Item>();
                                    _npc.Inventory.Add(container);
                                }
                                catch { }

                                Console.WriteLine($"[BarTender] Restock created container ItemID={container.ItemID} Template={container.TemplateID} Flags={container.FlagsJson}");
                            }

                            for (int i = 0; i < needDrinks; i++)
                            {
                                var drink = _world.ItemFactory.CreateItem(templateId, "NPC", _npc.NpcID, 0, 1);
                                drink.Name = drink.Name ?? templateId;
                                MudPersistence.ItemPersistence.SaveItem(wConn, drink, wTx);
                                try
                                {
                                    if (_npc.Inventory == null) _npc.Inventory = new List<Item>();
                                    _npc.Inventory.Add(drink);
                                }
                                catch { }
                                Console.WriteLine($"[BarTender] Restock created drink ItemID={drink.ItemID} Template={drink.TemplateID}");
                            }
                        }
                        else
                        {
                            int have = current.Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Max(1, i.Quantity));
                            int need = Math.Max(0, targetCount - have);
                            for (int i = 0; i < need; i++)
                            {
                                var created = _world.ItemFactory.CreateItem(templateId, "NPC", _npc.NpcID, 0, 1);
                                created.Name = created.Name ?? templateId;
                                MudPersistence.ItemPersistence.SaveItem(wConn, created, wTx);
                                try
                                {
                                    if (_npc.Inventory == null) _npc.Inventory = new List<Item>();
                                    _npc.Inventory.Add(created);
                                }
                                catch { }
                                Console.WriteLine($"[BarTender] Restock created item ItemID={created.ItemID} Template={created.TemplateID}");
                            }
                        }
                    }
                    catch (Exception exInner)
                    {
                        Console.WriteLine($"[BarTender] Restock failed for {templateId}: {exInner.Message}");
                    }
                    await Task.CompletedTask;
                }, timeout: TimeSpan.FromSeconds(10));
            }
            catch
            {
                // best-effort - ignore enqueue failures
            }
        }//end RestockIfNeeded

        
    }// End Class BarTender

}