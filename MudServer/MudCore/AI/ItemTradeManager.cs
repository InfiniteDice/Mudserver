using System;
using System.Runtime.CompilerServices;
using MudCore.Entities;

namespace MudCore.AI
{
    // Top-level registry for ItemTrader instances.
    // Uses ConditionalWeakTable so entries do not prevent GC of Npc objects.
    public static class ItemTraderManager
    {
        private static readonly ConditionalWeakTable<Npc, ItemTrader> _map = new ConditionalWeakTable<Npc, ItemTrader>();

        public static void Register(Npc npc, ItemTrader trader)
        {
            if (npc == null || trader == null) return;
            try
            {
                try { _map.Remove(npc); } catch { }
                _map.Add(npc, trader);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ItemTraderManager] Register failed for NpcID={npc?.NpcID}: {ex.Message}");
            }
        }

        public static bool TryGet(Npc npc, out ItemTrader trader)
        {
            trader = null!;
            if (npc == null) return false;
            try
            {
                return _map.TryGetValue(npc, out trader);
            }
            catch
            {
                trader = null!;
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