using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MudCore.World
{
    public static class VisualTraitManager
    {
        private static readonly List<string> _traits = new();

        public static IReadOnlyList<string> Traits => _traits;
        public static bool HasTraits => _traits.Count > 0;

        // Call with the relative path to the JSON file (default matches your layout)
        public static void Load(string relativePath = "Data/config/visual_traits.json")
        {
            _traits.Clear();

            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, relativePath);

            string fileToUse = null;

            if (File.Exists(candidate))
            {
                fileToUse = candidate;
            }
            else
            {
                // walk upward to project dir (like other loaders)
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
                    var tryPath = Path.Combine(projectDir.FullName, relativePath);
                    if (File.Exists(tryPath))
                        fileToUse = tryPath;
                }
            }

            if (fileToUse == null)
            {
                // no traits file found — leave empty silently (safe default)
                return;
            }

            try
            {
                var json = File.ReadAllText(fileToUse);
                var loaded = JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (loaded == null) return;

                // Normalize: trim, remove empties and duplicates, preserve order
                foreach (var t in loaded.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)))
                {
                    if (!_traits.Contains(t, StringComparer.OrdinalIgnoreCase))
                        _traits.Add(t);
                }
            }
            catch
            {
                // swallow errors here — loader shouldn't prevent server start
                // consider logging during development: Console.WriteLine($"[VisualTrait] Failed to load: {ex.Message}");
            }
        }

        // Returns a random trait or null if none
        public static string? GetRandomTrait(Random? rng = null)
        {
            if (!HasTraits) return null;
            var r = rng ?? Random.Shared;
            return _traits[r.Next(_traits.Count)];
        }
    }
}