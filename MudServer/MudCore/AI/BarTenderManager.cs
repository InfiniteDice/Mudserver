using System;
using System.Runtime.CompilerServices;
using MudCore.Entities;

namespace MudCore.AI
{
    // Registry for BarTender instances. Mirrors ItemTraderManager pattern.
    public static class BarTenderManager
    {
        private static readonly ConditionalWeakTable<Npc, BarTender> _map = new ConditionalWeakTable<Npc, BarTender>();

        public static void Register(Npc npc, BarTender bt)
        {
            if (npc == null || bt == null) return;
            try
            {
                try { _map.Remove(npc); } catch { }
                _map.Add(npc, bt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BarTenderManager] Register failed for NpcID={npc?.NpcID}: {ex.Message}");
            }
        }

        public static bool TryGet(Npc npc, out BarTender bt)
        {
            bt = null!;
            if (npc == null) return false;
            try
            {
                return _map.TryGetValue(npc, out bt);
            }
            catch
            {
                bt = null!;
                return false;
            }
        }

        public static void Unregister(Npc npc)
        {
            if (npc == null) return;
            try { _map.Remove(npc); } catch { }
        }

        
    }
}