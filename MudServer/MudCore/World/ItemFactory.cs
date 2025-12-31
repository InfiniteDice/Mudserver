using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace MudCore.World
{
    public class ItemTemplate
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        //NEVER DELETE What type of item is this? Weapon, Armor, Tool, Food, etc.
        [JsonPropertyName("ItemCategory")]
        public string ItemCategory { get; set; } = "";

        public double Weight { get; set; } = 0.0;
        public int Value { get; set; } = 0;
        public string ShortDesc { get; set; } = "";
        public string LongDesc { get; set; } = "";
        public string WearLoc { get; set; } = "";
        public int HandLoc { get; set; } = 1;
        public List<string> Flags { get; set; } = new List<string>();

        // Optional stackable marker in templates; accept either a boolean or string in JSON.
        // Many existing templates may use Flags with "Stackable" so we check both.
        [JsonPropertyName("Stackable")]
        public string? Stackable { get; set; } = null;
        public string? Material { get; set; } = null;

        // Example new properties
        public int Condition { get; set; } = 100; // default condition
        public double BaseACBonus { get; set; } = 0.0; // default AC bonus

        // NEW: per-template damage dice multiplier (maps to Item.DmgDiceMult)
        public double DmgDiceMult { get; set; } = 1.0;
    }

    public class ItemTemplateManager
    {
        public Dictionary<string, ItemTemplate> Templates { get; private set; } = new();

        public void LoadTemplates(string relativeFolder)
        {
            // First try the output directory (where the app runs)
            var baseDir = AppContext.BaseDirectory;
            var outputDir = Path.Combine(baseDir, relativeFolder);

            string folderToUse = null;

            if (Directory.Exists(outputDir))
            {
                folderToUse = outputDir;
            }
            else
            {
                // Walk upward to find the project directory (with .csproj)
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
                    var tryPath = Path.Combine(projectDir.FullName, relativeFolder);
                    if (Directory.Exists(tryPath))
                        folderToUse = tryPath;
                }
            }

            if (folderToUse == null)
                throw new DirectoryNotFoundException($"ItemTemplates folder not found: {outputDir}");

            Console.WriteLine($"[ITEM] Loading item templates from: {folderToUse}");

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            // Search subdirectories as well so templates may be organized into folders.
            foreach (var file in Directory.GetFiles(folderToUse, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var tmpl = JsonSerializer.Deserialize<ItemTemplate>(json, options);

                    if (tmpl != null && !string.IsNullOrWhiteSpace(tmpl.Id))
                    {
                        Templates[tmpl.Id] = tmpl;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load item template from {file}: {ex.Message}");
                }
            }
        } // end LoadTemplates

        public ItemTemplate Get(string id) => Templates[id];
    }

    public class ItemFactory
    {
        private readonly ItemTemplateManager _templateManager;

        public ItemFactory(ItemTemplateManager templateManager)
        {
            _templateManager = templateManager;
        }

        // quantity: optional number of units to create for stackable templates (defaults to 1)
        public Item CreateItem(string templateId, string ownerType, int ownerId, int roomId = 0, int quantity = 1)
        {
            var template = _templateManager.Get(templateId);

            // Start with a copy of template flags (runtime list)
            var flags = template.Flags != null ? new List<string>(template.Flags) : new List<string>();

            // Convert any StartsBuriedN -> BuriedN at creation time so spawned items persist correctly.
            try
            {
                var original = flags.ToList();
                foreach (var f in original)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    if (f.StartsWith("StartsBuried", StringComparison.OrdinalIgnoreCase))
                    {
                        var num = f.Substring("StartsBuried".Length).Trim();
                        if (int.TryParse(num, out var pct))
                        {
                            try { flags.Remove(f); } catch { }
                            var buriedFlag = $"Buried{pct}";
                            if (!flags.Any(ff => string.Equals(ff, buriedFlag, StringComparison.OrdinalIgnoreCase)))
                                flags.Add(buriedFlag);
                        }
                    }
                }
            }
            catch { /* best-effort conversion; do not fail creation */ }

            // Determine if template is stackable from template.Stackable or Flags marker.
            bool templateStackable = false;
            if (!string.IsNullOrWhiteSpace(template.Stackable))
            {
                // template.Stackable may be "true"/"false" (string) or "True" etc.
                if (bool.TryParse(template.Stackable, out var parsed))
                    templateStackable = parsed;
                else
                    templateStackable = string.Equals(template.Stackable, "true", StringComparison.OrdinalIgnoreCase);
            }

            // Also allow explicit Flags marker "Stackable"
            if (!templateStackable && flags != null && flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase)))
                templateStackable = true;

            // Robust fix: when a template declares Stackable (via property or flags),
            // ensure the runtime Flags list contains the "Stackable" token so persistence
            // merge logic (which inspects FlagsJson) sees it.
            if (templateStackable)
            {
                if (!flags.Any(f => string.Equals(f, "Stackable", StringComparison.OrdinalIgnoreCase)))
                    flags.Add("Stackable");
            }

            var item = new Item
            {
                ItemID = 0, // DB will assign autoincrement
                TemplateID = template.Id,
                Name = template.Name,
                ItemCategory = template.ItemCategory,
                Weight = template.Weight,
                Value = template.Value,
                ShortDesc = template.ShortDesc,
                LongDesc = template.LongDesc,
                WearLoc = template.WearLoc,
                BaseACBonus = template.BaseACBonus,
                HandLoc = template.HandLoc,
                Flags = flags,
                FlagsJson = JsonSerializer.Serialize(flags),
                OwnerType = ownerType,
                OwnerID = ownerId,
                RoomID = roomId,
                Condition = 100, // fully intact for freshly created items
                                 // ensure template multiplier is applied to the new item
                DmgDiceMult = template.DmgDiceMult,
                Quantity = 1, // default; may be updated below for stackables
                              // Propagate material from template into the item instance (nullable)
                Material = template.Material
            };

            if (templateStackable)
                item.Quantity = Math.Max(1, quantity);
            else
                item.Quantity = 1; // non-stackables always created as single item

            // Assign descriptor for created items if applicable.
            // This is best-effort and does not persist the item; callers should SaveItem(...) afterwards.
            try
            {
                MudCore.World.ItemDescriptorManager.AssignDescriptorIfApplicable(item);
            }
            catch
            {
                // Non-fatal; descriptor assignment should not prevent item creation.
            }

            // Ensure FlagsJson is stable and sorted for deterministic DB merges
            try
            {
                var sorted = (item.Flags ?? new List<string>()).OrderBy(s => s ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                item.FlagsJson = JsonSerializer.Serialize(sorted);
            }
            catch { /* best-effort */ }

            return item;
        } // end ItemFactory.CreateItem

        // Helper: attempt to resolve friendly display name for a template id.
        // Returns true when a template was found and displayName set; otherwise returns false and displayName is the templateId fallback.
        public bool TryGetTemplateDisplayName(string templateId, out string displayName)
        {
            displayName = templateId ?? "";
            if (string.IsNullOrWhiteSpace(templateId)) return false;

            try
            {
                if (_templateManager != null)
                {
                    // The local ItemTemplateManager implementation exposes a public Templates dictionary.
                    // Use that dictionary directly since TryGet may not exist on this class.
                    var dict = _templateManager.Templates;
                    if (dict != null && dict.TryGetValue(templateId, out var tmpl))
                    {
                        if (!string.IsNullOrWhiteSpace(tmpl.Name))
                        {
                            displayName = tmpl.Name;
                            return true;
                        }

                        if (!string.IsNullOrWhiteSpace(tmpl.Id))
                        {
                            displayName = tmpl.Id;
                            return true;
                        }
                    }

                    // Fallback: try calling Get(...) which will throw if missing; capture and ignore.
                    try
                    {
                        var tmpl2 = _template_manager_get_fallback(templateId);
                        if (tmpl2 != null)
                        {
                            displayName = !string.IsNullOrWhiteSpace(tmpl2.Name) ? tmpl2.Name :
                                          (!string.IsNullOrWhiteSpace(tmpl2.Id) ? tmpl2.Id : templateId);
                            return true;
                        }
                    }
                    catch
                    {
                        // ignore fallback errors
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            return false;
        }

        // Small private helper to call _templateManager.Get without changing existing public API usage.
        private ItemTemplate? _template_manager_get_fallback(string templateId)
        {
            try
            {
                return _templateManager.Get(templateId);
            }
            catch
            {
                return null;
            }
        }

    }
}
