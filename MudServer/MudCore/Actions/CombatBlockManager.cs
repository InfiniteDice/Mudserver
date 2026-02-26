using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MudCore.Actions
{
    public static class CombatBlockManager
    {
        /// <summary>
        /// Handle block command during combat.
        /// Syntax: block <opponent|all>
        /// </summary>
        public static Message HandleBlockCommand(Player player, string arg, World.World world, SqliteConnection dbConnection)
        {
            if (player == null || world == null)
                return MessageHandler.System("Invalid state.");

            var room = player.CurrentRoom;
            if (room == null)
                return MessageHandler.System("You are not in a valid room.");

            // Check if player has held shield
            var shield = Combat.GetHeldShield(player);
            if (shield == null)
                return MessageHandler.System("You must be holding a shield to block.");

            // Check if in combat
            if (!CombatManager.IsEntityInCombat(player))
                return MessageHandler.System("You are not in combat.");

            arg = arg.ToLowerInvariant();

            // Handle "block all"
            if (arg == "all")
            {
                return HandleBlockAll(player, shield, room, dbConnection);
            }

            // Resolve opponent by name
            var npcMatches = NpcTargeting.FindMatches(room, arg);

            if (npcMatches.Count == 0)
            {
                // Try to match player name
                var playerMatches = room.Players.Where(p => p != player &&
                    !string.IsNullOrWhiteSpace(p.Name) &&
                    p.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase)).ToList();

                if (playerMatches.Count == 0)
                    return MessageHandler.System($"You don't see '{arg}' here to block.");

                if (playerMatches.Count > 1)
                    return MessageHandler.System("Multiple players match that name. Be more specific.");

                return HandleBlockTarget(player, playerMatches[0], shield, room, dbConnection);
            }

            if (npcMatches.Count > 1)
                return MessageHandler.System($"Multiple opponents match '{arg}'. Be more specific.");

            return HandleBlockTarget(player, npcMatches[0], shield, room, dbConnection);
        }

        /// <summary>
        /// Reset to blocking all opponents with 50% shield bonus.
        /// </summary>
        private static Message HandleBlockAll(Player player, Item shield, Room room, SqliteConnection dbConnection)
        {
            player.ShieldFocusNpcId = null;

            // Persist
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = "UPDATE Player SET ShieldFocusNpcId = NULL WHERE PlayerID = @pid";
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    cmd.ExecuteNonQuery();
                    return null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLOCK] Failed to persist block all: {ex.Message}");
            }

            // Broadcast
            MessageHandler.SendSystem(player, $"You will block attacks from all opponents with reduced effectiveness using {shield.Name}.");

            foreach (var p in room.Players.Where(p => p != player))
            {
                MessageHandler.SendSystem(p, $"{player.Name} attempts to block attacks from all opponents with {shield.Name}.");
            }

            return null;
        }

        /// <summary>
        /// Set shield focus on specific opponent (NPC or Player).
        /// </summary>
        private static Message HandleBlockTarget(Player player, dynamic target, Item shield, Room room, SqliteConnection dbConnection)
        {
            string targetName = "";
            int targetId = 0;
            bool isAlive = false;

            if (target is Npc npc)
            {
                targetName = npc.DisplayName;
                targetId = npc.NpcID;
                isAlive = npc.Health > 0;
            }
            else if (target is Player targetPlayer)
            {
                targetName = targetPlayer.Name;
                targetId = targetPlayer.DbPlayerID;
                isAlive = targetPlayer.Health > 0;
            }
            else
            {
                return MessageHandler.System("Invalid target type.");
            }

            if (!isAlive)
                return MessageHandler.System("That opponent is not a valid target.");

            // Check if NPC/Player is in combat with player
            if (!CombatManager.IsEntityInCombat(target))
                return MessageHandler.System("That opponent is not fighting you.");

            player.ShieldFocusNpcId = targetId;

            // Persist
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var cmd = wConn.CreateCommand();
                    cmd.Transaction = wTx;
                    cmd.CommandText = "UPDATE Player SET ShieldFocusNpcId = @focusId WHERE PlayerID = @pid";
                    cmd.Parameters.AddWithValue("@focusId", targetId);
                    cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    cmd.ExecuteNonQuery();
                    return null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLOCK] Failed to persist focus: {ex.Message}");
            }

            // Broadcast
            MessageHandler.SendSystem(player, $"You use {shield.Name} to block attacks from {targetName}!");

            foreach (var p in room.Players.Where(p => p != player))
            {
                MessageHandler.SendSystem(p, $"{player.Name} uses {shield.Name} to block attacks from {targetName}!");
            }

            return null;
        }

        /// <summary>
        /// Reset shield focus when focused opponent dies.
        /// Call from combat death handling.
        /// </summary>
        public static void ResetFocusIfTargeted(Player player, Npc deadNpc, Room room)
        {
            if (player?.ShieldFocusNpcId == null || deadNpc == null || room == null)
                return;

            if (player.ShieldFocusNpcId.Value != deadNpc.NpcID)
                return;  // Player was focused on someone else

            player.ShieldFocusNpcId = null;

            var shield = Combat.GetHeldShield(player);
            if (shield != null)
            {
                MessageHandler.SendSystem(player, $"Your block focus on {deadNpc.DisplayName} is no longer valid. Shield protection set to all opponents.");
                foreach (var p in room.Players.Where(p => p != player))
                {
                    MessageHandler.SendSystem(p, $"{player.Name}'s block focus is no longer valid.");
                }
            }
        }

        /// <summary>
        /// Reset shield focus when player enters combat (default to block all).
        /// </summary>
        public static void ResetFocusOnCombatStart(Player player)
        {
            if (player != null)
            {
                player.ShieldFocusNpcId = null;
            }
        }
    }
}