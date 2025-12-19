using MudCore.Entities;
using MudCore.Templates;
using System;

namespace MudCore.World
{
    public class NpcFactory
    {
        private readonly NpcTemplateManager _templateManager;

        public NpcFactory(NpcTemplateManager templateManager)
        {
            _templateManager = templateManager;
        }

        public Npc CreateNpc(string templateId, Room room)
        {
            if (!_templateManager.TryGet(templateId, out var template))
                throw new KeyNotFoundException($"NPC template '{templateId}' not found.");

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

            // Select visual trait. Prefer category-aware selection when available.
            if (VisualTraitManager.HasTraits && Random.Shared.NextDouble() < 0.9)
            {
                // If template marks the NPC as humanoid, request a humanoid trait; otherwise request creature trait.
                // VisualTraitManager falls back to the combined pool if the category is missing or empty.
                if (template.IsHumanoid)
                {
                    npc.VisualTrait = VisualTraitManager.GetRandomTrait("humanoid", Random.Shared) ?? VisualTraitManager.GetRandomTrait(Random.Shared);
                }
                else
                {
                    npc.VisualTrait = VisualTraitManager.GetRandomTrait("creature", Random.Shared) ?? VisualTraitManager.GetRandomTrait(Random.Shared);
                }
            }

            npc.ApplyTemplateDefaults();
            MudServer.Combat.InitializeSizeDefaults(npc);
            MudServer.Combat.UpdateEffectiveStats(npc);

            // Ensure NPC inherits area from the room so it can wander within its own area.
            npc.CurrentAreaID = room.AreaID;
            // If template does not specify WanderAreas, default to the NPC's current area.
            if (npc.WanderAreas == null || npc.WanderAreas.Count == 0)
            {
                //DEPRECATED// WanderAreas empty at creation; default to room.AreaID for safe local wandering.
                npc.WanderAreas = new System.Collections.Generic.List<int> { room.AreaID };
            }

            room.Npcs.Add(npc);
            return npc;
        }
    }
}
