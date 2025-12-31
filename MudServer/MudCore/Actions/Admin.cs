using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MudCore.Entities;
using MudCore.Templates;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using MudServer.MudUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MudServer
{
    // Encapsulates admin command logic previously in CommandHandler.
    // Construct with the same runtime dependencies and call from CommandHandler.
    public class AdminService
    {
        private readonly World _world;
        private readonly SqliteConnection _db;
        
        private static bool setVerboseServerLogs = false;

        public AdminService(World world, SqliteConnection dbConnection)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _db = dbConnection ?? throw new ArgumentNullException(nameof(dbConnection));
        }

        // --------------------------------------------------------------------
        // NEW: admin coin exchange command
        // Syntax:
        //   - exchange <from> to <to>           (convert all of <from> that can be converted)
        //   - exchange <n> <from> to <to>       (convert exactly n units of <from>)
        // Accepts "to" or "for" as separator, and unit names: copper/silver/gold (or c/s/g).
        //
        // Examples:
        //   exchange copper to silver
        //   exchange 234 copper to silver
        //   exchange 5 silver to copper
        //
        // This operates on the invoking player's inventory (admin-only for now).
        // --------------------------------------------------------------------
        public Message? HandleExchange(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null || _world.ItemFactory == null) return MessageHandler.System("Server item factory unavailable.");

            // Tokenize and find separator - allow "to" or "for"
            var toks = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (toks.Count < 3) return MessageHandler.System("Usage: exchange [<n>] <from> to <to>");

            // remove leading "exchange"
            if (string.Equals(toks[0], "exchange", StringComparison.OrdinalIgnoreCase))
                toks.RemoveAt(0);

            int sepIdx = toks.FindIndex(t => string.Equals(t, "to", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "for", StringComparison.OrdinalIgnoreCase));
            if (sepIdx <= 0 || sepIdx >= toks.Count - 1)
                return MessageHandler.System("Usage: exchange [<n>] <from> to <to>");

            // Left side (amount?/from), right side (to unit)
            var left = toks.Take(sepIdx).ToArray();
            var right = toks.Skip(sepIdx + 1).ToArray();

            if (right.Length == 0) return MessageHandler.System("Missing target unit (gold/silver/copper).");

            string toUnit = right[0];
            string fromUnit;
            long requestedUnits = -1; // -1 => convert all available

            if (left.Length == 1)
            {
                // either "copper" or "100" (invalid)
                if (int.TryParse(left[0], out var n))
                    return MessageHandler.System("Specify a unit: exchange <n> <from> to <to>");
                fromUnit = left[0];
            }
            else if (left.Length >= 2)
            {
                // Expect "<n> <from>"
                if (!int.TryParse(left[0], out var n))
                    return MessageHandler.System("Invalid amount. Usage: exchange [<n>] <from> to <to>");
                requestedUnits = Math.Max(0, n);
                fromUnit = left[1];
            }
            else
            {
                return MessageHandler.System("Usage: exchange [<n>] <from> to <to>");
            }

            // Normalize unit names
            bool TryResolveUnit(string token, out string unitKey)
            {
                unitKey = token?.Trim().ToLowerInvariant() ?? "";
                if (unitKey == "c" || unitKey == "copper") { unitKey = "copper"; return true; }
                if (unitKey == "s" || unitKey == "silver") { unitKey = "silver"; return true; }
                if (unitKey == "g" || unitKey == "gold") { unitKey = "gold"; return true; }
                return false;
            }

            if (!TryResolveUnit(fromUnit, out var fromKey))
                return MessageHandler.System($"Unknown unit '{fromUnit}'. Use copper/silver/gold.");

            if (!TryResolveUnit(toUnit, out var toKey))
                return MessageHandler.System($"Unknown unit '{toUnit}'. Use copper/silver/gold.");

            if (fromKey == toKey) return MessageHandler.System("Nothing to do: same unit specified for from/to.");

            // Map to template IDs and copper values
            (string templateId, long copperValue) GetTemplateInfo(string unit)
            {
                return unit switch
                {
                    "copper" => ("coin_copper", 1L),
                    "silver" => ("coin_silver", (long)Currency.CopperPerSilver),
                    "gold" => ("coin_gold", Currency.CopperPerGold),
                    _ => ("", 0L)
                };
            }

            var fromInfo = GetTemplateInfo(fromKey);
            var toInfo = GetTemplateInfo(toKey);

            if (string.IsNullOrEmpty(fromInfo.templateId) || string.IsNullOrEmpty(toInfo.templateId))
                return MessageHandler.System("Unsupported currency unit.");

            // Sum available from-units in player's inventory
            int availableFromUnits = 0;
            try
            {
                availableFromUnits = player.Inventory?
                    .Where(i => i != null && string.Equals(i.TemplateID, fromInfo.templateId, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => Math.Max(1, i.Quantity)) ?? 0;
            }
            catch
            {
                availableFromUnits = 0;
            }

            if (availableFromUnits == 0)
                return MessageHandler.System($"You have no {fromKey} to exchange.");

            long unitsToConvert = requestedUnits < 0 ? availableFromUnits : Math.Min(availableFromUnits, requestedUnits);
            if (unitsToConvert <= 0) return MessageHandler.System("Nothing to convert.");

            // Compute total copper represented by the unitsToConvert
            long totalCopper = unitsToConvert * fromInfo.copperValue;

            // Compute how many whole target units can be produced
            long targetUnits = totalCopper / toInfo.copperValue;
            if (targetUnits == 0)
            {
                // Nothing convertible (e.g., trying to convert 1 silver -> gold)
                return MessageHandler.System($"Not enough {fromKey} to convert to {toKey}.");
            }

            // Compute consumed from-units (integer)
            long consumedFromUnits = (targetUnits * toInfo.copperValue) / fromInfo.copperValue;
            // Safety clamp
            consumedFromUnits = Math.Min(consumedFromUnits, unitsToConvert);

            // Perform inventory modifications:
            // 1) consume consumedFromUnits of 'from' coin from player's inventory
            // 2) add targetUnits of 'to' coin to player's inventory (merge if stackable)
            try
            {
                ConsumeFromCoinStacks(player, fromInfo.templateId, (int)consumedFromUnits);

                // Add target coins: prefer merging into existing stack if present
                AddCoinsToPlayer(player, toInfo.templateId, (int)targetUnits);

                // Persist DB and notify clients
                _world.SaveDirtyRooms(_db); // no-op for inventories but keep consistent (rooms may be marked)
                MessageHandler.EnqueuePlayerUpdate(player);

                // Compute leftover (if the admin requested a specific amount less than available, that amount may leave remainder)
                long leftoverFromUnits = unitsToConvert - consumedFromUnits;
                string msg;
                if (requestedUnits < 0)
                {
                    // Converted as much as possible from all available of 'from'
                    // There may still be leftover from-units that didn't make a full target
                    // e.g., converting all copper: 234c -> 2s and 34c remain (leftoverFromUnits is 34 when from=copper)
                    long leftoverCopper = (availableFromUnits * fromInfo.copperValue) - (targetUnits * toInfo.copperValue);
                    var leftoverCurrency = Currency.FromCopper(leftoverCopper);
                    msg = $"Converted {consumedFromUnits} {fromKey} into {targetUnits} {toKey}. Leftover: {leftoverCurrency.Silver} silver {leftoverCurrency.Copper} copper (if applicable).";
                }
                else
                {
                    // Admin specified exact amountUnits; leftover is relative to that requested amount
                    if (leftoverFromUnits > 0)
                    {
                        msg = $"Converted {consumedFromUnits} {fromKey} into {targetUnits} {toKey}. {leftoverFromUnits} {fromKey} were not convertible and remain.";
                    }
                    else
                    {
                        msg = $"Converted {consumedFromUnits} {fromKey} into {targetUnits} {toKey}.";
                    }
                }

                return MessageHandler.System(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EXCHANGE][ERROR] {ex}");
                return MessageHandler.System("Exchange failed due to a server error.");
            }
        }

        // Helper: consume 'unitsToConsume' items of templateId from player's inventory (updates DB + in-memory)
        private void ConsumeFromCoinStacks(Player player, string templateId, int unitsToConsume)
        {
            if (unitsToConsume <= 0) return;
            var stacks = player.Inventory
                .Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.ItemID)
                .ToList();

            int remaining = unitsToConsume;
            foreach (var st in stacks)
            {
                if (remaining <= 0) break;
                int take = Math.Min(st.Quantity, remaining);
                st.Quantity -= take;
                remaining -= take;

                if (st.Quantity <= 0)
                {
                    // remove item
                    MudPersistence.ItemPersistence.DeleteItem(_db, st.ItemID);
                }
                else
                {
                    // persist updated quantity
                    MudPersistence.ItemPersistence.SaveItem(_db, st);
                }
            }

            // remove zero-qty stacks from in-memory inventory
            player.Inventory.RemoveAll(i => i != null && i.Quantity <= 0);
        }

        // Helper: add coin stacks (merge with existing stack when possible)
        private void AddCoinsToPlayer(Player player, string templateId, int unitsToAdd)
        {
            if (unitsToAdd <= 0) return;

            // Try to find existing stack to merge into
            var existing = player.Inventory.FirstOrDefault(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Quantity += unitsToAdd;
                MudPersistence.ItemPersistence.SaveItem(_db, existing);
                return;
            }

            // No existing stack, create a new item via ItemFactory
            var roomId = player.CurrentRoom?.RoomID ?? 0;
            var item = _world.ItemFactory.CreateItem(templateId, "Player", player.DbPlayerID, roomId, unitsToAdd);
            // Ensure owner fields are correct
            item.OwnerType = "Player";
            item.OwnerID = player.DbPlayerID;
            MudPersistence.ItemPersistence.SaveItem(_db, item);

            // Add to player's in-memory inventory (if not present)
            player.Inventory.Add(item);
        }
        // --------------------------------------------------------------------


        public Message? HandleCreateArea(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            string dir = "";
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateArea <Direction>");

            dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            // Prompt for area name
            player.PendingAction = new PendingAction
            {
                Command = "CreateArea",
                Direction = dir
            };

            return MessageHandler.System("Enter name for the new area:");
        }

        public Message? HandleCreateRoom(Player player, string command)
        {
            var parts = command.Trim().Split(' ', 2);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: CreateRoom <Direction>");

            string dir = parts[1].Trim().ToUpper();
            if (!IsValidDirection(dir))
                return MessageHandler.System("Invalid direction.");

            if (player.CurrentRoom.Exits.Any(e =>
                    e.Direction.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            {
                return MessageHandler.System($"Cannot create room. An exit already exists in direction {dir}.");
            }

            player.PendingAction = new PendingAction
            {
                Command = "CreateRoom",
                Direction = dir
            };

            return MessageHandler.System("Enter name for the new room:");
        }

        public Message? HandleEditRoom(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null)
                return MessageHandler.System("You are not inside a valid room.");

            player.PendingAction = new PendingAction
            {
                Command = "EditRoom",
                Step = "SelectField"
            };

            return MessageHandler.System(
                $"Editing room: {room.Name}\n" +
                "Choose a field to edit:\n" +
                "1) Name\n" +
                "2) Description\n" +
                "3) DistantDescription\n" +
                "4) Generate Description from Prompt\n" +
                "Type the option name/number, or type EndEdit to finish."
            );
        }

        // HandleEditRoomPending: processes pending steps for EditRoom flows (Name/Description/Distant + AI generation).
        // This method assumes pa.Command == "EditRoom" and returns a non-null Message if it consumes/handles the pending input.
        public Message? HandleEditRoomPending(Player player, PendingAction pa, string input)
        {
            if (!player.IsAdmin)
            {
                player.PendingAction = null!;
                return MessageHandler.System("Permission denied. Admins only.");
            }

            var room = player.CurrentRoom;
            if (room == null)
            {
                player.PendingAction = null!;
                return MessageHandler.System("You are not inside a valid room.");
            }

            // Cancel/finish
            if (!string.IsNullOrEmpty(input) && input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
            {
                player.PendingAction = null!;
                return MessageHandler.System("Room editing ended.");
            }

            // Selection menu
            if (pa.Step == "SelectField")
            {
                if (input == "1" || input.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    pa.Step = "EditName";
                    return MessageHandler.System("Enter new name for this room:");
                }
                if (input == "2" || input.Equals("Description", StringComparison.OrdinalIgnoreCase))
                {
                    pa.Step = "EditDescription";
                    return MessageHandler.System("Enter new description for this room:");
                }
                if (input == "3" || input.Equals("DistantDescription", StringComparison.OrdinalIgnoreCase))
                {
                    pa.Step = "EditDistantDescription";
                    return MessageHandler.System("Enter new distant description for this room:");
                }
                if (input == "4" || input.Equals("GenerateDescription", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("Generate Description", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("Generate Description from Prompt", StringComparison.OrdinalIgnoreCase))
                {
                    // Initialize typed pending data if missing
                    var edata = pa.Data as EditRoomPendingData ?? new EditRoomPendingData { RoomID = room.RoomID };
                    pa.Data = edata;
                    pa.Step = "CollectKeywords";
                    return MessageHandler.System("Enter comma-separated keywords to guide the AI (or type 'none' to skip). Type 'EndEdit' to cancel.");
                }

                return MessageHandler.System("Invalid option. Type Name, Description, DistantDescription, GenerateDescription, 1, 2, 3, 4, or EndEdit.");
            }

            // Simple text edits
            if (pa.Step == "EditName")
            {
                room.Name = input ?? "";
                room.IsDirty = true;
                _world.SaveDirtyRooms(_db);
                player.PendingAction = null!;
                return MessageHandler.System($"Room name updated to '{room.Name}'.");
            }

            if (pa.Step == "EditDescription")
            {
                room.Description = input ?? "";
                room.IsDirty = true;
                _world.SaveDirtyRooms(_db);
                player.PendingAction = null!;
                return MessageHandler.System("Room description updated.");
            }

            if (pa.Step == "EditDistantDescription")
            {
                room.DistantDescription = input ?? "";
                room.IsDirty = true;
                _world.SaveDirtyRooms(_db);
                player.PendingAction = null!;
                return MessageHandler.System("Room distant description updated.");
            }

            // AI generation flow: CollectKeywords -> GenerateDescriptionConfirm
            if (pa.Step == "CollectKeywords")
            {
                var edata = pa.Data as EditRoomPendingData ?? new EditRoomPendingData { RoomID = room.RoomID };

                var keywordsRaw = (input ?? "").Trim();
                if (string.Equals(keywordsRaw, "none", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(keywordsRaw))
                    edata.Keywords = Array.Empty<string>();
                else
                    edata.Keywords = keywordsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                .Select(s => s.Trim())
                                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                                .Take(12)
                                                .ToArray();

                pa.Data = edata;

                // Build user payload for the model
                var exitsList = new List<string>();
                try
                {
                    if (room.Exits != null)
                    {
                        foreach (var ex in room.Exits)
                        {
                            var toRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == ex.ToRoomID);
                            var toName = toRoom?.Name ?? "";
                            var dirWord = Movement.DirectionDisplayName(ex.Direction);
                            var label = string.IsNullOrWhiteSpace(ex.DiscoverKeyword) ? ex.Direction : ex.DiscoverKeyword;
                            exitsList.Add($"{dirWord}: {label} -> {toName}");
                        }
                    }
                }
                catch { /* best-effort */ }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Title: {room.Name}");
                if (exitsList.Count > 0) sb.AppendLine($"Exits: {string.Join(", ", exitsList)}");
                if (edata.Keywords != null && edata.Keywords.Length > 0) sb.AppendLine($"Keywords: {string.Join(", ", edata.Keywords)}");
                sb.AppendLine($"ExistingDescription: {room.Description ?? ""}");
                sb.AppendLine();
                sb.AppendLine("Produce a JSON object exactly like: {\"Description\":\"<full text>\",\"DistantDescription\":\"<short text>\"} with Description and DistantDescription only.");

                var lmUrl = Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://127.0.0.1:8000/";
                var lmModel = Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "default";
                var systemPrompt = Environment.GetEnvironmentVariable("LMSTUDIO_SYSTEM_PROMPT") ??
                    "You are a concise immersive worldbuilding assistant for a text MUD. Use only information provided in the structured user payload (title, exits, keywords, existingDescription). Do NOT invent objects, plants, buildings, or people not present in the inputs. If information is not provided, be vague. Produce exactly one JSON object with keys 'Description' and 'DistantDescription'. Do not include any extra text. Use neutral, evocative language; no markup. Description should be 60-300 characters. DistantDescription should be one or two brief sentences.";

                try
                {
                    using var lmClient = new MudServer.Integrations.LmStudioClient(lmUrl);
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));

                    // Prefer using the wrapper if available
                    try
                    {
                        var (generatedDesc, generatedDistant) = lmClient.GenerateRoomDescriptionsAsync(
                            lmModel,
                            systemPrompt,
                            sb.ToString(),
                            maxDescriptionChars: 4000,
                            maxDistantChars: 500,
                            temperature: 0.7,
                            ct: cts.Token).GetAwaiter().GetResult();

                        edata.DraftDescription = generatedDesc ?? "";
                        edata.DraftDistant = generatedDistant ?? "";
                    }
                    catch (Exception wrapperEx)
                    {
                        // Fallback: call chat completion directly and attempt to extract JSON if wrapper not supported
                        var messages = new List<(string role, string content)>
                {
                    ("system", systemPrompt),
                    ("user", sb.ToString())
                };
                        using var respDoc = lmClient.CreateChatCompletionAsync(lmModel, messages, temperature: 0.7, ct: cts.Token)
                                                   .GetAwaiter().GetResult();

                        string raw = "";
                        try
                        {
                            var root = respDoc.RootElement;
                            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                            {
                                var first = choices[0];
                                if (first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content))
                                    raw = content.GetString() ?? "";
                                else if (first.TryGetProperty("text", out var textProp))
                                    raw = textProp.GetString() ?? "";
                            }
                            if (string.IsNullOrWhiteSpace(raw) && root.TryGetProperty("output_text", out var outText) && outText.ValueKind == JsonValueKind.String)
                                raw = outText.GetString() ?? "";
                            if (string.IsNullOrWhiteSpace(raw) && root.TryGetProperty("output", out var outNode))
                            {
                                if (outNode.ValueKind == JsonValueKind.String) raw = outNode.GetString() ?? "";
                                else if (outNode.ValueKind == JsonValueKind.Array && outNode.GetArrayLength() > 0)
                                    raw = string.Join("\n", outNode.EnumerateArray().Select(e => e.GetString() ?? ""));
                            }
                        }
                        catch { raw = ""; }

                        raw = (raw ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(raw))
                            return MessageHandler.System("LLM returned empty description. Try different keywords or try again later.");

                        // Extract JSON substring
                        int firstIdx = raw.IndexOf('{');
                        int lastIdx = raw.LastIndexOf('}');
                        if (firstIdx >= 0 && lastIdx > firstIdx)
                            raw = raw.Substring(firstIdx, lastIdx - firstIdx + 1);

                        try
                        {
                            using var jd = JsonDocument.Parse(raw);
                            var root = jd.RootElement;
                            string desc = root.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                            string distant = root.TryGetProperty("DistantDescription", out var dd) && dd.ValueKind == JsonValueKind.String ? dd.GetString() ?? "" : "";

                            if (string.IsNullOrWhiteSpace(desc))
                                return MessageHandler.System("LLM returned JSON but Description was empty. Try different keywords.");

                            edata.DraftDescription = desc.Trim();
                            edata.DraftDistant = distant.Trim();
                        }
                        catch (Exception exJson)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse JSON from LM output: {exJson.Message}");
                            Console.WriteLine($"[LLM][WARN] Raw LM output: {raw}");
                            return MessageHandler.System("LLM returned output that could not be parsed as JSON. Try different keywords or try again later.");
                        }
                    }

                    // save draft to pending and move to confirm
                    pa.Data = edata;
                    pa.Step = "GenerateDescriptionConfirm";

                    var preview = $"Preview - Description:\n{edata.DraftDescription}\n\nDistantDescription:\n{edata.DraftDistant}\n\nType 'confirm' to save, 'edit' to enter new keywords, or 'cancel'.";
                    return MessageHandler.System(preview);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LLM] Generation failed: {ex.Message}");
                    return MessageHandler.System($"LLM generation failed: {ex.Message}");
                }
            }

            // Confirmation step: save or re-edit or cancel
            if (pa.Step == "GenerateDescriptionConfirm")
            {
                var edata = pa.Data as EditRoomPendingData;
                string draftDesc = edata?.DraftDescription ?? "";
                string draftDistant = edata?.DraftDistant ?? "";

                var cmd = (input ?? "").Trim().ToLowerInvariant();
                if (cmd == "confirm")
                {
                    if (string.IsNullOrWhiteSpace(draftDesc))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("No draft description available to save.");
                    }

                    room.Description = draftDesc;
                    room.DistantDescription = draftDistant;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    // Broadcast updated room payloads to occupants
                    foreach (var p in room.Players)
                    {
                        p.PendingMessageQueue.Add(MessageHandler.Room(
                            room,
                            room.Npcs.Select(n => n.DisplayName).ToList(),
                            room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                            _world));
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System("Generated description saved and broadcast to room.");
                }
                else if (cmd == "edit")
                {
                    pa.Step = "CollectKeywords";
                    return MessageHandler.System("Enter comma-separated keywords to guide the AI (or 'none' to skip). Type 'EndEdit' to cancel.");
                }
                else if (cmd == "cancel" || cmd == "endedit")
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Generation cancelled.");
                }
                else
                {
                    return MessageHandler.System("Invalid option. Type 'confirm' to save, 'edit' to re-enter keywords, or 'cancel'.");
                }
            }

            return null;
        }

        public Message? HandleEditExit(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return MessageHandler.System("Usage: editexit <direction>");

            string dir = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(dir))
                return MessageHandler.System("Usage: editexit <direction>");

            // Normalize direction (store raw string on pending; matching will normalize on lookup)
            player.PendingAction = new PendingAction
            {
                Command = "EditExit",
                Direction = dir,
                Step = "SelectField"
            };

            var room = player.CurrentRoom;
            string exitName = dir;
            if (room != null)
            {
                // Try to resolve a friendly title for the exit if present
                var match = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    exitName = match.Direction;
            }

            return MessageHandler.System(
                $"Editing exit: {exitName}\n" +
                "Choose a field to edit:\n" +
                "1) HasDoor\n" +
                "Type the option name/number, or type EndEdit to finish."
            );
        }

        public Message? HandleCreateNpc(Player player, string templateId)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            try
            {
                // Create in-memory NPC via NpcFactory
                var newNpc = _world.NpcFactory.CreateNpc(templateId, room);

                // Persist the NPC and assign NpcID
                int newId = MudPersistence.NpcPersistence.InsertNpc(_db, newNpc);
                newNpc.NpcID = newId;

                // Diagnostic: when environment var set, dump DB row + in-memory values to help debug missing fields
                try
                {
                    var diag = (Environment.GetEnvironmentVariable("NPC_CREATE_DIAG") ?? "").Trim();
                    if (diag == "1")
                    {
                        try
                        {
                            Console.WriteLine($"[NPC-DIAG] Created Npc in-memory snapshot: NpcID={newNpc.NpcID} TemplateID='{newNpc.TemplateID}' Name='{newNpc.Name}' RoomID={newNpc.CurrentRoomID}");
                            try { Console.WriteLine($"[NPC-DIAG] In-memory Flags: {System.Text.Json.JsonSerializer.Serialize(newNpc.Flags ?? new System.Collections.Generic.List<string>())}"); } catch { }
                            try { Console.WriteLine($"[NPC-DIAG] In-memory AiBehaviors (legacy strings): {System.Text.Json.JsonSerializer.Serialize(newNpc.AiBehaviors ?? new System.Collections.Generic.List<string>())}"); } catch { }
                            try { Console.WriteLine($"[NPC-DIAG] Template AiBehaviorConfigs: {System.Text.Json.JsonSerializer.Serialize(newNpc.Template?.AiBehaviorConfigs ?? new System.Collections.Generic.List<NpcTemplate.AiBehaviorConfig>())}"); } catch { }
                            try { Console.WriteLine($"[NPC-DIAG] Template.Flags: {System.Text.Json.JsonSerializer.Serialize(newNpc.Template?.Flags ?? new System.Collections.Generic.List<string>())}"); } catch { }

                            using var cmd = _db.CreateCommand();
                            cmd.CommandText = "SELECT * FROM Npc WHERE NpcID = @id;";
                            cmd.Parameters.AddWithValue("@id", newNpc.NpcID);
                            using var rdr = cmd.ExecuteReader();
                            if (rdr.Read())
                            {
                                var cols = new System.Text.StringBuilder();
                                cols.AppendLine($"[NPC-DIAG] DB row for NpcID={newNpc.NpcID}:");
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    try
                                    {
                                        var name = rdr.GetName(i);
                                        object val = rdr.IsDBNull(i) ? "(null)" : rdr.GetValue(i);
                                        cols.AppendLine($"  {name} = {val}");
                                    }
                                    catch (Exception exCol)
                                    {
                                        cols.AppendLine($"  (col {i}) read error: {exCol.Message}");
                                    }
                                }
                                Console.WriteLine(cols.ToString());
                            }
                            else
                            {
                                Console.WriteLine($"[NPC-DIAG] No DB row found for newly inserted NpcID={newNpc.NpcID}.");
                            }
                        }
                        catch (Exception exDiag)
                        {
                            Console.WriteLine($"[NPC-DIAG][ERROR] Diagnostic read failed: {exDiag.Message}");
                        }
                    }
                }
                catch { /* best-effort only; do not block creation */ }

                // Ensure world list contains it
                if (!_world.Npcs.Any(n => n.NpcID == newNpc.NpcID))
                {
                    _world.Npcs.Add(newNpc);
                    Console.WriteLine($"[WORLD][TRACE] NPC created and added: NpcID={newNpc.NpcID} Name='{newNpc.Name}' RoomID={room.RoomID}");
                }
                else
                {
                    Console.WriteLine($"[WORLD][TRACE] NPC already in world list: NpcID={newNpc.NpcID}");
                }

                // If template specifies equipment, instantiate items and assign to NPC
                try
                {
                    var equipTemplates = newNpc.Template?.EquipmentTemplateIds;
                    if (equipTemplates != null && equipTemplates.Count > 0 && _world.ItemFactory != null)
                    {
                        foreach (var equipTemplateId in equipTemplates)
                        {
                            try
                            {
                                var item = _world.ItemFactory.CreateItem(equipTemplateId, "NPC", newNpc.NpcID, newNpc.CurrentRoom?.RoomID ?? 0);
                                MudPersistence.ItemPersistence.SaveItem(_db, item);
                                newNpc.Inventory.Add(item);
                            }
                            catch (Exception iex)
                            {
                                Console.WriteLine($"[NPC] Failed to create/equip item '{equipTemplateId}' for NPC {newNpc.Name}: {iex.Message}");
                            }
                        }
                    }
                }
                catch (Exception exEquip)
                {
                    Console.WriteLine($"[NPC] Equipment assignment error for NPC {newNpc.Name}: {exEquip.Message}");
                }

                // Broadcast updated room so clients immediately see the NPC
                foreach (var p in room.Players)
                {
                    p.PendingMessageQueue.Add(MessageHandler.Room(
                        room,
                        room.Npcs.Select(n => n.DisplayName).ToList(),
                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                        _world));
                }

                // send NPC actions to log
                foreach (var n in room.Npcs)
                {
                    if (!string.IsNullOrWhiteSpace(n.LastAction))
                    {
                        foreach (var recipient in room.Players)
                            MessageHandler.SendSystem(recipient, n.LastAction);
                    }
                }

                return MessageHandler.System($"NPC '{newNpc.Name}' created (ID {newId}).");
            }
            catch (KeyNotFoundException)
            {
                return MessageHandler.System($"NPC template not found: {templateId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPC] Failed to create NPC from template {templateId}: {ex}");
                return MessageHandler.System($"Failed to create NPC: {ex.Message}");
            }
        }//end HandleCreateNpc

        public Message? AddItem(Player player, string itemTemplateId, int quantity = 1)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (_world.ItemFactory == null)
                return MessageHandler.System("Item factory not available.");

            if (string.IsNullOrWhiteSpace(itemTemplateId))
                return MessageHandler.System("Usage: additem <item> [quantity] OR additem <item> to <player|container>");

            // Allow admin to specify target: "itemName to targetName"
            string payload = itemTemplateId.Trim();
            string itemNamePart = payload;
            string targetPart = "";
            int idxTo = payload.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (idxTo >= 0)
            {
                itemNamePart = payload.Substring(0, idxTo).Trim();
                targetPart = payload.Substring(idxTo + " to ".Length).Trim();
            }

            try
            {
                // If no explicit target, preserve existing behavior but disallow liquids in room.
                if (string.IsNullOrWhiteSpace(targetPart))
                {
                    // instantiate from template so we can inspect flags (but do not yet persist)
                    var preview = _world.ItemFactory.CreateItem(itemNamePart, "Room", room.RoomID, room.RoomID, Math.Max(1, quantity));
                    bool isLiquid = (preview.Flags != null && preview.Flags.Any(f => string.Equals(f, "IsLiquid", StringComparison.OrdinalIgnoreCase)))
                                     || (!string.IsNullOrEmpty(preview.FlagsJson) && preview.FlagsJson.IndexOf("\"IsLiquid\"", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isLiquid)
                    {
                        return MessageHandler.System("You cannot spawn liquids directly into a room. Use: additem <item> to <player> or additem <item> to <container>");
                    }

                    // fall back to existing room-add flow (re-use InventoryManager)
                    return InventoryManager.AddItemToRoom(_world, _db, player, itemNamePart, quantity);
                }

                // Resolve target: prefer player match, then container match (player's inventory then room)
                // 1) Try find a player by name (exact, starts-with, case-insensitive)
                Player? targetPlayer = null;
                try
                {
                    var candidates = _world.Players
                        .Where(p => p != null && p.IsAuthenticated)
                        .ToList();

                    // exact
                    targetPlayer = candidates.FirstOrDefault(p => string.Equals(p.Name, targetPart, StringComparison.OrdinalIgnoreCase));
                    // starts-with
                    if (targetPlayer == null)
                        targetPlayer = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name.StartsWith(targetPart, StringComparison.OrdinalIgnoreCase));
                    // token contains
                    if (targetPlayer == null)
                        targetPlayer = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name.IndexOf(targetPart, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch { targetPlayer = null; }

                // 2) If not player, try to resolve a container (parser-first then legacy fallback)
                Item? targetContainer = null;
                if (targetPlayer == null)
                {
                    try
                    {
                        var optsCont = new ItemParserOptions { IncludeDescriptorTokens = false, IncludeMaterialTokens = false, AllowStartsWith = true };
                        var pres = ItemParser.ResolveForAny(player, targetPart, optsCont);
                        if (pres?.Winner != null) targetContainer = pres.Winner;
                    }
                    catch { /* best-effort */ }

                    if (targetContainer == null)
                    {
                        try { targetContainer = InventoryManager.FindContainer(player, targetPart); } catch { targetContainer = null; }
                    }
                }

                if (targetPlayer == null && targetContainer == null)
                {
                    return MessageHandler.System($"No player or container matching '{targetPart}' found.");
                }

                // Create item with correct owner fields and persist
                if (targetPlayer != null)
                {
                    // create item owned by the target player
                    var newItem = _world.ItemFactory.CreateItem(itemNamePart, "Player", targetPlayer.DbPlayerID != 0 ? targetPlayer.DbPlayerID : 0,
                                                                targetPlayer.CurrentRoom?.RoomID ?? room.RoomID, Math.Max(1, quantity));
                    newItem.OwnerType = "Player";
                    newItem.OwnerID = targetPlayer.DbPlayerID != 0 ? targetPlayer.DbPlayerID : 0;
                    newItem.RoomID = targetPlayer.CurrentRoom?.RoomID ?? room.RoomID;
                    newItem.OwnerAreaID = targetPlayer.CurrentRoom?.AreaID ?? room.AreaID;

                    // disallow placing liquids into a room-owned player when target player is null (shouldn't happen) - already covered
                    // persist
                    ItemPersistence.SaveItem(_db, newItem);

                    // Add to in-memory inventory for target player
                    try
                    {
                        targetPlayer.Inventory.Add(newItem);
                    }
                    catch
                    {
                        // best-effort; even if in-memory add fails, DB contains the item.
                    }

                    // notify target player and room occupants
                    try { MessageHandler.EnqueuePlayerUpdate(targetPlayer); } catch { }
                    try
                    {
                        foreach (var p in (targetPlayer.CurrentRoom ?? room).Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(targetPlayer.CurrentRoom ?? room,
                                (targetPlayer.CurrentRoom ?? room).Npcs.Select(n => n.DisplayName).ToList(),
                                (targetPlayer.CurrentRoom ?? room).Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world));
                        }
                    }
                    catch { }

                    return MessageHandler.System($"Added {newItem.Name} to {targetPlayer.Name}.");
                }
                else // container target
                {
                    if (targetContainer == null) return MessageHandler.System("Target container resolution failed.");

                    // Ensure container can accept liquids / items (respect Accept flag)
                    var accepts = InventoryManager.ParseAcceptList(targetContainer);
                    if (!accepts.Contains("All", StringComparer.OrdinalIgnoreCase))
                    {
                        // instantiate preview to get category
                        var preview = _world.ItemFactory.CreateItem(itemNamePart, "Item", targetContainer.ItemID, targetContainer.RoomID, Math.Max(1, quantity));
                        if (!accepts.Contains(preview.ItemCategory ?? "", StringComparer.OrdinalIgnoreCase))
                        {
                            return MessageHandler.System($"Container '{targetContainer.Name}' does not accept items of category '{preview.ItemCategory}'.");
                        }
                    }

                    // create item owned by the container (OwnerType='Item', OwnerID = container.ItemID)
                    var contItem = _world.ItemFactory.CreateItem(itemNamePart, "Item", targetContainer.ItemID, targetContainer.RoomID, Math.Max(1, quantity));
                    contItem.OwnerType = "Item";
                    contItem.OwnerID = targetContainer.ItemID;
                    contItem.RoomID = targetContainer.RoomID;
                    contItem.OwnerAreaID = targetContainer.OwnerAreaID;

                    // Persist into DB
                    ItemPersistence.SaveItem(_db, contItem);

                    // No in-memory container contents list is maintained here (client will re-query via inspect).
                    // Notify owner and room occupants so UI updates
                    try
                    {
                        // If container is carried by a player, notify that player
                        var ownerPlayer = (player.Inventory ?? new List<Item>()).Any(i => i.ItemID == targetContainer.ItemID)
                                          ? player
                                          : targetContainer.RoomID == room.RoomID ? player : null;

                        // Broadcast room update
                        foreach (var p in (player.CurrentRoom ?? room).Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(player.CurrentRoom ?? room,
                                (player.CurrentRoom ?? room).Npcs.Select(n => n.DisplayName).ToList(),
                                (player.CurrentRoom ?? room).Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world));
                        }

                        if (ownerPlayer != null) MessageHandler.EnqueuePlayerUpdate(ownerPlayer);
                    }
                    catch { /* best-effort */ }

                    return MessageHandler.System($"Added {contItem.Name} into {targetContainer.Name}.");
                }
            }
            catch (KeyNotFoundException)
            {
                return MessageHandler.System($"Item template not found: {itemNamePart}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AddItem][ERROR] AddItem failed: {ex}");
                return MessageHandler.System($"Failed to add item: {ex.Message}");
            }
        } // end AddItem

        public Message? RemoveItem(Player player, string identifier)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            var target = room.Inventory.FirstOrDefault(i => i.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                                                   || i.TemplateID.Equals(identifier, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                return MessageHandler.System($"No item '{identifier}' found in the room.");

            target.OwnerType = "ROOM";
            target.OwnerID = 0;
            MudPersistence.ItemPersistence.SaveItem(_db, target);

            room.Inventory.Remove(target);
            room.IsDirty = true;
            _world.SaveDirtyRooms(_db);

            foreach (var p in room.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.Room(
                    room,
                    room.Npcs.Select(n => n.DisplayName).ToList(),
                    room.Players.Where(pl => pl != p).Select(pl => p.Name).ToList(),
                    _world));
            }

            foreach (var n in room.Npcs)
            {
                if (!string.IsNullOrWhiteSpace(n.LastAction))
                {
                    foreach (var recipient in room.Players)
                        MessageHandler.SendSystem(recipient, n.LastAction);
                }
            }

            return MessageHandler.System($"Item '{target.Name}' removed from the room.");
        }

        public Message? ListItems(Player player)
        {
            var room = player.CurrentRoom;
            if (room == null) return MessageHandler.System("You are not in a valid room.");

            if (room.Inventory == null || room.Inventory.Count == 0)
                return MessageHandler.System("This room contains no items.");

            var lines = room.Inventory.Select((it, idx) => $"{idx + 1}) {it.Name} (Template: {it.TemplateID})");
            return MessageHandler.System("Items in room:\n" + string.Join("\n", lines));
        }

        public Message? DebugItems(Player player)
        {
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");

            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT ItemID, TemplateID, Name, OwnerType, OwnerID, RoomID, OwnerAreaID FROM Item ORDER BY ItemID DESC";
                using var rdr = cmd.ExecuteReader();

                int n = 0;
                while (rdr.Read())
                {
                    n++;
                    var id = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                    var tid = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                    var name = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                    var otype = rdr.IsDBNull(3) ? "" : rdr.GetString(3);
                    var oid = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                    var rid = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5);
                    var oarea = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);

                    Console.WriteLine($"[DEBUGITEMS] id={id} tid='{tid}' name='{name}' otype='{otype}' oid={oid} rid={rid} oarea={oarea}");
                }

                return MessageHandler.System($"DebugItems: printed {n} row(s) to server log.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUGITEMS][ERROR] {ex}");
                return MessageHandler.System($"DebugItems failed: {ex.Message}");
            }
        }

        public Message? DebugRooms(Player player, string args = "")
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World not available.");

            try
            {
                IEnumerable<Room> rooms = _world.Rooms;

                // If an area id was supplied, try parse and filter
                if (!string.IsNullOrWhiteSpace(args))
                {
                    if (int.TryParse(args.Trim(), out var areaId))
                    {
                        rooms = rooms.Where(r => r.AreaID == areaId);
                    }
                    else
                    {
                        return MessageHandler.System("Usage: debugrooms [areaid] — areaid must be an integer.");
                    }
                }

                var list = rooms
                    .OrderBy(r => r.RoomID)
                    .Select(r => $"{r.RoomID}: {r.Name}")
                    .ToList();

                if (list.Count == 0)
                    return MessageHandler.System(string.IsNullOrWhiteSpace(args)
                        ? "No rooms found."
                        : $"No rooms found for AreaID = {args.Trim()}.");

                // Limit output length to avoid very large payloads; admin can refine by areaId
                const int maxLines = 500;
                if (list.Count > maxLines)
                {
                    var head = list.Take(maxLines).ToList();
                    head.Add($"... {list.Count - maxLines} more (specify an areaid to narrow results).");
                    return MessageHandler.System(string.Join("\n", head));
                }

                return MessageHandler.System(string.Join("\n", list));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][DEBUGROOMS] Failed: {ex.Message}");
                return MessageHandler.System("Failed to retrieve rooms due to a server error.");
            }
        }

        // NEW: admin Kill command handler (supports pending flow and immediate execution)
        // Usage:
        //   kill <npcname>                         -> removes one matching NPC in current room
        //   kill all <npcname> in room             -> removes all matching NPCs in current room
        //   kill all <npcname> in area             -> removes all matching NPCs in the current area
        // If called with just 'kill' this will set a pending action so admin can enter target next.
        public Message? HandleKill(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");
            if (player.CurrentRoom == null) return MessageHandler.System("You are not inside a valid room.");

            // Normalize input after 'kill'
            var payload = command ?? "";
            if (payload.StartsWith("kill", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring(4).Trim();

            // If no payload provided, enter pending flow (like CreateRoom)
            if (string.IsNullOrWhiteSpace(payload))
            {
                player.PendingAction = new PendingAction
                {
                    Command = "Kill",
                    Step = "SpecifyTarget"
                };
                return MessageHandler.System("Enter target to kill (examples: 'goblin' or 'all goblin in area' or 'all goblin in room'):");
            }

            // Parse pattern: optional leading "all", optional trailing "in room"/"in area"
            bool all = false;
            string scope = "room"; // "room" or "area"
            string rest = payload;

            if (rest.StartsWith("all ", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
                rest = rest.Substring(4).Trim();
            }

            // detect " in <scope>" suffix
            var idxIn = rest.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (idxIn >= 0)
            {
                var scopeTok = rest.Substring(idxIn + 4).Trim().ToLowerInvariant();
                rest = rest.Substring(0, idxIn).Trim();
                if (scopeTok.StartsWith("area")) scope = "area";
                else scope = "room";
            }

            var npcToken = rest.Trim();
            if (string.IsNullOrWhiteSpace(npcToken))
                return MessageHandler.System("Usage: kill [npcname] OR kill all [npcname] in [room|area]");

            try
            {
                // Determine target rooms
                List<Room> targetRooms;
                if (scope == "area")
                {
                    var curAreaId = player.CurrentRoom.AreaID;
                    targetRooms = _world.Rooms.Where(r => r.AreaID == curAreaId).ToList();
                }
                else
                {
                    targetRooms = new List<Room> { player.CurrentRoom };
                }

                var removedCount = 0;
                var affectedRooms = new HashSet<int>();

                foreach (var rm in targetRooms)
                {
                    if (rm.Npcs == null || rm.Npcs.Count == 0) continue;

                    // Find matches according to name/template heuristics
                    var matches = rm.Npcs.Where(n =>
                        (!string.IsNullOrWhiteSpace(n.Name) && string.Equals(n.Name, npcToken, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(n.TemplateID) && string.Equals(n.TemplateID, npcToken, StringComparison.OrdinalIgnoreCase)) ||
                        (n.Template != null && !string.IsNullOrWhiteSpace(n.Template.Id) && string.Equals(n.Template.Id, npcToken, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(n.Name) && n.Name.StartsWith(npcToken, StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (matches.Count == 0) continue;

                    // If not 'all', remove only a single match (prefer first)
                    var toRemove = all ? matches.ToList() : new List<Npc> { matches[0] };

                    foreach (var npc in toRemove)
                    {
                        try
                        {
                            // Remove NPC-owned items (best-effort)
                            try
                            {
                                if (npc.Inventory != null)
                                {
                                    foreach (var it in npc.Inventory.ToList())
                                    {
                                        try
                                        {
                                            if (it.ItemID != 0)
                                                MudPersistence.ItemPersistence.DeleteItem(_db, it.ItemID);
                                        }
                                        catch (Exception iex)
                                        {
                                            Console.WriteLine($"[KILL][WARN] Failed to delete item {it.ItemID} for NPC {npc.NpcID}: {iex.Message}");
                                        }
                                    }
                                }
                            }
                            catch { /* best-effort */ }

                            // Delete NPC DB row (best-effort)
                            try
                            {
                                if (npc.NpcID != 0)
                                    MudPersistence.NpcPersistence.DeleteNpc(_db, npc.NpcID);
                            }
                            catch (Exception ne)
                            {
                                Console.WriteLine($"[KILL][WARN] Failed to delete NPC row {npc.NpcID}: {ne.Message}");
                            }

                            // Remove from room and world lists
                            rm.Npcs.RemoveAll(n => n.NpcID == npc.NpcID);
                            _world.Npcs.RemoveAll(n => n.NpcID == npc.NpcID);

                            removedCount++;
                            affectedRooms.Add(rm.RoomID);

                            Console.WriteLine($"[KILL] Admin removed NPC '{npc.Name}' (NpcID={npc.NpcID}) from Room {rm.RoomID}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[KILL][ERROR] Failed removing NPC {npc?.NpcID}: {ex.Message}");
                        }

                        // If not removing 'all', break after first
                        if (!all) break;
                    }
                }

                // Persist any dirty room state (best-effort)
                try
                {
                    // Mark affected rooms dirty so SaveDirtyRooms persists related state if needed
                    foreach (var rid in affectedRooms)
                    {
                        var r = _world.Rooms.FirstOrDefault(rr => rr.RoomID == rid);
                        if (r != null) r.IsDirty = true;
                    }
                    _world.SaveDirtyRooms(_db);
                }
                catch { /* best-effort */ }

                // Notify players in affected rooms
                try
                {
                    foreach (var rid in affectedRooms)
                    {
                        var r = _world.Rooms.FirstOrDefault(rr => rr.RoomID == rid);
                        if (r == null) continue;
                        foreach (var p in r.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                r,
                                r.Npcs.Select(n => n.DisplayName).ToList(),
                                r.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                }
                catch { /* best-effort */ }

                if (removedCount == 0)
                {
                    return MessageHandler.System($"No matching NPCs found for '{npcToken}' in the specified scope ({scope}).");
                }

                return MessageHandler.System($"Removed {removedCount} NPC(s) matching '{npcToken}' in {scope}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADMIN][KILL] Failed: {ex.Message}");
                return MessageHandler.System("Kill command failed due to a server error.");
            }
        }

        // Handles admin-related pending actions: EditRoom, CreateArea, CreateRoom.
        // Returns non-null Message if handled and the pending action consumed.
        public Message? HandlePendingAction(Player player, PendingAction pa, string input)
        {
            if (pa == null) return null;

            // --- EditExit pending action ---
            if (pa.Command == "EditExit")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;
                if (room == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("You are not inside a valid room.");
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Edit exit cancelled.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("HasDoor", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditHasDoor";
                        return MessageHandler.System("Set HasDoor: type 't' for true or 'f' for false.");
                    }

                    return MessageHandler.System("Invalid option. Type '1' or 'HasDoor', or 'EndEdit'.");
                }

                if (pa.Step == "EditHasDoor")
                {
                    var v = input?.Trim().ToLowerInvariant();
                    if (v != "t" && v != "f" && v != "true" && v != "false")
                        return MessageHandler.System("Invalid value. Type 't' (true) or 'f' (false).");

                    bool newVal = (v == "t" || v == "true");

                    // Find the exit by normalized direction
                    var exit = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(pa.Direction), StringComparison.OrdinalIgnoreCase));
                    if (exit == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System($"No exit found for direction '{pa.Direction}'.");
                    }

                    // Transition from boolean HasDoor -> DoorId nullable:
                    // - true: create a new Doors row (closed by default) if one not already linked and assign DoorId
                    // - false: unlink DoorId (set to null). Do NOT delete Doors rows automatically.
                    try
                    {
                        if (newVal)
                        {
                            if (!exit.DoorId.HasValue)
                            {
                                // Enqueue an atomic writer job to create Doors row and upsert both mirrored RoomExit rows.
                                int newDoorId = DbWriteQueue.Instance.RunWriteAndWait<int>((conn, tx) =>
                                {
                                    // Insert Door
                                    using var ins = conn.CreateCommand();
                                    ins.Transaction = tx;
                                    ins.CommandText = "INSERT INTO Doors (IsOpen, DoorType, Flags) VALUES (0, NULL, NULL);";
                                    ins.ExecuteNonQuery();

                                    using var last = conn.CreateCommand();
                                    last.Transaction = tx;
                                    last.CommandText = "SELECT last_insert_rowid();";
                                    var vobj = last.ExecuteScalar();
                                    var createdId = Convert.ToInt32(vobj);

                                    // Helper upsert by RoomID+ToRoomID (deterministic)
                                    void UpsertExitInternal(int rRoomId, string dir, int toRoomId, int doorIdVal, bool isHidden, string? keyword)
                                    {
                                        using var upd = conn.CreateCommand();
                                        upd.Transaction = tx;
                                        upd.CommandText = @"
                                    UPDATE RoomExit
                                    SET Direction = @dir,
                                        IsHidden = @hidden,
                                        DiscoverKeyword = @keyword,
                                        DoorId = @doorId
                                    WHERE RoomID = @rid AND ToRoomID = @toRid;";
                                        upd.Parameters.AddWithValue("@rid", rRoomId);
                                        upd.Parameters.AddWithValue("@dir", dir ?? "");
                                        upd.Parameters.AddWithValue("@toRid", toRoomId);
                                        upd.Parameters.AddWithValue("@hidden", isHidden ? 1 : 0);
                                        upd.Parameters.AddWithValue("@keyword", keyword ?? "");
                                        upd.Parameters.AddWithValue("@doorId", doorIdVal);

                                        var affected = upd.ExecuteNonQuery();
                                        if (affected == 0)
                                        {
                                            using var insCmd = conn.CreateCommand();
                                            insCmd.Transaction = tx;
                                            insCmd.CommandText = @"
                                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                                        VALUES (@rid, @dir, @toRid, @hidden, @keyword, @doorId);";
                                            insCmd.Parameters.AddWithValue("@rid", rRoomId);
                                            insCmd.Parameters.AddWithValue("@dir", dir ?? "");
                                            insCmd.Parameters.AddWithValue("@toRid", toRoomId);
                                            insCmd.Parameters.AddWithValue("@hidden", isHidden ? 1 : 0);
                                            insCmd.Parameters.AddWithValue("@keyword", keyword ?? "");
                                            insCmd.Parameters.AddWithValue("@doorId", doorIdVal);
                                            insCmd.ExecuteNonQuery();
                                        }
                                    }

                                    // Attempt to find mate exit in-memory to determine its metadata (direction/isHidden/keyword)
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    // Upsert mate side first (if exists) using its in-memory values; otherwise still upsert with best-effort placeholders
                                    if (mateExit != null && mateRoom != null)
                                    {
                                        UpsertExitInternal(mateRoom.RoomID, mateExit.Direction, room.RoomID, createdId, mateExit.IsHidden, mateExit.DiscoverKeyword);
                                    }

                                    // Upsert original side using current in-memory exit metadata
                                    UpsertExitInternal(room.RoomID, exit.Direction, exit.ToRoomID, createdId, exit.IsHidden, exit.DiscoverKeyword);

                                    return createdId;
                                }, timeout: TimeSpan.FromSeconds(5)); // short timeout for interactive admin

                                // Apply newDoorId to in-memory model (avoid stomping if concurrent)
                                exit.DoorId = newDoorId;
                                exit.DoorIsOpen = false;
                                room.IsDirty = true;

                                try
                                {
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && !mateExit.DoorId.HasValue)
                                    {
                                        mateExit.DoorId = newDoorId;
                                        mateExit.DoorIsOpen = false;
                                        mateRoom!.IsDirty = true;
                                    }
                                }
                                catch { /* best-effort in-memory sync */ }
                            }
                        }
                        else
                        {
                            // unlink only: if this exit had a DoorId, clear it and also clear mate exit if it referenced the same DoorId.
                            int? oldDoor = exit.DoorId;
                            exit.DoorId = null;

                            if (oldDoor.HasValue)
                            {
                                // Enqueue an atomic job to clear DoorId = 0 on both mirrored rows, but only when they currently reference the same DoorId.
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                                {
                                    try
                                    {
                                        // Find mate room and mate exit in-memory to get RoomIDs
                                        var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                        Room.RoomExit? mateExit = null;
                                        if (mateRoom != null && mateRoom.Exits != null)
                                        {
                                            string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                            mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                                e.ToRoomID == room.RoomID &&
                                                string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                                ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                        }

                                        if (mateRoom != null && mateExit != null)
                                        {
                                            using var cmd = conn.CreateCommand();
                                            cmd.Transaction = tx;
                                            cmd.CommandText = @"
                                        UPDATE RoomExit
                                        SET DoorId = 0
                                        WHERE RoomID = @mrid AND ToRoomID = @toRid AND DoorId = @door;";
                                            cmd.Parameters.AddWithValue("@mrid", mateRoom.RoomID);
                                            cmd.Parameters.AddWithValue("@toRid", room.RoomID);
                                            cmd.Parameters.AddWithValue("@door", oldDoor.Value);
                                            cmd.ExecuteNonQuery();
                                        }

                                        using var cmd2 = conn.CreateCommand();
                                        cmd2.Transaction = tx;
                                        cmd2.CommandText = @"
                                    UPDATE RoomExit
                                    SET DoorId = 0
                                    WHERE RoomID = @rid AND ToRoomID = @toRid AND DoorId = @door;";
                                        cmd2.Parameters.AddWithValue("@rid", room.RoomID);
                                        cmd2.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                                        cmd2.Parameters.AddWithValue("@door", oldDoor.Value);
                                        cmd2.ExecuteNonQuery();
                                    }
                                    catch (Exception ex)
                                    {
                                        throw new Exception("Failed to clear DoorId in DB: " + ex.Message, ex);
                                    }
                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(5));

                                // Also clear mate in-memory if it referenced the same door
                                try
                                {
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && mateExit.DoorId.HasValue && mateExit.DoorId.Value == oldDoor.Value)
                                    {
                                        mateExit.DoorId = null;
                                        mateExit.DoorIsOpen = null;
                                        mateRoom!.IsDirty = true;
                                    }
                                }
                                catch { /* best-effort */ }
                            }
                        }

                        // Mark dirty and persist via RoomPersistence to keep other exit metadata consistent.
                        room.IsDirty = true;
                        _world.SaveDirtyRooms(_db); // Persist any other dirty rooms in world (safe)

                        // Persist both rooms' exits via RoomPersistence so SaveRoomExits has canonical state to write.
                        try
                        {
                            // Persist mateRoom first if present so both sides are synchronized in DB and cache.
                            var mateRoomForSave = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                            if (mateRoomForSave != null)
                                RoomPersistence.SaveRoomExits(_db, mateRoomForSave);

                            RoomPersistence.SaveRoomExits(_db, room);
                        }
                        catch (Exception exSave)
                        {
                            Console.WriteLine($"[ADMIN][EditExit] SaveRoomExits failed: {exSave.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EDITEXIT] Failed saving exit change: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save exit changes due to a server error.");
                    }

                    // Broadcast updated room to occupants (and mate room if modified)
                    try
                    {
                        foreach (var p in room.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                room,
                                room.Npcs.Select(n => n.DisplayName).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }

                        // notify mate room players if mate exit exists and was modified
                        try
                        {
                            var mateRoomNotify = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                            if (mateRoomNotify != null)
                            {
                                foreach (var p in mateRoomNotify.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        mateRoomNotify,
                                        mateRoomNotify.Npcs.Select(n => n.DisplayName).ToList(),
                                        mateRoomNotify.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world, _db));
                                }
                            }
                        }
                        catch { /* best-effort */ }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Exit '{exit.Direction}' HasDoor set to {(exit.DoorId.HasValue ? "true" : "false")}.");
                }

                return null;
            }

            // --- Kill pending action ---
            if (pa.Command == "Kill")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                if (pa.Step == "SpecifyTarget")
                {
                    // input contains the target string the admin typed after the prompt
                    player.PendingAction = null!; // consume pending
                    return HandleKill(player, "kill " + (input ?? ""));
                }

                return null;
            }

            // EditRoom pending action
            if (pa.Command == "EditRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Room editing ended.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System("Enter new name for this room:");
                    }
                    if (input == "2" || input.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current description to make editing easier, then prompt for new value.
                        pa.Step = "EditDescription";
                        var current = string.IsNullOrWhiteSpace(room?.Description) ? "(no current description)" : room.Description;
                        return MessageHandler.System($"Current description is:\n{current}\n\nEnter new description for this room:");
                    }
                    // NEW: allow editing distant description
                    if (input == "3" || input.Equals("DistantDescription", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current distant description to make editing easier, then prompt for new value.
                        pa.Step = "EditDistantDescription";
                        var currentD = string.IsNullOrWhiteSpace(room?.DistantDescription) ? "(no current distant description)" : room.DistantDescription;
                        return MessageHandler.System($"Current distant description is:\n{currentD}\n\nEnter new distant description for this room:");
                    }

                    // NEW: LLM generation option
                    if (input == "4" || input.Equals("GenerateDescription", StringComparison.OrdinalIgnoreCase) ||
                        input.Equals("Generate Description", StringComparison.OrdinalIgnoreCase) ||
                        input.Equals("Generate Description from Prompt", StringComparison.OrdinalIgnoreCase))
                    {
                        // Transition to collecting operator keywords for AI generation
                        pa.Step = "CollectKeywords";
                        // Store room id for reference in pa.Data
                        pa.Data = new Dictionary<string, object> { { "RoomID", room?.RoomID ?? 0 } };
                        return MessageHandler.System("Enter comma-separated keywords to guide the AI (or type 'none' to skip). Type 'EndEdit' to cancel.");
                    }

                    return MessageHandler.System("Invalid option. Type Name, Description, DistantDescription, GenerateDescription, 1, 2, 3, 4, or EndEdit.");
                }

                if (pa.Step == "EditName")
                {
                    room.Name = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System($"Room name updated to '{room.Name}'.");
                }

                if (pa.Step == "EditDescription")
                {
                    room.Description = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room description updated.");
                }

                /// handle generate-from-prompt flow - NEW: CollectKeywords -> GenerateDescriptionPreview -> GenerateDescriptionConfirm
                if (pa.Step == "CollectKeywords")
                {
                    // input contains comma-separated keywords (or 'none')
                    var keywordsRaw = (input ?? "").Trim();
                    string[] keywordArray;
                    if (string.Equals(keywordsRaw, "none", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(keywordsRaw))
                        keywordArray = new string[0];
                    else
                    {
                        keywordArray = keywordsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(12).ToArray();
                    }

                    // save into pa.Data dictionary
                    try
                    {
                        if (!(pa.Data is Dictionary<string, object> dict)) dict = new Dictionary<string, object>();
                        dict["Keywords"] = keywordArray;
                        pa.Data = dict;
                    }
                    catch { pa.Data = new Dictionary<string, object> { { "Keywords", keywordArray } }; }

                    // Build structured payload for LLM
                    var lmUrl = Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://127.0.0.1:8000/";
                    var lmModel = Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "default";
                    var systemPrompt = Environment.GetEnvironmentVariable("LMSTUDIO_SYSTEM_PROMPT") ??
                        "You are a concise immersive worldbuilding assistant for a text MUD. Produce exactly one JSON object with keys 'Description' and 'DistantDescription'. Do not include any extra text. Use neutral, evocative language; no markup; avoid profanity. Description should be 60-800 characters. DistantDescription should be one or two brief sentences.";

                    try
                    {
                        // Assemble exits info
                        var exitsList = new List<string>();
                        try
                        {
                            if (room != null && room.Exits != null)
                            {
                                foreach (var ex in room.Exits)
                                {
                                    // toRoomName best-effort
                                    var toRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == ex.ToRoomID);
                                    var toName = toRoom?.Name ?? "";
                                    var dirWord = Movement.DirectionDisplayName(ex.Direction);
                                    var label = ex.DiscoverKeyword ?? "";
                                    // e.g. north: the gate -> Gatehouse
                                    var piece = $"{dirWord}:{(string.IsNullOrWhiteSpace(label) ? ex.Direction : label)}->{toName}";
                                    exitsList.Add(piece);
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        var userBuilder = new System.Text.StringBuilder();
                        userBuilder.AppendLine($"Title: {room?.Name ?? "Unknown"}");
                        userBuilder.AppendLine($"Exits: {string.Join(", ", exitsList)}");
                        if (keywordArray.Length > 0) userBuilder.AppendLine($"Keywords: {string.Join(", ", keywordArray)}");
                        userBuilder.AppendLine($"ExistingDescription: {room?.Description ?? ""}");
                        userBuilder.AppendLine();
                        userBuilder.AppendLine("Produce a JSON object exactly like: {\"Description\":\"<full text>\",\"DistantDescription\":\"<short text>\"} with Description and DistantDescription only.");

                        using var lmClient = new MudServer.Integrations.LmStudioClient(lmUrl);
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));

                        var messages = new List<(string role, string content)>
                {
                    ("system", systemPrompt),
                    ("user", userBuilder.ToString())
                };

                        using var respDoc = lmClient.CreateChatCompletionAsync(lmModel, messages, temperature: 0.7, ct: cts.Token)
                                                   .GetAwaiter().GetResult();

                        string generated = "";

                        try
                        {
                            var root = respDoc.RootElement;

                            // chat/completions: choices[0].message.content (preferred)
                            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
                            {
                                var first = choices[0];
                                if (first.TryGetProperty("message", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.Object && msg.TryGetProperty("content", out var content))
                                {
                                    generated = content.GetString() ?? "";
                                }
                                else if (first.TryGetProperty("text", out var textProp))
                                {
                                    generated = textProp.GetString() ?? "";
                                }
                            }

                            // fallback: responses style
                            if (string.IsNullOrWhiteSpace(generated) && root.TryGetProperty("output_text", out var outText) && outText.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                generated = outText.GetString() ?? "";
                            }

                            if (string.IsNullOrWhiteSpace(generated) && root.TryGetProperty("output", out var outNode))
                            {
                                if (outNode.ValueKind == System.Text.Json.JsonValueKind.String)
                                    generated = outNode.GetString() ?? "";
                                else if (outNode.ValueKind == System.Text.Json.JsonValueKind.Array && outNode.GetArrayLength() > 0)
                                    generated = string.Join("\n", outNode.EnumerateArray().Select(e => e.GetString() ?? ""));
                            }
                        }
                        catch (Exception exParse)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse LM response container: {exParse.Message}");
                            generated = "";
                        }

                        generated = (generated ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(generated))
                        {
                            return MessageHandler.System("LLM returned empty description. Try different keywords or try again later.");
                        }

                        // Attempt to extract JSON object from generated string
                        string jsonText = generated;
                        try
                        {
                            // If output contains surrounding text, find first { and last } and parse substring
                            int firstIdx = jsonText.IndexOf('{');
                            int lastIdx = jsonText.LastIndexOf('}');
                            if (firstIdx >= 0 && lastIdx > firstIdx)
                                jsonText = jsonText.Substring(firstIdx, lastIdx - firstIdx + 1);

                            using var jd = JsonDocument.Parse(jsonText);
                            var root = jd.RootElement;

                            string desc = "";
                            string distant = "";

                            if (root.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String)
                                desc = d.GetString() ?? "";
                            if (root.TryGetProperty("DistantDescription", out var dd) && dd.ValueKind == JsonValueKind.String)
                                distant = dd.GetString() ?? "";

                            if (string.IsNullOrWhiteSpace(desc))
                            {
                                return MessageHandler.System("LLM returned JSON but Description was empty. Try different keywords.");
                            }

                            // store draft in pa.Data
                            if (!(pa.Data is Dictionary<string, object> dct)) dct = new Dictionary<string, object>();
                            dct["DraftDescription"] = desc.Trim();
                            dct["DraftDistant"] = distant.Trim();
                            dct["Keywords"] = keywordArray;
                            pa.Data = dct;
                            pa.Step = "GenerateDescriptionConfirm";

                            // Present preview and prompt for confirmation
                            var preview = $"Preview - Description:\n{desc}\n\nDistantDescription:\n{distant}\n\nType 'confirm' to save, 'edit' to enter new keywords, or 'cancel'.";
                            return MessageHandler.System(preview);
                        }
                        catch (Exception exJson)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse JSON from LM output: {exJson.Message}");
                            Console.WriteLine($"[LLM][WARN] Raw LM output: {generated}");
                            return MessageHandler.System("LLM returned output that could not be parsed as JSON. Try different keywords or try again later.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LLM] Generation failed: {ex.Message}");
                        return MessageHandler.System($"LLM generation failed: {ex.Message}");
                    }
                }

                if (pa.Step == "GenerateDescriptionConfirm")
                {
                    // Expecting confirm / edit / cancel
                    var dct = pa.Data as Dictionary<string, object>;
                    string draftDesc = dct != null && dct.TryGetValue("DraftDescription", out var ddObj) ? (ddObj as string ?? "") : "";
                    string draftDistant = dct != null && dct.TryGetValue("DraftDistant", out var drObj) ? (drObj as string ?? "") : "";

                    var cmd = (input ?? "").Trim().ToLowerInvariant();
                    if (cmd == "confirm")
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(draftDesc))
                            {
                                room.Description = draftDesc;
                                room.DistantDescription = draftDistant;
                                room.IsDirty = true;
                                _world.SaveDirtyRooms(_db);

                                // Broadcast updated room payloads to occupants
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => n.DisplayName).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world));
                                }

                                player.PendingAction = null!;
                                return MessageHandler.System("Generated description saved and broadcast to room.");
                            }
                            else
                            {
                                player.PendingAction = null!;
                                return MessageHandler.System("No draft description available to save.");
                            }
                        }
                        catch (Exception exSave)
                        {
                            Console.WriteLine($"[LLM][ERROR] Failed to save generated description: {exSave.Message}");
                            player.PendingAction = null!;
                            return MessageHandler.System("Failed to save generated description due to a server error.");
                        }
                    }
                    else if (cmd == "edit")
                    {
                        // allow operator to re-enter keywords
                        pa.Step = "CollectKeywords";
                        return MessageHandler.System("Enter comma-separated keywords to guide the AI (or 'none' to skip). Type 'EndEdit' to cancel.");
                    }
                    else if (cmd == "cancel" || cmd == "endedit")
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Generation cancelled.");
                    }
                    else
                    {
                        return MessageHandler.System("Invalid option. Type 'confirm' to save, 'edit' to re-enter keywords, or 'cancel'.");
                    }
                }

                // NEW: persist DistantDescription edits
                if (pa.Step == "EditDistantDescription")
                {
                    room.DistantDescription = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room distant description updated.");
                }

                return null;
            }

            // CreateArea pending action
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var newArea = new Area(input, 0);

                try
                {
                    // capture current room id and normalized directions for the writer
                    int srcRoomId = player.CurrentRoom?.RoomID ?? 0;
                    string dirNorm = dir;
                    string oppDir = OppositeDirection(dirNorm);

                    // Perform both Area insert and starting Room creation inside single-writer so they are atomic and serialized.
                    var result = MudServer.DbWriteQueue.Instance.RunWriteAndWait<(int areaId, int roomId)>((wConn, wTx) =>
                    {
                        // Insert Area row
                        using var ins = wConn.CreateCommand();
                        ins.Transaction = wTx;
                        ins.CommandText = @"
                    INSERT INTO Area (Name, Description, WorldID, Theme, LevelRangeMin, LevelRangeMax, IsPvPAllowed, Owner)
                    VALUES (@name, @desc, @wid, @theme, @min, @max, @pvp, @owner);
                    SELECT last_insert_rowid();";
                        ins.Parameters.AddWithValue("@name", newArea.Name ?? "");
                        ins.Parameters.AddWithValue("@desc", newArea.Description ?? "");
                        ins.Parameters.AddWithValue("@wid", _world.WorldID);
                        ins.Parameters.AddWithValue("@theme", newArea.Theme ?? "Generic");
                        ins.Parameters.AddWithValue("@min", newArea.LevelRangeMin);
                        ins.Parameters.AddWithValue("@max", newArea.LevelRangeMax);
                        ins.Parameters.AddWithValue("@pvp", newArea.IsPvPAllowed ? 1 : 0);
                        ins.Parameters.AddWithValue("@owner", newArea.Owner ?? "System");

                        long newAreaIdLong = (long)ins.ExecuteScalar();
                        int newAreaId = (int)newAreaIdLong;
                        newArea.AreaID = newAreaId;

                        // Create a starting room for the area using the writer connection/tx (atomic)
                        var newRoom = new Room($"{input} - Undefined Room", "Starting room for the area")
                        {
                            AreaID = newArea.AreaID
                        };

                        int roomDbId = _world.CreateRoomInDB(newRoom, wConn, wTx);

                        // Persist two RoomExit rows linking srcRoom <-> newRoom so reverse exit exists in DB.
                        // Insert src -> new
                        using (var e1 = wConn.CreateCommand())
                        {
                            e1.Transaction = wTx;
                            e1.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e1.Parameters.AddWithValue("@rid", srcRoomId);
                            e1.Parameters.AddWithValue("@dir", dirNorm ?? "");
                            e1.Parameters.AddWithValue("@toRid", roomDbId);
                            e1.ExecuteNonQuery();
                        }

                        // Insert new -> src (reverse)
                        using (var e2 = wConn.CreateCommand())
                        {
                            e2.Transaction = wTx;
                            e2.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e2.Parameters.AddWithValue("@rid", roomDbId);
                            e2.Parameters.AddWithValue("@dir", oppDir ?? "");
                            e2.Parameters.AddWithValue("@toRid", srcRoomId);
                            e2.ExecuteNonQuery();
                        }

                        return (newAreaId, roomDbId);
                    }, timeout: TimeSpan.FromSeconds(10));

                    // Update in-memory world with new area and room
                    newArea.AreaID = result.areaId;

                    // Try to find the room instance that CreateRoomInDB likely added (writer may have added to _world.Rooms).
                    Room createdRoomInstance = _world.Rooms.FirstOrDefault(r => r.RoomID == result.roomId);
                    if (createdRoomInstance == null)
                    {
                        // If not present, create a local instance and add to world's Rooms.
                        createdRoomInstance = new Room($"{input} - Undefined Room", "Starting room for the area") { AreaID = newArea.AreaID, RoomID = result.roomId };
                        _world.Rooms.Add(createdRoomInstance);
                    }
                    else
                    {
                        // Ensure AreaID is set correctly
                        createdRoomInstance.AreaID = newArea.AreaID;
                    }

                    newArea.Rooms.Add(createdRoomInstance);
                    _world.Areas.Add(newArea);

                    var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                    //CODE REMOVED - 2025-12-21 12:00:00 // 'currentArea.Rooms.Add(createdRoomInstance);' // Rationale: Do not add the new-area room into the previous area's Rooms collection — this caused the client header to resolve the wrong area via Rooms.Contains(...) leading to a flicker.
                    // ensure we didn't accidentally duplicate the room into the wrong area's Rooms list

                    // Link exits in-memory using the authoritative room instance from world.Rooms
                    // ensure we don't duplicate exits
                    if (!player.CurrentRoom.Exits.Any(x => x.ToRoomID == createdRoomInstance.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase)))
                        player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, createdRoomInstance.RoomID));

                    if (!createdRoomInstance.Exits.Any(x => x.ToRoomID == player.CurrentRoom.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(OppositeDirection(dir)), StringComparison.OrdinalIgnoreCase)))
                        createdRoomInstance.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                    player.CurrentRoom.IsDirty = true;
                    createdRoomInstance.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    player.PendingAction = null!;
                    return MessageHandler.System("Area created.");
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEAREA][ERROR] Failed to insert Area '{input}': {ex.Message}");
                    return MessageHandler.System($"Error creating area {input}: {ex.Message}");
                }
            }

            // CreateRoom pending action
            if (pa.Command == "CreateRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Current room is not in an area.");
                }

                var newRoom = new Room(input, "Newly created room")
                {
                    AreaID = currentArea.AreaID
                };

                try
                {
                    int srcRoomId = player.CurrentRoom?.RoomID ?? 0;
                    string dirNorm = dir;
                    string oppDir = OppositeDirection(dirNorm);

                    // Create the room using writer queue so the DB insert and related writes are serialized.
                    int createdRoomId = MudServer.DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                    {
                        int id = _world.CreateRoomInDB(newRoom, wConn, wTx);

                        // Persist exits linking both sides inside writer tx
                        using (var e1 = wConn.CreateCommand())
                        {
                            e1.Transaction = wTx;
                            e1.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e1.Parameters.AddWithValue("@rid", srcRoomId);
                            e1.Parameters.AddWithValue("@dir", dirNorm ?? "");
                            e1.Parameters.AddWithValue("@toRid", id);
                            e1.ExecuteNonQuery();
                        }

                        using (var e2 = wConn.CreateCommand())
                        {
                            e2.Transaction = wTx;
                            e2.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e2.Parameters.AddWithValue("@rid", id);
                            e2.Parameters.AddWithValue("@dir", oppDir ?? "");
                            e2.Parameters.AddWithValue("@toRid", srcRoomId);
                            e2.ExecuteNonQuery();
                        }

                        return id;
                    }, timeout: TimeSpan.FromSeconds(10));

                    if (createdRoomId == 0)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to create room in database.");
                    }

                    // Prefer to locate the authoritative room instance that may have been added by CreateRoomInDB.
                    Room createdRoomInstance = _world.Rooms.FirstOrDefault(r => r.RoomID == createdRoomId);
                    if (createdRoomInstance == null)
                    {
                        createdRoomInstance = new Room(input, "Newly created room") { AreaID = currentArea.AreaID, RoomID = createdRoomId };
                        _world.Rooms.Add(createdRoomInstance);
                    }
                    else
                    {
                        // ensure name/area set
                        createdRoomInstance.Name = input;
                        createdRoomInstance.AreaID = currentArea.AreaID;
                    }

                    currentArea.Rooms.Add(createdRoomInstance);

                    // Link exits in-memory (avoid duplicates)
                    if (!player.CurrentRoom.Exits.Any(x => x.ToRoomID == createdRoomInstance.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase)))
                        player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, createdRoomInstance.RoomID));

                    if (!createdRoomInstance.Exits.Any(x => x.ToRoomID == player.CurrentRoom.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(oppDir), StringComparison.OrdinalIgnoreCase)))
                        createdRoomInstance.Exits.Add(new Room.RoomExit(oppDir, player.CurrentRoom.RoomID));

                    player.CurrentRoom.IsDirty = true;
                    createdRoomInstance.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    // Broadcast updated room views so clients immediately see the new exit
                    try
                    {
                        var srcRoom = player.CurrentRoom;
                        if (srcRoom != null)
                        {
                            foreach (var p in srcRoom.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    srcRoom,
                                    srcRoom.Npcs.Select(n => n.DisplayName).ToList(),
                                    srcRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }

                        if (createdRoomInstance != null)
                        {
                            foreach (var p in createdRoomInstance.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    createdRoomInstance,
                                    createdRoomInstance.Npcs.Select(n => n.DisplayName).ToList(),
                                    createdRoomInstance.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System("Room created.");
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEROOM][ERROR] Failed to create room '{input}': {ex.Message}");
                    return MessageHandler.System("Failed to create room due to a server error.");
                }
            }

            return null;
        } // end HandlePendingAction

        // Typed pending data for EditRoom -> AI generation flow.
        // Keeps strongly-typed fields instead of using an unstructured Dictionary.
        private class EditRoomPendingData
        {
            public int RoomID { get; set; } = 0;
            public string[] Keywords { get; set; } = Array.Empty<string>();
            public string DraftDescription { get; set; } = "";
            public string DraftDistant { get; set; } = "";
        } // end EditRoomPendingData

        private static bool IsValidDirection(string dir)
        {
            string[] valid = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "UP", "DOWN" };
            return valid.Any(v => v.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeDirection(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return string.Empty;
            var s = d.Trim().ToUpperInvariant();
            return s switch
            {
                "N" or "NORTH" => "N",
                "S" or "SOUTH" => "S",
                "E" or "EAST" => "E",
                "W" or "WEST" => "W",
                "NE" or "NORTHEAST" => "NE",
                "NW" or "NORTHWEST" => "NW",
                "SE" or "SOUTHEAST" => "SE",
                "SW" or "SOUTHWEST" => "SW",
                "U" or "UP" => "UP",
                "D" or "DOWN" => "DOWN",
                _ => s
            };
        }

        public Message? HandleNpcCounts(Player player, string command)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (!player.IsAdmin) return MessageHandler.System("Permission denied. Admins only.");
            if (_world == null) return MessageHandler.System("World unavailable.");

            // Parse optional scope tokens from command: "npccounts", optionally followed by "room <id>" or "area <id>"
            string payload = (command ?? "").Trim();
            if (payload.StartsWith("npccounts", StringComparison.OrdinalIgnoreCase))
                payload = payload.Substring("npccounts".Length).Trim();

            int? roomFilter = null;
            int? areaFilter = null;

            try
            {
                var toks = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < toks.Length; i++)
                {
                    var t = toks[i].Trim().ToLowerInvariant();
                    if (t == "room" && i + 1 < toks.Length && int.TryParse(toks[i + 1], out var rid))
                    {
                        roomFilter = rid;
                        i++;
                        continue;
                    }
                    if (t == "area" && i + 1 < toks.Length && int.TryParse(toks[i + 1], out var aid))
                    {
                        areaFilter = aid;
                        i++;
                        continue;
                    }
                }
            }
            catch { /* best-effort parsing */ }

            IEnumerable<Npc> candidates = _world.Npcs ?? Enumerable.Empty<Npc>();

            if (roomFilter.HasValue)
            {
                candidates = candidates.Where(n => n != null && n.CurrentRoom != null && n.CurrentRoom.RoomID == roomFilter.Value);
            }
            else if (areaFilter.HasValue)
            {
                candidates = candidates.Where(n => n != null && (n.CurrentAreaID == areaFilter.Value || (n.CurrentRoom != null && n.CurrentRoom.AreaID == areaFilter.Value)));
            }

            var grouped = candidates
                .GroupBy(n => (n.Template?.Id ?? n.TemplateID ?? n.Name ?? "Unknown"), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var sample = g.FirstOrDefault();
                    string displayName = sample?.Template?.Name ?? sample?.Name ?? g.Key;
                    return new { TemplateId = g.Key, DisplayName = displayName, Count = g.Count() };
                })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.DisplayName)
                .ToList();

            if (grouped.Count == 0)
            {
                string scopeText = roomFilter.HasValue ? $"room {roomFilter.Value}" : (areaFilter.HasValue ? $"area {areaFilter.Value}" : "world");
                return MessageHandler.System($"No NPCs found in {scopeText}.");
            }

            var lines = new List<string>();
            int total = grouped.Sum(g => g.Count);
            string scopeLabel = roomFilter.HasValue ? $"room {roomFilter.Value}" : (areaFilter.HasValue ? $"area {areaFilter.Value}" : "world");
            lines.Add($"NPC counts for {scopeLabel} — total {total}:");

            foreach (var g in grouped)
            {
                lines.Add($"{g.Count}x {g.DisplayName} ({g.TemplateId})");
            }

            // Limit output to reasonable size (safety)
            const int maxLines = 500;
            if (lines.Count > maxLines)
            {
                var head = lines.Take(maxLines).ToList();
                head.Add($"... {lines.Count - maxLines} more (narrow scope with 'room <id>' or 'area <id>').");
                return MessageHandler.System(string.Join("\n", head));
            }

            return MessageHandler.System(string.Join("\n", lines));
        }

        private static string OppositeDirection(string dir) => dir.ToUpper() switch
        {
            "N" => "S",
            "S" => "N",
            "E" => "W",
            "W" => "E",
            "NE" => "SW",
            "SW" => "NE",
            "NW" => "SE",
            "SE" => "NW",
            "UP" => "DOWN",
            "DOWN" => "UP",
            _ => ""
        };
    }
}

