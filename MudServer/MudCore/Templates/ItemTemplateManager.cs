using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudCore.Templates
{
    public class ItemTemplateManager
    {
        public Dictionary<string, ItemTemplate> Templates { get; private set; } = new();

        public void LoadTemplates(string relativeFolder)
        {
            // 1) Try output directory first
            var baseDir = AppContext.BaseDirectory;
            var outputDir = Path.Combine(baseDir, relativeFolder);

            string folderToUse = null;

            if (Directory.Exists(outputDir))
            {
                folderToUse = outputDir;
            }
            else
            {
                // 2) Walk upward to find project directory (with .csproj)
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
                throw new DirectoryNotFoundException(
                    $"ItemTemplates folder not found. Checked:\n" +
                    $"1) {outputDir}\n" +
                    $"2) (search for csproj and then relative folder)"
                );

            // Load JSON templates
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            // NOTE: changed to search subdirectories as well so templates may be organized into folders.
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

        public ItemTemplate Get(string id)
        {
            if (!Templates.ContainsKey(id))
                throw new KeyNotFoundException($"Item template not found: {id}");

            return Templates[id];
        }

        public bool TryGet(string id, out ItemTemplate template)
        {
            return Templates.TryGetValue(id, out template);
        }
    }
}
