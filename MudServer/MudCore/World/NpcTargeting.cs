using System;
using System.Collections.Generic;
using System.Linq;
using MudCore.Entities;

namespace MudCore.World
{
    public static class NpcTargeting
    {
        public static List<Npc> FindMatches(Room room, string raw)
        {
            var results = new List<Npc>();
            if (room == null || string.IsNullOrWhiteSpace(raw)) return results;

            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim()).ToList();

            foreach (var npc in room.Npcs)
            {
                if (npc.MatchesTokens(tokens))
                    results.Add(npc);
            }

            // If no multi-token matches, try single-token prefix fallback
            if (results.Count == 0 && tokens.Count == 1)
            {
                string t = tokens[0].ToLowerInvariant();
                results.AddRange(room.Npcs.Where(n => n.DisplayName.ToLowerInvariant().StartsWith(t)));
            }

            return results;
        }
    }
}