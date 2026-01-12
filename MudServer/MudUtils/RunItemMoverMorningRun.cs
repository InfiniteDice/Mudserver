
using System;
using Microsoft.Data.Sqlite;
using MudCore.World;
using MudCore.Entities;
using MudCore.AI;

namespace MudServer.Utils
{
    public static class RunItemMoverMorningRun
    {
        // Call from Program.cs after world load, or invoke from a short test harness.
        public static void Run(World world, SqliteConnection db, int moverNpcId)
        {
            if (world == null || db == null) throw new ArgumentNullException(nameof(world));
            var mover = world.Npcs?.Find(n => n != null && n.NpcID == moverNpcId);
            if (mover == null) { Console.WriteLine($"[DRYRUN] Mover NpcID={moverNpcId} not found."); return; }

            // Force the game clock into Morning to run the Morning behavior (restore afterwards).
            var prev = world.GameTime;
            try
            {
                world.GameTime = new GameClock(prev.Year, prev.Month, prev.Day, 9, 0, 0); // 9 = Morning
                Console.WriteLine($"[DRYRUN] Forcing Hour={world.GameTime.Hour} ({world.GameTime.GetPeriodDescriptor()}) and running ItemMover for NpcID={moverNpcId}");
                ItemMoverManager.ProcessItemMoverForNpc(mover, world, db);
            }
            finally
            {
                world.GameTime = prev;
            }
        }
    }
}