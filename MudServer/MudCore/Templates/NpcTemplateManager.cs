using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudCore.Templates
{
    public class NpcTemplateManager
    {
        public Dictionary<string, NpcTemplate> Templates { get; private set; } = new();

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
                // 2) Find the project directory by walking upward until we find the .csproj file
                var dir = new DirectoryInfo(baseDir);
                DirectoryInfo? projectDir = null;

                while (dir != null && dir.Exists)
                {
                    var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
                    if (csproj != null)
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
                    $"NpcTemplates folder not found. Checked:\n" +
                    $"1) {outputDir}\n" +
                    $"2) (search for csproj and then relative folder)"
                );

            // Load templates from the selected directory
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
                    var tmpl = JsonSerializer.Deserialize<NpcTemplate>(json, options);

                    if (tmpl == null)
                    {
                        Console.WriteLine($"[NpcTemplateManager] Skipping {file}: deserialized template is null.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(tmpl.Id))
                    {
                        Console.WriteLine($"[NpcTemplateManager] Skipping {file}: template Id is missing or empty.");
                        continue;
                    }

                    // Add/overwrite template (log duplicate)
                    if (Templates.ContainsKey(tmpl.Id))
                        Console.WriteLine($"[NpcTemplateManager] Overwriting template '{tmpl.Id}' from file {file}.");

                    Templates[tmpl.Id] = tmpl;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcTemplateManager] Failed to load template from {file}: {ex.Message}");
                }
            }
        }

        public NpcTemplate Get(string id)
        {
            return Templates[id];
        }
    }
}