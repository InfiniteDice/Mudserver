using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;

namespace MudCore.Actions
{
    /// <summary>
    /// Service for managing NPC leading and hitching functionality.
    /// Allows players to lead NPCs using rope, hitch them to posts, and maintain ownership claims.
    /// </summary>
    public static class LeadService
    {
        private const string RopeTemplateId = "tool_rope";

        // ==================== VALIDATION METHODS ====================

        /// <summary>
        /// Checks if player can lead the specified NPC.
        /// Requirements: Player has rope, NPC alignment compatible, NPC not already led, player not leading another NPC.
        /// </summary>
        public static (bool canLead, string reason) CanLeadNpc(Player player, Npc npc, World.World world)
        {
            if (player == null || npc == null)
                return (false, "Invalid target.");

            // Check if player has rope
            var rope = GetRopeFromInventory(player);
            if (rope == null)
                return (false, "You need a rope to lead an animal.");

            // Check if NPC is already being led
            if (npc.IsBeingLed)
                return (false, $"The {npc.DisplayName} is already being led by someone else.");

            // Check if NPC is hitched
            if (npc.IsHitched)
                return (false, $"The {npc.DisplayName} is hitched. It must be unhitched first.");

            // Check if player is already leading another NPC
            var alreadyLeading = world.Npcs.FirstOrDefault(n => n != null && n.IsBeingLed && n.LedByPlayerId == player.DbPlayerID);
            if (alreadyLeading != null)
                return (false, $"You are already leading {alreadyLeading.DisplayName}.");

            // Check alignment compatibility
            if (!IsAlignmentCompatible(player.Alignment, npc.Alignment))
                return (false, $"The {npc.DisplayName} resists your attempt to lead it.");

            return (true, string.Empty);
        }

        /// <summary>
        /// Checks alignment compatibility for leading.
        /// Neutral NPCs can be led by anyone. Otherwise, alignments must match.
        /// </summary>
        private static bool IsAlignmentCompatible(Alignment playerAlignment, Alignment npcAlignment)
        {
            if (npcAlignment == Alignment.Neutral)
                return true;

            return playerAlignment == npcAlignment;
        }

