using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Linq;


namespace MudUtils
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

        // NEW: when true, include items marked Buried* in room candidate lists.
        // Default false: buried items are invisible to normal item resolution.
        public bool IncludeBuried { get; set; } = false;
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
            var candidates = EnumerateCandidates(player, scope, opts).ToList();
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

            // Build separate token sets for name, descriptor, and material for each candidate
            var candidateNameTokens = new Dictionary<Item, HashSet<string>>();
            var candidateDescriptorTokens = new Dictionary<Item, HashSet<string>>();
            var candidateMaterialTokens = new Dictionary<Item, HashSet<string>>();
            var candidateAllTokens = new Dictionary<Item, HashSet<string>>();

            foreach (var it in candidates)
            {
                var nameSet = new HashSet<string>(Tokenize(it?.Name), StringComparer.OrdinalIgnoreCase);
                candidateNameTokens[it] = nameSet;

                var descSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (opts.IncludeDescriptorTokens && !string.IsNullOrWhiteSpace(it?.Descriptor))
                {
                    foreach (var t in Tokenize(it.Descriptor))
                        descSet.Add(t);
                }
                candidateDescriptorTokens[it] = descSet;

                var matSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (opts.IncludeMaterialTokens && !string.IsNullOrWhiteSpace(it?.Material))
                {
                    foreach (var t in Tokenize(it.Material))
                        matSet.Add(t);
                }
                candidateMaterialTokens[it] = matSet;

                // Combined set includes name + descriptor + material + templateID
                var allSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in nameSet) allSet.Add(t);
                foreach (var t in descSet) allSet.Add(t);
                foreach (var t in matSet) allSet.Add(t);
                if (!string.IsNullOrWhiteSpace(it?.TemplateID))
                {
                    foreach (var t in Tokenize(it.TemplateID))
                        allSet.Add(t);
                }
                candidateAllTokens[it] = allSet;
            }

            // Detect if user provided descriptor or material tokens explicitly
            var inputDescriptorTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inputMaterialTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inputNameTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tok in inputTokens)
            {
                bool isDescriptor = false;
                bool isMaterial = false;

                foreach (var it in candidates)
                {
                    if (candidateDescriptorTokens[it].Contains(tok))
                    {
                        isDescriptor = true;
                        inputDescriptorTokens.Add(tok);
                    }
                    if (candidateMaterialTokens[it].Contains(tok))
                    {
                        isMaterial = true;
                        inputMaterialTokens.Add(tok);
                    }
                    if (candidateNameTokens[it].Contains(tok))
                    {
                        inputNameTokens.Add(tok);
                    }
                }
            }

            bool userProvidedDescriptor = inputDescriptorTokens.Count > 0;
            bool userProvidedMaterial = inputMaterialTokens.Count > 0;
            bool userProvidedNameOnly = inputNameTokens.Count > 0 && !userProvidedDescriptor && !userProvidedMaterial;

            // Quick exact TemplateID check (highest priority, bypasses all other logic)
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
            }

            // Phase 1: Find candidates that match ALL input tokens (subset match)
            var initialMatches = new List<Item>();
            foreach (var kv in candidateAllTokens)
            {
                bool allPresent = inputTokens.All(tok => kv.Value.Contains(tok));
                if (allPresent)
                    initialMatches.Add(kv.Key);
            }

            // If no subset matches, try substring contains fallback
            if (initialMatches.Count == 0)
            {
                foreach (var kv in candidateAllTokens)
                {
                    var normalized = string.Join(" ", kv.Value);
                    var inNorm = string.Join(" ", inputTokens);
                    if (!string.IsNullOrWhiteSpace(normalized) &&
                        normalized.IndexOf(inNorm, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        initialMatches.Add(kv.Key);
                    }
                }
            }

            if (initialMatches.Count == 0)
            {
                res.Reason = $"No items matched '{input}'.";
                return res;
            }

            // Phase 2: If user provided descriptor/material, REQUIRE those to match
            // This is the critical fix - when descriptors exist, filter aggressively
            List<Item> matches = initialMatches;

            if (userProvidedDescriptor || userProvidedMaterial)
            {
                var descriptorMatches = new List<Item>();
                foreach (var it in initialMatches)
                {
                    bool descMatch = userProvidedDescriptor && inputDescriptorTokens.All(tok => candidateDescriptorTokens[it].Contains(tok));
                    bool matMatch = userProvidedMaterial && inputMaterialTokens.All(tok => candidateMaterialTokens[it].Contains(tok));

                    // Must match ALL descriptor tokens AND all material tokens provided
                    if (userProvidedDescriptor && userProvidedMaterial)
                    {
                        if (descMatch && matMatch)
                            descriptorMatches.Add(it);
                    }
                    else if (userProvidedDescriptor)
                    {
                        if (descMatch)
                            descriptorMatches.Add(it);
                    }
                    else if (userProvidedMaterial)
                    {
                        if (matMatch)
                            descriptorMatches.Add(it);
                    }
                }

                if (descriptorMatches.Count > 0)
                    matches = descriptorMatches;
                else
                {
                    // User provided descriptor/material but no matches - be explicit
                    res.Reason = $"No items matched descriptor/material '{input}'.";
                    return res;
                }
            }

            // Phase 3: If multiple items remain and user ONLY provided name tokens (no descriptor/material)
            // Check if the name tokens match multiple candidates with DIFFERENT descriptors/materials
            // If so, require disambiguation
            if (matches.Count > 1 && userProvidedNameOnly)
            {
                // Check if these matches have different descriptors or materials
                var uniqueDescriptors = matches.Select(m => candidateDescriptorTokens[m].Count > 0
                    ? string.Join("", candidateDescriptorTokens[m].OrderBy(s => s))
                    : "").Distinct().ToList();

                var uniqueMaterials = matches.Select(m => candidateMaterialTokens[m].Count > 0
                    ? string.Join("", candidateMaterialTokens[m].OrderBy(s => s))
                    : "").Distinct().ToList();

                // If there are different descriptors or materials, require user to disambiguate
                if (uniqueDescriptors.Count > 1 || uniqueMaterials.Count > 1)
                {
                    res.Reason = $"Multiple items match '{input}'. Please specify descriptor or material (e.g., 'embroidered bag', 'stained bag', 'oak bowl').";
                    res.Candidates.AddRange(matches);
                    return res;
                }
            }

            // If exactly one match, return it
            if (matches.Count == 1)
            {
                res.Winner = matches[0];
                res.Score = 1000;
                res.Reason = userProvidedDescriptor || userProvidedMaterial
                    ? "Descriptor/material match"
                    : "Single match";
                res.Candidates.AddRange(candidates.Where(c => c != res.Winner));
                return res;
            }

            // Phase 4: Multiple matches remain after descriptor/material filtering
            // Use location-based tiebreaker (room > equipped > inventory)
            if (player != null)
            {
                var roomCandidates = matches.Where(it => !IsInInventory(player, it) && !IsEquipped(player, it)).ToList();
                if (roomCandidates.Count == 1)
                {
                    res.Winner = roomCandidates[0];
                    res.Reason = "Preferred room item with descriptor/material match";
                    return res;
                }
                if (roomCandidates.Count > 1)
                {
                    // Still ambiguous - return first stable room item
                    res.Winner = roomCandidates.First();
                    res.Reason = "Multiple room matches (stable order)";
                    return res;
                }

                var equippedMatch = matches.FirstOrDefault(it => IsEquipped(player, it));
                if (equippedMatch != null)
                {
                    res.Winner = equippedMatch;
                    res.Reason = "Preferred equipped item";
                    return res;
                }

                var invMatch = matches.FirstOrDefault(it => IsInInventory(player, it));
                if (invMatch != null)
                {
                    if (matches.Count > 1)
                    {
                        res.Reason = $"Ambiguous: {matches.Count} candidates match '{input}' (use more specific descriptor/material).";
                        res.Candidates.AddRange(matches);
                        return res;
                    }

                    res.Winner = invMatch;
                    res.Reason = "Inventory item";
                    return res;
                }
            }

            // Last resort: deterministic stable ordering by SafeName
            var fallback = matches.OrderBy(it => SafeName(it)).First();
            res.Winner = fallback;
            res.Reason = "Fallback (stable name order)";
            return res;
        } // end ResolveCandidates

        private static IEnumerable<Item> EnumerateCandidates(Player player, ItemSearchScope scope, ItemParserOptions opts)
        {
            if (player == null) yield break;
            var room = player.CurrentRoom;

            switch (scope)
            {
                case ItemSearchScope.RoomItems:
                    if (room?.Inventory != null)
                    {
                        foreach (var it in room.Inventory)
                        {
                            if (it == null) continue;
                            // By default exclude buried items (flags starting with "Buried"), unless caller opted in.
                            try
                            {
                                if (!opts.IncludeBuried && it.Flags != null && it.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase)))
                                    continue;
                            }
                            catch { /* best-effort */ }

                            yield return it;
                        }
                    }
                    break;
                case ItemSearchScope.PlayerInventory:
                    if (player.Inventory != null) foreach (var it in player.Inventory) if (it != null) yield return it;
                    break;
                case ItemSearchScope.Equipped:
                    foreach (var it in EnumerateEquipped(player)) if (it != null) yield return it;
                    break;
                case ItemSearchScope.Any:
                    if (room?.Inventory != null)
                    {
                        foreach (var it in room.Inventory)
                        {
                            if (it == null) continue;
                            try
                            {
                                if (!opts.IncludeBuried && it.Flags != null && it.Flags.Any(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Buried", StringComparison.OrdinalIgnoreCase)))
                                    continue;
                            }
                            catch { /* best-effort */ }
                            yield return it;
                        }
                    }
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

            // Descriptor and Material support
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

        private static Dictionary<string, int> BuildItemTokenWeights(Item it, ItemParserOptions opts)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void AddTokens(string? s, int weight)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                foreach (var t in Tokenize(s))
                {
                    if (!map.ContainsKey(t)) map[t] = weight;
                    else map[t] = Math.Max(map[t], weight);
                }
            }

            AddTokens(it?.Name, 10);
            AddTokens(it?.ShortDesc, 5);

            if (opts.IncludeCategoryTokens && !string.IsNullOrWhiteSpace(it?.ItemCategory))
                AddTokens(it!.ItemCategory, 3);

            if (opts.IncludeVisualTraits)
            {
                if (it?.Flags != null)
                {
                    foreach (var f in it.Flags) AddTokens(f, 1);
                }
                if (!string.IsNullOrEmpty(it?.FlagsJson)) AddTokens(it.FlagsJson, 1);
            }

            var skipCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Component", "Organic", "Food", "Currency" };
            bool skipDescriptor = string.IsNullOrWhiteSpace(it?.Descriptor) || (!opts.IncludeDescriptorTokens) || (!string.IsNullOrWhiteSpace(it.ItemCategory) && skipCats.Contains(it.ItemCategory));
            bool skipMaterial = string.IsNullOrWhiteSpace(it?.Material) || (!opts.IncludeMaterialTokens) || (!string.IsNullOrWhiteSpace(it.ItemCategory) && skipCats.Contains(it.ItemCategory));

            if (!skipDescriptor) AddTokens(it!.Descriptor, 8);
            if (!skipMaterial) AddTokens(it!.Material, 7);

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
            return HasFlag(it, "Worn");
        }

        private static bool IsHeld(Item it)
        {
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