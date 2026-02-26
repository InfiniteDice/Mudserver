using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.Templates;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MudCore.Actions
{
    /// <summary>
    /// SpellScribingManager handles spell discovery and scribing mechanics.
    /// Spells are discovered from room flags (SpellSource:[spellId]) and scribed onto blank pages.
    /// </summary>
    public static class SpellScribingManager
    {
        private const string SPELL_SOURCE_FLAG_PREFIX = "SpellSource:";

        /// <summary>
        /// Notify player of available spells in the current room.
        /// Called when player enters a room with SpellSource:[spellId] flags.
        /// </summary>
        /// <param name="player">Player entering the room</param>
        /// <param name="room">The room being entered</param>
        /// <param name="world">World instance for spell template lookup</param>
        public static void NotifySpellSourcesInRoom(Player player, Room room, World.World world)
        {
            if (player == null || room == null || world == null)
                return;

            // Find all SpellSource:[spellId] flags in room
            var spellSources = new List<string>();
            if (room.Flags != null)
            {
                foreach (var flag in room.Flags)
                {
                    if (string.IsNullOrWhiteSpace(flag))
                        continue;

                    if (flag.StartsWith(SPELL_SOURCE_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {
                        var spellId = flag.Substring(SPELL_SOURCE_FLAG_PREFIX.Length).Trim();
                        if (!string.IsNullOrWhiteSpace(spellId))
                        {
                            spellSources.Add(spellId);
                        }
                    }
                }
            }

            if (spellSources.Count == 0)
                return; // No spell sources in this room

            // Notify player of each spell
            foreach (var spellId in spellSources)
            {
                string? spellName = GetSpellNameFromTemplate(spellId, world);
                if (string.IsNullOrWhiteSpace(spellName))
                    spellName = spellId; // Fallback to ID if template not found

                string notification = $"You sense magical knowledge in this place. A spell called \"{spellName}\" can be learned here.";
                MessageHandler.SendSystem(player, notification);
            }
        }

        /// <summary>
        /// Scribe a spell from a room source onto a blank page with full skill validation.
        /// 
        /// Requirements:
        /// 1. Player must have skill_scribing level >= 1
        /// 2. Player must be holding tool_quill
        /// 3. Player must have ink or blood in inventory/room/container
        /// 4. Page must be blank
        /// 5. Room must have matching SpellSource:[spellId] flag
        /// 
        /// Chance Calculation:
        /// - Level 1 spell with level 1 scribing = 100% success
        /// - Each spell level above player skill = -20% chance
        /// 
        /// Results:
        /// - Success: Consume 1 ink/blood, scribe page
        /// - Failure: Consume 1 ink/blood, destroy page
        /// </summary>
        public static Message ScribeSpellToPage(Player player, string spellId, Item targetPage, Room room, World.World world, SqliteConnection conn)
        {
            if (player == null || targetPage == null || room == null)
                return MessageHandler.System("Invalid operation.");

            if (string.IsNullOrWhiteSpace(spellId))
                return MessageHandler.System("Usage: scribe [spellId] to [pagekeyword]");

            // REQUIREMENT 1: Check scribing skill level
            int scribingLevel = player.GetSkillLevel("skill_scribing", conn);
            if (scribingLevel < 1)
                return MessageHandler.System("You need at least skill level 1 in Scribing to scribe spells.");

            // REQUIREMENT 2: Check for held tool_quill
            bool hasQuill = false;
            try
            {
                hasQuill = player.Inventory?.Any(i =>
                    i != null &&
                    string.Equals(i.TemplateID, "tool_quill", StringComparison.OrdinalIgnoreCase) &&
                    i.Flags != null &&
                    i.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase))) ?? false;
            }
            catch { hasQuill = false; }

            if (!hasQuill)
                return MessageHandler.System("You must be holding a quill to scribe spells.");

            // REQUIREMENT 3: Check for ink or blood (inventory, room, or in containers)
            var inkOrBlood = FindInkOrBlood(player, room, conn);
            if (inkOrBlood == null)
                return MessageHandler.System("You need ink or blood to scribe. Check your inventory, the room, or containers you're carrying.");

            // REQUIREMENT 4: Validate page is blank
            if (!PagesManager.IsBlankPage(targetPage))
                return MessageHandler.System("That page is not blank - it already has content.");

            // REQUIREMENT 5: Validate room has matching spell source
            if (!ValidateSpellSourceInRoom(room, spellId))
                return MessageHandler.System($"There is no source for '{spellId}' in this room.");

            // Look up spell name from template
            string? spellName = GetSpellNameFromTemplate(spellId, world);
            if (string.IsNullOrWhiteSpace(spellName))
                return MessageHandler.System($"Spell template '{spellId}' not found.");

            // CALCULATE SUCCESS CHANCE
            // Get spell "level" - extract number from spellId (e.g., "SpellLightning" = level 1, "SpellFireball" = level 1, etc.)
            // If no number in ID, assume level 1
            int spellLevel = ExtractSpellLevel(spellId);

            // Chance formula: Level 1 spell at level 1 scribing = 100%
            // Each level above scribing level = -20% (minimum 5%)
            int successChance = 100 - ((spellLevel - scribingLevel) * 20);
            successChance = Math.Max(5, Math.Min(100, successChance)); // Clamp 5-100%

            // Roll for success
            var roll = new Random().Next(100);
            bool success = roll < successChance;

            if (!success)
            {
                // FAILURE: Consume resource and destroy page
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        // Consume ink/blood
                        ConsumeInkOrBlood(inkOrBlood, wConn, wTx);

                        // Destroy page (delete from DB)
                        if (targetPage.ItemID > 0)
                        {
                            using var del = wConn.CreateCommand();
                            del.Transaction = wTx;
                            del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                            del.Parameters.AddWithValue("@id", targetPage.ItemID);
                            del.ExecuteNonQuery();
                        }

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException)
                {
                    // Fallback: direct persistence
                    try
                    {
                        ConsumeInkOrBlood(inkOrBlood, conn, null);
                        if (targetPage.ItemID > 0)
                            ItemPersistence.DeleteItem(conn, targetPage.ItemID);
                    }
                    catch { /* best-effort */ }
                }

                // Remove from player inventory
                try { player.Inventory.RemoveAll(i => i != null && i.ItemID == targetPage.ItemID); } catch { }

                string failMsg = $"You attempt to scribe '{spellName}' but your technique fails. The ink smudges across the page, ruining it. (Roll: {roll}% vs {successChance}% chance)";
                MessageHandler.SendSystem(player, failMsg);

                // Broadcast to room
                foreach (var p in room.Players.Where(p => p != player))
                {
                    MessageHandler.SendSystem(p, $"{player.Name}'s scribing attempt fails - the page is ruined.");
                }

                return null;
            }

            // SUCCESS: Scribe the page
            PagesManager.SetSpellOnPage(targetPage, spellId, spellName);

            // Consume ink/blood and save page
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    ConsumeInkOrBlood(inkOrBlood, wConn, wTx);
                    ItemPersistence.SaveItem(wConn, targetPage, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
                // Fallback: direct persistence
                try
                {
                    ConsumeInkOrBlood(inkOrBlood, conn, null);
                    ItemPersistence.SaveItem(conn, targetPage);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpellScribingManager] Failed to persist scribed page: {ex.Message}");
                    return MessageHandler.System("Failed to scribe spell due to a server error.");
                }
            }

            // Remove page from player inventory and update
            //try { player.Inventory.RemoveAll(i => i != null && i.ItemID == targetPage.ItemID); } catch { }
            try { MessageHandler.EnqueuePlayerUpdate(player); } catch { }

            string successMsg = $"You carefully scribe the spell '{spellName}' onto the blank page. (Roll: {roll}% vs {successChance}% chance - SUCCESS!)";
            MessageHandler.SendSystem(player, successMsg);

            // Broadcast to room
            foreach (var p in room.Players.Where(p => p != player))
            {
                MessageHandler.SendSystem(p, $"{player.Name} successfully scribes a spell onto a page.");
            }

            return null;
        }

        /// <summary>
        /// Validate that a room has a spell source for a given spell ID.
        /// Checks for flag "SpellSource:[spellId]" in room flags.
        /// </summary>
        /// <param name="room">The room to check</param>
        /// <param name="spellId">The spell ID to validate</param>
        /// <returns>True if room has matching spell source</returns>
        public static bool ValidateSpellSourceInRoom(Room room, string spellId)
        {
            if (room == null || string.IsNullOrWhiteSpace(spellId))
                return false;

            if (room.Flags == null)
                return false;

            // Check with case-insensitive comparison
            return room.Flags.Any(f =>
                !string.IsNullOrWhiteSpace(f) &&
                f.StartsWith("SpellSource:", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.Substring("SpellSource:".Length), spellId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate that a spell template exists by checking if the spell ID is known.
        /// For now, just return the spell ID itself if it matches expected format.
        /// </summary>
        /// <param name="spellId">The spell ID to validate (e.g., "SpellLightning")</param>
        /// <param name="world">World instance (unused)</param>
        /// <returns>Spell ID if valid, null if not found</returns>
        public static string? GetSpellNameFromTemplate(string spellId, World.World world)
        {
            if (string.IsNullOrWhiteSpace(spellId))
                return null;

            try
            {
                // Construct filename: spell_[spellid].json
                string fileName = $"spell_{spellId.ToLowerInvariant()}.json";

                // Find the project root by walking up from BaseDirectory until we find the Data folder
                var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
                DirectoryInfo? dataDir = null;

                while (currentDir != null && currentDir.Parent != null)
                {
                    var candidateData = Path.Combine(currentDir.FullName, "Data", "ItemTemplates", "SpellTemplates");
                    if (Directory.Exists(candidateData))
                    {
                        dataDir = new DirectoryInfo(candidateData);
                        break;
                    }
                    currentDir = currentDir.Parent;
                }

                if (dataDir == null || !dataDir.Exists)
                    return null;

                // Search recursively for the spell file
                var files = Directory.GetFiles(dataDir.FullName, fileName, SearchOption.AllDirectories);
                if (files.Length == 0)
                    return null;

                string spellFile = files[0];
                var json = File.ReadAllText(spellFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Try to get name from JSON, or fallback to spellId
                if (root.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }

                return spellId; // Fallback
            }
            catch
            {
                return spellId; // Fallback on any error
            }
        }

        /// <summary>
        /// Get all available spell sources in a room.
        /// Returns list of spell IDs that have sources in the room.
        /// </summary>
        /// <param name="room">The room to check</param>
        /// <returns>List of spell IDs available in this room</returns>
        public static List<string> GetAvailableSpellsInRoom(Room room)
        {
            var spells = new List<string>();

            if (room == null || room.Flags == null)
                return spells;

            foreach (var flag in room.Flags)
            {
                if (string.IsNullOrWhiteSpace(flag))
                    continue;

                if (flag.StartsWith(SPELL_SOURCE_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                {
                    var spellId = flag.Substring(SPELL_SOURCE_FLAG_PREFIX.Length).Trim();
                    if (!string.IsNullOrWhiteSpace(spellId))
                    {
                        spells.Add(spellId);
                    }
                }
            }

            return spells;
        }

        /// <summary>
        /// Find ink or blood in player inventory, room, or in player's containers.
        /// Returns the item to consume, or null if none found.
        /// </summary>
        private static Item? FindInkOrBlood(Player player, Room room, SqliteConnection conn)
        {
            if (player == null || room == null)
                return null;

            // Check player inventory for direct ink/blood item
            var directMatch = player.Inventory?.FirstOrDefault(i =>
                i != null && (
                    string.Equals(i.TemplateID, "ink", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(i.TemplateID, "blood", StringComparison.OrdinalIgnoreCase)
                )) ?? null;

            if (directMatch != null)
                return directMatch;

            // Check containers in player inventory for ink/blood
            try
            {
                foreach (var container in player.Inventory?.Where(i =>
                    i != null &&
                    string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<Item>())
                {
                    var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                    var found = contents.FirstOrDefault(c =>
                        c != null && (
                            string.Equals(c.TemplateID, "ink", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.TemplateID, "blood", StringComparison.OrdinalIgnoreCase)
                        ));
                    if (found != null)
                        return found;
                }
            }
            catch { /* best-effort */ }

            // Check room containers for ink/blood
            try
            {
                foreach (var container in room.Inventory?.Where(i =>
                    i != null &&
                    string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<Item>())
                {
                    var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                    var found = contents.FirstOrDefault(c =>
                        c != null && (
                            string.Equals(c.TemplateID, "ink", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.TemplateID, "blood", StringComparison.OrdinalIgnoreCase)
                        ));
                    if (found != null)
                        return found;
                }
            }
            catch { /* best-effort */ }

            return null;
        }

        /// <summary>
        /// Consume one unit of ink or blood item.
        /// </summary>
        private static void ConsumeInkOrBlood(Item resource, SqliteConnection conn, SqliteTransaction? tx)
        {
            if (resource == null || conn == null)
                return;

            try
            {
                int newQty = Math.Max(0, resource.Quantity - 1);

                if (newQty > 0)
                {
                    resource.Quantity = newQty;
                    ItemPersistence.SaveItem(conn, resource, tx);
                }
                else
                {
                    // Delete if quantity reaches 0
                    if (resource.ItemID > 0)
                    {
                        using var del = conn.CreateCommand();
                        if (tx != null) del.Transaction = tx;
                        del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                        del.Parameters.AddWithValue("@id", resource.ItemID);
                        del.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellScribingManager] Failed to consume ink/blood: {ex.Message}");
            }
        }

        /// <summary>
        /// Extract spell level from spell ID (currently all spells are level 1).
        /// Format: SpellName -> level 1
        /// Future: Could support SpellName2, SpellName3, etc.
        /// </summary>
        private static int ExtractSpellLevel(string spellId)
        {
            if (string.IsNullOrWhiteSpace(spellId))
                return 1;

            // Try to extract trailing number (e.g., "SpellLightning2" -> level 2)
            var match = System.Text.RegularExpressions.Regex.Match(spellId, @"(\d+)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var level))
            {
                return Math.Clamp(level, 1, 5);
            }

            // Default to level 1
            return 1;
        }
    }
}