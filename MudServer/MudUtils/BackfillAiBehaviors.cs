
using System;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudPersistence;
using MudCore.Entities;
using System.Text.Json;

namespace MudServer.Utils
{
    public static class BackfillAiBehaviors
    {
        // Run once after world and DB are available.
        // Example usage in Program.cs (testing only):
        // BackfillAiBehaviors.Run(world, db);
        public static void Run(World world, SqliteConnection db)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (db == null) throw new ArgumentNullException(nameof(db));

            Console.WriteLine("[BackfillAiBehaviors] Starting backfill...");

            int count = 0;
            foreach (var npc in world.Npcs)
            {
                try
                {
                    if (npc == null) continue;

                    // Decide which behavior list to persist:
                    // - prefer an explicit per-NPC list if you add one later (npc.AiBehaviorConfigs)
                    // - otherwise use npc.Template?.AiBehaviorConfigs (current design)
                    var configs = npc.GetType().GetProperty("AiBehaviorConfigs")?.GetValue(npc) as System.Collections.Generic.List<NpcTemplate.AiBehaviorConfig>;
                    if (configs == null || configs.Count == 0)
                    {
                        configs = npc.Template?.AiBehaviorConfigs ?? new System.Collections.Generic.List<NpcTemplate.AiBehaviorConfig>();
                    }

                    // Skip if nothing to write
                    if (configs == null || configs.Count == 0) continue;

                    // Ensure DB column exists (defensive)
                    using var cmdCheck = db.CreateCommand();
                    cmdCheck.CommandText = "PRAGMA table_info('Npc');";
                    bool hasCol = false;
                    using (var rdr = cmdCheck.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            try
                            {
                                if (!rdr.IsDBNull(1) && string.Equals(rdr.GetString(1), "AiBehaviors", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasCol = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    if (!hasCol)
                    {
                        Console.WriteLine("[BackfillAiBehaviors] AiBehaviors column missing. Add it with: ALTER TABLE Npc ADD COLUMN AiBehaviors TEXT;");
                        return;
                    }

                    // Serialize configs
                    var json = JsonSerializer.Serialize(configs, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    // Use NpcPersistence.UpdateNpc to persist via writer queue path.
                    // To avoid mutating template, attach a temporary per-npc AiBehaviors property used by persistence.
                    // If NpcPersistence.UpdateNpc now picks up npc.Template?.AiBehaviorConfigs, set that clone temporarily:
                    if (npc.Template != null)
                    {
                        // clone template to avoid mutating global template manager
                        var tmplJson = JsonSerializer.Serialize(npc.Template);
                        var clone = JsonSerializer.Deserialize<NpcTemplate>(tmplJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (clone != null)
                        {
                            clone.AiBehaviorConfigs = configs;
                            npc.Template = clone;
                        }
                    }

                    // Persist (this will write AiBehaviors if DB supports it)
                    NpcPersistence.UpdateNpc(db, npc);

                    count++;
                    if (count % 20 == 0) Console.WriteLine($"[BackfillAiBehaviors] Persisted {count} NPCs...");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BackfillAiBehaviors] Failed for NpcID={npc?.NpcID}: {ex.Message}");
                }
            }

            Console.WriteLine($"[BackfillAiBehaviors] Done. Persisted {count} NPC(s).");
        }
    }
}