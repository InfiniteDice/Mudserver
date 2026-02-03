using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using MudUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MudCore.Actions
{
    internal static class Crafting
    {
        // Public API called by CommandHandler: "smelt <metal> <source> to <dest>"
        // Examples:
        //   smelt iron bits to bars
        //   smelt iron ores to bars
        //
        // Requirements:
        // - Player must be in a room that has room.Flags containing "Forge" (case-insensitive)
        // - "bits" <-> internal template id uses singular token: bit_iron, ore_iron, bar_iron
        // - bits are stackable (Quantity); ores are non-stackable
        // - Behavior:
        //     bits -> bars : consume as many bits as possible in multiples of 10 producing floor(totalBits/10) bars
        //     ores -> bars  : each ore -> 1 bar
        //     ores -> bits  : each ore -> 10 bits
        // - Persistence: mutations are persisted via ItemPersistence.SaveItem / DeleteItem
        // - Messaging: return a system Message for CommandHandler to return to caller
        // Centralized allowed-material registries (accessible to other systems in this assembly).
        // Stored as HashSet for fast lookup; exposed as IReadOnlyCollection to discourage mutation.
        private static readonly HashSet<string> _allowedMetals = new(StringComparer.OrdinalIgnoreCase)
        {
            "iron","copper","tin","bronze","silver","gold","lead","zinc","brass","nickel","platinum","steel","meteoric","electrum","mithril"
        };

        // Expose a read-only view
        public static IReadOnlyCollection<string> AllowedMetals => _allowedMetals.ToList().AsReadOnly();

        // Internal mapping: material -> wear multiplier
        // Multiplier multiplies the base condition loss:
        //   - values < 1.0 => item loses condition more slowly
        //   - values > 1.0 => item loses condition faster
        // Example: Mithril is extremely durable (0.05) so nearly no wear; Iron is baseline (1.0).
        // Tweak these numbers to tune balance.
        private static readonly Dictionary<string, double> _metalWearMultiplier = new(StringComparer.OrdinalIgnoreCase)
        {
            // baseline / typical industrial metals
            { "Iron",     1.00 }, // baseline //ac per armor segment: 10
            { "Steel",    0.85 }, // ac per armor segment: 15
            { "Bronze",   1.10 }, // ac per armor segment: 8
            { "Copper",   1.20 }, // ac per armor segment: 6
            { "Tin",      1.50 }, // not used in armor; very soft
            { "Brass",    1.15 }, // ac per armor segment: 7
            { "Zinc",     1.30 }, // 5
            { "Lead",     1.55 }, // not used in armor; very soft

            // precious  — wear faster
            { "Silver",   1.30 }, // 5 
            { "Gold",     1.40 }, // 3 ceremonial armor only
            { "Electrum", 1.35 }, // 4

            // harder / exotic metals — wear slower
            { "Nickel",   0.95 }, // 12
            { "Platinum", 0.75 }, // 18
            { "Meteoric", 0.5 }, // 25
            { "Mithril",  0.01 } // 40  // nearly immune to wear (designer-tunable)
        };

        // Optional: expose a read-only hardness view (higher = harder / more durable).
        // Hardness is the inverse concept of wear multiplier for convenience.
        // Consumers may prefer one or the other; Combat should use GetMetalWearMultiplier(...) for direct wear scaling.
        private static readonly Dictionary<string, double> _metalHardnessScore = _metalWearMultiplier
            .ToDictionary(kv => kv.Key, kv => 1.0 / Math.Max(0.0001, kv.Value), StringComparer.OrdinalIgnoreCase);

        // Public API: get wear multiplier for a material (defaults to 1.0).
        // Combat should multiply computed per-hit condition loss by this multiplier.
        public static double GetMetalWearMultiplier(string? metal)
        {
            if (string.IsNullOrWhiteSpace(metal)) return 1.0;
            if (_metalWearMultiplier.TryGetValue(metal.Trim(), out var m)) return m;
            // Try common normalization: strip spaces/case
            var key = metal.Trim();
            if (_metalWearMultiplier.TryGetValue(key, out m)) return m;
            return 1.0;
        }

        // Public API: try-get hardness score (higher = more durable). Returns true if known.
        public static bool TryGetMetalHardness(string? metal, out double hardness)
        {
            hardness = 0.0;
            if (string.IsNullOrWhiteSpace(metal)) return false;
            return _metalHardnessScore.TryGetValue(metal.Trim(), out hardness);
        }

        // Convenience: return a snapshot of the table so callers can show designers the values.
        public static IReadOnlyDictionary<string, double> GetAllMetalWearMultipliers()
        {
            return new Dictionary<string, double>(_metalWearMultiplier, StringComparer.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> _allowedWoods = new(StringComparer.OrdinalIgnoreCase)
        {
            "oak","ash","maple","walnut","birch","pine","cherry","teak","mahogany","ebony"
        };

        private static readonly HashSet<string> _allowedCloths = new(StringComparer.OrdinalIgnoreCase)
        {
            "linen","wool","silk","cotton","canvas","velvet"
        };

        private static readonly HashSet<string> _allowedStones = new(StringComparer.OrdinalIgnoreCase)
        {
            "marble","granite","limestone","sandstone","basalt"
        };

        // Exposed read-only accessors (other code should read these, not mutate)

        public static IReadOnlyCollection<string> AllowedWoods => _allowedWoods;
        public static IReadOnlyCollection<string> AllowedCloths => _allowedCloths;
        public static IReadOnlyCollection<string> AllowedStones => _allowedStones;
        public static Message Smelt(Player player, string args, World.World world, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (world == null) return MessageHandler.System("Invalid world.");
            if (string.IsNullOrWhiteSpace(args)) return MessageHandler.System("Usage: smelt <metal> <bits|ores|bars> to <bars|bits> OR smelt <item>");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Require room flag "Forge"
            if (room.Flags == null || !room.Flags.Any(f => string.Equals(f, "Forge", StringComparison.OrdinalIgnoreCase)))
                return MessageHandler.System("You need a forge to do that!");

            // Use internal case-insensitive hashset so material tokens from templates (e.g. "Iron") match.
            var allowedMetals = _allowedMetals;

            // Accept singular or plural source/dest tokens (bit/bits, ore/ores, bar/bars)
            var m = Regex.Match(args.Trim(), @"^\s*(\w+)\s+(bits?|ores?|bars?)\s+to\s+(bars?|bits?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                // Try item-recycling form: "smelt <item>" (recycle an individual item by material + weight)
                var requestedItem = args.Trim();
                if (!string.IsNullOrEmpty(requestedItem))
                {
                    // Robust matching helpers (mirrors InventoryManager matching behavior)
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

                    string NormalizeTemplate(string? tid) => (tid ?? "").Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();

                    // Helper runtime state checks
                    static bool IsRuntimeWorn(Item it)
                    {
                        if (it == null) return false;
                        if (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase))) return true;
                        if (!string.IsNullOrEmpty(it.FlagsJson) && it.FlagsJson.IndexOf("\"Worn\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        return false;
                    }
                    static bool IsRuntimeHeld(Item it)
                    {
                        if (it == null) return false;
                        if (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase))) return true;
                        if (!string.IsNullOrEmpty(it.FlagsJson) && it.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        return false;
                    }

                    // Build candidates
                    var inv = player.Inventory ?? new List<Item>();
                    Item? found = null;
                    var req = requestedItem.Trim();

                    try
                    {
                        // 1) exact template id or exact name (case-insensitive)
                        found = inv.FirstOrDefault(i =>
                            i != null &&
                            ((!string.IsNullOrEmpty(i.TemplateID) && string.Equals(i.TemplateID, req, StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(i.Name) && string.Equals(i.Name, req, StringComparison.OrdinalIgnoreCase))));

                        // 2) token-equals on name or template tokens
                        if (found == null)
                        {
                            var reqTok = req.ToLowerInvariant();
                            found = inv.FirstOrDefault(i =>
                                i != null && (
                                    Tokenize(i.Name).Any(t => string.Equals(t, reqTok, StringComparison.OrdinalIgnoreCase)) ||
                                    Tokenize(NormalizeTemplate(i.TemplateID)).Any(t => string.Equals(t, reqTok, StringComparison.OrdinalIgnoreCase))
                                ));
                        }

                        // 3) name starts-with
                        if (found == null)
                        {
                            found = inv.FirstOrDefault(i => i != null && !string.IsNullOrEmpty(i.Name) && i.Name.StartsWith(req, StringComparison.OrdinalIgnoreCase));
                        }

                        // 4) any token starts-with
                        if (found == null)
                        {
                            var reqTok = req.ToLowerInvariant();
                            found = inv.FirstOrDefault(i => i != null && (Tokenize(i.Name).Any(t => t.StartsWith(reqTok, StringComparison.OrdinalIgnoreCase)) || Tokenize(NormalizeTemplate(i.TemplateID)).Any(t => t.StartsWith(reqTok, StringComparison.OrdinalIgnoreCase))));
                        }

                        // 5) substring in name or template contains
                        if (found == null)
                        {
                            var reqTok = req.ToLowerInvariant();
                            found = inv.FirstOrDefault(i =>
                                i != null && (
                                    (!string.IsNullOrEmpty(i.Name) && i.Name.IndexOf(req, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                    NormalizeTemplate(i.TemplateID).IndexOf(reqTok, StringComparison.OrdinalIgnoreCase) >= 0
                                ));
                        }
                    }
                    catch
                    {
                        found = null;
                    }

                    if (found != null)
                    {
                        // Prefer non-worn/non-held alternative when the parser found an equipped instance.
                        try
                        {
                            if (IsRuntimeWorn(found) || IsRuntimeHeld(found))
                            {
                                // Search for an alternative match that is NOT worn/held using the same matching heuristics.
                                Item? alt = null;
                                try
                                {
                                    // 1) exact non-equipped
                                    alt = inv.FirstOrDefault(i =>
                                        i != null && !IsRuntimeWorn(i) && !IsRuntimeHeld(i) &&
                                        ((!string.IsNullOrEmpty(i.TemplateID) && string.Equals(i.TemplateID, req, StringComparison.OrdinalIgnoreCase)) ||
                                         (!string.IsNullOrEmpty(i.Name) && string.Equals(i.Name, req, StringComparison.OrdinalIgnoreCase))));

                                    // 2) token-equals non-equipped
                                    if (alt == null)
                                    {
                                        var reqTok = req.ToLowerInvariant();
                                        alt = inv.FirstOrDefault(i =>
                                            i != null && !IsRuntimeWorn(i) && !IsRuntimeHeld(i) && (
                                                Tokenize(i.Name).Any(t => string.Equals(t, reqTok, StringComparison.OrdinalIgnoreCase)) ||
                                                Tokenize(NormalizeTemplate(i.TemplateID)).Any(t => string.Equals(t, reqTok, StringComparison.OrdinalIgnoreCase))
                                            ));
                                    }

                                    // 3) starts-with non-equipped
                                    if (alt == null)
                                    {
                                        alt = inv.FirstOrDefault(i => i != null && !IsRuntimeWorn(i) && !IsRuntimeHeld(i) && !string.IsNullOrEmpty(i.Name) && i.Name.StartsWith(req, StringComparison.OrdinalIgnoreCase));
                                    }

                                    // 4) token starts-with non-equipped
                                    if (alt == null)
                                    {
                                        var reqTok = req.ToLowerInvariant();
                                        alt = inv.FirstOrDefault(i => i != null && !IsRuntimeWorn(i) && !IsRuntimeHeld(i) && (Tokenize(i.Name).Any(t => t.StartsWith(reqTok, StringComparison.OrdinalIgnoreCase)) || Tokenize(NormalizeTemplate(i.TemplateID)).Any(t => t.StartsWith(reqTok, StringComparison.OrdinalIgnoreCase))));
                                    }

                                    // 5) substring non-equipped
                                    if (alt == null)
                                    {
                                        var reqTok = req.ToLowerInvariant();
                                        alt = inv.FirstOrDefault(i =>
                                            i != null && !IsRuntimeWorn(i) && !IsRuntimeHeld(i) && (
                                                (!string.IsNullOrEmpty(i.Name) && i.Name.IndexOf(req, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                NormalizeTemplate(i.TemplateID).IndexOf(reqTok, StringComparison.OrdinalIgnoreCase) >= 0
                                            ));
                                    }
                                }
                                catch { alt = null; }

                                if (alt != null)
                                {
                                    found = alt;
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        // Prevent smelting worn/held items (if still equipped after alternative search)
                        try
                        {
                            var flags = found.Flags ?? new List<string>();
                            if (flags.Any(f => string.Equals(f, "Worn", StringComparison.OrdinalIgnoreCase)) ||
                                flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(found.FlagsJson) && (found.FlagsJson.IndexOf("\"Worn\"", StringComparison.OrdinalIgnoreCase) >= 0 || found.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0)))
                            {
                                return MessageHandler.System($"You must remove {found.Name} before smelting it.");
                            }
                        }
                        catch { /* best-effort */ }

                        // Material check — require material on approved list (case-insensitive HashSet)
                        var material = (found.Material ?? "").Trim();
                        if (string.IsNullOrEmpty(material) || !_allowedMetals.Contains(material))
                            return MessageHandler.System($"You cannot smelt {found.Name}: material '{material}' is not supported for smelting.");

                        // Compute total weight to convert (weight * quantity)
                        double unitWeight = Math.Max(0.0, found.Weight);
                        int qty = Math.Max(1, found.Quantity);
                        double totalWeight = unitWeight * qty;

                        // Define conversions: 1.0 weight -> 1 bar, 0.1 weight -> 1 bit
                        const double weightPerBar = 1.0;
                        const double weightPerBit = 0.1;

                        int bars = (int)Math.Floor(totalWeight / weightPerBar);
                        double rem = totalWeight - bars * weightPerBar;
                        int bits = (int)Math.Floor((rem + 1e-9) / weightPerBit);

                        if (bars == 0 && bits == 0)
                            return MessageHandler.System($"Smelting {found.Name} yields nothing usable.");

                        // Perform persistence atomically when writer queue available
                        try
                        {
                            MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                            {
                                // Delete source item inside writer tx if it exists
                                if (found.ItemID > 0)
                                {
                                    using var del = wConn.CreateCommand();
                                    del.Transaction = wTx;
                                    del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                    del.Parameters.AddWithValue("@id", found.ItemID);
                                    del.ExecuteNonQuery();
                                }

                                // Create bar(s)
                                if (bars > 0)
                                {
                                    var barTemplate = $"bar_{material.ToLowerInvariant()}";
                                    var barItem = world.ItemFactory.CreateItem(barTemplate, "Player", player.DbPlayerID, room.RoomID, bars);
                                    barItem.OwnerType = "Player"; barItem.OwnerID = player.DbPlayerID; barItem.RoomID = room.RoomID;

                                    // Assign descriptor before persisting (best-effort)
                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(barItem); } catch { }

                                    ItemPersistence.SaveItem(wConn, barItem, wTx);
                                    // reflect ItemID/Quantity back to in-memory later
                                }

                                // Create bit(s)
                                if (bits > 0)
                                {
                                    var bitTemplate = $"bit_{material.ToLowerInvariant()}";
                                    var bitItem = world.ItemFactory.CreateItem(bitTemplate, "Player", player.DbPlayerID, room.RoomID, bits);
                                    bitItem.OwnerType = "Player"; bitItem.OwnerID = player.DbPlayerID; bitItem.RoomID = room.RoomID;

                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(bitItem); } catch { }

                                    ItemPersistence.SaveItem(wConn, bitItem, wTx);
                                }

                                return (object?)null;
                            }, timeout: TimeSpan.FromSeconds(10));
                        }
                        catch (InvalidOperationException)
                        {
                            // Writer not running — fall back to direct DB ops (legacy behavior)
                            try
                            {
                                if (found.ItemID > 0) ItemPersistence.DeleteItem(conn, found.ItemID);

                                if (bars > 0)
                                {
                                    var barTemplate = $"bar_{material.ToLowerInvariant()}";
                                    var barItem = world.ItemFactory.CreateItem(barTemplate, "Player", player.DbPlayerID, room.RoomID, bars);
                                    barItem.OwnerType = "Player"; barItem.OwnerID = player.DbPlayerID; barItem.RoomID = room.RoomID;

                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(barItem); } catch { }

                                    ItemPersistence.SaveItem(conn, barItem);
                                }

                                if (bits > 0)
                                {
                                    var bitTemplate = $"bit_{material.ToLowerInvariant()}";
                                    var bitItem = world.ItemFactory.CreateItem(bitTemplate, "Player", player.DbPlayerID, room.RoomID, bits);
                                    bitItem.OwnerType = "Player"; bitItem.OwnerID = player.DbPlayerID; bitItem.RoomID = room.RoomID;

                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(bitItem); } catch { }

                                    ItemPersistence.SaveItem(conn, bitItem);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CRAFTING] Smelt fallback failed: {ex.Message}");
                                return MessageHandler.System("Smelting failed due to an internal error.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CRAFTING] Smelt writer-path failed: {ex.Message}");
                            return MessageHandler.System("Smelting failed due to an internal error.");
                        }

                        // Remove source from in-memory inventory and add results by reloading player's items from DB (best-effort)
                        try
                        {
                            player.Inventory.RemoveAll(i => ReferenceEquals(i, found) || (found.ItemID != 0 && i.ItemID == found.ItemID));
                        }
                        catch { }

                        // Refresh by loading any new items for player from DB for those templates (best-effort)
                        try
                        {
                            // Attempt to fetch any created bar/bit rows into memory by loading items by owner (simple approach)
                            var created = ItemPersistence.LoadItemsByOwner(conn, "Player", player.DbPlayerID);
                            if (created != null)
                            {
                                // Merge: keep existing non-materials and replace/append created ones
                                player.Inventory = created;
                            }
                        }
                        catch { /* best-effort */ }

                        var createdParts = new List<string>();
                        if (bars > 0) createdParts.Add($"{bars} {material} bar(s)");
                        if (bits > 0) createdParts.Add($"{bits} {material} bit(s)");

                        try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                        var resultText = $"You smelted {found.Name} and recovered {string.Join(" and ", createdParts)}.";
                        return MessageHandler.System(resultText);
                    }

                    // No item found -> fall through to usage message
                }

                return MessageHandler.System("Usage: smelt <metal> <bits|ores|bars> to <bars|bits> OR smelt <item>");
            }

            string metal = m.Groups[1].Value.ToLowerInvariant();
            string srcRaw = m.Groups[2].Value.ToLowerInvariant();   // e.g. "bit" or "bits" or "ore"/"ores" or "bar"/"bars"
            string dstRaw = m.Groups[3].Value.ToLowerInvariant();   // e.g. "bars" or "bits"

            // Normalize to plural form for easier branch logic
            string NormalizeToPlural(string tok)
            {
                if (string.IsNullOrEmpty(tok)) return tok;
                tok = tok.ToLowerInvariant();
                if (tok.EndsWith("s")) return tok;
                return tok + "s";
            }

            string src = NormalizeToPlural(srcRaw); // "bits" | "ores" | "bars"
            string dst = NormalizeToPlural(dstRaw); // "bars" | "bits"

            // Validate requested metal against allowed list
            if (!allowedMetals.Contains(metal))
                return MessageHandler.System($"Unsupported metal '{metal}'. Supported: {string.Join(", ", allowedMetals)}");

            // Map to template ids: singular prefix (bit/ore/bar)
            string srcSing = src.EndsWith("s") ? src[..^1] : src; // "bits"->"bit", "ores"->"ore", "bars"->"bar"
            string dstSing = dst.EndsWith("s") ? dst[..^1] : dst; // "bars"->"bar", "bits"->"bit"

            string srcTemplate = $"{srcSing}_{metal}"; // e.g. "bit_iron" or "ore_iron" or "bar_iron"
            string dstTemplate = $"{dstSing}_{metal}"; // e.g. "bar_iron" or "bit_iron"

            // Helper: find matching items in player's inventory by TemplateID
            List<Item> FindPlayerItems(string templateId)
            {
                return player.Inventory?.Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<Item>();
            }

            try
            {
                // bits -> bars (existing)
                if (src == "bits" && dst == "bars")
                {
                    var bitItems = FindPlayerItems(srcTemplate);
                    int totalBits = bitItems.Sum(i => Math.Max(1, i.Quantity));

                    int barsToMake = totalBits / 10;
                    if (barsToMake <= 0)
                        return MessageHandler.System($"You do not have enough {metal} bits to smelt into bars (need 10 bits per bar).");

                    int bitsToConsume = barsToMake * 10;
                    int remainingToConsume = bitsToConsume;

                    // Do persistence atomically when possible
                    try
                    {
                        MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            // consume bits
                            foreach (var it in bitItems.ToList())
                            {
                                if (remainingToConsume <= 0) break;
                                int take = Math.Min(remainingToConsume, Math.Max(1, it.Quantity));
                                int newQty = it.Quantity - take;
                                remainingToConsume -= take;

                                if (it.ItemID > 0)
                                {
                                    if (newQty > 0)
                                    {
                                        it.Quantity = newQty;
                                        ItemPersistence.SaveItem(wConn, it, wTx);
                                    }
                                    else
                                    {
                                        using var del = wConn.CreateCommand();
                                        del.Transaction = wTx;
                                        del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                        del.Parameters.AddWithValue("@id", it.ItemID);
                                        del.ExecuteNonQuery();
                                    }
                                }
                            }

                            // create bars
                            var barItem = world.ItemFactory.CreateItem(dstTemplate, "Player", player.DbPlayerID, room.RoomID, barsToMake);
                            barItem.OwnerType = "Player"; barItem.OwnerID = player.DbPlayerID; barItem.RoomID = room.RoomID;

                            try { ItemDescriptorManager.AssignDescriptorIfApplicable(barItem); } catch { }

                            ItemPersistence.SaveItem(wConn, barItem, wTx);

                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(10));
                    }
                    catch (InvalidOperationException)
                    {
                        // fallback
                        foreach (var it in bitItems.ToList())
                        {
                            if (remainingToConsume <= 0) break;
                            int take = Math.Min(remainingToConsume, Math.Max(1, it.Quantity));
                            int newQty = it.Quantity - take;
                            remainingToConsume -= take;

                            if (it.ItemID > 0)
                            {
                                if (newQty > 0)
                                {
                                    it.Quantity = newQty;
                                    ItemPersistence.SaveItem(conn, it);
                                }
                                else
                                {
                                    ItemPersistence.DeleteItem(conn, it.ItemID);
                                }
                            }

                            if (it.Quantity <= 0) player.Inventory.Remove(it);
                        }

                        var barItem = world.ItemFactory.CreateItem(dstTemplate, "Player", player.DbPlayerID, room.RoomID, barsToMake);
                        barItem.OwnerType = "Player"; barItem.OwnerID = player.DbPlayerID; barItem.RoomID = room.RoomID;

                        try { ItemDescriptorManager.AssignDescriptorIfApplicable(barItem); } catch { }

                        ItemPersistence.SaveItem(conn, barItem);
                    }

                    // Update in-memory inventory
                    try
                    {
                        // remove consumed stacks
                        foreach (var it in bitItems.ToList())
                        {
                            if (it.Quantity <= 0) player.Inventory.Remove(it);
                        }

                        // ensure bar present in inventory by reloading player items (best-effort)
                        var created = ItemPersistence.LoadItemsByOwner(conn, "Player", player.DbPlayerID);
                        if (created != null) player.Inventory = created;
                    }
                    catch { }

                    try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                    int leftoverBits = totalBits - bitsToConsume;
                    return MessageHandler.System($"You smelted {bitsToConsume} {metal} bits into {barsToMake} {metal} bar(s). Remaining bits: {leftoverBits}.");
                }

                // ores -> bars or bits (existing)
                if (src == "ores")
                {
                    var oreItems = FindPlayerItems(srcTemplate);
                    if (oreItems.Count == 0)
                        return MessageHandler.System($"You are not carrying any {metal} ores.");

                    if (dst == "bars" || dst == "bits")
                    {
                        int oreCount = oreItems.Count;
                        int bitsQty = oreCount * 10;
                        int barsQty = oreCount;

                        try
                        {
                            MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                            {
                                // delete ore rows
                                foreach (var ore in oreItems.ToList())
                                {
                                    if (ore.ItemID > 0)
                                    {
                                        using var del = wConn.CreateCommand();
                                        del.Transaction = wTx;
                                        del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                        del.Parameters.AddWithValue("@id", ore.ItemID);
                                        del.ExecuteNonQuery();
                                    }
                                }

                                // create result
                                if (dst == "bars")
                                {
                                    var barItem = world.ItemFactory.CreateItem($"bar_{metal}", "Player", player.DbPlayerID, room.RoomID, barsQty);
                                    barItem.OwnerType = "Player"; barItem.OwnerID = player.DbPlayerID; barItem.RoomID = room.RoomID;

                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(barItem); } catch { }

                                    ItemPersistence.SaveItem(wConn, barItem, wTx);
                                }
                                else
                                {
                                    var bitItem = world.ItemFactory.CreateItem($"bit_{metal}", "Player", player.DbPlayerID, room.RoomID, bitsQty);
                                    bitItem.OwnerType = "Player"; bitItem.OwnerID = player.DbPlayerID; bitItem.RoomID = room.RoomID;

                                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(bitItem); } catch { }

                                    ItemPersistence.SaveItem(wConn, bitItem, wTx);
                                }

                                return (object?)null;
                            }, timeout: TimeSpan.FromSeconds(10));
                        }
                        catch (InvalidOperationException)
                        {
                            // fallback
                            foreach (var ore in oreItems.ToList())
                            {
                                if (ore.ItemID > 0) ItemPersistence.DeleteItem(conn, ore.ItemID);
                                player.Inventory.Remove(ore);
                            }

                            if (dst == "bars")
                            {
                                var barItem = world.ItemFactory.CreateItem($"bar_{metal}", "Player", player.DbPlayerID, room.RoomID, barsQty);
                                barItem.OwnerType = "Player"; barItem.OwnerID = player.DbPlayerID; barItem.RoomID = room.RoomID;

                                try { ItemDescriptorManager.AssignDescriptorIfApplicable(barItem); } catch { }

                                ItemPersistence.SaveItem(conn, barItem);
                            }
                            else
                            {
                                var bitItem = world.ItemFactory.CreateItem($"bit_{metal}", "Player", player.DbPlayerID, room.RoomID, bitsQty);
                                bitItem.OwnerType = "Player"; bitItem.OwnerID = player.DbPlayerID; bitItem.RoomID = room.RoomID;

                                try { ItemDescriptorManager.AssignDescriptorIfApplicable(bitItem); } catch { }

                                ItemPersistence.SaveItem(conn, bitItem);
                            }
                        }

                        // Refresh in-memory inventory
                        try
                        {
                            var created = ItemPersistence.LoadItemsByOwner(conn, "Player", player.DbPlayerID);
                            if (created != null) player.Inventory = created;
                        }
                        catch { }

                        try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                        if (dst == "bars") return MessageHandler.System($"You smelted {oreCount} {metal} ore(s) into {oreCount} {metal} bar(s).");
                        return MessageHandler.System($"You smelted {oreCount} {metal} ore(s) into {bitsQty} {metal} bits.");
                    }
                }

                // NEW: bars -> bits (reverse of bits->bars)
                if (src == "bars" && dst == "bits")
                {
                    var barItems = FindPlayerItems(srcTemplate);
                    if (barItems.Count == 0)
                        return MessageHandler.System($"You are not carrying any {metal} bars.");

                    // Sum quantities (bars are stackable)
                    int totalBars = barItems.Sum(i => Math.Max(1, i.Quantity));
                    int bitsQtyResult = totalBars * 10;

                    try
                    {
                        MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            // remove bar rows
                            foreach (var b in barItems.ToList())
                            {
                                if (b.ItemID > 0)
                                {
                                    using var del = wConn.CreateCommand();
                                    del.Transaction = wTx;
                                    del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                    del.Parameters.AddWithValue("@id", b.ItemID);
                                    del.ExecuteNonQuery();
                                }
                            }

                            // create bits
                            var bitItem = world.ItemFactory.CreateItem($"bit_{metal}", "Player", player.DbPlayerID, room.RoomID, bitsQtyResult);
                            bitItem.OwnerType = "Player"; bitItem.OwnerID = player.DbPlayerID; bitItem.RoomID = room.RoomID;

                            try { ItemDescriptorManager.AssignDescriptorIfApplicable(bitItem); } catch { }

                            ItemPersistence.SaveItem(wConn, bitItem, wTx);

                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(10));
                    }
                    catch (InvalidOperationException)
                    {
                        // fallback
                        foreach (var b in barItems.ToList())
                        {
                            if (b.ItemID > 0) ItemPersistence.DeleteItem(conn, b.ItemID);
                            player.Inventory.Remove(b);
                        }

                        var bitItem = world.ItemFactory.CreateItem($"bit_{metal}", "Player", player.DbPlayerID, room.RoomID, bitsQtyResult);
                        bitItem.OwnerType = "Player"; bitItem.OwnerID = player.DbPlayerID; bitItem.RoomID = room.RoomID;

                        try { ItemDescriptorManager.AssignDescriptorIfApplicable(bitItem); } catch { }

                        ItemPersistence.SaveItem(conn, bitItem);
                    }

                    // Refresh in-memory
                    try
                    {
                        var created = ItemPersistence.LoadItemsByOwner(conn, "Player", player.DbPlayerID);
                        if (created != null) player.Inventory = created;
                    }
                    catch { }

                    try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                    return MessageHandler.System($"You broke down {totalBars} {metal} bar(s) into {bitsQtyResult} {metal} bits.");
                }

                return MessageHandler.System("Unsupported smelt operation or invalid parameters.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRAFTING] Smelt failed: {ex.Message}");
                return MessageHandler.System("Smelting failed due to an internal error.");
            }
        } // end Smelt

        // NEW: Milling functionality
        // Usage:
        //   mill [#] <grainkeyword>   -> consumes # grain (default 1) -> produces same # grist_XXX and bran_XXX (bran not millable)
        //   mill [#] <gristkeyword>   -> consumes (2*#) grist -> produces # flour_XXX (2 grist -> 1 flour)
        // Requirements:
        //  - Player must be in a room with flag "Grindstone"
        //  - Player must be holding a Temse tool (template "tool_temse")
        //  - Input items are taken from player's inventory; results placed into player's inventory
        public static Message Mill(Player player, string args, World.World world, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (world == null) return MessageHandler.System("Invalid world.");
            if (string.IsNullOrWhiteSpace(args)) return MessageHandler.System("Usage: mill [#] <grain|grist>");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Require Grindstone in room flags
            if (room.Flags == null || !room.Flags.Any(f => string.Equals(f, "Grindstone", StringComparison.OrdinalIgnoreCase)))
                return MessageHandler.System("You need a grindstone to mill.");

            // Ensure player is holding a Temse tool
            bool IsRuntimeHeld(Item it)
            {
                if (it == null) return false;
                if (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase))) return true;
                if (!string.IsNullOrEmpty(it.FlagsJson) && it.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            bool hasTemse = false;
            try
            {
                hasTemse = player.Inventory != null && player.Inventory.Any(i => i != null && string.Equals(i.TemplateID, "tool_temse", StringComparison.OrdinalIgnoreCase) && IsRuntimeHeld(i));
            }
            catch { hasTemse = false; }

            if (!hasTemse) return MessageHandler.System("You must be holding a temse to operate the grindstone.");

            // Parse count + keyword
            var m = Regex.Match(args.Trim(), @"^\s*(?:(\d+)\s+)?(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return MessageHandler.System("Usage: mill [#] <grain|grist>");

            int requested = 1;
            if (!string.IsNullOrWhiteSpace(m.Groups[1].Value))
            {
                if (!int.TryParse(m.Groups[1].Value, out requested) || requested <= 0) requested = 1;
            }
            string targetToken = m.Groups[2].Value.Trim();

            // Tokenize helpers like Smelt
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
            string NormalizeTemplate(string? tid) => (tid ?? "").Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();

            // Find matching inventory items by token (match template or name tokens)
            List<Item> FindMatches(Func<Item, bool> filter)
            {
                var inv = player.Inventory ?? new List<Item>();
                var list = new List<Item>();
                try
                {
                    foreach (var it in inv)
                    {
                        try
                        {
                            if (it == null) continue;
                            if (filter(it)) list.Add(it);
                        }
                        catch { }
                    }
                }
                catch { }
                return list;
            }

            // Resolve request: check if token matches a grain template (grain_<type>) or grist_<type> or bran_<type>
            // We'll inspect player's inventory templates to choose best match
            var invSnapshot = player.Inventory?.ToList() ?? new List<Item>();
            Item? explicitMatch = null;
            try
            {
                // 1) exact template id match
                explicitMatch = invSnapshot.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, targetToken, StringComparison.OrdinalIgnoreCase));
                if (explicitMatch == null)
                {
                    // 2) token-equals on name or template tokens
                    var reqTok = targetToken.ToLowerInvariant();
                    explicitMatch = invSnapshot.FirstOrDefault(i =>
                        Tokenize(i.Name).Any(t => string.Equals(t, reqTok, StringComparison.OrdinalIgnoreCase)) ||
                        Tokenize(NormalizeTemplate(i.TemplateID)).Any(t => string.Equals(t, reqTok, StringComparison.OrdinalIgnoreCase)));
                }

                if (explicitMatch == null)
                {
                    var reqTok = targetToken.ToLowerInvariant();
                    explicitMatch = invSnapshot.FirstOrDefault(i => Tokenize(i.Name).Any(t => t.StartsWith(reqTok, StringComparison.OrdinalIgnoreCase)) || Tokenize(NormalizeTemplate(i.TemplateID)).Any(t => t.StartsWith(reqTok, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch { explicitMatch = null; }

            if (explicitMatch == null)
            {
                // If no explicit match, also attempt to interpret token as plain type (e.g. "wheat")
                var reqTok = targetToken.ToLowerInvariant();
                explicitMatch = invSnapshot.FirstOrDefault(i =>
                    (!string.IsNullOrEmpty(i.TemplateID) && i.TemplateID.IndexOf(reqTok, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(i.Name) && i.Name.IndexOf(reqTok, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (explicitMatch == null)
            {
                return MessageHandler.System($"You are not carrying anything matching '{targetToken}'.");
            }

            // Determine kind by template prefix: grain_, grist_, bran_, flour_
            string tpl = explicitMatch.TemplateID ?? "";
            if (tpl.StartsWith("grain_", StringComparison.OrdinalIgnoreCase))
            {
                // Milling grain -> 1 grist + 1 bran per grain
                string type = tpl.Substring("grain_".Length); // e.g. "wheat"
                int totalAvailable = invSnapshot.Where(i => string.Equals(i.TemplateID, explicitMatch.TemplateID, StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Max(1, i.Quantity));
                int toProcess = Math.Min(requested, totalAvailable);
                if (toProcess <= 0) return MessageHandler.System($"You do not have any {explicitMatch.Name} to mill.");

                int gristQty = toProcess;
                int branQty = toProcess;

                // Perform DB changes
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        int remaining = toProcess;
                        // consume grains
                        foreach (var it in invSnapshot.Where(i => string.Equals(i.TemplateID, explicitMatch.TemplateID, StringComparison.OrdinalIgnoreCase)).ToList())
                        {
                            if (remaining <= 0) break;
                            int take = Math.Min(remaining, Math.Max(1, it.Quantity));
                            int newQty = it.Quantity - take;
                            remaining -= take;

                            if (it.ItemID > 0)
                            {
                                if (newQty > 0)
                                {
                                    it.Quantity = newQty;
                                    ItemPersistence.SaveItem(wConn, it, wTx);
                                }
                                else
                                {
                                    using var del = wConn.CreateCommand();
                                    del.Transaction = wTx;
                                    del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                    del.Parameters.AddWithValue("@id", it.ItemID);
                                    del.ExecuteNonQuery();
                                }
                            }
                        }

                        // create grist
                        var gristTpl = $"grist_{type}";
                        var gristItem = world.ItemFactory.CreateItem(gristTpl, "Player", player.DbPlayerID, room.RoomID, gristQty);
                        gristItem.OwnerType = "Player"; gristItem.OwnerID = player.DbPlayerID; gristItem.RoomID = room.RoomID;
                        try { ItemDescriptorManager.AssignDescriptorIfApplicable(gristItem); } catch { }
                        ItemPersistence.SaveItem(wConn, gristItem, wTx);

                        // create bran
                        var branTpl = $"bran_{type}";
                        var branItem = world.ItemFactory.CreateItem(branTpl, "Player", player.DbPlayerID, room.RoomID, branQty);
                        branItem.OwnerType = "Player"; branItem.OwnerID = player.DbPlayerID; branItem.RoomID = room.RoomID;
                        try { ItemDescriptorManager.AssignDescriptorIfApplicable(branItem); } catch { }
                        ItemPersistence.SaveItem(wConn, branItem, wTx);

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException)
                {
                    // Fallback path
                    int remaining = toProcess;
                    foreach (var it in invSnapshot.Where(i => string.Equals(i.TemplateID, explicitMatch.TemplateID, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        if (remaining <= 0) break;
                        int take = Math.Min(remaining, Math.Max(1, it.Quantity));
                        int newQty = it.Quantity - take;
                        remaining -= take;

                        if (it.ItemID > 0)
                        {
                            if (newQty > 0)
                            {
                                it.Quantity = newQty;
                                ItemPersistence.SaveItem(conn, it);
                            }
                            else
                            {
                                ItemPersistence.DeleteItem(conn, it.ItemID);
                            }
                        }
                        if (it.Quantity <= 0) player.Inventory.Remove(it);
                    }

                    var gristTpl = $"grist_{type}";
                    var gristItem = world.ItemFactory.CreateItem(gristTpl, "Player", player.DbPlayerID, room.RoomID, gristQty);
                    gristItem.OwnerType = "Player"; gristItem.OwnerID = player.DbPlayerID; gristItem.RoomID = room.RoomID;
                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(gristItem); } catch { }
                    ItemPersistence.SaveItem(conn, gristItem);

                    var branTpl = $"bran_{type}";
                    var branItem = world.ItemFactory.CreateItem(branTpl, "Player", player.DbPlayerID, room.RoomID, branQty);
                    branItem.OwnerType = "Player"; branItem.OwnerID = player.DbPlayerID; branItem.RoomID = room.RoomID;
                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(branItem); } catch { }
                    ItemPersistence.SaveItem(conn, branItem);
                }

                // Refresh inventory
                try
                {
                    var created = ItemPersistence.LoadItemsByOwner(conn, "Player", player.DbPlayerID);
                    if (created != null) player.Inventory = created;
                }
                catch { }

                try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                return MessageHandler.System($"You mill {toProcess} {explicitMatch.Name} into {gristQty} grist_{type} and {branQty} bran_{type}.");
            }
            else if (tpl.StartsWith("grist_", StringComparison.OrdinalIgnoreCase))
            {
                // Milling grist -> flour (2 grist -> 1 flour)
                string type = tpl.Substring("grist_".Length);
                int totalGrist = invSnapshot.Where(i => string.Equals(i.TemplateID, explicitMatch.TemplateID, StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Max(1, i.Quantity));
                int maxFlourPossible = totalGrist / 2;
                int toMake = Math.Min(requested, maxFlourPossible);
                if (toMake <= 0) return MessageHandler.System($"You need 2 grist_{type} to mill 1 flour_{type}.");

                int gristToConsume = toMake * 2;
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        int remaining = gristToConsume;
                        // consume grist stacks
                        foreach (var it in invSnapshot.Where(i => string.Equals(i.TemplateID, explicitMatch.TemplateID, StringComparison.OrdinalIgnoreCase)).ToList())
                        {
                            if (remaining <= 0) break;
                            int take = Math.Min(remaining, Math.Max(1, it.Quantity));
                            int newQty = it.Quantity - take;
                            remaining -= take;

                            if (it.ItemID > 0)
                            {
                                if (newQty > 0)
                                {
                                    it.Quantity = newQty;
                                    ItemPersistence.SaveItem(wConn, it, wTx);
                                }
                                else
                                {
                                    using var del = wConn.CreateCommand();
                                    del.Transaction = wTx;
                                    del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                    del.Parameters.AddWithValue("@id", it.ItemID);
                                    del.ExecuteNonQuery();
                                }
                            }
                        }

                        // create flour
                        var flourTpl = $"flour_{type}";
                        var flourItem = world.ItemFactory.CreateItem(flourTpl, "Player", player.DbPlayerID, room.RoomID, toMake);
                        flourItem.OwnerType = "Player"; flourItem.OwnerID = player.DbPlayerID; flourItem.RoomID = room.RoomID;
                        try { ItemDescriptorManager.AssignDescriptorIfApplicable(flourItem); } catch { }
                        ItemPersistence.SaveItem(wConn, flourItem, wTx);

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException)
                {
                    // fallback
                    int remaining = gristToConsume;
                    foreach (var it in invSnapshot.Where(i => string.Equals(i.TemplateID, explicitMatch.TemplateID, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        if (remaining <= 0) break;
                        int take = Math.Min(remaining, Math.Max(1, it.Quantity));
                        int newQty = it.Quantity - take;
                        remaining -= take;

                        if (it.ItemID > 0)
                        {
                            if (newQty > 0)
                            {
                                it.Quantity = newQty;
                                ItemPersistence.SaveItem(conn, it);
                            }
                            else
                            {
                                ItemPersistence.DeleteItem(conn, it.ItemID);
                            }
                        }
                        if (it.Quantity <= 0) player.Inventory.Remove(it);
                    }

                    var flourTpl = $"flour_{type}";
                    var flourItem = world.ItemFactory.CreateItem(flourTpl, "Player", player.DbPlayerID, room.RoomID, toMake);
                    flourItem.OwnerType = "Player"; flourItem.OwnerID = player.DbPlayerID; flourItem.RoomID = room.RoomID;
                    try { ItemDescriptorManager.AssignDescriptorIfApplicable(flourItem); } catch { }
                    ItemPersistence.SaveItem(conn, flourItem);
                }

                // Refresh inventory
                try
                {
                    var created = ItemPersistence.LoadItemsByOwner(conn, "Player", player.DbPlayerID);
                    if (created != null) player.Inventory = created;
                }
                catch { }

                try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                return MessageHandler.System($"You mill {gristToConsume} grist_{type} into {toMake} flour_{type}.");
            }
            else if (tpl.StartsWith("bran_", StringComparison.OrdinalIgnoreCase))
            {
                return MessageHandler.System("Bran cannot be milled further.");
            }
            else
            {
                return MessageHandler.System("You can only mill grain or grist (e.g. grain_wheat, grist_wheat).");
            }
        } // end Mill


        // Repair command:
        // Usage: repair <item> with <material> bits
        // Requirements:
        //  - Player must be in a room with flag "Forge"
        //  - Player must be holding a hammer (template id contains "hammer")
        //  - Player must have water available: either a carried "water" item, any container in the room containing a "water" item, or room flag "Water"
        //  - Player must have at least 1 bit of the requested material in inventory (template "bit_<material>")
        // Behavior:
        //  - consumes 1 bit
        //  - increases item.Condition by (5 + 5 * smithingLevel)
        //  - caps: no-skill -> max 25; skill tiers read from Data/SkillTemplates/skill_smithing.json determine max% per level
        //  - uses DbWriteQueue when available; falls back to direct persistence
        public static Message Repair(Player player, string args, World.World world, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (world == null) return MessageHandler.System("Invalid world.");
            if (string.IsNullOrWhiteSpace(args)) return MessageHandler.System("Usage: repair <item> with <material> bits");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Require Forge in room flags
            try
            {
                if (room.Flags == null || !room.Flags.Any(f => string.Equals(f, "Forge", StringComparison.OrdinalIgnoreCase)))
                    return MessageHandler.System("You need a forge to repair items.");
            }
            catch { return MessageHandler.System("You need a forge to repair items."); }

            // Parse "X with Y bits" where X may contain spaces
            var m = Regex.Match(args.Trim(), @"^\s*(.+?)\s+with\s+([A-Za-z0-9_]+)\s+bits?\s*$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return MessageHandler.System("Usage: repair <item> with <material> bits");
            }

            string itemExpr = m.Groups[1].Value.Trim();
            string materialToken = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(itemExpr) || string.IsNullOrWhiteSpace(materialToken))
                return MessageHandler.System("Usage: repair <item> with <material> bits");

            string bitTemplate = $"bit_{materialToken.ToLowerInvariant()}";

            // Helper: runtime held detection
            static bool IsRuntimeHeld(Item it)
            {
                if (it == null) return false;
                if (it.Flags != null && it.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase))) return true;
                if (!string.IsNullOrEmpty(it.FlagsJson) && it.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            // Ensure player is holding a hammer (template id contains "hammer")
            bool hasHammer = false;
            try
            {
                hasHammer = player.Inventory != null && player.Inventory.Any(i =>
                    i != null &&
                    !string.IsNullOrWhiteSpace(i.TemplateID) &&
                    i.TemplateID.IndexOf("hammer", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    IsRuntimeHeld(i));
            }
            catch { hasHammer = false; }

            if (!hasHammer) return MessageHandler.System("You must be holding a hammer to perform repairs.");

            // Ensure water availability: player inventory contains a "water" item OR a container in the room contains water
            // OR a container in the player's inventory contains water OR room flag "Water"
            bool hasWater = false;
            try
            {
                if (room.Flags != null && room.Flags.Any(f => string.Equals(f, "Water", StringComparison.OrdinalIgnoreCase)))
                    hasWater = true;
            }
            catch { }

            // Check player inventory for direct water item
            try
            {
                if (!hasWater && player.Inventory != null && player.Inventory.Any(i => i != null && !string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, "water", StringComparison.OrdinalIgnoreCase)))
                    hasWater = true;
            }
            catch { }

            // Check containers in player's inventory for water (important: user reported cup with water)
            try
            {
                if (!hasWater && conn != null && player.Inventory != null)
                {
                    foreach (var container in player.Inventory)
                    {
                        try
                        {
                            if (container == null) continue;
                            bool isContainerCat = string.Equals(container.ItemCategory ?? "", "Container", StringComparison.OrdinalIgnoreCase);
                            bool holdsLiquidFlag = (container.Flags != null && container.Flags.Any(f => string.Equals(f, "HoldsLiquid", StringComparison.OrdinalIgnoreCase)))
                                                   || (!string.IsNullOrEmpty(container.FlagsJson) && container.FlagsJson.IndexOf("\"HoldsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (!isContainerCat && !holdsLiquidFlag) continue;

                            // Load items inside the container (DB authoritative)
                            var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                            if (contents.Any(c => !string.IsNullOrWhiteSpace(c.TemplateID) && string.Equals(c.TemplateID, "water", StringComparison.OrdinalIgnoreCase)))
                            {
                                hasWater = true;
                                break;
                            }
                        }
                        catch { /* ignore per-container errors */ }
                    }
                }
            }
            catch { /* best-effort */ }

            // Check room containers for water (existing behavior)
            try
            {
                if (!hasWater && conn != null)
                {
                    foreach (var container in room.Inventory ?? new List<Item>())
                    {
                        try
                        {
                            if (container == null) continue;
                            bool isContainerCat = string.Equals(container.ItemCategory ?? "", "Container", StringComparison.OrdinalIgnoreCase);
                            bool holdsLiquidFlag = (container.Flags != null && container.Flags.Any(f => string.Equals(f, "HoldsLiquid", StringComparison.OrdinalIgnoreCase)))
                                                   || (!string.IsNullOrEmpty(container.FlagsJson) && container.FlagsJson.IndexOf("\"HoldsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (!isContainerCat && !holdsLiquidFlag) continue;

                            var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                            if (contents.Any(c => !string.IsNullOrWhiteSpace(c.TemplateID) && string.Equals(c.TemplateID, "water", StringComparison.OrdinalIgnoreCase)))
                            {
                                hasWater = true;
                                break;
                            }
                        }
                        catch { /* ignore per-container errors */ }
                    }
                }
            }
            catch { /* best-effort */ }

            if (!hasWater) return MessageHandler.System("You need water nearby (a carried water container, a container in the room with water, or the room must provide water).");

            // Resolve the target item in player's inventory using ItemParser (prefer non-equipped when possible)
            Item? target = null;
            try
            {
                var pres = ItemParser.ResolveForInventoryCommand(player, itemExpr);
                if (pres?.Winner != null)
                {
                    target = player.Inventory?.FirstOrDefault(i => i != null && (ReferenceEquals(i, pres.Winner) || i.ItemID == pres.Winner.ItemID));
                }
            }
            catch { /* best-effort */ }

            // Fallback heuristic: match by template/name tokens
            if (target == null)
            {
                try
                {
                    var toks = (itemExpr ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    target = player.Inventory?.FirstOrDefault(i =>
                        i != null && (!string.IsNullOrWhiteSpace(i.TemplateID) && toks.Any(t => i.TemplateID.ToLowerInvariant().Contains(t))
                                      || (!string.IsNullOrWhiteSpace(i.Name) && toks.Any(t => i.Name.ToLowerInvariant().Contains(t)))
                        ));
                }
                catch { target = null; }
            }

            if (target == null)
                return MessageHandler.System($"You are not carrying anything matching '{itemExpr}'.");

            // Ensure target has a material to match against
            var targetMaterial = (target.Material ?? "").Trim();
            if (string.IsNullOrWhiteSpace(targetMaterial))
            {
                // try Template material via world factory if available
                try
                {
                    if (!string.IsNullOrWhiteSpace(target.TemplateID) && world?.ItemFactory != null)
                    {
                        // attempt to lookup template name in ItemFactory Templates dictionary (best-effort)
                        try
                        {
                            if (world.ItemFactory != null)
                            {
                                // ItemFactory has a private TemplateManager; attempt to fetch friendly info isn't part of public API reliably
                                // Fallback: use target.Material (already attempted)
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(targetMaterial))
            {
                return MessageHandler.System($"Cannot determine material for {target.Name}. Repair needs a matching material bit.");
            }

            // Material must match requested bits (case-insensitive)
            if (!string.Equals(targetMaterial, materialToken, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetMaterial, materialToken, StringComparison.OrdinalIgnoreCase))
            {
                // allow matching e.g. "Iron" vs "iron"
                // but if mismatch, reject
                if (!string.Equals(targetMaterial, materialToken, StringComparison.OrdinalIgnoreCase))
                    return MessageHandler.System($"{target.Name} is made of '{targetMaterial}'. Use {targetMaterial} bits to repair it.");
            }

            // Find a bit stack for the material
            Item? bitStack = null;
            try
            {
                bitStack = player.Inventory?.FirstOrDefault(i => i != null && !string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, bitTemplate, StringComparison.OrdinalIgnoreCase) && i.Quantity > 0);
            }
            catch { bitStack = null; }

            if (bitStack == null)
                return MessageHandler.System($"You do not have any {materialToken} bits to repair with.");

            // Determine player's smithing skill level (best-effort)
            int smithLevel = 0;
            try
            {
                smithLevel = GetPlayerSmithingLevel(player);
            }
            catch { smithLevel = 0; }

            // Load smithing repair caps (cached)
            var caps = _cachedSmithingRepairCaps ??= LoadSmithingRepairCaps();

            int allowedMaxPercent = 25; // default for no-skill
            if (smithLevel <= 0)
            {
                allowedMaxPercent = 25;
            }
            else
            {
                if (!caps.TryGetValue(smithLevel, out allowedMaxPercent))
                {
                    // if not present, use highest known tier or 100
                    allowedMaxPercent = caps.Count > 0 ? caps.Values.Max() : 100;
                }
            }

            // Compute repair delta
            int delta = 5 + (5 * Math.Max(0, smithLevel));

            int beforeCond = Math.Max(0, target.Condition);
            if (beforeCond >= allowedMaxPercent)
                return MessageHandler.System($"{target.Name} is already at or above your repairable limit ({allowedMaxPercent}%).");

            int newCond = Math.Min(allowedMaxPercent, beforeCond + delta);

            // Persist changes: consume one bit + save target condition
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    // consume one bit from bitStack row
                    if (bitStack.ItemID > 0)
                    {
                        int remaining = Math.Max(0, bitStack.Quantity - 1);
                        if (remaining > 0)
                        {
                            bitStack.Quantity = remaining;
                            ItemPersistence.SaveItem(wConn, bitStack, wTx);
                        }
                        else
                        {
                            using var del = wConn.CreateCommand();
                            del.Transaction = wTx;
                            del.CommandText = "DELETE FROM Item WHERE ItemID = @id;";
                            del.Parameters.AddWithValue("@id", bitStack.ItemID);
                            del.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        // In-memory-only stack: remove from player's inventory after writer completes (best-effort)
                    }

                    // Save updated target condition
                    target.Condition = newCond;
                    ItemPersistence.SaveItem(wConn, target, wTx);

                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException)
            {
                //no fallback
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRAFTING] Repair writer-path failed: {ex.Message}");
                return MessageHandler.System("Repair failed due to an internal error.");
            }

            // Update in-memory inventory: adjust / remove consumed bit
            try
            {
                if (bitStack != null)
                {
                    if (bitStack.Quantity <= 0)
                        player.Inventory.RemoveAll(i => i != null && i.ItemID == bitStack.ItemID);
                }
            }
            catch { /* best-effort */ }

            // Notify client(s)
            try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

            return MessageHandler.System($"You repair {target.Name} using one {materialToken} bit: condition {beforeCond}% → {newCond}% (max allowed for you: {allowedMaxPercent}%).");
        } // end Repair

        // Cached smithing repair caps: level -> repair_effectiveness (percent)
        private static Dictionary<int, int>? _cachedSmithingRepairCaps = null;

        private static Dictionary<int, int> LoadSmithingRepairCaps()
        {
            var defaults = new Dictionary<int, int>
            {
                { 1, 70 }, { 2, 80 }, { 3, 90 }, { 4, 100 }, { 5, 100 }
            };

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var path = System.IO.Path.Combine(baseDir, "Data", "SkillTemplates", "skill_smithing.json");
                if (!System.IO.File.Exists(path))
                {
                    // try relative project layout
                    var alt = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "Data", "SkillTemplates", "skill_smithing.json"));
                    if (System.IO.File.Exists(alt)) path = alt;
                }

                if (!System.IO.File.Exists(path)) return defaults;

                var json = System.IO.File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("skill_tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                {
                    var map = new Dictionary<int, int>();
                    foreach (var el in tiers.EnumerateArray())
                    {
                        try
                        {
                            int lvl = 0;
                            int eff = 0;
                            if (el.TryGetProperty("level", out var v1) && v1.TryGetInt32(out var lv)) lvl = lv;
                            if (el.TryGetProperty("repair_effectiveness", out var v2) && v2.TryGetInt32(out var re)) eff = re;
                            if (lvl > 0 && eff > 0) map[lvl] = eff;
                        }
                        catch { /* ignore per-entry errors */ }
                    }

                    if (map.Count > 0) return map;
                }

                return defaults;
            }
            catch
            {
                return defaults;
            }
        }

        // Best-effort: determine player's smithing level from runtime flags
        // Supported conventions:
        //  - runtime Flag "SmithingLevel:<n>"
        //  - FlagsJson containing "SmithingLevel" token
        // If none present returns 0.

        private static int GetPlayerSmithingLevel(Player player)
        {
            if (player == null) return 0;

            // If player has no DB id we cannot create/read a persistent skill row — treat as no-skill.
            if (player.DbPlayerID <= 0) return 0;

            const string skillId = "skill_smithing";

            try
            {
                // Use writer queue to perform read-then-insert atomically and avoid races.
                var level = MudServer.DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                {
                    // 1) Try read existing row
                    using (var sel = wConn.CreateCommand())
                    {
                        sel.Transaction = wTx;
                        sel.CommandText = "SELECT SkillHasLevels, SkillLevel FROM PlayerSkills WHERE PlayerID = @pid AND SkillID = @skill LIMIT 1;";
                        sel.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        sel.Parameters.AddWithValue("@skill", skillId);

                        using var rdr = sel.ExecuteReader();
                        if (rdr.Read())
                        {
                            // If SkillLevel stored, return it (clamped)
                            try
                            {
                                var obj = rdr.IsDBNull(1) ? (object?)null : rdr.GetValue(1);
                                if (obj != null && int.TryParse(obj.ToString(), out var lvl))
                                {
                                    return Math.Clamp(lvl, 0, 5);
                                }
                            }
                            catch { /* fall through to default return 0 */ }

                            return 0;
                        }
                    }

                    // 2) Row missing -> determine SkillHasLevels by inspecting skill template JSON
                    int hasLevelsInt = 0;
                    try
                    {
                        bool hasLevels = false;
                        var baseDir = AppContext.BaseDirectory;
                        var candidatePaths = new[]
                        {
                            System.IO.Path.Combine(baseDir, "Data", "SkillTemplates", $"{skillId}.json"),
                            System.IO.Path.Combine(baseDir, "..", "Data", "SkillTemplates", $"{skillId}.json"),
                            System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "Data", "SkillTemplates", $"{skillId}.json"))
                        };

                        string? found = null;
                        foreach (var p in candidatePaths)
                        {
                            try { if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)) { found = p; break; } } catch { }
                        }

                        if (!string.IsNullOrWhiteSpace(found))
                        {
                            var json = System.IO.File.ReadAllText(found);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("skill_tiers", out var tiers) && tiers.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                hasLevels = tiers.GetArrayLength() > 0;
                            }
                        }

                        hasLevelsInt = hasLevels ? 1 : 0;
                    }
                    catch
                    {
                        hasLevelsInt = 0;
                    }

                    // 3) Insert default row (SkillLevel = 0) since not present
                    using (var ins = wConn.CreateCommand())
                    {
                        ins.Transaction = wTx;
                        ins.CommandText = "INSERT INTO PlayerSkills (PlayerID, SkillID, SkillHasLevels, SkillLevel) VALUES (@pid, @skill, @has, @lvl);";
                        ins.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        ins.Parameters.AddWithValue("@skill", skillId);
                        ins.Parameters.AddWithValue("@has", hasLevelsInt);
                        ins.Parameters.AddWithValue("@lvl", 0);
                        try
                        {
                            ins.ExecuteNonQuery();
                        }
                        catch (Microsoft.Data.Sqlite.SqliteException ex)
                        {
                            // If another writer raced and inserted concurrently, swallow unique-constraint and read again.
                            Console.WriteLine($"[CRAFTING] PlayerSkills insert race or error: {ex.Message}");
                            using var sel2 = wConn.CreateCommand();
                            sel2.Transaction = wTx;
                            sel2.CommandText = "SELECT SkillLevel FROM PlayerSkills WHERE PlayerID = @pid AND SkillID = @skill LIMIT 1;";
                            sel2.Parameters.AddWithValue("@pid", player.DbPlayerID);
                            sel2.Parameters.AddWithValue("@skill", skillId);
                            var obj = sel2.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value && int.TryParse(obj.ToString(), out var lvl2))
                                return Math.Clamp(lvl2, 0, 5);
                            return 0;
                        }
                    }

                    // newly created -> return 0
                    return 0;
                }, timeout: TimeSpan.FromSeconds(5));

                return Math.Clamp(level, 0, 5);
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[CRAFTING] GetPlayerSmithingLevel direct DB fallback failed: {ex.Message}");
                return 0;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRAFTING] GetPlayerSmithingLevel failed: {ex.Message}");
                return 0;
            }
        } // end GetPlayerSmithingLevel
    }
}