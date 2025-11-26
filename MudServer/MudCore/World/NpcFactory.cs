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
                Id = Guid.NewGuid().ToString(),
                Template = template,
                Health = template.MaxHealth,
                CurrentRoom = room,
                LastAction = "stands here"
            };

            room.Npcs.Add(npc);
            return npc;
        }
    }
}
