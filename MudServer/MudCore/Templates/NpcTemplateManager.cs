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
        private readonly Dictionary<string, NpcTemplate> _byId = new(StringComparer.OrdinalIgnoreCase);

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

                    _byId[tmpl.Id] = tmpl;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcTemplateManager] Failed loading {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        // Register a single template
        public void Register(NpcTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (string.IsNullOrWhiteSpace(template.Id)) throw new ArgumentException("Template.Id is required.", nameof(template));
            _byId[template.Id] = template;
        }

        // Register a set of templates
        public void RegisterRange(IEnumerable<NpcTemplate> templates)
        {
            if (templates == null) return;
            foreach (var t in templates)
            {
                if (t != null && !string.IsNullOrWhiteSpace(t.Id))
                    _byId[t.Id] = t;
            }
        }

        // Lookup
        public bool TryGet(string id, out NpcTemplate template)
        {
            template = null!;
            if (string.IsNullOrWhiteSpace(id)) return false;
            return _byId.TryGetValue(id, out template);
        }

        // Diagnostics
        public int Count => _byId.Count;

        // Optional: clear all
        public void Clear() => _byId.Clear();
    }
}