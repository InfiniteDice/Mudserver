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

            if (VisualTraitManager.HasTraits && Random.Shared.NextDouble() < 0.9)
                npc.VisualTrait = VisualTraitManager.GetRandomTrait(Random.Shared);

            npc.ApplyTemplateDefaults();
            MudServer.Combat.InitializeSizeDefaults(npc);
            MudServer.Combat.UpdateEffectiveStats(npc);

            npc.CurrentAreaID = room.AreaID; // Set CurrentAreaID after NPC is created and assigned a room

            room.Npcs.Add(npc);
            return npc;
        }
    }
}
