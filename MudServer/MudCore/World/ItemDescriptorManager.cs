using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudCore.Entities;

namespace MudCore.World
{
    public static class ItemDescriptorManager
    {
        private static readonly object _loadLock = new();
        private static Dictionary<string, List<string>> _groups = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded = false;
        private static readonly Random _rng = new();

        /// <summary>
        /// Ensure descriptors are loaded (lazy).
        /// Looks for Data/config/item_descriptors.json under AppContext.BaseDirectory or project tree.
        /// </summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    var candidate = Path.Combine(baseDir, "Data", "config", "item_descriptors.json");
                    string? json = null;

                    if (File.Exists(candidate))
                    {
                        json = File.ReadAllText(candidate);
                    }
                    else
                    {
                        // try to find project folder (development layout)
                        var dir = new DirectoryInfo(baseDir);
                        DirectoryInfo? projectDir = null;
                        while (dir != null && dir.Exists)
                        {
                            if (dir.GetFiles("*.csproj").Any())
                            {
                                projectDir = dir;
                                break;
                            }
                            dir = dir.Parent;
                        }

                        if (projectDir != null)
                        {
                            var alt = Path.Combine(projectDir.FullName, "Data", "config", "item_descriptors.json");
                            if (File.Exists(alt)) json = File.ReadAllText(alt);
                        }
                    }

                    if (!string.IsNullOrEmpty(json))
                    {
                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var dict = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, opts);
                        if (dict != null)
                        {
                            _groups = dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ItemDescriptorManager] Failed to load descriptors: {ex.Message}");
                    _groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }
                finally
                {
                    _loaded = true;
                }
            }
        }

        /// <summary>
        /// Public initializer to be called at startup to preload descriptors and surface load errors early.
        /// Returns true when the descriptors are loaded (or attempted) and false only on unexpected exception.
        /// Safe to call multiple times.
        /// </summary>
        public static bool Initialize()
        {
            try
            {
                EnsureLoaded();
                if (_groups == null || _groups.Count == 0)
                {
                    // No descriptors loaded is not fatal; log for visibility.
                    Console.WriteLine("[ItemDescriptorManager] Initialize completed: no descriptor groups found (file may be missing).");
                }
                else
                {
                    Console.WriteLine($"[ItemDescriptorManager] Initialize completed: loaded {_groups.Count} descriptor groups.");
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemDescriptorManager] Initialize failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get a random descriptor string from the named group. Returns empty string when none available.
        /// </summary>
        public static string GetRandomDescriptor(string group)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(group)) return string.Empty;
            if (!_groups.TryGetValue(group, out var list) || list == null || list.Count == 0) return string.Empty;
            lock (_rng)
            {
                return list[_rng.Next(list.Count)];
            }
        }

        /// <summary>
        /// Choose a best-fit group for the given item (based on Material, ItemCategory heuristics).
        /// Returns group name or null when none matched.
        /// </summary>
        public static string? GetGroupForItem(Item item)
        {
            EnsureLoaded();
            if (item == null) return null;

            // 1) Prefer explicit Material names that map to a known group (e.g., "Oak" -> Wood, "Iron" -> Metal)
            try
            {
                var mat = (item.Material ?? "").Trim();
                if (!string.IsNullOrEmpty(mat))
                {
                    // simple heuristics: metals contain common metal names
                    var m = mat.ToLowerInvariant();
                    if (m.Contains("iron") || m.Contains("steel") || m.Contains("bronze") || m.Contains("copper") || m.Contains("gold") || m.Contains("silver") || m.Contains("brass") || m.Contains("nickel") || m.Contains("platinum"))
                        return _groups.ContainsKey("Metal") ? "Metal" : null;
                    if (m.Contains("oak") || m.Contains("ash") || m.Contains("maple") || m.Contains("walnut") || m.Contains("wood"))
                        return _groups.ContainsKey("Wood") ? "Wood" : null;
                    if (m.Contains("linen") || m.Contains("wool") || m.Contains("silk") || m.Contains("cloth"))
                        return _groups.ContainsKey("Cloth") ? "Cloth" : null;
                    if (m.Contains("stone") || m.Contains("marble") || m.Contains("granite") || m.Contains("limestone"))
                        return _groups.ContainsKey("Stone") ? "Stone" : null;
                }
            }
            catch { }

            // 2) ItemCategory heuristics
            try
            {
                var cat = (item.ItemCategory ?? "").ToLowerInvariant();
                if (!string.IsNullOrEmpty(cat))
                {
                    if (cat.Contains("armor") || cat.Contains("weapon") || cat.Contains("tool") || cat.Contains("component"))
                        return _groups.ContainsKey("Metal") ? "Metal" : _groups.Keys.FirstOrDefault();
                    if (cat.Contains("cloth") || cat.Contains("clothing") || cat.Contains("robe") || cat.Contains("garment"))
                        return _groups.ContainsKey("Cloth") ? "Cloth" : _groups.Keys.FirstOrDefault();
                    if (cat.Contains("container") || cat.Contains("chest") || cat.Contains("furniture") || cat.Contains("wood"))
                        return _groups.ContainsKey("Wood") ? "Wood" : _groups.Keys.FirstOrDefault();
                    if (cat.Contains("stone") || cat.Contains("gem") || cat.Contains("statue"))
                        return _groups.ContainsKey("Stone") ? "Stone" : _groups.Keys.FirstOrDefault();
                }
            }
            catch { }

            // 3) TemplateID/name heuristics (look for substrings)
            try
            {
                var name = (item.Name ?? item.TemplateID ?? "").ToLowerInvariant();
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.IndexOf("branch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("oak", StringComparison.OrdinalIgnoreCase) >= 0)
                        return _groups.ContainsKey("Wood") ? "Wood" : null;
                    if (name.IndexOf("stone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("marble", StringComparison.OrdinalIgnoreCase) >= 0)
                        return _groups.ContainsKey("Stone") ? "Stone" : null;
                    if (name.IndexOf("cloth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("robe", StringComparison.OrdinalIgnoreCase) >= 0)
                        return _groups.ContainsKey("Cloth") ? "Cloth" : null;
                    if (name.IndexOf("sword", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("dagger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0)
                        return _groups.ContainsKey("Metal") ? "Metal" : null;
                }
            }
            catch { }

            // 4) Fallback: pick any available group (so descriptor is assigned, even if generic)
            return _groups.Keys.FirstOrDefault();
        }

        /// <summary>
        /// Assign a random descriptor to the item if applicable and unset.
        /// Skips ItemTypes: Component, Organic, Food, Currency (by ItemCategory).
        /// Returns true when a descriptor was assigned (in-memory) and false otherwise.
        /// Note: this method does not persist the Item — callers should call ItemPersistence.SaveItem(...) as appropriate.
        /// </summary>
        public static bool AssignDescriptorIfApplicable(Item item)
        {
            if (item == null) return false;
            try
            {
                if (!string.IsNullOrEmpty(item.Descriptor)) return false;

                var skipCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Component", "Organic", "Food", "Currency" };
                if (!string.IsNullOrEmpty(item.ItemCategory) && skipCats.Contains(item.ItemCategory)) return false;

                var group = GetGroupForItem(item);
                if (string.IsNullOrEmpty(group)) return false;

                var desc = GetRandomDescriptor(group);
                if (string.IsNullOrEmpty(desc)) return false;

                item.Descriptor = desc;
                return true;
            }
            catch
            {
                return false;
            }
        }
    } // end ItemDescriptorManager
}