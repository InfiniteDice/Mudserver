
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using MudUtils;


namespace MudCore.AI
{
    // Lightweight strongly-typed view of the JSON AiBehavior object for ItemTrader.
    // Fields are permissive and defaulted so missing properties in template JSON are tolerated.
    public sealed class TraderConfig
    {
        public string Behavior { get; set; } = "ItemTrader";
        public List<string> TradePeriod { get; set; } = new List<string>(); // e.g. ["Morning","Evening"]
        public int TradeCenter { get; set; } = 0;
        public bool Buy { get; set; } = true;
        public bool Sell { get; set; } = true;
        public bool ConvertCurrency { get; set; } = false;
        public int ConversionFee { get; set; } = 0; // copper absolute minimum
        public double BarterSkill { get; set; } = 0.0;
        public int ItemSaturationLimit { get; set; } = 5;
        public double SellMarkup { get; set; } = 0.1;
        public bool DynamicPricing { get; set; } = false;
        public double DynamicPricingRate { get; set; } = 0.1;
        public int RequiredLevel { get; set; } = 0;
        public List<string> RequiredAlignment { get; set; } = new List<string>(); // e.g. ["Neutral","Good"]
        public int RequiredQuestId { get; set; } = 0;
        public double InventoryWeight { get; set; } = 0.0;
        public double InventoryMaxWeight { get; set; } = 100.0;
        public long CopperTradePool { get; set; } = 0; // copper
        public long MinTradePool { get; set; } = 0;
        public List<string> ItemTypesAllowed { get; set; } = new List<string>(); // e.g. ["Component","Armor"]
        public List<string> DesiredItems { get; set; } = new List<string>();
        public string Notes { get; set; } = "";
    }

    // Runtime state persisted into the NPC via a Flags-style marker (TraderState:{json}).
    // Keep minimal to avoid schema changes.
    public sealed class TraderState
    {
        public double InventoryWeight { get; set; } = 0.0;
        public long CopperTradePool { get; set; } = 0;
        // counts by template for dynamic pricing/saturation
        public Dictionary<string, int> CountsByTemplate { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // last time trader announced desired item (used by periodic tick)
        public DateTime? LastDesiredAnnounceUtc { get; set; } = null;
    }

    // Per-NPC trader behavior manager. Lightweight, minimal-impact implementation.
    // Responsibilities:
    //  - Parse TraderConfig from npc.Template.AiBehaviorsRaw (object matching "Behavior":"ItemTrader")
    //  - Maintain TraderState persisted into npc.Flags (as a single string "TraderState:<json>")
    //  - Provide simple public API to drive interactions from CommandHandler or NpcAI:
    //      * ItemTrader.TryHandleCommand(player, verb, args) -> Message
    //      * ItemTrader.IsOpenNow(world) etc.
    // Notes:
    //  - This implementation focuses on deterministic logic, messaging and state updates.
    //  - Full transactional item/currency transfers implemented here using the writer queue.
    public sealed class ItemTrader
    {
        private readonly Npc _npc;
        private readonly World.World _world;
        private readonly TraderConfig _cfg;
        private TraderState _state;
        
        private readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        // Reservation map for in-progress purchases: ItemID -> PlayerID
        // This is runtime-only (not persisted). We keep it simple and lock for thread-safety.
        private readonly object _reserveLock = new object();
        private readonly System.Collections.Generic.Dictionary<int, string> _reservedItems = new System.Collections.Generic.Dictionary<int, string>();

        // Try to reserve an item for a player. Returns true when reserved (or already reserved by same player).
        private bool ReserveItemForPlayer(int itemId, string playerId)
        {
            if (itemId <= 0 || string.IsNullOrWhiteSpace(playerId)) return false;
            lock (_reserveLock)
            {
                if (_reservedItems.TryGetValue(itemId, out var existing))
                {
                    return string.Equals(existing, playerId, StringComparison.OrdinalIgnoreCase);
                }
                _reservedItems[itemId] = playerId;
                return true;
            }
        }

        // Release reservation for a single item
        private void ReleaseReservationForItem(int itemId)
        {
            if (itemId <= 0) return;
            lock (_reserveLock)
            {
                _reservedItems.Remove(itemId);
            }
        }

        // Release all reservations held by a player
        private void ReleaseReservationsForPlayer(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;
            lock (_reserveLock)
            {
                var toRemove = _reservedItems.Where(kv => string.Equals(kv.Value, playerId, StringComparison.OrdinalIgnoreCase))
                                             .Select(kv => kv.Key).ToList();
                foreach (var k in toRemove) _reservedItems.Remove(k);
            }
        }

        private const string TraderStateFlagPrefix = "TraderState:";

        private ItemTrader(Npc npc, World.World world, TraderConfig cfg)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _cfg = cfg ?? new TraderConfig();
            _state = new TraderState();
            LoadStateFromNpc();
        }

        // Factory: create an ItemTrader from Npc.Template.AiBehaviorsRaw when an ItemTrader behavior object exists.
        // Returns null when no ItemTrader behavior declared.
        public static ItemTrader? CreateIfConfigured(Npc npc, World.World world)
        {
            try
            {
                if (npc?.Template == null || npc.Template.AiBehaviorsRaw == null) return null;
                foreach (var el in npc.Template.AiBehaviorsRaw)
                {
                    try
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        if (el.TryGetProperty("Behavior", out var be) && string.Equals(be.GetString() ?? "", "ItemTrader", StringComparison.OrdinalIgnoreCase))
                        {
                            var cfg = JsonSerializer.Deserialize<TraderConfig>(el.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (cfg == null) cfg = new TraderConfig();
                            var t = new ItemTrader(npc, world, cfg);
                            return t;
                        }
                    }
                    catch { /* best-effort per-element */ }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] CreateIfConfigured failed for Npc '{npc?.Name}': {ex.Message}");
            }
            return null;
        }

        // Load TraderState from npc.Flags (looks for single string starting with TraderState:).
        private void LoadStateFromNpc()
        {
            try
            {
                if (_npc.Flags != null)
                {
                    foreach (var f in _npc.Flags)
                    {
                        if (string.IsNullOrWhiteSpace(f)) continue;
                        if (f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var json = f.Substring(TraderStateFlagPrefix.Length).Trim();
                            if (!string.IsNullOrWhiteSpace(json))
                            {
                                try
                                {
                                    var s = JsonSerializer.Deserialize<TraderState>(json, _jsonOpts);
                                    if (s != null) { _state = s; return; }
                                }
                                catch { /* best-effort parse */ }
                            }
                        }
                    }
                }

                // Not present -> seed from config fallback
                _state = new TraderState
                {
                    InventoryWeight = _cfg.InventoryWeight,
                    CopperTradePool = _cfg.CopperTradePool
                };

                // seed counts by scanning npc inventory
                RebuildCountsFromInventory();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] LoadStateFromNpc error for NpcID={_npc?.NpcID}: {ex.Message}");
                _state = new TraderState { InventoryWeight = _cfg.InventoryWeight, CopperTradePool = _cfg.CopperTradePool };
            }
        }

        // Persist state back into npc.Flags (single entry TraderState:<json>) and request DB save via NpcPersistence.
        public void PersistState(SqliteConnection? conn = null)
        {
            try
            {
                var json = JsonSerializer.Serialize(_state, _jsonOpts);
                // replace or add TraderState:... flag
                var flags = _npc.Flags ?? new List<string>();
                flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                flags.Add($"{TraderStateFlagPrefix}{json}");
                _npc.Flags = flags;
                _npc.FlagsJson = JsonSerializer.Serialize(flags);

                // Persist via NpcPersistence.UpdateNpc when connection provided (or try writer otherwise)
                try
                {
                    if (conn != null && _npc.NpcID > 0)
                        NpcPersistence.UpdateNpc(conn, _npc);
                    else
                    {
                        // best-effort: attempt to persist using writer queue so the Flags column is updated atomically when available
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
                catch
                {
                    // best-effort: don't throw from persistence
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] PersistState failed for NpcID={_npc?.NpcID}: {ex.Message}");
            }
        }

        private void RebuildCountsFromInventory()
        {
            try
            {
                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in _npc.Inventory ?? Enumerable.Empty<Item>())
                {
                    if (it == null || string.IsNullOrWhiteSpace(it.TemplateID)) continue;
                    if (!dict.TryGetValue(it.TemplateID, out var c)) c = 0;
                    c += Math.Max(1, it.Quantity);
                    dict[it.TemplateID] = c;
                }
                _state.CountsByTemplate = dict;
            }
            catch { _state.CountsByTemplate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); }
        }

