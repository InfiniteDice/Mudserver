using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MudCore.Actions
{
    /// <summary>
    /// PagesManager handles all page-related operations within books.
    /// Manages page numbering, spell flags, blank page detection, and page sorting.
    /// Pages are Item objects stored in book containers with flags for position and content.
    /// </summary>
    public static class PagesManager
    {
        private const string PAGE_NUMBER_FLAG_PREFIX = "Page:";
        private const string BLANK_PAGE_FLAG = "BlankPage";

        /// <summary>
        /// Get the page number from a page item.
        /// Looks for flag "Page:[N]" where N is the position in the book.
        /// </summary>
        /// <param name="page">The page item to check</param>
        /// <returns>Page number (1-based) or null if not numbered</returns>
        public static int? GetPageNumber(Item page)
        {
            if (page?.Flags == null)
                return null;

            foreach (var flag in page.Flags)
            {
                if (string.IsNullOrWhiteSpace(flag))
                    continue;

                if (flag.StartsWith(PAGE_NUMBER_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                {
                    var numPart = flag.Substring(PAGE_NUMBER_FLAG_PREFIX.Length).Trim();
                    if (int.TryParse(numPart, out int pageNum) && pageNum > 0)
                    {
                        return pageNum;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Set the page number on a page item.
        /// Removes any existing "Page:[N]" flag and adds the new one.
        /// </summary>
        /// <param name="page">The page item to number</param>
        /// <param name="pageNum">Page number to assign (1-based)</param>
        public static void SetPageNumber(Item page, int pageNum)
        {
            if (page == null || pageNum <= 0)
                return;

            if (page.Flags == null)
                page.Flags = new List<string>();

            // Remove existing Page:[N] flag
            page.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                f.StartsWith(PAGE_NUMBER_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase));

            // Add new Page:[N] flag
            page.Flags.Add($"{PAGE_NUMBER_FLAG_PREFIX}{pageNum}");

            // Update FlagsJson
            UpdateFlagsJson(page);
        }

        /// <summary>
        /// Remove the page number from a page item.
        /// Removes the "Page:[N]" flag so page is no longer positioned in a book.
        /// </summary>
        /// <param name="page">The page item to unnumber</param>
        public static void RemovePageNumber(Item page)
        {
            if (page?.Flags == null)
                return;

            page.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                f.StartsWith(PAGE_NUMBER_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase));

            UpdateFlagsJson(page);
        }

        /// <summary>
        /// Check if a page is blank (has no spell written on it).
        /// Blank pages have the "BlankPage" flag and no spell ID flag.
        /// </summary>
        /// <param name="page">The page item to check</param>
        /// <returns>True if page is blank, false otherwise</returns>
        public static bool IsBlankPage(Item page)
        {
            if (page?.Flags == null)
                return false;

            return page.Flags.Any(f => !string.IsNullOrWhiteSpace(f) &&
                string.Equals(f, BLANK_PAGE_FLAG, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get the spell ID from a page (if page has a spell written on it).
        /// Spell flags are the spell template ID (e.g., "SpellLightning").
        /// </summary>
        /// <param name="page">The page item to check</param>
        /// <returns>Spell ID or null if page is blank</returns>
        public static string? GetSpellIdFromPage(Item page)
        {
            if (page?.Flags == null)
                return null;

            foreach (var flag in page.Flags)
            {
                if (string.IsNullOrWhiteSpace(flag))
                    continue;

                // Skip system flags
                if (flag.StartsWith(PAGE_NUMBER_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(flag, BLANK_PAGE_FLAG, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Any other flag is assumed to be a spell ID
                return flag;
            }

            return null;
        }

        /// <summary>
        /// Write a spell to a blank page.
        /// Replaces "BlankPage" flag with spell ID flag and updates page name.
        /// </summary>
        /// <param name="page">The page item (must be blank)</param>
        /// <param name="spellId">The spell ID to write (e.g., "SpellLightning")</param>
        /// <param name="spellName">The display name of the spell (e.g., "Lightning")</param>
        public static void SetSpellOnPage(Item page, string spellId, string spellName)
        {
            if (page?.Flags == null || string.IsNullOrWhiteSpace(spellId))
                return;

            // Create new flag list without BlankPage
            var newFlags = new List<string>();
            foreach (var f in page.Flags)
            {
                if (string.IsNullOrWhiteSpace(f))
                    continue;
                if (string.Equals(f, BLANK_PAGE_FLAG, StringComparison.OrdinalIgnoreCase))
                    continue; // Skip BlankPage
                newFlags.Add(f); // Keep all other flags (like Stackable)
            }

            // Add spell ID flag
            if (!newFlags.Any(f => string.Equals(f, spellId, StringComparison.OrdinalIgnoreCase)))
            {
                newFlags.Add(spellId);
            }

            // Replace flags list
            page.Flags = newFlags;

            // Update page name
            if (!string.IsNullOrWhiteSpace(spellName))
            {
                page.Name = $"Page of {spellName}";
            }

            UpdateFlagsJson(page);
        }

        /// <summary>
        /// Clear a spell from a page, reverting it to blank.
        /// Removes spell ID flag and adds back "BlankPage" flag.
        /// </summary>
        /// <param name="page">The page item</param>
        public static void ClearSpellFromPage(Item page)
        {
            if (page?.Flags == null)
                return;

            // Remove any spell ID flags (non-system flags except BlankPage)
            page.Flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                !f.StartsWith(PAGE_NUMBER_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f, BLANK_PAGE_FLAG, StringComparison.OrdinalIgnoreCase));

            // Add back BlankPage flag if not present
            if (!page.Flags.Any(f => string.Equals(f, BLANK_PAGE_FLAG, StringComparison.OrdinalIgnoreCase)))
            {
                page.Flags.Add(BLANK_PAGE_FLAG);
            }

            // Reset name
            page.Name = "Blank Page";

            UpdateFlagsJson(page);
        }

        /// <summary>
        /// Validate that a page belongs to a specific book.
        /// Checks OwnerType and OwnerID match book's ItemID.
        /// </summary>
        /// <param name="page">The page item to validate</param>
        /// <param name="book">The book container</param>
        /// <returns>True if page is in the book, false otherwise</returns>
        public static bool ValidatePageInBook(Item page, Item book)
        {
            if (page == null || book == null)
                return false;

            return string.Equals(page.OwnerType, "Item", StringComparison.OrdinalIgnoreCase) &&
                   page.OwnerID == book.ItemID;
        }

        /// <summary>
        /// Sort a list of pages by their page numbers.
        /// Pages without numbers are placed at the end in order encountered.
        /// </summary>
        /// <param name="pages">List of page items to sort</param>
        /// <returns>Sorted list by page number</returns>
        public static List<Item> SortPagesByNumber(List<Item> pages)
        {
            if (pages == null)
                return new List<Item>();

            var sorted = pages
                .OrderBy(p =>
                {
                    var pageNum = GetPageNumber(p);
                    return pageNum ?? int.MaxValue; // Unnumbered pages go to end
                })
                .ThenBy(p => p.ItemID) // Stable sort by ItemID for ties
                .ToList();

            return sorted;
        }

        /// <summary>
        /// Renumber all pages in a book starting from a given number.
        /// Updates Page:[N] flags on all pages to be sequential.
        /// </summary>
        /// <param name="pages">List of pages to renumber (should be sorted)</param>
        /// <param name="startFromNum">Starting page number (default 1)</param>
        public static void RenumberPages(List<Item> pages, int startFromNum = 1)
        {
            if (pages == null || pages.Count == 0)
                return;

            int currentNum = startFromNum;
            foreach (var page in pages)
            {
                SetPageNumber(page, currentNum);
                currentNum++;
            }
        }

        /// <summary>
        /// Create a new blank page item for use in a book.
        /// </summary>
        /// <param name="itemFactory">ItemFactory to create the page</param>
        /// <param name="bookItemId">ItemID of the book container this page will be in</param>
        /// <returns>New blank page item</returns>
        public static Item CreateBlankPage(ItemFactory itemFactory, int bookItemId)
        {
            if (itemFactory == null)
                return null;

            // Create page from template
            var page = itemFactory.CreateItem("page", "Item", bookItemId, 0, 1);
            if (page == null)
                return null;

            // Ensure BlankPage flag
            if (page.Flags == null)
                page.Flags = new List<string>();

            if (!page.Flags.Any(f => string.Equals(f, BLANK_PAGE_FLAG, StringComparison.OrdinalIgnoreCase)))
            {
                page.Flags.Add(BLANK_PAGE_FLAG);
            }

            page.Name = "Blank Page";
            UpdateFlagsJson(page);

            return page;
        }

        /// <summary>
        /// Update the FlagsJson property based on the current Flags list.
        /// Needed for persistence since ItemPersistence serializes FlagsJson.
        /// </summary>
        /// <param name="page">The page item to update</param>
        private static void UpdateFlagsJson(Item page)
        {
            if (page == null)
                return;

            try
            {
                if (page.Flags == null || page.Flags.Count == 0)
                {
                    page.FlagsJson = "[]";
                }
                else
                {
                    page.FlagsJson = System.Text.Json.JsonSerializer.Serialize(page.Flags);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PagesManager] Failed to update FlagsJson: {ex.Message}");
            }
        }
    }
}