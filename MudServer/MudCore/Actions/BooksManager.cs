using MudCore.Entities;
using MudPersistence;
using MudNetworking;
using Microsoft.Data.Sqlite;
using MudCore.Templates;
using System;
using System.Collections.Generic;
using System.Linq;
using MudCore.World;

namespace MudCore.Actions
{
    /// <summary>
    /// BooksManager handles all book-related operations including initialization,
    /// page management, browsing, and persistence.
    /// Books are containers that hold pages (Item objects with ItemCategory="Page").
    /// Pages are persisted as separate Item rows with OwnerType="Item" and OwnerID=book.ItemID.
    /// </summary>
    public static class BooksManager
    {
        private const string BLANK_PAGES_FLAG_PREFIX = "BlankPages:";

        /// <summary>
        /// Initialize a book with blank pages when it's first created.
        /// Reads the "BlankPages:[N]" flag and spawns N page items inside the book.
        /// Called by ItemFactory when a book is created.
        /// </summary>
        /// <param name="book">The book item to initialize</param>
        /// <param name="world">World instance for ItemFactory access</param>
        /// <param name="conn">Database connection for persistence</param>
        public static void InitializeBookWithBlankPages(Item book, ItemFactory itemFactory, SqliteConnection conn)
        {
            if (book == null)
                return;

            // Find BlankPages:[N] flag
            int numPages = 0;
            if (book.Flags != null)
            {
                foreach (var flag in book.Flags)
                {
                    if (string.IsNullOrWhiteSpace(flag))
                        continue;

                    if (flag.StartsWith(BLANK_PAGES_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {
                        var numPart = flag.Substring(BLANK_PAGES_FLAG_PREFIX.Length).Trim();
                        if (int.TryParse(numPart, out int parsed) && parsed > 0)
                        {
                            numPages = parsed;
                            break;
                        }
                    }
                }
            }

            if (numPages <= 0)
                return; // No blank pages to create

            // Create blank pages and persist them
            for (int i = 1; i <= numPages; i++)
            {
                var page = PagesManager.CreateBlankPage(itemFactory, book.ItemID);
                if (page != null)
                {
                    page.OwnerType = "Item";
                    page.OwnerID = book.ItemID;
                    page.RoomID = 0; // Pages are not in a room, they're in the book

                    // Set page number
                    PagesManager.SetPageNumber(page, i);

                    // Persist page
                    try
                    {
                        ItemPersistence.SaveItem(conn, page);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BooksManager] Failed to persist page {i} in book {book.Name}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Load all pages in a book, sorted by page number.
        /// Pages are stored as Item rows with OwnerType="Item" and OwnerID=book.ItemID.
        /// </summary>
        /// <param name="book">The book container</param>
        /// <param name="conn">Database connection</param>
        /// <returns>List of pages sorted by page number</returns>
        public static List<Item> LoadPagesInBook(Item book, SqliteConnection conn)
        {
            if (book == null || book.ItemID == 0)
                return new List<Item>();

            try
            {
                // Load all items where OwnerType="Item" and OwnerID=book.ItemID
                var pages = ItemPersistence.LoadItemsByOwner(conn, "Item", book.ItemID);

                if (pages == null)
                    pages = new List<Item>();

                // Filter to only pages (ItemCategory="Page")
                pages = pages.Where(p => p != null &&
                    string.Equals(p.ItemCategory, "Page", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Sort by page number
                pages = PagesManager.SortPagesByNumber(pages);

                return pages;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BooksManager] Failed to load pages for book {book.Name}: {ex.Message}");
                return new List<Item>();
            }
        }

        /// <summary>
        /// Add a spell page to a book at a specific position.
        /// Page is moved from inventory to book container.
        /// </summary>
        /// <param name="player">Player performing the action</param>
        /// <param name="page">The page to add (must have spell flag)</param>
        /// <param name="book">The book to add page to</param>
        /// <param name="position">Position to insert at (1-based, null = append)</param>
        /// <param name="conn">Database connection</param>
        /// <returns>Message to send to player</returns>
        public static Message AddPageToBook(Player player, Item page, Item book, int? position, SqliteConnection conn)
        {
            if (player == null || page == null || book == null)
                return MessageHandler.System("Invalid operation.");

            // Validate page has spell flag (not blank)
            string? spellId = PagesManager.GetSpellIdFromPage(page);
            if (string.IsNullOrWhiteSpace(spellId) || PagesManager.IsBlankPage(page))
                return MessageHandler.System("You can only add spell pages to books, not blank pages.");

            // Validate book is container
            if (!string.Equals(book.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System("That is not a valid book.");

            // Load current pages in book
            var currentPages = LoadPagesInBook(book, conn);

            // Determine insertion position
            int insertAt = position ?? (currentPages.Count + 1);
            if (insertAt < 1)
                insertAt = 1;
            if (insertAt > currentPages.Count + 1)
                insertAt = currentPages.Count + 1;

            // Remove page from player inventory
            try
            {
                player.Inventory?.Remove(page);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BooksManager] Failed to remove page from player inventory: {ex.Message}");
            }

            // Move page to book
            page.OwnerType = "Item";
            page.OwnerID = book.ItemID;
            page.RoomID = 0;

            // If inserting in middle, renumber affected pages
            if (insertAt <= currentPages.Count)
            {
                for (int i = insertAt - 1; i < currentPages.Count; i++)
                {
                    var pageNum = PagesManager.GetPageNumber(currentPages[i]) ?? i + 1;
                    PagesManager.SetPageNumber(currentPages[i], pageNum + 1);

                    // Persist updated page
                    try
                    {
                        ItemPersistence.SaveItem(conn, currentPages[i]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BooksManager] Failed to renumber page: {ex.Message}");
                    }
                }
            }

            // Set page number
            PagesManager.SetPageNumber(page, insertAt);

            // Persist page
            try
            {
                ItemPersistence.SaveItem(conn, page);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BooksManager] Failed to persist page in book: {ex.Message}");
            }

            string pageName = page.Name ?? "Spell Page";
            return MessageHandler.System($"You carefully place the {pageName} into {book.Name} at position {insertAt}.");
        }

        /// <summary>
        /// Remove a spell page from a book, returning it to player inventory.
        /// Remaining pages are renumbered.
        /// </summary>
        /// <param name="player">Player performing the action</param>
        /// <param name="page">The page to remove</param>
        /// <param name="book">The book to remove from</param>
        /// <param name="conn">Database connection</param>
        /// <returns>Message to send to player</returns>
        public static Message RemovePageFromBook(Player player, Item page, Item book, SqliteConnection conn)
        {
            if (player == null || page == null || book == null)
                return MessageHandler.System("Invalid operation.");

            // Validate page is in book
            if (!PagesManager.ValidatePageInBook(page, book))
                return MessageHandler.System("That page is not in this book.");

            // Remove page from book
            PagesManager.RemovePageNumber(page);

            // Move page back to player
            page.OwnerType = "Player";
            page.OwnerID = player.DbPlayerID;
            page.RoomID = player.CurrentRoom?.RoomID ?? 0;

            // Add to player inventory
            if (player.Inventory == null)
                player.Inventory = new List<Item>();
            player.Inventory.Add(page);

            // Persist page
            try
            {
                ItemPersistence.SaveItem(conn, page);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BooksManager] Failed to persist page removal: {ex.Message}");
            }

            // Renumber remaining pages in book
            var remainingPages = LoadPagesInBook(book, conn);
            PagesManager.RenumberPages(remainingPages, 1);

            foreach (var remainingPage in remainingPages)
            {
                try
                {
                    ItemPersistence.SaveItem(conn, remainingPage);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BooksManager] Failed to renumber page: {ex.Message}");
                }
            }

            string pageName = page.Name ?? "Spell Page";
            return MessageHandler.System($"You carefully remove the {pageName} from {book.Name}.");
        }

        /// <summary>
        /// Display the contents of a book (list of pages).
        /// </summary>
        /// <param name="player">Player viewing the book</param>
        /// <param name="book">The book to display</param>
        /// <param name="conn">Database connection</param>
        /// <returns>Message with book contents</returns>
        public static Message DisplayBook(Player player, Item book, SqliteConnection conn)
        {
            if (player == null || book == null)
                return MessageHandler.System("Invalid book.");

            var pages = LoadPagesInBook(book, conn);

            var lines = new List<string>
            {
                $"\n{book.Name} - {pages.Count} page{(pages.Count != 1 ? "s" : "")}",
                new string('=', 40)
            };

            if (pages.Count == 0)
            {
                lines.Add("(This book is empty)");
            }
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    var pageNum = PagesManager.GetPageNumber(page) ?? (i + 1);
                    string pageName = page.Name ?? "Unnamed Page";
                    lines.Add($"{pageNum}) {pageName}");
                }
            }

            lines.Add(string.Empty);
            lines.Add("Type: read [book] [page #] to view a page");
            lines.Add("Type: remove [page] from [book] to take out a page");

            return MessageHandler.System(string.Join("\n", lines));
        }

        /// <summary>
        /// View the contents of a specific page within a book.
        /// If spell page, loads spell template and displays details.
        /// </summary>
        public static Message ViewPageInBook(Player player, Item book, int pageNum, World.World world, SqliteConnection conn)
        {
            if (player == null || book == null)
                return MessageHandler.System("Invalid book.");

            if (pageNum < 1)
                return MessageHandler.System("Invalid page number.");

            var pages = LoadPagesInBook(book, conn);

            if (pageNum > pages.Count)
                return MessageHandler.System($"This book only has {pages.Count} page{(pages.Count != 1 ? "s" : "")}.");

            var page = pages[pageNum - 1];
            string pageName = page.Name ?? "Unnamed Page";

            if (PagesManager.IsBlankPage(page))
            {
                return MessageHandler.System($"\n{pageName}\n{new string('=', 40)}\nThis page is blank. You could scribe a spell here.");
            }

            // Get spell ID from page
            string? spellId = PagesManager.GetSpellIdFromPage(page);
            if (string.IsNullOrWhiteSpace(spellId))
                return MessageHandler.System($"\n{pageName}\n{new string('=', 40)}\nThis page appears to be corrupted.");

            // Load spell template
            var spellTemplate = LoadSpellTemplate(spellId);
            if (spellTemplate == null)
                return MessageHandler.System($"\n{pageName}\n{new string('=', 40)}\nSpell template not found: {spellId}");

            // Format spell details
            var lines = new List<string>
            {
                $"\n{pageName}",
                new string('=', 40)
            };

            // Skill
            string skillDisplay = "None";
            if (!string.IsNullOrWhiteSpace(spellTemplate.RequiredSkill))
            {
                skillDisplay = FormatSkillName(spellTemplate.RequiredSkill);
            }
            lines.Add($"Skill: {skillDisplay}");

            // Skill Level Required
            string skillLevelDisplay = spellTemplate.MinSkillLevel > 0 ? spellTemplate.MinSkillLevel.ToString() : "None";
            lines.Add($"Skill Level Required: {skillLevelDisplay}");

            // Requirements (Tools + Room Flags)
            string requirementsDisplay = FormatRequirements(spellTemplate);
            lines.Add($"Requirements: {requirementsDisplay}");

            // Casting Cost
            string costDisplay = spellTemplate.PowerCost > 0 ? spellTemplate.PowerCost.ToString() : "None";
            lines.Add($"Casting Cost: {costDisplay}");

            // Damage or Effect
            string damageDisplay = FormatDamageEffect(spellTemplate);
            lines.Add($"Damage: {damageDisplay}");

            // Duration
            string durationDisplay = FormatDuration(spellTemplate);
            lines.Add($"Duration: {durationDisplay}");

            // Casting Time
            string castTimeDisplay = spellTemplate.CastTimeSeconds > 0 ? $"{spellTemplate.CastTimeSeconds} seconds" : "None";
            lines.Add($"Casting Time: {castTimeDisplay}");

            // Components
            string componentsDisplay = FormatComponents(spellTemplate);
            lines.Add($"Components: {componentsDisplay}");

            return MessageHandler.System(string.Join("\n", lines));
        }

        /// <summary>
        /// Load spell template from JSON file by spell ID.
        /// </summary>
        private static SpellTemplate? LoadSpellTemplate(string spellId)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "Data", "ItemTemplates", "SpellTemplates"),
                    Path.Combine(baseDir, "..", "..", "..", "Data", "ItemTemplates", "SpellTemplates"),
                    Path.Combine(baseDir, "..", "Data", "ItemTemplates", "SpellTemplates")
                };

