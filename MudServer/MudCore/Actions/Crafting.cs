using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudPersistence;
using MudNetworking;

namespace MudServer.MudCore.Actions
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
        public static Message Smelt(Player player, string args, World world, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (world == null) return MessageHandler.System("Invalid world.");
            if (string.IsNullOrWhiteSpace(args)) return MessageHandler.System("Usage: smelt <metal> <bits|ores|bars> to <bars|bits> OR smelt <item>");

            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            // Require room flag "Forge"
            if (room.Flags == null || !room.Flags.Any(f => string.Equals(f, "Forge", StringComparison.OrdinalIgnoreCase)))
                return MessageHandler.System("You need a forge to do that!");

            // Allowed metals (editable list) - keep lower/upper flexible via OrdinalIgnoreCase
            var allowedMetals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "iron","copper","tin","bronze","silver","gold","lead","zinc","brass","nickel","platinum","steel","aluminum","aluminium","cobalt","titanium"
};

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

                        // Material check — require material on approved list
                        var material = (found.Material ?? "").Trim();
                        if (string.IsNullOrEmpty(material) || !allowedMetals.Contains(material))
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

                        // Delete source item (persist)
                        try
                        {
                            if (found.ItemID > 0) ItemPersistence.DeleteItem(conn, found.ItemID);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CRAFTING] Failed to delete source item {found.ItemID}: {ex.Message}");
                        }

                        // Remove from in-memory inventory
                        try { player.Inventory.Remove(found); } catch { }

                        // Create resulting items
                        List<string> createdParts = new();

                        var materialLower = material.ToLowerInvariant();

                        if (bars > 0)
                        {
                            var barTemplate = $"bar_{materialLower}";
                            try
                            {
                                var barItem = world.ItemFactory.CreateItem(barTemplate, "Player", player.DbPlayerID, room.RoomID, bars);
                                barItem.OwnerType = "Player";
                                barItem.OwnerID = player.DbPlayerID;
                                barItem.RoomID = room.RoomID;
                                try { ItemPersistence.SaveItem(conn, barItem); } catch { /* best-effort */ }

                                // Merge into in-memory inventory (respect stackable)
                                try
                                {
                                    var existingById = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == barItem.ItemID && barItem.ItemID > 0);
                                    if (existingById != null) existingById.Quantity = barItem.Quantity;
                                    else
                                    {
                                        var merged = player.Inventory.FirstOrDefault(i =>
                                            i != null && !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == barItem.TemplateID
                                            && string.Equals(i.FlagsJson ?? "[]", barItem.FlagsJson ?? "[]", StringComparison.Ordinal));
                                        if (merged != null)
                                        {
                                            merged.Quantity = barItem.Quantity;
                                            if (merged.ItemID == 0 && barItem.ItemID > 0) merged.ItemID = barItem.ItemID;
                                        }
                                        else
                                        {
                                            player.Inventory.Add(barItem);
                                        }
                                    }
                                }
                                catch { player.Inventory.Add(world.ItemFactory.CreateItem(barTemplate, "Player", player.DbPlayerID, room.RoomID, bars)); }

                                createdParts.Add($"{bars} {material} bar(s)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CRAFTING] Failed to create bar item: {ex.Message}");
                            }
                        }

                        if (bits > 0)
                        {
                            var bitTemplate = $"bit_{materialLower}";
                            try
                            {
                                var bitItem = world.ItemFactory.CreateItem(bitTemplate, "Player", player.DbPlayerID, room.RoomID, bits);
                                bitItem.OwnerType = "Player";
                                bitItem.OwnerID = player.DbPlayerID;
                                bitItem.RoomID = room.RoomID;
                                try { ItemPersistence.SaveItem(conn, bitItem); } catch { /* best-effort */ }

                                try
                                {
                                    var existingById = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == bitItem.ItemID && bitItem.ItemID > 0);
                                    if (existingById != null) existingById.Quantity = bitItem.Quantity;
                                    else
                                    {
                                        var merged = player.Inventory.FirstOrDefault(i =>
                                            i != null && !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == bitItem.TemplateID
                                            && string.Equals(i.FlagsJson ?? "[]", bitItem.FlagsJson ?? "[]", StringComparison.Ordinal));
                                        if (merged != null)
                                        {
                                            merged.Quantity = bitItem.Quantity;
                                            if (merged.ItemID == 0 && bitItem.ItemID > 0) merged.ItemID = bitItem.ItemID;
                                        }
                                        else
                                        {
                                            player.Inventory.Add(bitItem);
                                        }
                                    }
                                }
                                catch { player.Inventory.Add(world.ItemFactory.CreateItem(bitTemplate, "Player", player.DbPlayerID, room.RoomID, bits)); }

                                createdParts.Add($"{bits} {material} bit(s)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CRAFTING] Failed to create bit item: {ex.Message}");
                            }
                        }

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

                    foreach (var it in bitItems.ToList())
                    {
                        if (remainingToConsume <= 0) break;
                        int take = Math.Min(remainingToConsume, Math.Max(1, it.Quantity));
                        it.Quantity -= take;
                        remainingToConsume -= take;

                        if (it.ItemID > 0)
                        {
                            if (it.Quantity > 0)
                                ItemPersistence.SaveItem(conn, it);
                            else
                                ItemPersistence.DeleteItem(conn, it.ItemID);
                        }

                        if (it.Quantity <= 0)
                            player.Inventory.Remove(it);
                    }

                    var barItem = world.ItemFactory.CreateItem(dstTemplate, "Player", player.DbPlayerID, room.RoomID, barsToMake);
                    barItem.OwnerType = "Player";
                    barItem.OwnerID = player.DbPlayerID;
                    barItem.RoomID = room.RoomID;
                    try { ItemPersistence.SaveItem(conn, barItem); } catch { }

                    try
                    {
                        var existingById = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == barItem.ItemID && barItem.ItemID > 0);
                        if (existingById != null)
                        {
                            existingById.Quantity = barItem.Quantity;
                            existingById.Name = string.IsNullOrWhiteSpace(existingById.Name) ? barItem.Name : existingById.Name;
                            existingById.FlagsJson = barItem.FlagsJson ?? existingById.FlagsJson;
                        }
                        else
                        {
                            var merged = player.Inventory.FirstOrDefault(i =>
                                i != null && !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == barItem.TemplateID
                                && string.Equals(i.FlagsJson ?? "[]", barItem.FlagsJson ?? "[]", StringComparison.Ordinal));
                            if (merged != null)
                            {
                                merged.Quantity = barItem.Quantity;
                                if (merged.ItemID == 0 && barItem.ItemID > 0) merged.ItemID = barItem.ItemID;
                            }
                            else
                            {
                                player.Inventory.Add(barItem);
                            }
                        }
                    }
                    catch { player.Inventory.Add(barItem); }

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

                    if (dst == "bars")
                    {
                        int oreCount = oreItems.Count;
                        foreach (var ore in oreItems.ToList())
                        {
                            if (ore.ItemID > 0) ItemPersistence.DeleteItem(conn, ore.ItemID);
                            player.Inventory.Remove(ore);
                        }

                        var barItem = world.ItemFactory.CreateItem(dstTemplate, "Player", player.DbPlayerID, room.RoomID, oreCount);
                        barItem.OwnerType = "Player";
                        barItem.OwnerID = player.DbPlayerID;
                        barItem.RoomID = room.RoomID;
                        try { ItemPersistence.SaveItem(conn, barItem); } catch { }

                        try
                        {
                            var existingById = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == barItem.ItemID && barItem.ItemID > 0);
                            if (existingById != null)
                            {
                                existingById.Quantity = barItem.Quantity;
                            }
                            else
                            {
                                var merged = player.Inventory.FirstOrDefault(i =>
                                    i != null && !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == barItem.TemplateID
                                    && string.Equals(i.FlagsJson ?? "[]", barItem.FlagsJson ?? "[]", StringComparison.Ordinal));
                                if (merged != null)
                                {
                                    merged.Quantity = barItem.Quantity;
                                    if (merged.ItemID == 0 && barItem.ItemID > 0) merged.ItemID = barItem.ItemID;
                                }
                                else
                                {
                                    player.Inventory.Add(barItem);
                                }
                            }
                        }
                        catch { player.Inventory.Add(barItem); }

                        try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                        return MessageHandler.System($"You smelted {oreCount} {metal} ore(s) into {oreCount} {metal} bar(s).");
                    }
                    else if (dst == "bits")
                    {
                        int oreCount = oreItems.Count;
                        foreach (var ore in oreItems.ToList())
                        {
                            if (ore.ItemID > 0) ItemPersistence.DeleteItem(conn, ore.ItemID);
                            player.Inventory.Remove(ore);
                        }

                        int bitsQty = oreCount * 10;
                        var bitItem = world.ItemFactory.CreateItem($"bit_{metal}", "Player", player.DbPlayerID, room.RoomID, bitsQty);
                        bitItem.OwnerType = "Player";
                        bitItem.OwnerID = player.DbPlayerID;
                        bitItem.RoomID = room.RoomID;
                        try { ItemPersistence.SaveItem(conn, bitItem); } catch { }

                        try
                        {
                            var existingById = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == bitItem.ItemID && bitItem.ItemID > 0);
                            if (existingById != null)
                            {
                                existingById.Quantity = bitItem.Quantity;
                            }
                            else
                            {
                                var merged = player.Inventory.FirstOrDefault(i =>
                                    i != null && !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == bitItem.TemplateID
                                    && string.Equals(i.FlagsJson ?? "[]", bitItem.FlagsJson ?? "[]", StringComparison.Ordinal));
                                if (merged != null)
                                {
                                    merged.Quantity = bitItem.Quantity;
                                    if (merged.ItemID == 0 && bitItem.ItemID > 0) merged.ItemID = bitItem.ItemID;
                                }
                                else
                                {
                                    player.Inventory.Add(bitItem);
                                }
                            }
                        }
                        catch { player.Inventory.Add(bitItem); }

                        try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

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

                    // Remove bar rows (delete or decrement)
                    foreach (var b in barItems.ToList())
                    {
                        if (b.ItemID > 0) ItemPersistence.DeleteItem(conn, b.ItemID);
                        player.Inventory.Remove(b);
                    }

                    int bitsQty = totalBars * 10;
                    var bitItem = world.ItemFactory.CreateItem($"bit_{metal}", "Player", player.DbPlayerID, room.RoomID, bitsQty);
                    bitItem.OwnerType = "Player";
                    bitItem.OwnerID = player.DbPlayerID;
                    bitItem.RoomID = room.RoomID;
                    try { ItemPersistence.SaveItem(conn, bitItem); } catch { }

                    try
                    {
                        var existingById = player.Inventory.FirstOrDefault(i => i != null && i.ItemID == bitItem.ItemID && bitItem.ItemID > 0);
                        if (existingById != null)
                        {
                            existingById.Quantity = bitItem.Quantity;
                        }
                        else
                        {
                            var merged = player.Inventory.FirstOrDefault(i =>
                                i != null && !string.IsNullOrEmpty(i.TemplateID) && i.TemplateID == bitItem.TemplateID
                                && string.Equals(i.FlagsJson ?? "[]", bitItem.FlagsJson ?? "[]", StringComparison.Ordinal));
                            if (merged != null)
                            {
                                merged.Quantity = bitItem.Quantity;
                                if (merged.ItemID == 0 && bitItem.ItemID > 0) merged.ItemID = bitItem.ItemID;
                            }
                            else
                            {
                                player.Inventory.Add(bitItem);
                            }
                        }
                    }
                    catch { player.Inventory.Add(bitItem); }

                    try { MessageHandler.EnqueuePlayerUpdate(player, world); } catch { }

                    return MessageHandler.System($"You broke down {totalBars} {metal} bar(s) into {bitsQty} {metal} bits.");
                }

                return MessageHandler.System("Unsupported smelt operation or invalid parameters.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRAFTING] Smelt failed: {ex.Message}");
                return MessageHandler.System("Smelting failed due to an internal error.");
            }
        } // end Smelt
    }
}