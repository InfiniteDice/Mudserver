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
        // Centralized allowed-material registries (accessible to other systems in this assembly).
        // Stored as HashSet for fast lookup; exposed as IReadOnlyCollection to discourage mutation.
        private static readonly HashSet<string> _allowedMetals = new(StringComparer.OrdinalIgnoreCase)
        {
            "iron","copper","tin","bronze","silver","gold","lead","zinc","brass","nickel","platinum","steel","aluminum","cobalt","titanium"
        };

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
        public static IReadOnlyCollection<string> AllowedMetals => _allowedMetals;
        public static IReadOnlyCollection<string> AllowedWoods => _allowedWoods;
        public static IReadOnlyCollection<string> AllowedCloths => _allowedCloths;
        public static IReadOnlyCollection<string> AllowedStones => _allowedStones;
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

            // Allowed metals / materials are now resolved from MaterialRegistry so other systems can reuse them.
            var allowedMetals = AllowedMetals;

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
    }
}