                string spellDir = candidates.FirstOrDefault(Directory.Exists);
                if (spellDir == null)
                    return null;

                // Search ALL spell template files (not just by filename match)
                var spellFiles = Directory.GetFiles(spellDir, "*.json", SearchOption.AllDirectories);

                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                foreach (var file in spellFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var template = System.Text.Json.JsonSerializer.Deserialize<SpellTemplate>(json, options);

                        // Match by ID field in the template
                        if (template != null && string.Equals(template.Id, spellId, StringComparison.OrdinalIgnoreCase))
                        {
                            return template;
                        }
                    }
                    catch { /* try next file */ }
                }
            }
            catch { /* best-effort */ }

            return null;
        }

        /// <summary>
        /// Format skill name by removing "skill_" prefix and title-casing.
        /// Example: "skill_arcane_knowledge" → "Arcane Knowledge"
        /// </summary>
        private static string FormatSkillName(string skillId)
        {
            if (string.IsNullOrWhiteSpace(skillId))
                return "None";

            string cleaned = skillId;
            if (cleaned.StartsWith("skill_", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(6);
            }

            // Title case: replace underscores with spaces and capitalize words
            var words = cleaned.Split('_', StringSplitOptions.RemoveEmptyEntries);
            var titleCased = string.Join(" ", words.Select(w =>
                char.ToUpper(w[0]) + (w.Length > 1 ? w.Substring(1) : "")));

            return titleCased;
        }

        /// <summary>
        /// Format requirements from tools and room flags.
        /// </summary>
        private static string FormatRequirements(SpellTemplate spell)
        {
            var requirements = new List<string>();

            if (spell.ToolsRequired != null && spell.ToolsRequired.Count > 0)
            {
                requirements.AddRange(spell.ToolsRequired);
            }

            if (spell.RoomFlags != null && spell.RoomFlags.Count > 0)
            {
                requirements.AddRange(spell.RoomFlags);
            }

            if (requirements.Count == 0)
                return "None";

            return string.Join(", ", requirements);
        }

        /// <summary>
        /// Format damage effect or return "None" if no damage effect.
        /// </summary>
        private static string FormatDamageEffect(SpellTemplate spell)
        {
            if (spell.SpellEffects == null || spell.SpellEffects.Count == 0)
                return "None";

            var damageEffect = spell.SpellEffects.FirstOrDefault(e =>
                string.Equals(e.Type, "Damage", StringComparison.OrdinalIgnoreCase));

            if (damageEffect == null)
                return "None";

            if (damageEffect.DmgPerLevel <= 0)
                return "None";

            string damageTypeDisplay = string.IsNullOrWhiteSpace(damageEffect.DamageType)
                ? "Unknown"
                : damageEffect.DamageType;

            return $"{damageEffect.DmgPerLevel} per level ({damageTypeDisplay})";
        }

        /// <summary>
        /// Format duration from spell effects.
        /// </summary>
        private static string FormatDuration(SpellTemplate spell)
        {
            if (spell.SpellEffects == null || spell.SpellEffects.Count == 0)
                return "None";

            var effect = spell.SpellEffects.FirstOrDefault();
            if (effect == null)
                return "None";

            if (effect.EffectDuration == 0)
                return "Instantaneous";

            return $"{effect.EffectDuration} seconds";
        }

        /// <summary>
        /// Format casting components as comma-separated list.
        /// Example: "coin_silver:1, dust_fairy:1"
        /// </summary>
        private static string FormatComponents(SpellTemplate spell)
        {
            if (spell.CastingComponents == null || spell.CastingComponents.Count == 0)
                return "None";

            var formatted = spell.CastingComponents
                .Where(c => !string.IsNullOrWhiteSpace(c.Template))
                .Select(c => $"{c.Template}:{c.Qty}")
                .ToList();

            if (formatted.Count == 0)
                return "None";

            return string.Join(", ", formatted);
        }

        /// <summary>
        /// Get position choices for inserting a page into a book.
        /// Returns list of tuples: (position, description)
        /// </summary>
        /// <param name="book">The book to insert into</param>
        /// <param name="conn">Database connection</param>
        /// <returns>List of position options</returns>
        public static List<(int position, string description)> GetPositionChoices(Item book, SqliteConnection conn)
        {
            var result = new List<(int, string)>();

            if (book == null)
                return result;

            var pages = LoadPagesInBook(book, conn);

            if (pages.Count == 0)
            {
                result.Add((1, "At the beginning (only page)"));
                return result;
            }

            // Option: at the end
            result.Add((pages.Count + 1, $"At the end (after page {pages.Count})"));

            // Options: between each page
            for (int i = 0; i < pages.Count; i++)
            {
                var beforePage = pages[i].Name ?? "Unnamed Page";
                var afterPage = i + 1 < pages.Count ? pages[i + 1].Name ?? "Unnamed Page" : null;

                if (afterPage != null)
                {
                    result.Add((i + 1, $"Between '{beforePage}' and '{afterPage}'"));
                }
            }

            return result;
        }

        /// <summary>
        /// Validate that a book is a valid container for pages.
        /// </summary>
        /// <param name="book">The item to validate</param>
        /// <returns>True if book is a valid page container</returns>
        public static bool ValidateBook(Item book)
        {
            if (book == null)
                return false;

            return string.Equals(book.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Get the total number of pages in a book.
        /// </summary>
        /// <param name="book">The book to count</param>
        /// <param name="conn">Database connection</param>
        /// <returns>Number of pages</returns>
        public static int GetPageCount(Item book, SqliteConnection conn)
        {
            if (book == null)
                return 0;

            var pages = LoadPagesInBook(book, conn);
            return pages.Count;
        }
    }
}