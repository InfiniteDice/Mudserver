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
            var template = _templateManager.Get(templateId);

            var npc = new Npc
            {
                // persistent fields
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
                IsPersistent = true, // createnpc will persist
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

            room.Npcs.Add(npc);
            return npc;
        }
    }
}
