using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MudCore.Entities;

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

            // Search JSON files in the chosen folder and all subfolders so templates can be organized into subdirectories.
            foreach (var file in Directory.GetFiles(folderToUse, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = File.ReadAllText(file);

                    // Deserialize into the current NpcTemplate shape (legacy AiBehaviors as strings preserved)
                    var tmpl = JsonSerializer.Deserialize<NpcTemplate>(json, options);
                    if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.Id))
                        continue;

                    // Post-process: if JSON contains AiBehaviors as an array of objects, deserialize into AiBehaviorConfig list.
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("AiBehaviors", out var abNode) && abNode.ValueKind == JsonValueKind.Array)
                        {
                            // Detect whether elements are objects (structured) or strings (legacy).
                            bool elementsAreObjects = abNode.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object);
                            if (elementsAreObjects)
                            {
                                try
                                {
                                    var configs = JsonSerializer.Deserialize<List<NpcTemplate.AiBehaviorConfig>>(abNode.GetRawText(), options);
                                    if (configs != null)
                                        tmpl.AiBehaviorConfigs = configs;
                                }
                                catch (Exception exc)
                                {
                                    Console.WriteLine($"[NpcTemplateManager] Failed to parse structured AiBehaviors in {Path.GetFileName(file)}: {exc.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception exDoc)
                    {
                        // best-effort: do not fail template load if post-parse fails
                        Console.WriteLine($"[NpcTemplateManager] Post-parse AiBehaviors check failed for {Path.GetFileName(file)}: {exDoc.Message}");
                    }

                    _byId[tmpl.Id] = tmpl;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcTemplateManager] Failed loading {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }//End LoadTemplates

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