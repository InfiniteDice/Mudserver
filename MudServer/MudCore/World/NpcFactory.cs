using MudCore.Entities;
using MudCore.Templates;
using System;

namespace MudCore.World
{
    public class NpcFactory
    {
        private readonly NpcTemplateManager _templateManager;
        private readonly World _world;

        public NpcFactory(NpcTemplateManager templateManager, World world)
        {
            _templateManager = templateManager;
            _world = world;
        }

        public Npc CreateNpc(string templateId, Room room)
        {
            if (!_templateManager.TryGet(templateId, out var template))
                throw new KeyNotFoundException($"NPC template '{templateId}' not found.");

            // Diagnostic: when investigating missing persisted fields, log a compact snapshot for the 'cow' template.
            // This is intentionally narrow to avoid spamming logs in normal runs.
            bool diagCow = false;
            if (diagCow)
            {
                try
                {
                    // Print high-level template info (do not serialize entire object to avoid noise)
                    Console.WriteLine($"[NpcFactory][DIAG] Loading template '{templateId}': Name='{template.Name}', AiBehaviorConfigs={(template.AiBehaviorConfigs?.Count ?? 0)}, AiBehaviors={(template.AiBehaviors?.Count ?? 0)}, Flags={(template.Flags != null ? string.Join(",", template.Flags) : "(none)")}");
                }
                catch { /* best-effort */ }
            }

            var npc = new Npc
            {
                TemplateID = template.Id,
                Name = template.Name,
                Alignment = template.Alignment,
                HealthMax = template.MaxHealth,
                Health = template.Health > 0 ? template.Health : template.MaxHealth,
                Description = template.Description,
                IdleActions = new System.Collections.Generic.List<string>(template.IdleActions ?? new System.Collections.Generic.List<string>()),
                WanderChance = template.WanderChance,
                CurrentRoom = room,
                CurrentRoomID = room?.RoomID ?? 0,
                IsPersistent = true,
                Template = template,
                LastAction = "stands here",
                Level = template.Level,
                Mass = template.Mass,
                EffMass = template.EffMass,
                SizeEnum = template.SizeEnum,
                BaseDmgDice = template.BaseDmgDice,
                BaseArmorClass = template.BaseArmorClass,
                EffArmorClass = template.EffArmorClass,
                IsHumanoid = template.IsHumanoid,
                CombatSkill = template.CombatSkill,
                EffCombatSkill = template.EffCombatSkill
            };

            // Explicitly inherit hunger when the template provides it (including zero).
            // This gives a single-time assignment on create. ApplyTemplateDefaults() also guards assignment to avoid overwriting DB-loaded values.
            try
            {
                if (template.Hunger.HasValue)
                    npc.Hunger = template.Hunger.Value;
            }
            catch { /* best-effort */ }

            // Pre-apply diagnostics for cow: inspect AiBehaviorConfigs / AiBehaviors prior to ApplyTemplateDefaults
            if (diagCow)
            {
                try
                {
                    Console.WriteLine($"[NpcFactory][DIAG] BEFORE ApplyTemplateDefaults: Template.AiBehaviorConfigs.Count={(template.AiBehaviorConfigs?.Count ?? 0)} Template.AiBehaviors.Count={(template.AiBehaviors?.Count ?? 0)}");
                    if (template.AiBehaviorConfigs != null && template.AiBehaviorConfigs.Count > 0)
                    {
                        try
                        {
                            string sj = System.Text.Json.JsonSerializer.Serialize(template.AiBehaviorConfigs);
                            Console.WriteLine($"[NpcFactory][DIAG] Template.AiBehaviorConfigs JSON: {sj}");
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Behavior:
            //  - If the template declares a VisualTraitType and the visual_traits.json contains that category,
            //    choose one random trait from that category.
            //  - If VisualTraitType is missing or the category is not present, assign no trait (null / empty).
            if (VisualTraitManager.HasTraits && !string.IsNullOrWhiteSpace(template.VisualTraitType) && Random.Shared.NextDouble() < 0.9)
            {
                try
                {
                    // VisualTraitManager.GetRandomTrait(category) returns null when category missing/empty.
                    npc.VisualTrait = VisualTraitManager.GetRandomTrait(template.VisualTraitType.Trim(), Random.Shared);
                }
                catch
                {
                    // best-effort: if something goes wrong, leave VisualTrait empty.
                    npc.VisualTrait = null;
                }
            }

            npc.ApplyTemplateDefaults();

            // Post-apply diagnostics for cow: inspect instance fields after template defaults applied.
            if (diagCow)
            {
                try
                {
                    Console.WriteLine($"[NpcFactory][DIAG] AFTER ApplyTemplateDefaults: npc.AiBehaviors.Count={(npc.AiBehaviors?.Count ?? 0)} npc.Template.AiBehaviorConfigs.Count={(npc.Template?.AiBehaviorConfigs?.Count ?? 0)}");
                    try { Console.WriteLine($"[NpcFactory][DIAG] npc.Flags (runtime): {System.Text.Json.JsonSerializer.Serialize(npc.Flags)}"); } catch { }
                    try { Console.WriteLine($"[NpcFactory][DIAG] npc.WanderAreas (runtime): {System.Text.Json.JsonSerializer.Serialize(npc.WanderAreas)}"); } catch { }
                }
                catch { }
            }

            MudServer.Combat.InitializeSizeDefaults(npc);
            MudServer.Combat.UpdateEffectiveStats(npc);

            // Ensure NPC inherits area from the room so it can wander within its own area.
            npc.CurrentAreaID = room.AreaID;
            if (npc.WanderAreas == null || npc.WanderAreas.Count == 0)
            {
                npc.WanderAreas = new System.Collections.Generic.List<int> { room.AreaID };
            }

            // Register ItemTrader if configured on this NPC template
            try
            {
                var trader = MudServer.MudCore.AI.ItemTrader.CreateIfConfigured(npc, _world);
                if (trader != null)
                {
                    MudServer.MudCore.AI.ItemTraderManager.Register(npc, trader);
                }
            }
            catch (Exception exReg)
            {
                Console.WriteLine($"[NpcFactory] Failed to create/register ItemTrader for NPC '{npc.Name}': {exReg.Message}");
            }

            room.Npcs.Add(npc);
            return npc;
        }
    }
}
