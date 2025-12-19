using System.Text.Json;

public static class VisualTraitManager
{
    private static readonly List<string> _traits = new();
    private static readonly Dictionary<string, List<string>> _categorizedTraits = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Traits => _traits;
    public static bool HasTraits => _traits.Count > 0;

    // Expose categorized view when the file uses an object shape.
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CategorizedTraits =>
        _categorizedTraits.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value, StringComparer.OrdinalIgnoreCase);

    // Call with the relative path to the JSON file (default matches your layout)
    public static void Load(string relativePath = "Data/config/visual_traits.json")
    {
        _traits.Clear();
        _categorizedTraits.Clear();

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

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Helper adds trait to combined list and to optional category
            void AddTrait(string trait, string? category = null)
            {
                if (string.IsNullOrWhiteSpace(trait)) return;
                var t = trait.Trim();
                if (!_traits.Contains(t, StringComparer.OrdinalIgnoreCase))
                    _traits.Add(t);

                if (!string.IsNullOrWhiteSpace(category))
                {
                    if (!_categorizedTraits.TryGetValue(category!, out var list))
                    {
                        list = new List<string>();
                        _categorizedTraits[category!] = list;
                    }

                    if (!list.Contains(t, StringComparer.OrdinalIgnoreCase))
                        list.Add(t);
                }
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                // legacy flat list
                var loaded = JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded == null) return;

                foreach (var t in loaded.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)))
                {
                    AddTrait(t!);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // new categorized shape: { "humanoid": [...], "creature": [...] }
                foreach (var prop in root.EnumerateObject())
                {
                    var key = prop.Name?.Trim() ?? "";
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in prop.Value.EnumerateArray())
                        {
                            if (elem.ValueKind == JsonValueKind.String)
                                AddTrait(elem.GetString() ?? "", key);
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        AddTrait(prop.Value.GetString() ?? "", key);
                    }
                    // ignore other kinds
                }

                // Also maintain a combined list order: append category lists in property order
                // (already added via AddTrait above)
            }
            else
            {
                // Unexpected JSON root - attempt best-effort parse as flat array
                var loaded = JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null)
                {
                    foreach (var t in loaded.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)))
                        AddTrait(t!);
                }
            }
        }
        catch
        {
            // swallow errors here — loader shouldn't prevent server start
            // consider logging during development if needed
        }
    }

    // Returns a random trait from the combined pool or null if none
    public static string? GetRandomTrait(Random? rng = null)
    {
        if (!HasTraits) return null;
        var r = rng ?? Random.Shared;
        return _traits[r.Next(_traits.Count)];
    }

    // Returns a random trait for a specific category (e.g. "humanoid" or "creature").
    // Falls back to combined pool if the category is not found or empty.
    public static string? GetRandomTrait(string category, Random? rng = null)
    {
        if (string.IsNullOrWhiteSpace(category)) return GetRandomTrait(rng);

        if (_categorizedTraits.TryGetValue(category.Trim(), out var list) && list.Count > 0)
        {
            var r = rng ?? Random.Shared;
            return list[r.Next(list.Count)];
        }

        return GetRandomTrait(rng);
    }
} // end VisualTraitManager