        // PUBLIC API: Is the trader open now? If closed, friendlyText lists open periods.
        public bool IsOpenNow(out string friendlyText)
        {
            friendlyText = "";
            try
            {
                if (_cfg.TradePeriod == null || _cfg.TradePeriod.Count == 0)
                {
                    // no restriction -> always open
                    return true;
                }

                foreach (var p in _cfg.TradePeriod)
                {
                    if (_world?.GameTime != null && _world.GameTime.IsInPeriod(p)) return true;
                }

                friendlyText = string.Join(" or ", _cfg.TradePeriod);
                return false;
            }
            catch
            {
                friendlyText = _cfg.TradePeriod != null && _cfg.TradePeriod.Count > 0 ? string.Join(",", _cfg.TradePeriod) : "normal hours";
                throw;
            }
        }

        // PUBLIC API: checks level/alignment/quest requirements; returns (canTrade, reason)
        public (bool CanTrade, string? Reason) CanTradeWith(Player player)
        {
            if (player == null) return (false, "Invalid player.");

            // Level
            try
            {
                if (_cfg.RequiredLevel > 0 && player.Level < _cfg.RequiredLevel)
                    return (false, "Sorry I don't deal with minor players! Come back when you have more experience.");
            }
            catch { /* ignore */ }

            // Alignment: check player.Alignment enum (Good/Neutral/Evil)
            try
            {
                if (_cfg.RequiredAlignment != null && _cfg.RequiredAlignment.Count > 0)
                {
                    bool ok = false;
                    string playerAlignment = player.Alignment.ToString(); // "Good", "Neutral", or "Evil"

                    foreach (var want in _cfg.RequiredAlignment)
                    {
                        if (string.Equals(want, playerAlignment, StringComparison.OrdinalIgnoreCase))
                        {
                            ok = true;
                            break;
                        }
                    }

                    if (!ok)
                        return (false, "Sorry I don't trade with your kind. Come back when your alignment suits my wares.");
                }
            }
            catch { /* best-effort */ }

            // Quest gating: not implemented fully (no central quest store). If RequiredQuestId != 0 we block with message.
            try
            {
                if (_cfg.RequiredQuestId > 0)
                {
                    // TODO: integrate with Quest subsystem when available. For now, refuse politely.
                    return (false, $"Come back once you've accomplished that task (Quest {_cfg.RequiredQuestId})!");
                }
            }
            catch { /* best-effort */ }

            // Min trade pool / inventory overweight immediate refusal
            try
            {
                if (_cfg.TradeCenter != 0 && (_state.CopperTradePool <= _cfg.MinTradePool || _state.InventoryWeight >= _cfg.InventoryMaxWeight))
                {
                    var destRoom = ResolveTradeCenterRoomName();
                    return (false, $"Sorry I'm travelling to {destRoom}, I should be back in a few days!");
                }
            }
            catch { /* best-effort */ }

            return (true, null);
        }

        // Resolve TradeCenter to room name for messages
        private string ResolveTradeCenterRoomName()
        {
            try
            {
                if (_cfg.TradeCenter <= 0 || _world == null) return "the trading center";
                var room = _world.Rooms.FirstOrDefault(r => r.RoomID == _cfg.TradeCenter);
                if (room != null) return room.Name ?? $"room {_cfg.TradeCenter}";
            }
            catch { }
            return "the trading center";
        }

        // Public entry: handle a trading-related command directed at this NPC.
        // Verbs supported (basic): "trade" (summary), "list" (items for sale), "buy <template>", "sell <item>", "convert <amount>", "pay <amount>", "confirm", "cancel"
        // This implementation returns friendly messages and updates TraderState where applicable.
        // Implements a payment confirmation & transactional transfer flow.
        public Message HandleCommand(Player player, string rawArgs, SqliteConnection? conn = null)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            try
            {
                if (!IsOpenNow(out var next)) return MessageHandler.System($"I'm not trading right now. Come back in {next}.");

                var ct = CanTradeWith(player);
                if (!ct.CanTrade) return MessageHandler.System(ct.Reason ?? "You cannot trade with me.");

                var args = (rawArgs ?? "").Trim();

                // When user opens trade (via 'trade <npc>'), the router sends args=="trade".
                // Open a short-lived trader session so subsequent short commands (list, buy, sell, confirm, cancel, quit)
                // can be issued without repeating the NPC name.
                if (string.IsNullOrWhiteSpace(args) || string.Equals(args, "trade", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Set a simple pending action to scope future commands to this trader until cancelled.
                        player.PendingAction = new PendingAction
                        {
                            Command = "TraderSession",
                            Step = "Active",
                            Data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["NpcId"] = _npc.NpcID,
                                ["NpcName"] = _npc.DisplayName ?? _npc.Name ?? _npc.TemplateID ?? "trader"
                            }
                        };

                        // Lock this NPC in-memory to prevent wandering/auto-moving while session is active.
                        try
                        {
                            _npc.IsTradeLocked = true;
                            // NEW: also add an owner-scoped interrupt so scheduler/executor suppress movement.
                            _npc.AddInterrupt(Npc.InterruptType.Trade, owner: player.PlayerID, note: "TraderSession", ttl: null);
                        }
                        catch { /* best-effort */ }
                    }
                    catch { /* best-effort; do not block showing summary */ }

                    return MessageHandler.System(BuildSummaryText(player));
                }

                var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var verb = parts[0].ToLowerInvariant();
                var payload = parts.Length > 1 ? parts[1].Trim() : "";

