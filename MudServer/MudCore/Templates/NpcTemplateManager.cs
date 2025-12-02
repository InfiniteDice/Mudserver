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
        // Make dictionary case-insensitive so createnpc goblin / Goblin both work.
        public Dictionary<string, NpcTemplate> Templates { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        public void LoadTemplates(string relativeFolder)
        {
            var baseDir = AppContext.BaseDirectory;
            var outputDir = Path.Combine(baseDir, relativeFolder);

            string folderToUse = null;

            if (Directory.Exists(outputDir))
            {
                folderToUse = outputDir;
            }
            else
            {
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
                throw new DirectoryNotFoundException($"NpcTemplates folder not found: {outputDir}");

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
                    if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.Id))
                        continue;

                    Templates[tmpl.Id] = tmpl;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcTemplateManager] Failed loading {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        public NpcTemplate Get(string id) => Templates[id];

        // NEW: Safe lookup used by NpcFactory
        public bool TryGet(string id, out NpcTemplate template)
        {
            template = null!;
            if (string.IsNullOrWhiteSpace(id)) return false;
            return Templates.TryGetValue(id, out template);
        }
    }
}