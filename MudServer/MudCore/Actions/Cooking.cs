using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudPersistence;
using MudServer.MudUtils;
using MudNetworking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MudServer.MudCore.Actions
{
    // Centralized cooking service: loads recipes from Data/Recipes and manages in-memory cook jobs.
    // Minimal, maintainable implementation:
    // - StartCooking validates recipe/tool/room/container/ingredients
    // - Consumes ingredients at start (transactional)
    // - Schedules a background completion task that creates the result item inside the container
    // - Sends system messages to the room and updates player inventories
    public static class Cooking
    {
        private static readonly object _loadLock = new();
        private static bool _loaded = false;
        private static List<Recipe> _recipes = new();

        private class Recipe
        {
            public string Id { get; set; } = "";
            public ResultSpec Result { get; set; } = new ResultSpec();
            public List<IngredientSpec> Ingredients { get; set; } = new List<IngredientSpec>();
            public List<string> Container { get; set; } = new List<string>();
            public string RequiredSkill { get; set; } = "None";
            public int MinSkillLevel { get; set; } = 0;
            public List<string> ToolsRequired { get; set; } = new List<string>();
            public List<string> RoomFlags { get; set; } = new List<string>();
            public int TimeSeconds { get; set; } = 0;
            public bool AllowSubstitutions { get; set; } = false;
            public Dictionary<string, List<string>> Substitutions { get; set; } = new();
            public string Notes { get; set; } = "";
        }

        private class ResultSpec
        {
            public string Template { get; set; } = "";
            public int Quantity { get; set; } = 1;
        }

        private class IngredientSpec
        {
            public string Template { get; set; } = "";
            public int Qty { get; set; } = 1;
        }

        private class CookJob
        {
            public string RecipeId { get; set; } = "";
            public int ContainerItemId { get; set; }
            public DateTime EndAtUtc { get; set; }
            public World World { get; set; } = null!;
            public SqliteConnection Conn { get; set; } = null!;
        }

        private static readonly List<CookJob> _activeJobs = new();

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;

                var baseDir = AppContext.BaseDirectory;
                var recipesDir = Path.Combine(baseDir, "Data", "Recipes");
                string folderToUse = null;

                // 1) Prefer the output directory path (where the app runs)
                if (Directory.Exists(recipesDir))
                {
                    folderToUse = recipesDir;
                }
                else
                {
                    // 2) Walk upward to find the project directory (contains .csproj) and try its Data/Recipes
                    var dir = new DirectoryInfo(baseDir);
                    DirectoryInfo? projectDir = null;
                    while (dir != null && dir.Exists)
                    {
                        if (dir.GetFiles("*.csproj").Any())
                        {
                            projectDir = dir;
                            break;
                        }
                        dir = dir.Parent;
                    }

                    if (projectDir != null)
                    {
                        var tryPath = Path.Combine(projectDir.FullName, "Data", "Recipes");
                        if (Directory.Exists(tryPath))
                            folderToUse = tryPath;
                    }

                    // 3) Try common editor-run layout (two levels up -> Data/Recipes)
                    if (folderToUse == null)
                    {
                        try
                        {
                            var alt = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Data", "Recipes"));
                            if (Directory.Exists(alt))
                                folderToUse = alt;
                        }
                        catch { /* best-effort */ }
                    }
                }

                if (folderToUse == null)
                {
                    Console.WriteLine($"[COOK] Recipes folder not found: {recipesDir}");
                    // Also log helpful hint about where we looked
                    Console.WriteLine("[COOK] Looked for recipes in output folder, project folder and fallback ../..\\Data\\Recipes. Place your recipe JSON files in one of these locations.");
                    _loaded = true;
                    return;
                }

                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                foreach (var file in Directory.EnumerateFiles(folderToUse, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var r = JsonSerializer.Deserialize<Recipe>(json, opts);
                        if (r != null && !string.IsNullOrWhiteSpace(r.Id))
                            _recipes.Add(r);
                        else
                            Console.WriteLine($"[COOK] Skipping recipe file (invalid): {file}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[COOK] Failed to load recipe {file}: {ex.Message}");
                    }
                }

                _loaded = true;
                Console.WriteLine($"[COOK] Loaded {_recipes.Count} recipe(s) from {folderToUse}");
            }
        } // end EnsureLoaded

        // Minimal public entrypoint called from CommandHandler
        public static Message? TryStartCooking(Player player, string containerName, World world, SqliteConnection conn)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (world == null) return MessageHandler.System("Server world unavailable.");
            if (conn == null) return MessageHandler.System("Server DB unavailable.");
            EnsureLoaded();

            // Strict parser options for explicit container resolution:
            // - include descriptor/material tokens so "polished pot" or "iron pot" match correctly
            // - disallow starts-with to require stronger matches for two-object commands
            // - prefer room on ties by disabling PreferInventoryOnTie/PreferEquippedOnTie
            var strictOpts = new ItemParserOptions
            {
                IncludeDescriptorTokens = true,
                IncludeMaterialTokens = true,
                AllowStartsWith = false,
                PreferInventoryOnTie = false,
                PreferEquippedOnTie = false
            };

            // First, try parser across Any scope (room + inventory + equipped) using strict options.
            Item? container = null;
            ItemResolutionResult? pres = null;
            try
            {
                pres = ItemParser.ResolveForAny(player, containerName, strictOpts);
                if (pres?.Winner != null) container = pres.Winner;
            }
            catch { /* best-effort */ }

            // If parser gave us a carried instance, attempt to resolve unambiguously against the room inventory
            // using the same strict options — prefer a room-held exact match when available.
            try
            {
                if (container != null && player.Inventory != null && player.Inventory.Contains(container))
                {
                    var room = player.CurrentRoom;
                    if (room != null && room.Inventory != null && room.Inventory.Count > 0)
                    {
                        var roomPres = ItemParser.ResolveCandidates(room.Inventory, player, containerName, strictOpts);
                        if (roomPres?.Winner != null)
                        {
                            Console.WriteLine($"[COOK][DEBUG] Parser returned carried instance; switching to room winner ItemID={roomPres.Winner.ItemID}");
                            container = roomPres.Winner;
                            pres = roomPres;
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            // Legacy fallback: attempt to find a container by name in player's inventory / room if parser failed.
            if (container == null) container = InventoryManager.FindContainer(player, containerName);
            if (container == null) return MessageHandler.System($"No container matching '{containerName}' found.");
            if (!string.Equals(container.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System($"{container.Name} is not a container.");

            var roomRef = player.CurrentRoom;
            if (roomRef == null) return MessageHandler.System("You are not in a valid room.");

            // Enforce: container must be in the room to cook. If the container instance is in player's inventory, fail.
            if (player.Inventory != null && player.Inventory.Contains(container))
            {
                return MessageHandler.System($"You must place {container.Name} in the room before you can cook with it.");
            }

            // Ensure that the resolved container is actually present in this room (defensive)
            if (roomRef.Inventory == null || !roomRef.Inventory.Any(i => i != null && i.ItemID == container.ItemID))
                return MessageHandler.System($"{container.Name} must be placed in this room to start cooking.");

            // Lock check
            var lockMsg = KeyLocks.EnsureAccessible(conn, player, container);
            if (lockMsg != null) return MessageHandler.System(lockMsg);

            // Diagnostic logging (contents / flags)
            try
            {
                Console.WriteLine($"[COOK][DEBUG] Start cooking request: player={player?.Name} containerName='{containerName}' ResolvedItemID={container.ItemID} TemplateID='{container.TemplateID}' Name='{container.Name}' RoomID={roomRef.RoomID}");
                var roomFlags = roomRef.Flags ?? new List<string>();
                Console.WriteLine($"[COOK][DEBUG] Room flags: {(roomFlags.Count == 0 ? "(none)" : string.Join(", ", roomFlags))}");
                var invContents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                Console.WriteLine($"[COOK][DEBUG] Container contents count (DB): {invContents.Count}");
                foreach (var c in invContents)
                    Console.WriteLine($"[COOK][DEBUG]   Content: ItemID={c.ItemID} TemplateID='{c.TemplateID}' Name='{c.Name}' Qty={c.Quantity} FlagsJson='{c.FlagsJson}'");
            }
            catch { /* best-effort */ }

            // Find candidate recipes for this container (by TemplateID or Name)
            var candidateRecipes = _recipes.Where(r =>
                (r.Container == null || r.Container.Count == 0) ||
                r.Container.Any(cn =>
                    string.Equals(cn, container.TemplateID, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cn, container.Name, StringComparison.OrdinalIgnoreCase)
                )
            ).ToList();

            if (candidateRecipes.Count == 0)
                return MessageHandler.System("No recipes can be prepared in that container.");

            // Ensure room has required flags if all recipes require flags
            bool anyRecipeWithoutRoomFlag = candidateRecipes.Any(r => r.RoomFlags == null || r.RoomFlags.Count == 0);
            if (!anyRecipeWithoutRoomFlag)
            {
                bool anyMatchesRoom = candidateRecipes.Any(r => r.RoomFlags != null && r.RoomFlags.Count > 0 &&
                    roomRef.Flags != null && roomRef.Flags.Any(f => r.RoomFlags.Any(rf => string.Equals(rf, f, StringComparison.OrdinalIgnoreCase))));
                if (!anyMatchesRoom)
                {
                    var need = candidateRecipes.First().RoomFlags.FirstOrDefault() ?? "required equipment";
                    return MessageHandler.System($"You need a {need} here to cook!");
                }
            }

            // Evaluate candidate recipes for ingredient/tool/room requirements
            bool ingredientShortageFound = false;
            Recipe? matched = null;
            var chosenIngredientItems = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);

            foreach (var recipe in candidateRecipes)
            {
                // Room flag check
                if (recipe.RoomFlags != null && recipe.RoomFlags.Count > 0)
                {
                    if (roomRef.Flags == null || !roomRef.Flags.Any(f => recipe.RoomFlags.Any(rf => string.Equals(rf, f, StringComparison.OrdinalIgnoreCase))))
                        continue;
                }

                // Tools required: must be held by player
                if (recipe.ToolsRequired != null && recipe.ToolsRequired.Count > 0)
                {
                    bool allHeld = true;
                    string missingTool = "";
                    foreach (var toolReq in recipe.ToolsRequired)
                    {
                        if (!player.Inventory?.Any(i =>
                            i != null &&
                            (
                                (!string.IsNullOrWhiteSpace(i.TemplateID) && string.Equals(i.TemplateID, toolReq, StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(i.Name) && string.Equals(i.Name, toolReq, StringComparison.OrdinalIgnoreCase))
                            )
                            && ((i.Flags != null && i.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase)))
                                || (!string.IsNullOrEmpty(i.FlagsJson) && i.FlagsJson.IndexOf("\"Held\"", StringComparison.OrdinalIgnoreCase) >= 0))
                        ) ?? false)
                        {
                            allHeld = false;
                            missingTool = toolReq;
                            break;
                        }
                    }
                    if (!allHeld) return MessageHandler.System($"You need to hold a {missingTool} in order to cook that.");
                }

                // Load container contents and attempt ingredient mapping
                var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                var tempMap = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
                bool ingredientsOk = true;

                var remaining = contents.Select(it => new Item
                {
                    ItemID = it.ItemID,
                    TemplateID = it.TemplateID,
                    Name = it.Name,
                    Quantity = it.Quantity,
                    Flags = it.Flags,
                    FlagsJson = it.FlagsJson,
                    Weight = it.Weight,
                    ItemCategory = it.ItemCategory
                }).ToList();

                foreach (var ing in recipe.Ingredients)
                {
                    int need = Math.Max(1, ing.Qty);
                    var matchedItems = new List<Item>();

                    var directMatches = remaining.Where(r => !string.IsNullOrWhiteSpace(r.TemplateID) &&
                                                             string.Equals(r.TemplateID, ing.Template, StringComparison.OrdinalIgnoreCase)).ToList();
                    int have = directMatches.Sum(d => Math.Max(1, d.Quantity));
                    if (have >= need)
                    {
                        int want = need;
                        foreach (var dm in directMatches)
                        {
                            int take = Math.Min(want, dm.Quantity);
                            matchedItems.Add(new Item { ItemID = dm.ItemID, TemplateID = dm.TemplateID, Name = dm.Name, Quantity = take, Weight = dm.Weight, Flags = dm.Flags, FlagsJson = dm.FlagsJson, ItemCategory = dm.ItemCategory });
                            dm.Quantity -= take;
                            want -= take;
                            if (dm.Quantity <= 0) remaining.Remove(dm);
                            if (want <= 0) break;
                        }
                    }
                    else if (recipe.AllowSubstitutions && recipe.Substitutions != null && recipe.Substitutions.TryGetValue(ing.Template, out var subs) && subs.Count > 0)
                    {
                        var candidates = new List<Item>();
                        candidates.AddRange(directMatches);
                        foreach (var s in subs)
                        {
                            var sm = remaining.Where(r => !string.IsNullOrWhiteSpace(r.TemplateID) && string.Equals(r.TemplateID, s, StringComparison.OrdinalIgnoreCase)).ToList();
                            candidates.AddRange(sm);
                        }
                        int totalHave = candidates.Sum(c => Math.Max(1, c.Quantity));
                        if (totalHave >= need)
                        {
                            int want = need;
                            foreach (var c in candidates)
                            {
                                int take = Math.Min(want, c.Quantity);
                                matchedItems.Add(new Item { ItemID = c.ItemID, TemplateID = c.TemplateID, Name = c.Name, Quantity = take, Weight = c.Weight, Flags = c.Flags, FlagsJson = c.FlagsJson, ItemCategory = c.ItemCategory });
                                c.Quantity -= take;
                                want -= take;
                                if (c.Quantity <= 0) remaining.Remove(c);
                                if (want <= 0) break;
                            }
                        }
                        else
                        {
                            ingredientsOk = false;
                        }
                    }
                    else
                    {
                        ingredientsOk = false;
                    }

                    if (!ingredientsOk || matchedItems.Count == 0)
                    {
                        ingredientsOk = false;
                        break;
                    }

                    tempMap[ing.Template] = matchedItems;
                }

                if (!ingredientsOk)
                {
                    ingredientShortageFound = true;
                    continue;
                }

                matched = recipe;
                chosenIngredientItems = tempMap;
                Console.WriteLine($"[COOK] Recipe '{recipe.Id}' matched for container ItemID={container.ItemID}");
                break;
            } // end candidate loop

            if (matched == null)
            {
                if (ingredientShortageFound)
                    return MessageHandler.System("Are you sure you're not missing something?");
                return MessageHandler.System("No known recipe matches the contents of that container.");
            }

            // Consume ingredients transactionally (writer queue or local tx) - unchanged from previous logic
            try
            {
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        foreach (var kv in chosenIngredientItems)
                        {
                            foreach (var alloc in kv.Value)
                            {
                                if (alloc.Quantity <= 0) continue;

                                using var q = wConn.CreateCommand();
                                q.Transaction = wTx;
                                q.CommandText = "SELECT Quantity FROM Item WHERE ItemID = @id";
                                q.Parameters.AddWithValue("@id", alloc.ItemID);
                                var obj = q.ExecuteScalar();
                                int dbQty = 1;
                                if (obj != null && obj is not DBNull)
                                    dbQty = Convert.ToInt32(obj);

                                int remainingQty = dbQty - alloc.Quantity;
                                if (remainingQty <= 0)
                                {
                                    using var del = wConn.CreateCommand();
                                    del.Transaction = wTx;
                                    del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                    del.Parameters.AddWithValue("@id", alloc.ItemID);
                                    del.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var upd = wConn.CreateCommand();
                                    upd.Transaction = wTx;
                                    upd.CommandText = "UPDATE Item SET Quantity = @q WHERE ItemID = @id";
                                    upd.Parameters.AddWithValue("@q", remainingQty);
                                    upd.Parameters.AddWithValue("@id", alloc.ItemID);
                                    upd.ExecuteNonQuery();
                                }
                            }
                        }

                        // persist cooking flag on container
                        try
                        {
                            using var sel = wConn.CreateCommand();
                            sel.Transaction = wTx;
                            sel.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id";
                            sel.Parameters.AddWithValue("@id", container.ItemID);
                            var fobj = sel.ExecuteScalar();
                            string existingJson = fobj == null || fobj is DBNull ? "[]" : fobj.ToString() ?? "[]";
                            List<string> flagsList;
                            try { flagsList = JsonSerializer.Deserialize<List<string>>(existingJson) ?? new List<string>(); } catch { flagsList = new List<string>(); }

                            flagsList.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Cooking", StringComparison.OrdinalIgnoreCase));
                            flagsList.Add($"Cooking:{matched.Id}");
                            var newFlagsJson = JsonSerializer.Serialize(flagsList);

                            using var updFlags = wConn.CreateCommand();
                            updFlags.Transaction = wTx;
                            updFlags.CommandText = "UPDATE Item SET Flags = @flags WHERE ItemID = @id";
                            updFlags.Parameters.AddWithValue("@flags", newFlagsJson);
                            updFlags.Parameters.AddWithValue("@id", container.ItemID);
                            updFlags.ExecuteNonQuery();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[COOK] Failed to persist container cooking flag inside writer tx for ItemID={container.ItemID}: {ex.Message}");
                            throw;
                        }

                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException)
                {
                    // fallback local tx (unchanged)
                    EnsureOpen(conn);
                    using var tx = conn.BeginTransaction();
                    try
                    {
                        foreach (var kv in chosenIngredientItems)
                        {
                            foreach (var alloc in kv.Value)
                            {
                                if (alloc.Quantity <= 0) continue;

                                using var q = conn.CreateCommand();
                                q.Transaction = tx;
                                q.CommandText = "SELECT Quantity FROM Item WHERE ItemID = @id";
                                q.Parameters.AddWithValue("@id", alloc.ItemID);
                                var obj = q.ExecuteScalar();
                                int dbQty = 1;
                                if (obj != null && obj is not DBNull)
                                    dbQty = Convert.ToInt32(obj);

                                int remainingQty = dbQty - alloc.Quantity;
                                if (remainingQty <= 0)
                                {
                                    using var del = conn.CreateCommand();
                                    del.Transaction = tx;
                                    del.CommandText = "DELETE FROM Item WHERE ItemID = @id";
                                    del.Parameters.AddWithValue("@id", alloc.ItemID);
                                    del.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var upd = conn.CreateCommand();
                                    upd.Transaction = tx;
                                    upd.CommandText = "UPDATE Item SET Quantity = @q WHERE ItemID = @id";
                                    upd.Parameters.AddWithValue("@q", remainingQty);
                                    upd.Parameters.AddWithValue("@id", alloc.ItemID);
                                    upd.ExecuteNonQuery();
                                }
                            }
                        }

                        // update cooking flag locally
                        using var sel = conn.CreateCommand();
                        sel.Transaction = tx;
                        sel.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id";
                        sel.Parameters.AddWithValue("@id", container.ItemID);
                        var fobj = sel.ExecuteScalar();
                        string existingJson = fobj == null || fobj is DBNull ? "[]" : fobj.ToString() ?? "[]";
                        List<string> flagsList;
                        try { flagsList = JsonSerializer.Deserialize<List<string>>(existingJson) ?? new List<string>(); } catch { flagsList = new List<string>(); }

                        flagsList.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Cooking", StringComparison.OrdinalIgnoreCase));
                        flagsList.Add($"Cooking:{matched.Id}");
                        var newFlagsJson = JsonSerializer.Serialize(flagsList);

                        using var updFlags = conn.CreateCommand();
                        updFlags.Transaction = tx;
                        updFlags.CommandText = "UPDATE Item SET Flags = @flags WHERE ItemID = @id";
                        updFlags.Parameters.AddWithValue("@flags", newFlagsJson);
                        updFlags.Parameters.AddWithValue("@id", container.ItemID);
                        updFlags.ExecuteNonQuery();

                        tx.Commit();
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COOK] Failed consuming ingredients for recipe {matched.Id}: {ex}");
                return MessageHandler.System("Failed to consume ingredients due to a server error.");
            }

            // Update in-memory flags and broadcast (unchanged)
            try
            {
                var roomItem = roomRef.Inventory.FirstOrDefault(i => i.ItemID == container.ItemID);
                if (roomItem != null)
                {
                    try
                    {
                        using var q = conn.CreateCommand();
                        q.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id";
                        q.Parameters.AddWithValue("@id", container.ItemID);
                        var obj = q.ExecuteScalar();
                        var flagsJson = obj == null || obj is DBNull ? "[]" : obj.ToString() ?? "[]";
                        roomItem.Flags = JsonSerializer.Deserialize<List<string>>(flagsJson) ?? new List<string>();
                        roomItem.FlagsJson = flagsJson;
                        container.Flags = roomItem.Flags;
                        container.FlagsJson = roomItem.FlagsJson;
                    }
                    catch
                    {
                        var fl = container.Flags ?? new List<string>();
                        fl.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Cooking", StringComparison.OrdinalIgnoreCase));
                        fl.Add($"Cooking:{matched.Id}");
                        container.Flags = fl;
                        container.FlagsJson = JsonSerializer.Serialize(fl);
                        if (roomItem != null) { roomItem.Flags = container.Flags; roomItem.FlagsJson = container.FlagsJson; }
                    }
                }

                foreach (var p in roomRef.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(roomRef, roomRef.Npcs.Select(n => n.DisplayName).ToList(), roomRef.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), world));
                }
            }
            catch { /* best-effort */ }

            // Enqueue cook job (unchanged)
            var job = new CookJob
            {
                RecipeId = matched.Id,
                ContainerItemId = container.ItemID,
                EndAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, matched.TimeSeconds)),
                World = world
            };

            lock (_activeJobs) { _activeJobs.Add(job); }

            foreach (var p in roomRef.Players)
            {
                if (p == player) continue;
                MessageHandler.SendSystem(p, $"You begin to cook {matched.Result.Template}. The cooking will finish in {matched.TimeSeconds} seconds.");
            }

            return MessageHandler.System($"You begin to cook {matched.Result.Template}. It will be ready in {matched.TimeSeconds} seconds.");
        } // end TryStartCooking

        private static void FinishCooking(CookJob job, Recipe recipe, SqliteConnection conn)
        {
            if (job == null || recipe == null)
            {
                Console.WriteLine("[COOK] Invalid job or recipe in FinishCooking.");
                return;
            }

            // Prefer running finalization on the single-writer queue.
            // The writer-path implementation is extracted to FinishCookingWriter so Tick can
            // schedule it directly. If the queue is not available, fall back to the direct path.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    FinishCookingWriter(wConn, wTx, job, recipe);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(10));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct completion using provided conn (legacy behavior)
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COOK] Failed to enqueue finish job for Container={job.ContainerItemId} Recipe={recipe.Id}: {ex}");
                // fall back to direct completion
            }

            // Direct completion (fallback)
            try
            {
                EnsureOpen(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ItemID, TemplateID, OwnerType, OwnerID, RoomID, OwnerAreaID FROM Item WHERE ItemID = @id";
                cmd.Parameters.AddWithValue("@id", job.ContainerItemId);
                using var rdr = cmd.ExecuteReader();
                if (!rdr.Read())
                {
                    Console.WriteLine($"[COOK] Container ItemID={job.ContainerItemId} missing at completion for recipe {recipe.Id}.");
                    return;
                }

                int dbItemId = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                string tmpl = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                string ownerType = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                int ownerId = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
                int roomId = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                int ownerArea = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                rdr.Close();

                // Remove cooking flag from DB
                try
                {
                    using var sel = conn.CreateCommand();
                    sel.CommandText = "SELECT Flags FROM Item WHERE ItemID = @id";
                    sel.Parameters.AddWithValue("@id", job.ContainerItemId);
                    var flagsObj = sel.ExecuteScalar();
                    string fj = flagsObj == null || flagsObj is DBNull ? "[]" : flagsObj.ToString() ?? "[]";
                    List<string> flagsList;
                    try { flagsList = JsonSerializer.Deserialize<List<string>>(fj) ?? new List<string>(); }
                    catch { flagsList = new List<string>(); }

                    flagsList.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Cooking", StringComparison.OrdinalIgnoreCase));
                    var newFj = JsonSerializer.Serialize(flagsList);

                    using var upd = conn.CreateCommand();
                    upd.CommandText = "UPDATE Item SET Flags = @flags WHERE ItemID = @id";
                    upd.Parameters.AddWithValue("@flags", newFj);
                    upd.Parameters.AddWithValue("@id", job.ContainerItemId);
                    upd.ExecuteNonQuery();

                    // Update in-memory container instances (rooms and player inventories)
                    try
                    {
                        if (job.World != null)
                        {
                            foreach (var rm in job.World.Rooms)
                            {
                                var it = rm.Inventory.FirstOrDefault(i => i.ItemID == job.ContainerItemId);
                                if (it != null)
                                {
                                    it.Flags = flagsList;
                                    it.FlagsJson = newFj;
                                }
                            }

                            foreach (var ply in job.World.Players)
                            {
                                if (ply.Inventory == null) continue;
                                var it2 = ply.Inventory.FirstOrDefault(i => i.ItemID == job.ContainerItemId);
                                if (it2 != null)
                                {
                                    it2.Flags = flagsList;
                                    it2.FlagsJson = newFj;
                                }
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[COOK] Failed clearing Cooking flag for Container {job.ContainerItemId}: {ex}");
                }

                if (job.World == null || job.World.ItemFactory == null)
                {
                    Console.WriteLine("[COOK] World or ItemFactory unavailable at cook completion.");
                    return;
                }

                var resultItem = job.World.ItemFactory.CreateItem(recipe.Result.Template, "Item", job.ContainerItemId, roomId, Math.Max(1, recipe.Result.Quantity));

                try
                {
                    ItemPersistence.SaveItem(conn, resultItem);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[COOK] Failed to save cooked item {resultItem.TemplateID}: {ex}");
                    return;
                }

                var world = job.World;
                var rroom = world.Rooms.FirstOrDefault(r => r.RoomID == roomId);
                if (rroom != null)
                {
                    try
                    {
                        foreach (var p in rroom.Players)
                            MessageHandler.SendSystem(p, $"A {resultItem.Name} has finished cooking!");
                    }
                    catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COOK] FinishCooking fallback failed for Container={job.ContainerItemId}, Recipe={recipe.Id}: {ex}");
            }
            
        } // end FinishCooking

        // Writer-path finalizer: performs the flag removal and result insertion using the provided
        // writer connection + transaction so the operations are atomic with other writer jobs.
        // Throws on error so caller (writer queue) can handle rollback.
        private static void FinishCookingWriter(SqliteConnection wConn, SqliteTransaction wTx, CookJob job, Recipe recipe)
        {
            if (wConn == null) throw new ArgumentNullException(nameof(wConn));
            if (wTx == null) throw new ArgumentNullException(nameof(wTx));
            if (job == null || recipe == null)
            {
                Console.WriteLine("[COOK] Invalid job or recipe passed to FinishCookingWriter.");
                return;
            }

            try
            {
                // Read container row (inside writer tx)
                using var cmd = wConn.CreateCommand();
                cmd.Transaction = wTx;
                cmd.CommandText = "SELECT ItemID, TemplateID, OwnerType, OwnerID, RoomID, OwnerAreaID, Flags FROM Item WHERE ItemID = @id";
                cmd.Parameters.AddWithValue("@id", job.ContainerItemId);

                using var rdr = cmd.ExecuteReader();
                if (!rdr.Read())
                {
                    Console.WriteLine($"[COOK] Container ItemID={job.ContainerItemId} missing at writer completion for recipe {recipe.Id}.");
                    return;
                }

                int dbItemId = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                string tmpl = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                string ownerType = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                int ownerId = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
                int roomId = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                int ownerArea = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                string flagsJson = rdr.IsDBNull(6) ? "[]" : rdr.GetString(6);
                rdr.Close();

                // Remove Cooking flag
                List<string> flagsList;
                try { flagsList = JsonSerializer.Deserialize<List<string>>(flagsJson) ?? new List<string>(); }
                catch { flagsList = new List<string>(); }

                flagsList.RemoveAll(f => !string.IsNullOrWhiteSpace(f) && f.StartsWith("Cooking", StringComparison.OrdinalIgnoreCase));
                var newFlagsJson = JsonSerializer.Serialize(flagsList);

                using var upd = wConn.CreateCommand();
                upd.Transaction = wTx;
                upd.CommandText = "UPDATE Item SET Flags = @flags WHERE ItemID = @id";
                upd.Parameters.AddWithValue("@flags", newFlagsJson);
                upd.Parameters.AddWithValue("@id", job.ContainerItemId);
                upd.ExecuteNonQuery();

                // Create cooked result and persist inside writer tx using ItemPersistence internal contract
                if (job.World == null || job.World.ItemFactory == null)
                {
                    Console.WriteLine("[COOK] World or ItemFactory unavailable at writer completion.");
                    return;
                }

                var resultItem = job.World.ItemFactory.CreateItem(recipe.Result.Template, "Item", job.ContainerItemId, roomId, Math.Max(1, recipe.Result.Quantity));

                // Persist result using writer connection + tx to avoid nested enqueueing
                ItemPersistence.SaveItem(wConn, resultItem, wTx);

                // Update in-memory container instances so clients see the pot cleared and notify players
                try
                {
                    if (job.World != null)
                    {
                        foreach (var rm in job.World.Rooms)
                        {
                            var it = rm.Inventory.FirstOrDefault(i => i.ItemID == job.ContainerItemId);
                            if (it != null)
                            {
                                it.Flags = flagsList;
                                it.FlagsJson = newFlagsJson;
                            }
                        }

                        foreach (var ply in job.World.Players)
                        {
                            if (ply.Inventory == null) continue;
                            var it2 = ply.Inventory.FirstOrDefault(i => i.ItemID == job.ContainerItemId);
                            if (it2 != null)
                            {
                                it2.Flags = flagsList;
                                it2.FlagsJson = newFlagsJson;
                            }
                        }

                        var roomObj = job.World.Rooms.FirstOrDefault(r => r.RoomID == roomId);
                        if (roomObj != null)
                        {
                            try
                            {
                                foreach (var p in roomObj.Players)
                                    MessageHandler.SendSystem(p, $"A {resultItem.Name} has finished cooking!");
                            }
                            catch { /* best-effort */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[COOK] In-memory update/notify failed in writer finalizer: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COOK] FinishCookingWriter failed for Container={job.ContainerItemId}, Recipe={recipe.Id}: {ex}");
                throw;
            }
        }

        public static void Tick(World world, SqliteConnection conn)
        {
            if (world == null || conn == null) return;

            List<CookJob> toComplete = new List<CookJob>();
            lock (_activeJobs)
            {
                var now = DateTime.UtcNow;
                toComplete.AddRange(_activeJobs.Where(j => j.EndAtUtc <= now));
            }

            foreach (var job in toComplete)
            {
                // find recipe
                var recipe = _recipes.FirstOrDefault(r => string.Equals(r.Id, job.RecipeId, StringComparison.OrdinalIgnoreCase));
                if (recipe == null)
                {
                    // remove orphan jobs
                    lock (_activeJobs) { _activeJobs.RemoveAll(j => j == job); }
                    continue;
                }

                try
                {
                    // Prefer executing finish logic on the writer queue so completion runs with writer connection+tx.
                    try
                    {
                        MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            FinishCookingWriter(wConn, wTx, job, recipe);
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(10));
                    }
                    catch (InvalidOperationException)
                    {
                        // Queue not started — fallback to direct finish on provided connection
                        FinishCooking(job, recipe, conn);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[COOK] Failed to schedule finish on writer queue for Container={job.ContainerItemId}: {ex}");
                        // fallback to direct
                        FinishCooking(job, recipe, conn);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[COOK] Error finishing job Container={job.ContainerItemId} Recipe={job.RecipeId}: {ex}");
                }

                lock (_activeJobs)
                {
                    _activeJobs.RemoveAll(j => j.ContainerItemId == job.ContainerItemId && j.RecipeId == job.RecipeId);
                }
            }
        } // end Tick

        private static void EnsureOpen(SqliteConnection conn)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        }
    }
}