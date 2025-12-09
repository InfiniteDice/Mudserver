using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MudServer.MudUtils
{
    public enum ItemSearchScope
    {
        RoomItems,
        PlayerInventory,
        Equipped,
        Any
    }

    public sealed class ItemParserOptions
    {
        public bool AllowStartsWith { get; set; } = true;
        public int MinimumScore { get; set; } = 1;
        public bool IncludeCategoryTokens { get; set; } = true;
        public bool IncludeVisualTraits { get; set; } = true; // derives from Flags/FlagsJson
        public bool PreferInventoryOnTie { get; set; } = true;
        public bool PreferEquippedOnTie { get; set; } = true;
    }

    public sealed class ItemResolutionResult
    {
        public Item? Winner { get; set; }
        public List<Item> Candidates { get; } = new List<Item>();
        public string Reason { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    public static class ItemParser
    {
        public static ItemResolutionResult Resolve(Player player, string input, ItemSearchScope scope, ItemParserOptions? options = null)
        {
            var opts = options ?? new ItemParserOptions();
            var res = new ItemResolutionResult();

            var words = Tokenize(input).ToList();
            if (words.Count == 0) { res.Reason = "No input words."; return res; }

            var candidates = EnumerateCandidates(player, scope).ToList();
            if (candidates.Count == 0) { res.Reason = "No candidate items available in the selected scope."; return res; }

            var scored = new List<(Item item, int score, int exactMatches, bool inInventory, bool isEquipped)>();

            foreach (var it in candidates)
            {
                var tokens = BuildItemTokens(it, opts);
                if (tokens.Count == 0) continue;

                int score = 0, exact = 0;
                foreach (var w in words)
                {
                    if (tokens.Contains(w)) { score++; exact++; continue; }
                    if (opts.AllowStartsWith && tokens.Any(t => t.StartsWith(w, StringComparison.OrdinalIgnoreCase))) score++;
                }

                if (score >= opts.MinimumScore)
                {
                    bool inInv = IsInInventory(player, it);
                    bool eq = IsEquipped(player, it);
                    scored.Add((it, score, exact, inInv, eq));
                }
            }

            if (scored.Count == 0) { res.Reason = $"No items matched '{input}'."; return res; }

            var ordered = scored
                .OrderByDescending(s => s.score)
                .ThenByDescending(s => s.exactMatches)
                .ThenByDescending(s => opts.PreferEquippedOnTie ? (s.isEquipped ? 1 : 0) : 0)
                .ThenByDescending(s => opts.PreferInventoryOnTie ? (s.inInventory ? 1 : 0) : 0)
                .ThenBy(s => SafeName(s.item))
                .ToList();

            res.Winner = ordered[0].item;
            res.Score = ordered[0].score;
            foreach (var s in ordered.Skip(1)) res.Candidates.Add(s.item);
            res.Reason = $"Resolved with score={res.Score}, candidates={ordered.Count}.";
            return res;
        }

        public static ItemResolutionResult ResolveForGet(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.RoomItems, options);

        public static ItemResolutionResult ResolveForInventoryCommand(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.PlayerInventory, options);

        public static ItemResolutionResult ResolveForEquipped(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.Equipped, options);

        public static ItemResolutionResult ResolveForAny(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.Any, options);

        private static IEnumerable<Item> EnumerateCandidates(Player player, ItemSearchScope scope)
        {
            if (player == null) yield break;
            var room = player.CurrentRoom;

            switch (scope)
            {
                case ItemSearchScope.RoomItems:
                    if (room?.Inventory != null) foreach (var it in room.Inventory) if (it != null) yield return it;
                    break;
                case ItemSearchScope.PlayerInventory:
                    if (player.Inventory != null) foreach (var it in player.Inventory) if (it != null) yield return it;
                    break;
                case ItemSearchScope.Equipped:
                    foreach (var it in EnumerateEquipped(player)) if (it != null) yield return it;
                    break;
                case ItemSearchScope.Any:
                    if (room?.Inventory != null) foreach (var it in room.Inventory) if (it != null) yield return it;
                    if (player.Inventory != null) foreach (var it in player.Inventory) if (it != null) yield return it;
                    foreach (var it in EnumerateEquipped(player)) if (it != null) yield return it;
                    break;
            }
        }

        // Equipped = worn or held based on WearLoc / HandLoc / flags
        private static IEnumerable<Item> EnumerateEquipped(Player player)
        {
            if (player?.Inventory == null) yield break;
            foreach (var it in player.Inventory)
            {
                if (it == null) continue;
                bool worn = IsWorn(it);
                bool held = IsHeld(it);
                if (worn || held) yield return it;
            }
        }

        private static bool IsInInventory(Player player, Item it)
        {
            if (player?.Inventory == null || it == null) return false;
            return player.Inventory.Any(inv => ReferenceEquals(inv, it) || inv.ItemID == it.ItemID);
        }

        private static bool IsEquipped(Player player, Item it)
        {
            return EnumerateEquipped(player).Any(eq => ReferenceEquals(eq, it) || eq.ItemID == it.ItemID);
        }

        private static HashSet<string> BuildItemTokens(Item it, ItemParserOptions opts)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Tokenize(it?.Name)) tokens.Add(t);
            foreach (var t in Tokenize(it?.ShortDesc)) tokens.Add(t);

            if (opts.IncludeCategoryTokens && !string.IsNullOrWhiteSpace(it?.ItemCategory))
                foreach (var t in Tokenize(it!.ItemCategory)) tokens.Add(t);

            if (opts.IncludeVisualTraits)
            {
                // Derive trait-like words from Flags and FlagsJson
                if (it?.Flags != null)
                {
                    foreach (var f in it.Flags)
                        foreach (var t in Tokenize(f)) tokens.Add(t);
                }
                if (!string.IsNullOrEmpty(it?.FlagsJson))
                {
                    foreach (var t in Tokenize(it.FlagsJson)) tokens.Add(t);
                }
            }

            // Status tokens
            if (IsWorn(it)) tokens.Add("worn");
            if (IsHeld(it)) tokens.Add("held");

            return tokens;
        }

        private static IEnumerable<string> Tokenize(string? s)
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

        private static bool HasFlag(Item it, string flag)
        {
            if (it.Flags != null && it.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase))) return true;
            if (!string.IsNullOrEmpty(it.FlagsJson) && it.FlagsJson.IndexOf($"\"{flag}\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsWorn(Item it)
        {
            // Only treat actual runtime 'Worn' flag as worn; do not treat WearLoc template as runtime state.
            return HasFlag(it, "Worn");
        }

        private static bool IsHeld(Item it)
        {
            // Only treat runtime Held flag as held. Do NOT use HandLoc/template to infer runtime state.
            return HasFlag(it, "Held");
        }

        private static string SafeName(Item it)
        {
            var name = it?.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name!;
            var sd = it?.ShortDesc;
            if (!string.IsNullOrWhiteSpace(sd)) return sd!;
            return $"Item#{it?.ItemID}";
        }
    }
}