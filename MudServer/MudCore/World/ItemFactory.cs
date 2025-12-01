using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudCore.World
{
    public class ItemTemplate
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        // Map JSON field "Category" to this property (DB uses ItemCategory on Item).
        [JsonPropertyName("Category")]
        public string ItemCategory { get; set; } = "";

        public double Weight { get; set; } = 0.0;
        public int Value { get; set; } = 0;
        public string ShortDesc { get; set; } = "";
        public string LongDesc { get; set; } = "";
        public string WearLoc { get; set; } = "";
        public int HandLoc { get; set; } = 1;
        public List<string> Flags { get; set; } = new List<string>();
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

            foreach (var file in Directory.GetFiles(folderToUse, "*.json"))
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
        }

        public ItemTemplate Get(string id) => Templates[id];
    }

    public class ItemFactory
    {
        private readonly ItemTemplateManager _templateManager;

        public ItemFactory(ItemTemplateManager templateManager)
        {
            _templateManager = templateManager;
        }

        public Item CreateItem(string templateId, string ownerType, int ownerId, int roomId = 0)
        {
            var template = _templateManager.Get(templateId);

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
                HandLoc = template.HandLoc,
                Flags = template.Flags,
                FlagsJson = JsonSerializer.Serialize(template.Flags),
                OwnerType = ownerType,
                OwnerID = ownerId,
                RoomID = roomId,
                Condition = 100 // fully intact for freshly created items
            };

            return item;
        }
    }
}