        /// <summary>
        /// Finds rope in player's inventory.
        /// </summary>
        private static Item GetRopeFromInventory(Player player)
        {
            if (player.Inventory == null) return null;

            return player.Inventory.FirstOrDefault(i =>
                i != null &&
                string.Equals(i.TemplateID, RopeTemplateId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Checks if room contains an item with AllowHitch flag.
        /// </summary>
        private static Item GetHitchPostInRoom(Room room)
        {
            if (room?.Inventory == null) return null;

            return room.Inventory.FirstOrDefault(i =>
                i != null &&
                i.Flags != null &&
                i.Flags.Any(f => string.Equals(f, "AllowHitch", StringComparison.OrdinalIgnoreCase)));
        }

        // ==================== LEAD COMMAND ====================

        /// <summary>
        /// Start leading an NPC. Player must have rope and meet alignment requirements.
        /// </summary>
        public static void LeadNpc(Player player, Npc npc, World.World world, SqliteConnection conn)
        {
            if (player == null || npc == null || world == null)
            {
                MessageHandler.SendSystem(player, "Invalid target.");
                return;
            }

            // Validation
            var (canLead, reason) = CanLeadNpc(player, npc, world);
            if (!canLead)
            {
                MessageHandler.SendSystem(player, reason);
                return;
            }

            // Set lead state - USE DbPlayerID (the INTEGER primary key)
            npc.IsBeingLed = true;
            npc.LedByPlayerId = player.DbPlayerID;

            // Persist NPC state
            try
            {
                NpcPersistence.UpdateNpc(conn, npc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to persist lead state for NpcID={npc.NpcID}: {ex.Message}");
            }

            // Notify player and room
            MessageHandler.SendSystem(player, $"You begin leading the {npc.DisplayName}.");

            var room = player.CurrentRoom;
            if (room != null)
            {
                foreach (var other in room.Players.Where(p => p != null && p.DbPlayerID != player.DbPlayerID))
                {
                    MessageHandler.SendSystem(other, $"{player.Name} begins leading the {npc.DisplayName}.");
                }
            }
        }

        // ==================== RELEASE COMMAND ====================

        /// <summary>
        /// Stop leading an NPC. Restores normal NPC behavior.
        /// </summary>
        public static void ReleaseNpc(Player player, Npc npc, World.World world, SqliteConnection conn)
        {
            if (player == null || npc == null)
            {
                MessageHandler.SendSystem(player, "Invalid target.");
                return;
            }

            // Check if player is leading this NPC - USE DbPlayerID
            if (!npc.IsBeingLed || npc.LedByPlayerId != player.DbPlayerID)
            {
                MessageHandler.SendSystem(player, $"You are not leading the {npc.DisplayName}.");
                return;
            }

            // Check if NPC is in same room
            if (npc.CurrentRoom == null || npc.CurrentRoom.RoomID != player.CurrentRoom?.RoomID)
            {
                MessageHandler.SendSystem(player, $"The {npc.DisplayName} is not here.");
                return;
            }

            // Clear lead state
            npc.IsBeingLed = false;
            npc.LedByPlayerId = 0;

            // Persist NPC state
            try
            {
                NpcPersistence.UpdateNpc(conn, npc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to persist release state for NpcID={npc.NpcID}: {ex.Message}");
            }

            // Notify player and room
            MessageHandler.SendSystem(player, $"You release the {npc.DisplayName}.");

            var room = player.CurrentRoom;
            if (room != null)
            {
                foreach (var other in room.Players.Where(p => p != null && p.DbPlayerID != player.DbPlayerID))
                {
                    MessageHandler.SendSystem(other, $"{player.Name} releases the {npc.DisplayName}.");
                }
            }
        }

        // ==================== HITCH COMMAND ====================

        /// <summary>
        /// Hitch an NPC to a post. Requires AllowHitch item in room.
        /// Transfers rope to NPC, adds HitchClaim to player, locks NPC movement.
        /// </summary>
        public static void HitchNpc(Player player, Npc npc, World.World world, SqliteConnection conn)
        {
            if (player == null || npc == null || world == null)
            {
                MessageHandler.SendSystem(player, "Invalid target.");
                return;
            }

            // Check if player is leading this NPC - USE DbPlayerID
            if (!npc.IsBeingLed || npc.LedByPlayerId != player.DbPlayerID)
            {
                MessageHandler.SendSystem(player, $"You must be leading the {npc.DisplayName} to hitch it.");
                return;
            }

            // Check if NPC is in same room
            if (npc.CurrentRoom == null || npc.CurrentRoom.RoomID != player.CurrentRoom?.RoomID)
            {
                MessageHandler.SendSystem(player, $"The {npc.DisplayName} is not here.");
                return;
            }

            // Check for hitch post in room
            var hitchPost = GetHitchPostInRoom(player.CurrentRoom);
            if (hitchPost == null)
            {
                MessageHandler.SendSystem(player, "There is nothing to hitch to here.");
                return;
            }

            // Get rope from player inventory
            var rope = GetRopeFromInventory(player);
            if (rope == null)
            {
                MessageHandler.SendSystem(player, "You need a rope to hitch an animal.");
                return;
            }

            // Transfer rope from player to NPC
            try
            {
                rope.OwnerType = "NPC";
                rope.OwnerID = npc.NpcID;
                rope.RoomID = npc.CurrentRoom.RoomID;
                ItemPersistence.SaveItem(conn, rope);

                // Remove from player inventory, add to NPC inventory
                player.Inventory?.Remove(rope);
                if (npc.Inventory == null) npc.Inventory = new System.Collections.Generic.List<Item>();
                npc.Inventory.Add(rope);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to transfer rope for hitch: {ex.Message}");
                MessageHandler.SendSystem(player, "Failed to hitch the animal.");
                return;
            }

            // Set hitch state - USE DbPlayerID
            npc.IsBeingLed = false;
            npc.LedByPlayerId = 0;
            npc.IsHitched = true;
            npc.HitchedWithRopeId = rope.ItemID;
            npc.HitchedByPlayerId = player.DbPlayerID;

            // Add HitchClaim to player
            player.AddHitchClaim(rope.ItemID);

            // Persist changes
            try
            {
                NpcPersistence.UpdateNpc(conn, npc);
                PlayerPersistence.SavePlayer(conn, player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to persist hitch state: {ex.Message}");
            }

            // Notify player and room
            MessageHandler.SendSystem(player, $"You hitch the {npc.DisplayName} to the {hitchPost.Name}.");

            var room = player.CurrentRoom;
            if (room != null)
            {
                foreach (var other in room.Players.Where(p => p != null && p.DbPlayerID != player.DbPlayerID))
                {
                    MessageHandler.SendSystem(other, $"{player.Name} hitches the {npc.DisplayName} to the {hitchPost.Name}.");
                }
            }
        }

        // ==================== UNHITCH COMMAND ====================

        /// <summary>
        /// Unhitch an NPC. Requires player to have matching HitchClaim.
        /// Returns rope to player, restores NPC movement.
        /// </summary>
        public static void UnhitchNpc(Player player, Npc npc, World.World world, SqliteConnection conn)
        {
            if (player == null || npc == null)
            {
                MessageHandler.SendSystem(player, "Invalid target.");
                return;
            }

            // Check if NPC is hitched
            if (!npc.IsHitched)
            {
                MessageHandler.SendSystem(player, $"The {npc.DisplayName} is not hitched.");
                return;
            }

            // Check if NPC is in same room
            if (npc.CurrentRoom == null || npc.CurrentRoom.RoomID != player.CurrentRoom?.RoomID)
            {
                MessageHandler.SendSystem(player, $"The {npc.DisplayName} is not here.");
                return;
            }

            // Check if player has matching HitchClaim
            var claimedRopeId = player.GetHitchClaimRopeId();
            if (claimedRopeId != npc.HitchedWithRopeId)
            {
                MessageHandler.SendSystem(player, $"You do not have the authority to unhitch this {npc.DisplayName}.");
                return;
            }

            // Find rope in NPC inventory
            var rope = npc.Inventory?.FirstOrDefault(i => i != null && i.ItemID == npc.HitchedWithRopeId);
            if (rope == null)
            {
                // Rope missing - clear state anyway but warn
                Console.WriteLine($"[LeadService] Rope ItemID={npc.HitchedWithRopeId} not found in NPC inventory during unhitch.");
                MessageHandler.SendSystem(player, "The rope seems to have gone missing, but you manage to free the animal.");
            }
            else
            {
                // Transfer rope back to player - USE DbPlayerID for OwnerID
                try
                {
                    rope.OwnerType = "Player";
                    rope.OwnerID = player.DbPlayerID;
                    rope.RoomID = player.CurrentRoom.RoomID;
                    ItemPersistence.SaveItem(conn, rope);

                    // Move in inventory lists
                    npc.Inventory?.Remove(rope);
                    if (player.Inventory == null) player.Inventory = new System.Collections.Generic.List<Item>();
                    player.Inventory.Add(rope);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LeadService] Failed to transfer rope during unhitch: {ex.Message}");
                    MessageHandler.SendSystem(player, "Failed to retrieve the rope.");
                    return;
                }
            }

            // Clear hitch state
            npc.IsHitched = false;
            npc.HitchedWithRopeId = 0;
            npc.HitchedByPlayerId = 0;

            // Remove HitchClaim from player
            player.RemoveHitchClaim(claimedRopeId);

            // Persist changes
            try
            {
                NpcPersistence.UpdateNpc(conn, npc);
                PlayerPersistence.SavePlayer(conn, player);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to persist unhitch state: {ex.Message}");
            }

            // Notify player and room
            MessageHandler.SendSystem(player, $"You unhitch the {npc.DisplayName}.");

            var room = player.CurrentRoom;
            if (room != null)
            {
                foreach (var other in room.Players.Where(p => p != null && p.DbPlayerID != player.DbPlayerID))
                {
                    MessageHandler.SendSystem(other, $"{player.Name} unhitches the {npc.DisplayName}.");
                }
            }
        }

        // ==================== EDGE CASE HANDLERS ====================

        /// <summary>
        /// Called when rope is dropped while leading. Releases the led NPC.
        /// </summary>
        public static void OnRopeDropped(Player player, Item rope, World.World world, SqliteConnection conn)
        {
            if (player == null || rope == null || world == null) return;

            // Check if rope template matches
            if (!string.Equals(rope.TemplateID, RopeTemplateId, StringComparison.OrdinalIgnoreCase))
                return;

            // Find NPC being led by this player - USE DbPlayerID
            var ledNpc = world.Npcs.FirstOrDefault(n =>
                n != null &&
                n.IsBeingLed &&
                n.LedByPlayerId == player.DbPlayerID);

            if (ledNpc == null) return;

            // Release the NPC
            ledNpc.IsBeingLed = false;
            ledNpc.LedByPlayerId = 0;

            try
            {
                NpcPersistence.UpdateNpc(conn, ledNpc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to persist rope-drop release for NpcID={ledNpc.NpcID}: {ex.Message}");
            }

            // Notify
            MessageHandler.SendSystem(player, $"The rope slips from your hand and the {ledNpc.DisplayName} wanders free.");

            var room = player.CurrentRoom;
            if (room != null)
            {
                foreach (var other in room.Players.Where(p => p != null && p.DbPlayerID != player.DbPlayerID))
                {
                    MessageHandler.SendSystem(other, $"The {ledNpc.DisplayName} breaks free from {player.Name}.");
                }
            }
        }

        /// <summary>
        /// Called when player logs out. Releases any led NPC.
        /// </summary>
        public static void OnPlayerLogout(Player player, World.World world, SqliteConnection conn)
        {
            if (player == null || world == null) return;

            // Find NPC being led by this player - USE DbPlayerID
            var ledNpc = world.Npcs.FirstOrDefault(n =>
                n != null &&
                n.IsBeingLed &&
                n.LedByPlayerId == player.DbPlayerID);

            if (ledNpc == null) return;

            // Release the NPC
            ledNpc.IsBeingLed = false;
            ledNpc.LedByPlayerId = 0;

            try
            {
                NpcPersistence.UpdateNpc(conn, ledNpc);
                Console.WriteLine($"[LeadService] Released NpcID={ledNpc.NpcID} due to player logout (DbPlayerID={player.DbPlayerID}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LeadService] Failed to persist logout release for NpcID={ledNpc.NpcID}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when hitched NPC dies. Drops rope, clears claim.
        /// </summary>
        public static void OnNpcDeath(Npc npc, World.World world, SqliteConnection conn)
        {
            if (npc == null || world == null) return;

            // Handle hitched NPC death
            if (npc.IsHitched && npc.HitchedWithRopeId > 0)
            {
                // Find the rope
                var rope = npc.Inventory?.FirstOrDefault(i => i != null && i.ItemID == npc.HitchedWithRopeId);

                if (rope != null && npc.CurrentRoom != null)
                {
                    // Drop rope to room
                    try
                    {
                        rope.OwnerType = "Room";
                        rope.OwnerID = npc.CurrentRoom.RoomID;
                        rope.RoomID = npc.CurrentRoom.RoomID;
                        ItemPersistence.SaveItem(conn, rope);

                        npc.Inventory?.Remove(rope);
                        npc.CurrentRoom.Inventory?.Add(rope);

                        Console.WriteLine($"[LeadService] Rope ItemID={rope.ItemID} dropped to room {npc.CurrentRoom.RoomID} due to NPC death.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LeadService] Failed to drop rope on NPC death: {ex.Message}");
                    }
                }

                // Find player with HitchClaim and remove it - USE DbPlayerID
                var claimingPlayer = world.Players.FirstOrDefault(p =>
                    p != null &&
                    p.DbPlayerID == npc.HitchedByPlayerId);

                if (claimingPlayer != null)
                {
                    claimingPlayer.RemoveHitchClaim(npc.HitchedWithRopeId);
                    try
                    {
                        PlayerPersistence.SavePlayer(conn, claimingPlayer);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LeadService] Failed to remove HitchClaim on NPC death: {ex.Message}");
                    }
                }
            }

            // Handle led NPC death
            if (npc.IsBeingLed)
            {
                var leadingPlayer = world.Players.FirstOrDefault(p =>
                    p != null &&
                    p.DbPlayerID == npc.LedByPlayerId);

                if (leadingPlayer != null)
                {
                    MessageHandler.SendSystem(leadingPlayer, $"The {npc.DisplayName} collapses! You drop the rope.");
                }
            }

            // Clear all lead/hitch state (will be persisted when NPC is removed from world)
            npc.IsBeingLed = false;
            npc.LedByPlayerId = 0;
            npc.IsHitched = false;
            npc.HitchedWithRopeId = 0;
            npc.HitchedByPlayerId = 0;
        }

        // ==================== LIST HITCHED COMMAND ====================

        /// <summary>
        /// List all NPCs hitched by the player.
        /// Shows NPC name, room name, and area name for each hitched creature.
        /// </summary>
        public static Message ListHitched(Player player, World.World world)
        {
            if (player == null || world == null)
                return MessageHandler.System("Invalid player or world.");

            // Find all NPCs hitched by this player - USE DbPlayerID
            var hitchedNpcs = world.Npcs
                .Where(n => n != null && n.IsHitched && n.HitchedByPlayerId == player.DbPlayerID)
                .ToList();

            if (hitchedNpcs.Count == 0)
                return MessageHandler.System("You have no hitched creatures.");

            // Build output
            var lines = new System.Collections.Generic.List<string>
            {
                "List of Hitched Creatures:"
            };

            for (int i = 0; i < hitchedNpcs.Count; i++)
            {
                var npc = hitchedNpcs[i];

                // Get room name
                string roomName = npc.CurrentRoom?.Name ?? "Unknown Location";

                // Get area name by looking up room's AreaID
                string areaName = "Unknown Area";
                if (npc.CurrentRoom != null && world.Areas != null)
                {
                    var area = world.Areas.FirstOrDefault(a => a != null && a.AreaID == npc.CurrentRoom.AreaID);
                    areaName = area?.Name ?? "Unknown Area";
                }

                lines.Add($"{i + 1}) {npc.DisplayName}, {roomName}, {areaName}");
            }

            return MessageHandler.System(string.Join("\n", lines));
        }
    }
}