                switch (verb)
                {
                    case "list":
                        return MessageHandler.System(BuildInventoryListText());
                    case "buy":
                        if (!_cfg.Sell) return MessageHandler.System("I am not selling at this time.");
                        if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: buy <item name>");
                        return HandleBuy(player, payload, conn);
                    case "sell":
                        if (!_cfg.Buy) return MessageHandler.System("I am not buying at this time.");
                        if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: sell <item name>");
                        return HandleSell(player, payload, conn);
                    case "convert":
                        if (!_cfg.ConvertCurrency) return MessageHandler.System("I do not provide currency conversion services.");
                        if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: convert <amount> (e.g. convert 1g20s5c or convert 250c)");
                        return HandleConvertCurrency(player, payload, conn);
                    case "confirm":
                        return HandleConfirm(player, conn);
                    case "cancel":
                    case "quit":
                    case "end":
                        try
                        {
                            if (player.PendingAction != null && string.Equals(player.PendingAction.Command, "TraderBuy", StringComparison.OrdinalIgnoreCase))
                            {
                                HandlePlayerTerminationInternal(player, "cancel", conn);
                            }
                        }
                        catch { /* best-effort */ }
                        try { player.PendingAction = null; } catch { }
                        return MessageHandler.System("Trade session ended.");
                    default:
                        return MessageHandler.System("I don't understand that trade command. Try: list, buy <item>, sell <item> to <trader>, confirm, cancel, quit.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandleCommand error for NpcID={_npc?.NpcID}: {ex.Message}");
                return MessageHandler.System("Trader failed to process your request.");
            }
        }//End HandleCommand

        // Build brief summary of trader status
        private string BuildSummaryText(Player player)
        {
            var lines = new List<string>();
            lines.Add($"{_npc.DisplayName}: I trade in {(_cfg.ItemTypesAllowed?.Count > 0 ? string.Join(", ", _cfg.ItemTypesAllowed) : "various goods")}.");
            lines.Add($"I will {(_cfg.Buy ? "buy" : "not buy")} items and {(_cfg.Sell ? "sell" : "not sell")} items.");
            if (_cfg.DesiredItems != null && _cfg.DesiredItems.Count > 0)
                lines.Add($"Currently buying: {string.Join(", ", _cfg.DesiredItems)}");

            // Clear, user-focused command examples.
            // NOTE: 'confirm' autopay is intentionally removed from examples — use 'give <amount> to <npc>' to pay.
            lines.Add("Short commands while trading (you may use trader's name):");
            lines.Add(" - list                (shows goods on the trader)");
            lines.Add(" - buy <item>          (buy item use keyword)");
            lines.Add(" - sell <item> to <trader>");
            lines.Add(" - cancel | quit       (end the trade or cancel buy)");

            return string.Join("\n", lines);
        }

        // Build inventory listing with sell prices (what trader sells to player)
        private string BuildInventoryListText()
        {
            try
            {
                if (_npc.Inventory == null || _npc.Inventory.Count == 0) return "I have no goods to sell right now.";

                // Group identical items so the list shows aggregated quantities instead of repeated lines.
                // Group key: TemplateID if present, otherwise Name; this preserves correct grouping for stackables.
                var groups = _npc.Inventory
                    .Where(it => it != null)
                    .GroupBy(it => !string.IsNullOrWhiteSpace(it.TemplateID) ? it.TemplateID! : (it.Name ?? $"__id_{it.ItemID}"),
                             (key, items) => new
                             {
                                 Key = key,
                                 Representative = items.First(i => i != null),
                                 TotalQty = items.Sum(i => Math.Max(1, i.Quantity)),
                                 Price = ComputeSellPrice(items.First(i => i != null))
                             })
                    .OrderBy(g => (g.Representative?.Name ?? g.Key) ?? g.Key)
                    .ToList();

                var lines = new List<string> { "Goods for sale:" };
                foreach (var g in groups)
                {
                    var rep = g.Representative;
                    if (rep == null) continue;
                    var displayName = !string.IsNullOrWhiteSpace(rep.Name) ? rep.Name : "(unnamed item)";
                    var priceText = FormatCopper(g.Price);
                    lines.Add($"{displayName} - Price: {priceText} - Qty: {g.TotalQty}");
                }

                return string.Join("\n", lines);
            }
            catch { return "I have no goods to sell right now."; }
        }
                
        // Initiate a purchase: create a pending action awaiting payment or confirmation.
        private Message HandleBuy(Player player, string templateOrId, SqliteConnection? conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            try
            {
                // Parse optional quantity prefix: "4 fur", "4x fur", "all fur", default -> 1
                int requestedQty = 1;
                bool explicitQty = false;
                string expr = (templateOrId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(expr)) return MessageHandler.System("Usage: buy <item> or buy <n> <item> (or 'all <item>').");

                var toks = expr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length >= 2)
                {
                    // Numeric prefix "4"
                    if (int.TryParse(toks[0], out var n) && n > 0)
                    {
                        requestedQty = Math.Max(1, n);
                        explicitQty = true;
                        expr = toks[1].Trim();
                    }
                    else
                    {
                        // Support "4x" or "4X" prefix (e.g. "4x biscuit")
                        var qtok = toks[0].Trim();
                        if (qtok.EndsWith("x", StringComparison.OrdinalIgnoreCase))
                        {
                            var numPart = qtok.Substring(0, qtok.Length - 1);
                            if (int.TryParse(numPart, out var n2) && n2 > 0)
                            {
                                requestedQty = Math.Max(1, n2);
                                explicitQty = true;
                                expr = toks[1].Trim();
                            }
                        }
                        // "all <item>" handling falls through below
                    }
                }

                if (!explicitQty && toks.Length >= 1 && string.Equals(toks[0], "all", StringComparison.OrdinalIgnoreCase))
                {
                    // ambiguous "buy all" requires item name, so require second token
                    if (toks.Length < 2) return MessageHandler.System("Specify what to buy: buy all <item>");
                    requestedQty = -1;
                    explicitQty = true;
                    expr = toks[1].Trim();
                }

                // Use ItemParser to match by name/descriptor/material/template tokens against NPC inventory.
                var opts = new MudUtils.ItemParserOptions
                {
                    IncludeDescriptorTokens = true,
                    IncludeMaterialTokens = true,
                    IncludeBuried = false
                };

                // First attempt resolution
                var resolution = MudUtils.ItemParser.ResolveCandidates(_npc.Inventory ?? Enumerable.Empty<Item>(), player, expr, opts);

                // If no winner, try a simple singularization heuristic (e.g., "biscuits" -> "biscuit"), and re-resolve.
                if (resolution.Winner == null)
                {
                    try
                    {
                        var singular = expr;
                        if (expr.EndsWith("s", StringComparison.OrdinalIgnoreCase) && expr.Length > 1)
                            singular = expr.Substring(0, expr.Length - 1).Trim();

                        if (!string.Equals(singular, expr, StringComparison.OrdinalIgnoreCase))
                        {
                            var alt = MudUtils.ItemParser.ResolveCandidates(_npc.Inventory ?? Enumerable.Empty<Item>(), player, singular, opts);
                            if (alt?.Winner != null)
                                resolution = alt;
                        }
                    }
                    catch { /* best-effort */ }
                }

                if (resolution.Winner == null)
                {
                    // Return parser reason when available for better UX.
                    var reason = string.IsNullOrWhiteSpace(resolution.Reason) ? $"I don't have that item in stock." : resolution.Reason;
                    return MessageHandler.System(reason);
                }

                // Build candidate list from NPC inventory matching the resolved winner/template/name
                var winner = resolution.Winner;
                var candidates = (_npc.Inventory ?? Enumerable.Empty<Item>())
                    .Where(i => i != null && (
                        i.ItemID == winner.ItemID ||
                        (!string.IsNullOrEmpty(i.TemplateID) && !string.IsNullOrEmpty(winner.TemplateID) && string.Equals(i.TemplateID, winner.TemplateID, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(winner.Name) && string.Equals(i.Name, winner.Name, StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                if (candidates.Count == 0)
                    return MessageHandler.System("I don't have that item in stock.");

                // Combine available quantity across matching items
                int available = candidates.Sum(c => Math.Max(1, c.Quantity));

                if (explicitQty && requestedQty == -1)
                {
                    if (available == 0) return MessageHandler.System("There are none of that item to buy.");
                    requestedQty = available;
                }

                if (explicitQty && requestedQty > available)
                    return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                int toBuy = explicitQty ? requestedQty : 1;

                // Choose representative for pricing (prefer template)
                var representative = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.TemplateID)) ?? candidates.First();

                int perUnitPrice = ComputeSellPrice(representative);
                long totalPrice = perUnitPrice * (long)toBuy;

                // Check reservations: try to reserve all item rows required to satisfy toBuy.
                var reservedEntries = new List<(int ItemId, int TakeQty)>();
                int remaining = toBuy;

                // order by largest stacks first to minimize fragmentation
                foreach (var row in candidates.OrderByDescending(c => c.Quantity))
                {
                    if (remaining <= 0) break;
                    if (row == null) continue;

                    int availInRow = Math.Max(1, row.Quantity);
                    int take = Math.Min(remaining, availInRow);

                    // Try reserve this row
                    if (!ReserveItemForPlayer(row.ItemID, player.PlayerID))
                    {
                        // reservation collision: release previously made reservations and fail
                        foreach (var r in reservedEntries) ReleaseReservationForItem(r.ItemId);
                        return MessageHandler.System("Sorry — someone else is currently completing a purchase for that item. Try again shortly.");
                    }

                    reservedEntries.Add((row.ItemID, take));
                    remaining -= take;
                }

                if (remaining > 0)
                {
                    // Race condition: insufficient available after reservation attempts
                    foreach (var r in reservedEntries) ReleaseReservationForItem(r.ItemId);
                    return MessageHandler.System("Failed to reserve the requested quantity. Try again.");
                }

                // Build pending action data with precise line items (ItemId + Qty)
                var lineItems = new List<Dictionary<string, object>>();
                foreach (var re in reservedEntries)
                {
                    var row = candidates.FirstOrDefault(c => c.ItemID == re.ItemId);
                    var li = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ItemId"] = re.ItemId,
                        ["Qty"] = re.TakeQty,
                        ["TemplateId"] = row?.TemplateID ?? "",
                        ["Name"] = row?.Name ?? row?.TemplateID ?? "item"
                    };
                    lineItems.Add(li);
                }

                var pending = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NpcId"] = _npc.NpcID,
                    ["TemplateId"] = representative.TemplateID ?? "",
                    ["ItemName"] = representative.Name ?? representative.TemplateID ?? "item",
                    ["LineItems"] = lineItems,
                    ["Price"] = (long)totalPrice,
                    ["Paid"] = 0L
                };

                // Set player's pending action
                player.PendingAction = new PendingAction
                {
                    Command = "TraderBuy",
                    Step = "AwaitPayment",
                    Data = pending
                };

                // Ensure NPC remains locked for trade while awaiting payment
                try { _npc.IsTradeLocked = true; } catch { }

                // Prompt: show cost and instruct exact 'give' syntax to complete the purchase
                string BuildGiveExample(long copperAmt)
                {
                    var c = Currency.FromCopper(copperAmt);
                    var parts = new List<string>();
                    if (c.Gold > 0) parts.Add($"{c.Gold} gold");
                    if (c.Silver > 0) parts.Add($"{c.Silver} silver");
                    if (c.Copper > 0) parts.Add($"{c.Copper} copper");
                    if (parts.Count == 0) parts.Add("0 copper");
                    return string.Join(" ", parts);
                }

                var example = BuildGiveExample(totalPrice);
                var npcToken = (_npc.DisplayName ?? _npc.Name ?? "trader").ToLowerInvariant();

                var qtyText = toBuy > 1 ? $"{toBuy} x " : "";
                var itemToken = representative.Name ?? representative.TemplateID ?? "item";
                var msg = $"{_npc.DisplayName}: I have reserved {qtyText}{itemToken} for you. Total cost is {FormatCopper(totalPrice)}.\n" +
                          $"To complete the purchase type: give {example} to {npcToken}\n" +
                          $"Or type 'cancel' to abort the purchase.";
                return MessageHandler.System(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandleBuy error: {ex.Message}");
                return MessageHandler.System("Failed to initiate purchase.");
            }
        }//End HandleBuy

        // Process a 'pay <amount>' from the player targeted at this trader while a pending purchase exists.
        public Message HandlePaymentAttempt(Player player, string amountExpr, SqliteConnection? conn)
        {
            try
            {
                var pa = player.PendingAction;
                if (pa == null || !string.Equals(pa.Command, "TraderBuy", StringComparison.OrdinalIgnoreCase) || pa.Data == null)
                    return MessageHandler.System("You have nothing pending to pay for.");

                if (!(pa.Data is Dictionary<string, object> dict))
                    return MessageHandler.System("Internal error: pending payment data invalid.");

                // Validate this pending is for this trader
                if (!dict.TryGetValue("NpcId", out var npcObj) || Convert.ToInt32(npcObj) != _npc.NpcID)
                    return MessageHandler.System("That purchase is not for this trader.");

                // Parse amount
                long copper = ParseCurrencyExpressionToCopper(amountExpr);
                if (copper <= 0) return MessageHandler.System("Invalid payment amount.");

                // Attempt to take coins from player and persist removal & possible item movement transactionally.
                long price = Convert.ToInt64(dict["Price"]);
                long paidSoFar = Convert.ToInt64(dict["Paid"]);

                // Run writer transaction to remove coins from player inventory and possibly finalize purchase if fully paid.
                long amountPaidNow = 0;

                try
                {
                    // Debug: log player's copper on-hand before removal
                    try
                    {
                        var before = ComputePlayerCopperOnHand(player);
                        Console.WriteLine($"[ItemTrader][PAY] Player '{player?.Name}' attempting to pay {copper}c. On-hand before: {before}c");
                    }
                    catch { }

                    // Use writer queue to ensure DB consistency when modifying item rows for coins and moving purchased item.
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        // 1) Remove coins from player's inventory in writer tx; compute how much actually removed (in copper).
                        amountPaidNow = RemoveCoinsFromPlayerInTx(player, copper, wConn, wTx);

                        // Update in-memory player inventory quantities already modified by helper.

                        // 2) Update Paid so far in pending (in-memory)
                        paidSoFar += amountPaidNow;
                        dict["Paid"] = paidSoFar;

                        // 3) If fully paid, commit purchase: move item, persist changes, add change if needed, update trader state.
                        if (paidSoFar >= price)
                        {
                            long change = paidSoFar - price;
                            // Transfer item from NPC to player and handle change creation
                            CommitPurchaseFinalizeInTx(player, dict, price, amountPaidNow, change, wConn, wTx);
                            // clear pending (will be done after writer)
                        }
                        else
                        {
                            // Partial payment: update player's pending data only; update trader pool by adding amountPaidNow.
                            _state.CopperTradePool += amountPaidNow;
                            // Persist trader flags (update Npc.Flags) inside tx
                            try
                            {
                                _npc.Flags = _npc.Flags ?? new List<string>();
                                var json = JsonSerializer.Serialize(_state, _jsonOpts);
                                _npc.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                                _npc.Flags.Add($"{TraderStateFlagPrefix}{json}");
                                _npc.FlagsJson = JsonSerializer.Serialize(_npc.Flags);
                                using var cmd = wConn.CreateCommand();
                                if (wTx != null) cmd.Transaction = wTx;
                                cmd.CommandText = "UPDATE Npc SET Flags = @flags WHERE NpcID = @id;";
                                cmd.Parameters.AddWithValue("@flags", _npc.FlagsJson ?? "[]");
                                cmd.Parameters.AddWithValue("@id", _npc.NpcID);
                                cmd.ExecuteNonQuery();
                            }
                            catch { /* best-effort */ }
                        }

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ItemTrader] Payment writer failed: {ex.Message}");
                    return MessageHandler.System("Payment processing failed due to server error.");
                }

                // Debug: log amount actually removed and player's on-hand after
                try
                {
                    var after = ComputePlayerCopperOnHand(player);
                    Console.WriteLine($"[ItemTrader][PAY] Player '{player?.Name}' amountPaidNow={amountPaidNow}c. On-hand after: {after}c");
                }
                catch { }

                // Build response depending on outcome
                if (dict.ContainsKey("Paid") && Convert.ToInt64(dict["Paid"]) < Convert.ToInt64(dict["Price"]))
                {
                    long newPaid = Convert.ToInt64(dict["Paid"]);
                    long stillOwe = Convert.ToInt64(dict["Price"]) - newPaid;
                    string resultMsg = $"You paid {FormatCopper(amountPaidNow)}. You still owe {FormatCopper(stillOwe)}.";

                    // Return single system message (do NOT also SendSystem) to avoid duplicates.
                    return MessageHandler.System($"{_npc.DisplayName}: {resultMsg}");
                }
                else
                {
                    // fully paid path: the CommitPurchaseFinalizeInTx already moved items and gave change; clear pending
                    try { player.PendingAction = null; } catch { }

                    // Return a single, authoritative NPC-styled thank-you message (avoid duplicate messages)
                    return MessageHandler.System($"{_npc.DisplayName}: Thank you — that will do!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandlePaymentAttempt error: {ex.Message}");
                return MessageHandler.System("Payment attempt failed.");
            }
        }//End HandlePaymentAttempt

        // Confirm command: attempt to pay automatically using carried coins to cover remaining cost.
        public Message HandleConfirm(Player player, SqliteConnection? conn)
        {
            // Autopay via 'confirm' has been deprecated in favor of explicit 'give <amount> to <npc>' usage.
            // Inform the player and point them to the proper syntax for the active pending purchase.
            try
            {
                var pa = player.PendingAction;
                if (pa == null || !string.Equals(pa.Command, "TraderBuy", StringComparison.OrdinalIgnoreCase) || pa.Data == null)
                    return MessageHandler.System("You have nothing pending to confirm. Use 'buy <item>' to begin.");

                if (!(pa.Data is Dictionary<string, object> dict))
                    return MessageHandler.System("Internal error: pending confirmation data invalid.");

                if (!dict.TryGetValue("Price", out var priceObj) || !long.TryParse(priceObj.ToString(), out var priceCopper))
                    return MessageHandler.System("Pending purchase data missing price information.");

                var exampleParts = new List<string>();
                var c = Currency.FromCopper(priceCopper);
                if (c.Gold > 0) exampleParts.Add($"{c.Gold} gold");
                if (c.Silver > 0) exampleParts.Add($"{c.Silver} silver");
                if (c.Copper > 0) exampleParts.Add($"{c.Copper} copper");
                if (exampleParts.Count == 0) exampleParts.Add("0 copper");
                var example = string.Join(" ", exampleParts);

                var npcToken = (_npc.DisplayName ?? _npc.Name ?? "trader").ToLowerInvariant();
                return MessageHandler.System($"Autopay via 'confirm' is disabled. To complete the purchase give {example} to {npcToken}, or type 'cancel' to abort.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandleConfirm error: {ex.Message}");
                return MessageHandler.System("Confirm is not available for payments. Use 'give <amount> to <npc>' instead.");
            }
        }

        // Public helper to be called by external systems when player leaves room or enters combat.
        // reason: "leave" (moved away), "combat", "cancel", "quit"
        public void HandlePlayerTermination(Player player, string reason, SqliteConnection? conn = null)
        {
            try
            {
                if (string.Equals(reason, "leave", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePlayerTerminationInternal(player, "leave", conn);
                }
                else if (string.Equals(reason, "combat", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePlayerTerminationInternal(player, "combat", conn);
                }
                else
                {
                    HandlePlayerTerminationInternal(player, reason, conn);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandlePlayerTermination error: {ex.Message}");
            }
        }

        // INTERNAL: centralised termination handler that refunds partial payments, clears pending, updates state and notifies.
        // Must be robust and best-effort; uses writer queue if available to perform DB writes atomically.
        private void HandlePlayerTerminationInternal(Player player, string reason, SqliteConnection? conn)
        {
            if (player == null) return;
            var pa = player.PendingAction;
            if (pa == null) return;
            if (!string.Equals(pa.Command, "TraderBuy", StringComparison.OrdinalIgnoreCase)) return;
            if (!(pa.Data is Dictionary<string, object> dict)) { player.PendingAction = null; return; }

            // NEW: remove Trade interrupt for this owning player so NPC can resume movement
            try
            {
                try { _npc.RemoveInterrupt(Npc.InterruptType.Trade, owner: player.PlayerID); } catch { }
                _npc.IsTradeLocked = false;
            }
            catch { /* best-effort */ }

            long paid = 0;
            try { paid = dict.ContainsKey("Paid") ? Convert.ToInt64(dict["Paid"]) : 0L; } catch { paid = 0; }

            // Clear pending early to avoid re-entrancy; we'll refund below
            player.PendingAction = null;

            // Release any reservations held by this player (so item becomes available immediately)
            try { ReleaseReservationsForPlayer(player.PlayerID); } catch { /* best-effort */ }

            // If nothing paid, just notify
            if (paid <= 0)
            {
                try
                {
                    if (string.Equals(reason, "leave", StringComparison.OrdinalIgnoreCase))
                    {
                        var yell = $"{_npc.DisplayName} yells: \"Ok, see you another time then!\"";
                        foreach (var p in _npc.CurrentRoom?.Players ?? new List<Player>()) MessageHandler.SendSystem(p, yell);
                    }
                    else
                    {
                        MessageHandler.SendSystem(player, $"{_npc.DisplayName}: Ok, maybe some other time!");
                    }
                }
                catch { }
                return;
            }

            // Refund paid amount transactionally and update trader pool
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    try
                    {
                        // 1) subtract paid from trader pool (but not below zero)
                        _state.CopperTradePool = Math.Max(0, _state.CopperTradePool - paid);

                        // 2) persist TraderState into Npc.Flags inside tx
                        try
                        {
                            _npc.Flags = _npc.Flags ?? new List<string>();
                            var json = JsonSerializer.Serialize(_state, _jsonOpts);
                            _npc.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                            _npc.Flags.Add($"{TraderStateFlagPrefix}{json}");
                            _npc.FlagsJson = JsonSerializer.Serialize(_npc.Flags);

                            using var cmd = wConn.CreateCommand();
                            if (wTx != null) cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Npc SET Flags = @flags WHERE NpcID = @id;";
                            cmd.Parameters.AddWithValue("@flags", _npc.FlagsJson ?? "[]");
                            cmd.Parameters.AddWithValue("@id", _npc.NpcID);
                            cmd.ExecuteNonQuery();
                        }
                        catch (Exception exf) { Console.WriteLine($"[ItemTrader] Failed to persist TraderState during refund: {exf.Message}"); }

                        // 3) refund coins to player by creating/merging coin stacks
                        RefundPaidToPlayerInTx(player, paid, wConn, wTx);

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ItemTrader] Refund writer tx error: {ex.Message}");
                        throw;
                    }

                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(8));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] Refund enqueue failed: {ex.Message}");
                if (conn != null)
                {
                    try
                    {
                        using var tx = conn.BeginTransaction();
                        try
                        {
                            _state.CopperTradePool = Math.Max(0, _state.CopperTradePool - paid);

                            _npc.Flags = _npc.Flags ?? new List<string>();
                            var json = JsonSerializer.Serialize(_state, _jsonOpts);
                            _npc.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                            _npc.Flags.Add($"{TraderStateFlagPrefix}{json}");
                            _npc.FlagsJson = JsonSerializer.Serialize(_npc.Flags);

                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Npc SET Flags = @flags WHERE NpcID = @id;";
                            cmd.Parameters.AddWithValue("@flags", _npc.FlagsJson ?? "[]");
                            cmd.Parameters.AddWithValue("@id", _npc.NpcID);
                            cmd.ExecuteNonQuery();

                            RefundPaidToPlayerInTx(player, paid, conn, tx);

                            tx.Commit();
                        }
                        catch
                        {
                            try { tx.Rollback(); } catch { }
                        }
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[ItemTrader] Fallback refund failed: {ex2.Message}");
                    }
                }
            }

            // Notify player and/or room about termination and refund
            try
            {
                var refundText = $"Your payment of {FormatCopper(paid)} has been returned.";
                if (string.Equals(reason, "leave", StringComparison.OrdinalIgnoreCase))
                {
                    var yell = $"{_npc.DisplayName} yells: \"Ok, see you another time then!\"";
                    foreach (var p in _npc.CurrentRoom?.Players ?? new List<Player>()) MessageHandler.SendSystem(p, yell);
                    MessageHandler.SendSystem(player, refundText);
                }
                else if (string.Equals(reason, "combat", StringComparison.OrdinalIgnoreCase))
                {
                    MessageHandler.SendSystem(player, $"{_npc.DisplayName}: Ok, maybe some other time!");
                    MessageHandler.SendSystem(player, refundText);
                }
                else
                {
                    MessageHandler.SendSystem(player, $"{_npc.DisplayName}: Ok, maybe some other time!");
                    MessageHandler.SendSystem(player, refundText);
                }
            }
            catch { /* best-effort */ }
        }//End HandlePlayerTerminationInternal

        // Refund helper: creates/merges coin stacks back into player's inventory inside provided writer tx.
        // Also persists item rows. This method is safe to call inside a transaction.
        private void RefundPaidToPlayerInTx(Player player, long paidCopper, SqliteConnection wConn, SqliteTransaction? wTx)
        {
            if (player == null || paidCopper <= 0) return;
            try
            {
                var cur = Currency.FromCopper(paidCopper);

                // Helper to add units to existing stack or create new item if not found.
                void AddCoins(string templateId, int units)
                {
                    if (units <= 0) return;
                    try
                    {
                        // Look for existing coin stack in player's in-memory inventory
                        var existing = player.Inventory?.FirstOrDefault(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Quantity = Math.Max(0, existing.Quantity + units);
                            ItemPersistence.SaveItem(wConn, existing, wTx);
                        }
                        else
                        {
                            // Create new coin item owned by player
                            var it = _world.ItemFactory.CreateItem(templateId, "Player", player.DbPlayerID, 0, units);
                            ItemPersistence.SaveItem(wConn, it, wTx);
                            player.Inventory.Add(it);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ItemTrader] Failed to add coin stack {templateId}: {ex.Message}");
                    }
                }

                AddCoins("coin_gold", cur.Gold);
                AddCoins("coin_silver", cur.Silver);
                AddCoins("coin_copper", cur.Copper);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] RefundPaidToPlayerInTx error: {ex.Message}");
            }
        }

        // Finalize purchase inside writer tx: move item to player, create change if necessary, and update trader pool/state.
        private void CommitPurchaseFinalizeInTx(Player player, Dictionary<string, object> pending, long priceCopper, long paidNow, long changeCopper, SqliteConnection wConn, SqliteTransaction? wTx)
        {
            if (player == null || pending == null) return;

            try
            {
                // Increase trader pool by price (merchant retains sale price)
                _state.CopperTradePool += priceCopper;

                // Capture room reference for messaging
                var room = player.CurrentRoom;

                // Prepare list of line items: support new "LineItems" format or legacy single "ItemId"
                List<(int ItemId, int Qty)> lines = new List<(int, int)>();

                if (pending.TryGetValue("LineItems", out var liObj) && liObj is System.Collections.IEnumerable liEnum)
                {
                    try
                    {
                        foreach (var o in liEnum)
                        {
                            if (o is Dictionary<string, object> dct)
                            {
                                int iid = Convert.ToInt32(dct["ItemId"]);
                                int q = Convert.ToInt32(dct["Qty"]);
                                lines.Add((iid, Math.Max(1, q)));
                            }
                            else if (o is System.Text.Json.JsonElement je && je.ValueKind == JsonValueKind.Object)
                            {
                                try
                                {
                                    int iid = 0; int q = 1;
                                    if (je.TryGetProperty("ItemId", out var jid) && jid.TryGetInt32(out var ji)) iid = ji;
                                    if (je.TryGetProperty("Qty", out var jq) && jq.TryGetInt32(out var jqv)) q = jqv;
                                    if (iid > 0) lines.Add((iid, Math.Max(1, q)));
                                }
                                catch { }
                            }
                        }
                    }
                    catch { /* best-effort parse */ }
                }
                else if (pending.ContainsKey("ItemId"))
                {
                    try
                    {
                        int iid = Convert.ToInt32(pending["ItemId"]);
                        int q = 1;
                        if (pending.ContainsKey("Qty")) q = Convert.ToInt32(pending["Qty"]);
                        lines.Add((iid, Math.Max(1, q)));
                    }
                    catch { /* best-effort */ }
                }

                if (lines.Count == 0)
                {
                    Console.WriteLine($"[ItemTrader] No line items found in pending for NpcID={_npc.NpcID}");
                }

                var transferredSummaries = new List<string>();

                foreach (var (itemId, takeQty) in lines)
                {
                    if (takeQty <= 0) continue;

                    var itemRow = _npc.Inventory?.FirstOrDefault(i => i != null && i.ItemID == itemId);
                    if (itemRow == null)
                    {
                        Console.WriteLine($"[ItemTrader] Candidate item missing for NpcID={_npc.NpcID} ItemID={itemId}");
                        RefundPaidToPlayerInTx(player, paidNow, wConn, wTx);
                        try { ReleaseReservationForItem(itemId); } catch { }
                        try
                        {
                            MessageHandler.SendSystem(player, $"Sorry — some items were no longer available. Your payment of {FormatCopper(paidNow)} has been refunded.");
                            foreach (var p in _npc.CurrentRoom?.Players ?? new List<Player>()) if (p != player) try { MessageHandler.SendSystem(p, $"{_npc.DisplayName}: Sorry, some items were just sold to someone else."); } catch { }
                        }
                        catch { }
                        return;
                    }

                    try
                    {
                        int availableInRow = Math.Max(1, itemRow.Quantity);

                        if (takeQty < availableInRow)
                        {
                            // Partial transfer: reduce NPC stack and give player 'takeQty'
                            itemRow.Quantity = availableInRow - takeQty;
                            ItemPersistence.SaveItem(wConn, itemRow, wTx);

                            // Attempt to merge into existing player stack first (transactional)
                            bool mergedIntoPlayer = false;
                            if (!string.IsNullOrWhiteSpace(itemRow.TemplateID))
                            {
                                try
                                {
                                    var merged = InventoryManager.TryMergeIntoExistingStack(
                                        wConn, wTx,
                                        "Player", player.DbPlayerID,
                                        player.CurrentRoom?.RoomID ?? 0,
                                        itemRow.TemplateID ?? "",
                                        itemRow.FlagsJson ?? "[]",
                                        takeQty);

                                    if (merged.mergedId != 0)
                                    {
                                        // Update in-memory player stack quantity
                                        var mem = player.Inventory?.FirstOrDefault(i => i != null && i.ItemID == merged.mergedId);
                                        if (mem != null) mem.Quantity = merged.newQty;
                                        mergedIntoPlayer = true;
                                    }
                                }
                                catch { mergedIntoPlayer = false; }
                            }

                            if (!mergedIntoPlayer)
                            {
                                // Create new player-owned item row
                                var newItem = _world.ItemFactory.CreateItem(itemRow.TemplateID ?? itemRow.Name ?? "", "Player", player.DbPlayerID, player.CurrentRoom?.RoomID ?? 0, takeQty);
                                newItem.Name = itemRow.Name;
                                newItem.ItemCategory = itemRow.ItemCategory;
                                ItemPersistence.SaveItem(wConn, newItem, wTx);
                                player.Inventory.Add(newItem);
                            }

                            transferredSummaries.Add($"{takeQty} {(itemRow.Name ?? itemRow.TemplateID)}");

                            try { ReleaseReservationForItem(itemRow.ItemID); } catch { }
                        }
                        else
                        {
                            // Full-row transfer: prefer merging into existing player stack if possible
                            bool mergedIntoPlayer = false;
                            if (!string.IsNullOrWhiteSpace(itemRow.TemplateID))
                            {
                                try
                                {
                                    var merged = InventoryManager.TryMergeIntoExistingStack(
                                        wConn, wTx,
                                        "Player", player.DbPlayerID,
                                        player.CurrentRoom?.RoomID ?? 0,
                                        itemRow.TemplateID ?? "",
                                        itemRow.FlagsJson ?? "[]",
                                        availableInRow);

                                    if (merged.mergedId != 0)
                                    {
                                        // merged into existing player stack - remove original NPC row
                                        try
                                        {
                                            if (itemRow.ItemID > 0) MudPersistence.ItemPersistence.DeleteItem(wConn, itemRow.ItemID, wTx);
                                        }
                                        catch
                                        {
                                            try
                                            {
                                                using var cmd = wConn.CreateCommand();
                                                if (wTx != null) cmd.Transaction = wTx;
                                                cmd.CommandText = "DELETE FROM Item WHERE ItemID = @id;";
                                                cmd.Parameters.AddWithValue("@id", itemRow.ItemID);
                                                cmd.ExecuteNonQuery();
                                            }
                                            catch { }
                                        }

                                        // Update in-memory: remove from NPC and update player's merged row qty
                                        try
                                        {
                                            _npc.Inventory?.RemoveAll(i => i != null && i.ItemID == itemRow.ItemID);
                                            var mem = player.Inventory?.FirstOrDefault(i => i != null && i.ItemID == merged.mergedId);
                                            if (mem != null) mem.Quantity = merged.newQty;
                                        }
                                        catch { /* best-effort */ }

                                        transferredSummaries.Add($"{availableInRow} {(itemRow.Name ?? itemRow.TemplateID)}");
                                        mergedIntoPlayer = true;
                                        try { ReleaseReservationForItem(itemRow.ItemID); } catch { }
                                        // done with this line item
                                    }
                                }
                                catch { mergedIntoPlayer = false; }
                            }

                            if (!mergedIntoPlayer)
                            {
                                // No existing player stack: move ownership of the whole row to player
                                itemRow.OwnerType = "Player";
                                itemRow.OwnerID = player.DbPlayerID;
                                itemRow.RoomID = player.CurrentRoom?.RoomID ?? 0;
                                ItemPersistence.SaveItem(wConn, itemRow, wTx);

                                try { _npc.Inventory.RemoveAll(i => i != null && i.ItemID == itemRow.ItemID); } catch { }
                                player.Inventory.Add(itemRow);

                                transferredSummaries.Add($"{availableInRow} {(itemRow.Name ?? itemRow.TemplateID)}");

                                try { ReleaseReservationForItem(itemRow.ItemID); } catch { }
                            }
                        }

                        try { _npc.IsTradeLocked = false; } catch { }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ItemTrader] Failed to transfer item in tx: {ex.Message}");
                        try { ReleaseReservationForItem(itemId); } catch { }
                        throw;
                    }
                }

                if (changeCopper > 0)
                {
                    RefundPaidToPlayerInTx(player, changeCopper, wConn, wTx);
                    try { MessageHandler.SendSystem(player, $"You get {FormatCopper(changeCopper)} in change."); } catch { }
                    try { if (room != null) foreach (var p in room.Players.Where(p => p != player)) try { MessageHandler.SendSystem(p, $"{_npc.DisplayName} returns change to {player.Name} ({FormatCopper(changeCopper)})."); } catch { } } catch { }
                }

                try
                {
                    _npc.Flags = _npc.Flags ?? new List<string>();
                    var json = JsonSerializer.Serialize(_state, _jsonOpts);
                    _npc.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                    _npc.Flags.Add($"{TraderStateFlagPrefix}{json}");
                    _npc.FlagsJson = JsonSerializer.Serialize(_npc.Flags);

                    using var cmd = wConn.CreateCommand();
                    if (wTx != null) cmd.Transaction = wTx;
                    cmd.CommandText = "UPDATE Npc SET Flags = @flags WHERE NpcID = @id;";
                    cmd.Parameters.AddWithValue("@flags", _npc.FlagsJson ?? "[]");
                    cmd.Parameters.AddWithValue("@id", _npc.NpcID);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ItemTrader] Failed to persist TraderState inside tx: {ex.Message}");
                }

                try
                {
                    if (transferredSummaries.Count > 0)
                    {
                        var display = string.Join(", ", transferredSummaries);
                        MessageHandler.SendSystem(player, $"{_npc.DisplayName} gives you {display}.");
                        if (room != null)
                        {
                            foreach (var p in room.Players.Where(p => p != player))
                            {
                                try { MessageHandler.SendSystem(p, $"{_npc.DisplayName} gives {player.Name} {display}."); } catch { }
                            }
                        }
                    }
                }
                catch { /* best-effort */ }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] CommitPurchaseFinalizeInTx error: {ex.Message}");
                throw;
            }
            finally
            {
                // Clear player's pending action now that purchase completed
                try { player.PendingAction = null; } catch { }

                // NEW: remove Trade interrupt when transaction finalizes
                try { _npc.RemoveInterrupt(Npc.InterruptType.Trade, owner: player.PlayerID); } catch { }
                try { _npc.IsTradeLocked = false; } catch { }
            }
        }//end CommitPurchaseFinalizeInTx

        // Remove up-to 'copperNeeded' copper from player's coin stacks inside provided writer tx.
        // Returns actual copper removed. Updates player's in-memory inventory quantities accordingly and persists changes.
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
                            ItemPersistence.SaveItem(wConn, it, wTx);
                        }
                        else
                        {
                            // Delete item row
                            try
                            {
                                ItemPersistence.DeleteItem(wConn, it.ItemID);
                            }
                            catch
                            {
                                // fallback: attempt update to quantity 0 (best-effort)
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
                        Console.WriteLine($"[ItemTrader] Failed to persist coin removal for ItemID={it.ItemID}: {ex.Message}");
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
                Console.WriteLine($"[ItemTrader][WARN] Removed more copper ({removed}) than requested ({copperNeeded}) for player '{player?.Name}'.");
            }

            return removed;
        }//End RemoveCoinsFromPlayerInTx

        // Handler stubs: these currently compute prices and validate flows, but do not perform DB item/currency transfers for selling.
        // They update trader's in-memory state (counts/pool/weight) and persist the TraderState into npc.Flags.
        // Selling to trader (player sells) still uses a simplified path for now.
        private Message HandleSell(Player player, string itemExpr, SqliteConnection? conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            try
            {
                // Parse optional quantity prefix: "4 fur", "all fur", default -> 1
                int requestedQty = 1;
                bool explicitQty = false;
                string expr = (itemExpr ?? "").Trim();
                if (string.IsNullOrWhiteSpace(expr)) return MessageHandler.System("Usage: sell <item> or sell <n> <item> (or 'all <item>').");

                var toks = expr.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length >= 2 && int.TryParse(toks[0], out var n) && n > 0)
                {
                    requestedQty = Math.Max(1, n);
                    explicitQty = true;
                    expr = toks[1].Trim();
                }
                else if (toks.Length >= 2 && (string.Equals(toks[0], "all", StringComparison.OrdinalIgnoreCase) || string.Equals(toks[0], "everything", StringComparison.OrdinalIgnoreCase)))
                {
                    requestedQty = -1; // denote 'all'
                    explicitQty = true;
                    expr = toks[1].Trim();
                }
                else if (string.Equals(toks[0], "all", StringComparison.OrdinalIgnoreCase))
                {
                    // "sell all" ambiguous -> require an item
                    return MessageHandler.System("Specify what to sell: sell all <item>");
                }

                // Use ItemParser to resolve carried items (supports descriptor/material)
                var opts = new MudUtils.ItemParserOptions
                {
                    IncludeDescriptorTokens = true,
                    IncludeMaterialTokens = true,
                    AllowStartsWith = true
                };

                var parse = MudUtils.ItemParser.ResolveForInventoryCommand(player, expr, opts);

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
                        .Where(i => i != null)
                        .ToList();
                }
                else
                {
                    // Use the centralized matching helper so all commands share identical inventory resolution.
                    candidates = InventoryManager.FindNonEquippedMatches(player, expr);
                }

                if (candidates == null || candidates.Count == 0)
                    return MessageHandler.System("You are not carrying that item.");

                // Combine available quantity across matching carried items (treat stackable and multiple singletons the same)
                int available = candidates.Sum(c => Math.Max(1, c.Quantity));

                if (explicitQty && requestedQty == -1)
                {
                    if (available == 0) return MessageHandler.System("You have none of that item to sell.");
                    requestedQty = available;
                }

                if (explicitQty && requestedQty > available)
                    return MessageHandler.System($"Only {available} available. Enter a number up to {available}.");

                int toMove = explicitQty ? requestedQty : 1;

                // Choose a representative template for pricing calculation (prefer template match)
                var representative = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.TemplateID)) ?? candidates.First();

                if (!AcceptsItem(representative, out var rejectReason))
                {
                    return MessageHandler.System(rejectReason ?? "I cannot buy that.");
                }

                // Check saturation per-template (limit applies per unit)
                if (_cfg.ItemSaturationLimit > 0 && !string.IsNullOrWhiteSpace(representative.TemplateID))
                {
                    _state.CountsByTemplate.TryGetValue(representative.TemplateID, out var haveCount);
                    if (haveCount >= _cfg.ItemSaturationLimit)
                    {
                        return MessageHandler.System($"Sorry I don't need more of that right now. I already have {haveCount}.");
                    }
                }

                // Compute per-unit buy price (note: dynamic pricing could be refined per-unit)
                int perUnitPay = ComputeBuyPrice(representative);
                long totalPay = perUnitPay * (long)toMove;

                // Affordability check
                if (_state.CopperTradePool < totalPay)
                {
                    return MessageHandler.System("Sorry, I don't have enough funds to buy that right now.");
                }

                // Execute transactional move/pay for requested quantity
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        int remaining = toMove;
                        // iterate candidate rows ordering larger stacks first to minimize DB ops
                        foreach (var row in candidates.OrderByDescending(c => c.Quantity).ToList())
                        {
                            if (remaining <= 0) break;
                            if (row == null) continue;

                            int availableInRow = Math.Max(1, row.Quantity);
                            int take = Math.Min(remaining, availableInRow);

                            // Move 'take' units from player's row to NPC (handle partial stack)
                            if (take < availableInRow)
                            {
                                // reduce player's stack
                                int newQty = availableInRow - take;
                                row.Quantity = newQty;
                                ItemPersistence.SaveItem(wConn, row, wTx);

                                // try to merge into existing NPC stack
                                Item? existingNpcStack = _npc.Inventory?.FirstOrDefault(i =>
                                    i != null
                                    && !string.IsNullOrWhiteSpace(i.TemplateID)
                                    && string.Equals(i.TemplateID, row.TemplateID ?? "", StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(i.FlagsJson ?? "[]", row.FlagsJson ?? "[]", StringComparison.OrdinalIgnoreCase));

                                if (existingNpcStack != null)
                                {
                                    existingNpcStack.Quantity = Math.Max(1, existingNpcStack.Quantity + take);
                                    ItemPersistence.SaveItem(wConn, existingNpcStack, wTx);
                                }
                                else
                                {
                                    var created = _world.ItemFactory.CreateItem(row.TemplateID ?? row.Name ?? "", "NPC", _npc.NpcID, 0, take);
                                    created.Name = row.Name;
                                    created.ItemCategory = row.ItemCategory;
                                    ItemPersistence.SaveItem(wConn, created, wTx);
                                    _npc.Inventory.Add(created);
                                }
                            }
                            else
                            {
                                // move full item row ownership to NPC
                                row.OwnerType = "NPC";
                                row.OwnerID = _npc.NpcID;
                                row.RoomID = 0;
                                ItemPersistence.SaveItem(wConn, row, wTx);

                                // update in-memory lists
                                try { player.Inventory.RemoveAll(i => i != null && i.ItemID == row.ItemID); } catch { }
                                _npc.Inventory.Add(row);
                            }

                            remaining -= take;
                        }

                        // Deduct totalPay from trader pool and persist TraderState into Npc.Flags inside tx
                        _state.CopperTradePool = Math.Max(0, _state.CopperTradePool - totalPay);

                        try
                        {
                            _npc.Flags = _npc.Flags ?? new List<string>();
                            var json = JsonSerializer.Serialize(_state, _jsonOpts);
                            _npc.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith(TraderStateFlagPrefix, StringComparison.OrdinalIgnoreCase));
                            _npc.Flags.Add($"{TraderStateFlagPrefix}{json}");
                            _npc.FlagsJson = JsonSerializer.Serialize(_npc.Flags);

                            using var cmd = wConn.CreateCommand();
                            if (wTx != null) cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Npc SET Flags = @flags WHERE NpcID = @id;";
                            cmd.Parameters.AddWithValue("@flags", _npc.FlagsJson ?? "[]");
                            cmd.Parameters.AddWithValue("@id", _npc.NpcID);
                            cmd.ExecuteNonQuery();
                        }
                        catch (Exception exF) { Console.WriteLine($"[ItemTrader] Failed to persist TraderState during sell: {exF.Message}"); }

                        // Pay the player in the same tx
                        PayPlayerInTx(player, totalPay, wConn, wTx);

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(12));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ItemTrader] Sell transaction failed: {ex.Message}");
                    return MessageHandler.System("Sale failed due to server error.");
                }

                // Update runtime trader state (inventory weight and counts) in-memory and persist best-effort
                try
                {
                    double addedWeight = (representative.Weight * Math.Max(1, toMove));
                    _state.InventoryWeight += addedWeight;

                    if (!string.IsNullOrWhiteSpace(representative.TemplateID))
                    {
                        if (!_state.CountsByTemplate.ContainsKey(representative.TemplateID)) _state.CountsByTemplate[representative.TemplateID] = 0;
                        _state.CountsByTemplate[representative.TemplateID] += toMove;
                    }

                    PersistState(conn);
                }
                catch { /* best-effort */ }

                // UX: notify player and room (show per-unit price and total)
                try
                {
                    string itemDisplay = representative.Name ?? representative.TemplateID ?? "item";
                    string perUnitText = FormatCopper(perUnitPay);
                    string totalText = FormatCopper(totalPay);

                    MessageHandler.SendSystem(player, $"{_npc.DisplayName}: Thank you — I give you {totalText} ({perUnitText} x {toMove}) for your {itemDisplay}.");
                    foreach (var p in _npc.CurrentRoom?.Players ?? new List<Player>())
                    {
                        if (p == player) continue;
                        try { MessageHandler.SendSystem(p, $"{_npc.DisplayName} takes {itemDisplay} from {player.Name} and pays {totalText}."); } catch { }
                    }
                }
                catch { /* best-effort */ }

                return MessageHandler.System($"You sold {toMove} x {representative.Name} for {FormatCopper(totalPay)}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandleSell error: {ex}");
                return MessageHandler.System("Failed to complete sale.");
            }
        }//End HandleSell

        // Pay player helper: create/merge coin stacks into player's inventory inside a writer tx.
        // Mirrors RefundPaidToPlayerInTx but named for clarity when merchant is paying the player.
        private void PayPlayerInTx(Player player, long payCopper, SqliteConnection wConn, SqliteTransaction? wTx)
        {
            if (player == null || payCopper <= 0) return;
            try
            {
                var cur = Currency.FromCopper(payCopper);

                void AddCoins(string templateId, int units)
                {
                    if (units <= 0) return;
                    try
                    {
                        // Look for existing coin stack in player's in-memory inventory
                        var existing = player.Inventory?.FirstOrDefault(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Quantity = Math.Max(0, existing.Quantity + units);
                            ItemPersistence.SaveItem(wConn, existing, wTx);
                        }
                        else
                        {
                            var it = _world.ItemFactory.CreateItem(templateId, "Player", player.DbPlayerID, 0, units);
                            ItemPersistence.SaveItem(wConn, it, wTx);
                            player.Inventory.Add(it);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ItemTrader] PayPlayerInTx failed adding {templateId}: {ex.Message}");
                    }
                }

                AddCoins("coin_gold", cur.Gold);
                AddCoins("coin_silver", cur.Silver);
                AddCoins("coin_copper", cur.Copper);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] PayPlayerInTx error: {ex.Message}");
            }
        }//End HandleSell

        public Message HandleConvertCurrency(Player player, string amountExpr, SqliteConnection? conn)
        {
            try
            {
                // Parse a simple copper amount like "250c" or "1g20s5c" or numeric copper only.
                long copperRequested = ParseCurrencyExpressionToCopper(amountExpr);
                if (copperRequested <= 0) return MessageHandler.System("Invalid amount to convert.");

                // Fee: larger of ConversionFee (absolute) or 1% of total (rounded up)
                long percentFee = (long)Math.Ceiling(copperRequested * 0.01);
                long fee = Math.Max(_cfg.ConversionFee, percentFee);

                // Trade pool must be able to cover conversion (trader gives change)
                if (_state.CopperTradePool - fee < 0)
                    return MessageHandler.System("I don't have enough funds to do that conversion right now.");

                // Update pool (merchant collects fee)
                _state.CopperTradePool += fee;
                PersistState(conn);

                // TODO: perform actual conversion coin-move operations using ItemPersistence/InventoryManager.
                return MessageHandler.System($"I can convert that amount for a fee of {FormatCopper(fee)}. (Note: actual conversion not implemented.)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] HandleConvertCurrency error: {ex.Message}");
                return MessageHandler.System("Currency conversion failed.");
            }
        }

        // Compute the price (in copper) the trader will PAY to the player (when buying).
        private int ComputeBuyPrice(Item item)
        {
            if (item == null) return 0;
            // base: use item.Value as copper if set; fallback to 1
            var baseVal = Math.Max(1, item.Value);
            double price = baseVal;

            // dynamic pricing reduces payout when trader already holds copies
            try
            {
                if (_cfg.DynamicPricing && !string.IsNullOrWhiteSpace(item.TemplateID))
                {
                    _state.CountsByTemplate.TryGetValue(item.TemplateID, out var have);
                    price = price * Math.Max(0.0, 1.0 - (_cfg.DynamicPricingRate * have));
                }
            }
            catch { }

            // final: floor to at least 1 copper
            int result = Math.Max(1, (int)Math.Round(price));
            return result;
        }

        // Compute the price (in copper) the trader will SELL an item to a player (player buys from trader).
        private int ComputeSellPrice(Item item)
        {
            if (item == null) return 0;
            var baseVal = Math.Max(1, item.Value);
            double price = baseVal * (1.0 + _cfg.SellMarkup);
            int result = Math.Max(1, (int)Math.Round(price));
            return result;
        }

        // Check if trader accepts an item type
        private bool AcceptsItem(Item item, out string? reason)
        {
            reason = null;
            try
            {
                if (item == null) { reason = "Invalid item."; return false; }
                if (_cfg.ItemTypesAllowed != null && _cfg.ItemTypesAllowed.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(item.ItemCategory)) { reason = $"Sorry I don't buy that type of item! I trade in {string.Join(", ", _cfg.ItemTypesAllowed)}."; return false; }
                    if (!_cfg.ItemTypesAllowed.Any(t => string.Equals(t, item.ItemCategory, StringComparison.OrdinalIgnoreCase)))
                    {
                        reason = $"Sorry I don't buy that type of item! I trade in {string.Join(", ", _cfg.ItemTypesAllowed)}.";
                        return false;
                    }
                }
                // saturation
                if (_cfg.ItemSaturationLimit > 0 && !string.IsNullOrWhiteSpace(item.TemplateID))
                {
                    _state.CountsByTemplate.TryGetValue(item.TemplateID, out var have);
                    if (have >= _cfg.ItemSaturationLimit)
                    {
                        reason = $"Sorry I'm full of those right now (have {have}).";
                        return false;
                    }
                }
            }
            catch { /* best-effort */ }
            return true;
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

        // Parse simple expressions like "1g20s5c", "250c" or plain integer assumed copper.
        public static long ParseCurrencyExpressionToCopper(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return 0;
            expr = expr.Trim().ToLowerInvariant();
            try
            {
                long total = 0;
                // token pattern: (\d+g)?(\d+s)?(\d+c)? or single number
                if (expr.Contains('g') || expr.Contains('s') || expr.Contains('c'))
                {
                    int idx;
                    idx = expr.IndexOf('g');
                    if (idx > 0)
                    {
                        if (long.TryParse(expr.Substring(0, idx), out var g)) total += g * Currency.CopperPerGold;
                        expr = expr.Substring(idx + 1);
                    }
                    idx = expr.IndexOf('s');
                    if (idx > 0)
                    {
                        if (long.TryParse(expr.Substring(0, idx), out var s)) total += s * Currency.CopperPerSilver;
                        expr = expr.Substring(idx + 1);
                    }
                    idx = expr.IndexOf('c');
                    if (idx > 0)
                    {
                        if (long.TryParse(expr.Substring(0, idx), out var c)) total += c;
                    }
                }
                else
                {
                    if (long.TryParse(expr, out var plain)) total = plain;
                }
                return total;
            }
            catch { return 0; }
        }

        // Compute player's copper on-hand by scanning inventory for coin templates.
        private static long ComputePlayerCopperOnHand(Player player)
        {
            try
            {
                long total = 0;
                if (player?.Inventory == null) return 0;
                foreach (var it in player.Inventory)
                {
                    if (it == null) continue;
                    if (string.Equals(it.TemplateID, "coin_copper", StringComparison.OrdinalIgnoreCase))
                        total += (long)it.Quantity * 1;
                    else if (string.Equals(it.TemplateID, "coin_silver", StringComparison.OrdinalIgnoreCase))
                        total += (long)it.Quantity * Currency.CopperPerSilver;
                    else if (string.Equals(it.TemplateID, "coin_gold", StringComparison.OrdinalIgnoreCase))
                        total += (long)it.Quantity * Currency.CopperPerGold;
                    else
                    {
                        // fallback: if item Value seems to represent copper and the item is named coin, include it
                        if (!string.IsNullOrWhiteSpace(it.Name) && it.Name.ToLowerInvariant().Contains("coin"))
                            total += (long)it.Quantity * Math.Max(0, it.Value);
                    }
                }
                return total;
            }
            catch { return 0; }
        }

        // OPTIONAL: periodic tick for trader to announce desired items or check thresholds.
        // Call from NpcAI.Update or World.Tick when integrating.
        public void TickPeriodic(SqliteConnection? conn = null)
        {
            try
            {
                // Announce desired items occasionally (every 30 minutes game time)
                if (_cfg.DesiredItems != null && _cfg.DesiredItems.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    if (!_state.LastDesiredAnnounceUtc.HasValue || (now - _state.LastDesiredAnnounceUtc.Value).TotalMinutes >= 30)
                    {
                        var item = _cfg.DesiredItems[new Random().Next(_cfg.DesiredItems.Count)];
                        var text = $"Buying {item} for top dollar! Come sell your {item}s!";
                        try
                        {
                            // broadcast as a yell in the NPC's room
                            foreach (var p in _npc.CurrentRoom?.Players ?? new List<Player>())
                                MessageHandler.SendSystem(p, text);
                        }
                        catch { }
                        _state.LastDesiredAnnounceUtc = now;
                        PersistState(conn);
                    }
                }

                // Auto-check thresholds: if pool below MinTradePool or weight >= max and TradeCenter set -> mark traveling (not implemented)
                if (_cfg.TradeCenter != 0)
                {
                    if (_state.CopperTradePool <= _cfg.MinTradePool || _state.InventoryWeight >= _cfg.InventoryMaxWeight)
                    {
                        // announce and (in future) trigger MoveToTradeCenter behavior
                        try
                        {
                            var dest = ResolveTradeCenterRoomName();
                            foreach (var p in _npc.CurrentRoom?.Players ?? new List<Player>())
                                MessageHandler.SendSystem(p, $"Sorry I'm travelling to {dest}, I should be back in a few days!");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTrader] TickPeriodic error for NpcID={_npc?.NpcID}: {ex.Message}");
            }
        }
    }
}