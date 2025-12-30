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

        // New opt-ins for descriptor/material tokenization and progressive resolution.
        // Callers that don't want descriptor/material matching can set these to false.
        public bool IncludeDescriptorTokens { get; set; } = true;
        public bool IncludeMaterialTokens { get; set; } = true;
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
        // Backwards-compatible Resolve: enumerate candidates for scope and delegate to ResolveCandidates.
        public static ItemResolutionResult Resolve(Player player, string input, ItemSearchScope scope, ItemParserOptions? options = null)
        {
            var opts = options ?? new ItemParserOptions();
            var candidates = EnumerateCandidates(player, scope).ToList();
            return ResolveCandidates(candidates, player, input, opts);
        }

        public static ItemResolutionResult ResolveForGet(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.RoomItems, options);

        public static ItemResolutionResult ResolveForInventoryCommand(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.PlayerInventory, options);

        public static ItemResolutionResult ResolveForEquipped(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.Equipped, options);

        public static ItemResolutionResult ResolveForAny(Player player, string input, ItemParserOptions? options = null)
            => Resolve(player, input, ItemSearchScope.Any, options);

        // Public overload that accepts an explicit candidate set (useful for container contents)

        public static ItemResolutionResult ResolveCandidates(IEnumerable<Item> candidatesEnumerable, Player? player, string input, ItemParserOptions? options = null)
        {
            var opts = options ?? new ItemParserOptions();
            var res = new ItemResolutionResult();

            // Normalize input tokens (lowercased words)
            var inputTokens = Tokenize(input).ToList();
            if (inputTokens.Count == 0) { res.Reason = "No input words."; return res; }

            var candidates = (candidatesEnumerable ?? Enumerable.Empty<Item>()).Where(i => i != null).ToList();
            if (candidates.Count == 0) { res.Reason = "No candidate items available in provided candidate set."; return res; }

            // Quick exact checks (highest priority)
            foreach (var it in candidates)
            {
                if (!string.IsNullOrWhiteSpace(it.TemplateID) && string.Equals(it.TemplateID, input, StringComparison.OrdinalIgnoreCase))
                {
                    res.Winner = it;
                    res.Score = int.MaxValue / 4;
                    res.Reason = "Exact TemplateID match";
                    res.Candidates.AddRange(candidates.Where(c => c != it));
                    return res;
                }
                if (!string.IsNullOrWhiteSpace(it.Name) && string.Equals(it.Name, input, StringComparison.OrdinalIgnoreCase))
                {
                    res.Winner = it;
                    res.Score = int.MaxValue / 8;
                    res.Reason = "Exact Name match";
                    res.Candidates.AddRange(candidates.Where(c => c != it));
                    return res;
                }
            }

            // Build canonical token sets for each candidate: name + (optional) descriptor + (optional) material + templateid
            Dictionary<Item, HashSet<string>> candidateTokenSets = new Dictionary<Item, HashSet<string>>(candidates.Count);
            Dictionary<Item, HashSet<string>> candidateDescriptorSets = new Dictionary<Item, HashSet<string>>(candidates.Count);
            Dictionary<Item, HashSet<string>> candidateMaterialSets = new Dictionary<Item, HashSet<string>>(candidates.Count);

            foreach (var it in candidates)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in Tokenize(it?.Name)) set.Add(t);
                if (opts.IncludeDescriptorTokens && !string.IsNullOrWhiteSpace(it?.Descriptor))
                {
                    var dset = new HashSet<string>(Tokenize(it.Descriptor), StringComparer.OrdinalIgnoreCase);
                    candidateDescriptorSets[it] = dset;
                    foreach (var t in dset) set.Add(t);
                }
                else
                {
                    candidateDescriptorSets[it] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (opts.IncludeMaterialTokens && !string.IsNullOrWhiteSpace(it?.Material))
                {
                    var mset = new HashSet<string>(Tokenize(it.Material), StringComparer.OrdinalIgnoreCase);
                    candidateMaterialSets[it] = mset;
                    foreach (var t in mset) set.Add(t);
                }
                else
                {
                    candidateMaterialSets[it] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (!string.IsNullOrWhiteSpace(it?.TemplateID))
                {
                    foreach (var t in Tokenize(it.TemplateID)) set.Add(t);
                }

                candidateTokenSets[it] = set;
            }

            // If user included descriptor/material tokens explicitly, prefer candidates that match those first.
            bool inputMatchesDescriptorOrMaterial = candidates.Any(it =>
                candidateDescriptorSets[it].Any(tok => inputTokens.Contains(tok))
                || candidateMaterialSets[it].Any(tok => inputTokens.Contains(tok)));

            List<Item> matches = new List<Item>();

            // 1) If descriptor/material tokens are present in input and match candidates, restrict to those that match them
            if (inputMatchesDescriptorOrMaterial)
            {
                foreach (var it in candidates)
                {
                    bool descMatch = candidateDescriptorSets[it].Any(tok => inputTokens.Contains(tok));
                    bool matMatch = candidateMaterialSets[it].Any(tok => inputTokens.Contains(tok));
                    if (descMatch || matMatch)
                        matches.Add(it);
                }
            }
            else
            {
                // 2) Token-subset match: all input tokens must be present in candidate token set
                foreach (var kv in candidateTokenSets)
                {
                    bool allPresent = inputTokens.All(tok => kv.Value.Contains(tok));
                    if (allPresent) matches.Add(kv.Key);
                }

                // 3) If no token-subset matches, try substring contains on joined normalized text (fallback)
                if (matches.Count == 0)
                {
                    foreach (var kv in candidateTokenSets)
                    {
                        // build normalized string for contains
                        var normalized = string.Join(" ", kv.Value);
                        var inNorm = string.Join(" ", inputTokens);
                        if (!string.IsNullOrWhiteSpace(normalized) &&
                            normalized.IndexOf(inNorm, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(kv.Key);
                        }
                    }
                }
            }

            if (matches.Count == 0)
            {
                res.Reason = $"No items matched '{input}'.";
                return res;
            }

            // If exactly one match, return it
            if (matches.Count == 1)
            {
                res.Winner = matches[0];
                res.Score = 1000;
                res.Reason = "Single deterministic match";
                res.Candidates.AddRange(candidates.Where(c => c != res.Winner));
                return res;
            }

            // Multiple matches: disambiguate deterministically:
            // Prefer candidates that matched descriptor/material tokens explicitly (if input included them)
            if (inputMatchesDescriptorOrMaterial)
            {
                var descMatMatches = matches.Where(it =>
                    candidateDescriptorSets[it].Any(tok => inputTokens.Contains(tok))
                    || candidateMaterialSets[it].Any(tok => inputTokens.Contains(tok))).ToList();

                if (descMatMatches.Count == 1)
                {
                    res.Winner = descMatMatches[0];
                    res.Reason = "Disambiguated by descriptor/material explicit match";
                    return res;
                }

                if (descMatMatches.Count > 1)
                {
                    // Score remaining descriptor/material matches by strength (descriptor weighted higher)
                    var scored = descMatMatches.Select(it =>
                    {
                        int descMatchCount = candidateDescriptorSets[it].Count(tok => inputTokens.Contains(tok));
                        int matMatchCount = candidateMaterialSets[it].Count(tok => inputTokens.Contains(tok));
                        int score = descMatchCount * 10 + matMatchCount; // descriptor preferred
                        return (Item: it, Score: score);
                    }).ToList();

                    var bestScore = scored.Max(s => s.Score);
                    var bestCandidates = scored.Where(s => s.Score == bestScore).Select(s => s.Item).ToList();

                    if (bestCandidates.Count == 1)
                    {
                        res.Winner = bestCandidates[0];
                        res.Reason = "Disambiguated by strongest descriptor/material match";
                        return res;
                    }

                    // Narrow matches to the best-scored subset for further tie-breaking
                    matches = bestCandidates;
                }
            }

            // Prefer items located in the room (not in player's inventory / not equipped)
            if (player != null)
            {
                var roomCandidates = matches.Where(it => !IsInInventory(player, it) && !IsEquipped(player, it)).ToList();
                if (roomCandidates.Count == 1)
                {
                    res.Winner = roomCandidates[0];
                    res.Reason = "Multiple matches — preferred room item";
                    return res;
                }
                if (roomCandidates.Count > 1)
                {
                    // deterministic: pick the first room item encountered (stable based on room.Inventory order)
                    res.Winner = roomCandidates.First();
                    res.Reason = "Multiple matches — preferred first room item (stable order)";
                    return res;
                }

                var equippedMatch = matches.FirstOrDefault(it => IsEquipped(player, it));
                if (equippedMatch != null)
                {
                    res.Winner = equippedMatch;
                    res.Reason = "Multiple matches — preferred equipped item";
                    return res;
                }

                var invMatch = matches.FirstOrDefault(it => IsInInventory(player, it));
                if (invMatch != null)
                {
                    // Ambiguous among multiple inventory matches — do NOT auto-pick; return ambiguous.
                    if (matches.Count > 1)
                    {
                        res.Reason = $"Ambiguous: {matches.Count} candidates match '{input}' (use more specific descriptor).";
                        return res;
                    }

                    res.Winner = invMatch;
                    res.Reason = "Multiple matches — chose inventory item";
                    return res;
                }
            }

            // Last resort: deterministic stable ordering by SafeName, but only if unambiguous acceptable
            var fallback = matches.OrderBy(it => SafeName(it)).First();
            res.Winner = fallback;
            res.Reason = "Multiple matches — fallback chosen by name";
            return res;
        } // end ResolveCandidates

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

        // BuildItemTokens preserved for existing callers (returns set of distinct tokens).
        // It now includes Descriptor and Material tokens when appropriate and controlled by options.
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

            // Descriptor and Material support
            // Exclude for categories where descriptors/materials are not useful
            var skipCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Component", "Organic", "Food", "Currency" };
            bool skipDescriptor = string.IsNullOrWhiteSpace(it?.Descriptor) || (!opts.IncludeDescriptorTokens) || (!string.IsNullOrWhiteSpace(it.ItemCategory) && skipCats.Contains(it.ItemCategory));
            bool skipMaterial = string.IsNullOrWhiteSpace(it?.Material) || (!opts.IncludeMaterialTokens) || (!string.IsNullOrWhiteSpace(it.ItemCategory) && skipCats.Contains(it.ItemCategory));

            if (!skipDescriptor)
            {
                foreach (var t in Tokenize(it!.Descriptor)) tokens.Add(t);
            }

            if (!skipMaterial)
            {
                foreach (var t in Tokenize(it!.Material)) tokens.Add(t);
            }

            // Status tokens
            if (IsWorn(it)) tokens.Add("worn");
            if (IsHeld(it)) tokens.Add("held");

            return tokens;
        }

        // Build token -> weight map to allow weighted scoring (name tokens > descriptor > material > flags).
        // Weights are tunable; kept conservative to produce deterministic ranking.
        private static Dictionary<string, int> BuildItemTokenWeights(Item it, ItemParserOptions opts)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Helper to add tokens with given weight
            void AddTokens(string? s, int weight)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                foreach (var t in Tokenize(s))
                {
                    if (!map.ContainsKey(t)) map[t] = weight;
                    else map[t] = Math.Max(map[t], weight);
                }
            }

            // Name tokens are most important
            AddTokens(it?.Name, 10);

            // ShortDesc somewhat important
            AddTokens(it?.ShortDesc, 5);

            // Category tokens
            if (opts.IncludeCategoryTokens && !string.IsNullOrWhiteSpace(it?.ItemCategory))
                AddTokens(it!.ItemCategory, 3);

            // Flags / visual traits (lowest weight)
            if (opts.IncludeVisualTraits)
            {
                if (it?.Flags != null)
                {
                    foreach (var f in it.Flags) AddTokens(f, 1);
                }
                if (!string.IsNullOrEmpty(it?.FlagsJson)) AddTokens(it.FlagsJson, 1);
            }

            // Descriptor / Material (middle weights), skip for excluded categories
            var skipCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Component", "Organic", "Food", "Currency" };
            bool skipDescriptor = string.IsNullOrWhiteSpace(it?.Descriptor) || (!opts.IncludeDescriptorTokens) || (!string.IsNullOrWhiteSpace(it.ItemCategory) && skipCats.Contains(it.ItemCategory));
            bool skipMaterial = string.IsNullOrWhiteSpace(it?.Material) || (!opts.IncludeMaterialTokens) || (!string.IsNullOrWhiteSpace(it.ItemCategory) && skipCats.Contains(it.ItemCategory));

            if (!skipDescriptor) AddTokens(it!.Descriptor, 8);
            if (!skipMaterial) AddTokens(it!.Material, 7);

            // runtime status tokens lowest priority
            if (IsWorn(it)) map["worn"] = Math.Max(map.TryGetValue("worn", out var v1) ? v1 : 0, 1);
            if (IsHeld(it)) map["held"] = Math.Max(map.TryGetValue("held", out var v2) ? v2 : 0, 1);

            return map;
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