using Microsoft.Data.Sqlite;
using MudCore.AI;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MudCore.Actions
{
    /// <summary>
    /// Manages NPC "talk" interactions.
    /// Placeholder implementation - returns personality data for testing.
    /// Future: LLM integration via LmStudioClient, rumor system, event database.
    /// </summary>
    public static class NpcTalk
    {
        /// <summary>
        /// Start a talk session. Checks AiCanTalk flag and returns personality data as placeholder.
        /// </summary>
        public static Message BeginTalkSession(Player player, Npc npc, World.World? world, SqliteConnection? conn, string? topic = null)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (npc == null) return MessageHandler.System("Invalid NPC.");
            if (player.CurrentRoom == null || npc.CurrentRoom == null || player.CurrentRoom != npc.CurrentRoom)
                return MessageHandler.System("You must be in the same room to talk to that NPC.");

            // Check if NPC can talk (has AiCanTalk flag)
            if (!PersonalityManager.CanTalk(npc))
            {
                return MessageHandler.System(PersonalityManager.GetCannotTalkMessage(npc));
            }

            // If player already talking to someone, refuse
            try
            {
                if (player.PendingAction != null && string.Equals(player.PendingAction.Command, "TalkSession", StringComparison.OrdinalIgnoreCase))
                    return MessageHandler.System("You are already in a conversation. Type 'end' to finish it first.");
            }
            catch { /* best-effort */ }

            // Load or initialize personality
            PersonalityManager.PersonalityData? personality = null;
            if (conn != null && world != null)
            {
                try
                {
                    personality = PersonalityManager.GetPersonality(npc.NpcID, conn);
                    if (personality == null)
                    {
                        personality = PersonalityManager.InitializeForNpc(npc, world, conn);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NPCTALK] Failed to load personality for NpcID={npc.NpcID}: {ex.Message}");
                }
            }

            // Mark session on player
            try
            {
                var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NpcId"] = npc.NpcID,
                    ["NpcName"] = npc.DisplayName ?? npc.Name ?? npc.TemplateID ?? "npc",
                    ["Topic"] = topic ?? ""
                };

                player.PendingAction = new PendingAction
                {
                    Command = "TalkSession",
                    Step = "Active",
                    Data = data
                };
            }
            catch { /* best-effort */ }

            // Apply interrupt to keep NPC in place
            try
            {
                npc.AddInterrupt(Npc.InterruptType.Talk, owner: player.PlayerID, note: topic ?? "TalkSession", ttl: null);
            }
            catch { /* best-effort */ }

            // Build the LLM prompt (structured facts + rules) and store into PendingAction.Data for later use by LLM caller.
            string builtPrompt = "";
            try
            {
                builtPrompt = BuildNpcPrompt(npc, world, conn, personality);
                // Persist prompt in pending action so HandleTalkCommand can call LLM later
                try
                {
                    if (player.PendingAction != null && player.PendingAction.Data is Dictionary<string, object> dict)
                    {
                        dict["Prompt"] = builtPrompt;
                    }
                }
                catch { /* best-effort */ }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCTALK] BuildNpcPrompt failed for NpcID={npc.NpcID}: {ex.Message}");
                builtPrompt = ""; // continue with greeting fallback
            }

            // Produce a simple in-character greeting to start the interaction (few variations)
            var greetings = new List<string>
            {
                $"Good {GetTimeOfDayPhrase(world)}, to you! How can I help you?",
                $"A fine {GetTimeOfDayPhrase(world)} — what can I do for you?",
                $"Greetings this {GetTimeOfDayPhrase(world)} — do you need something?",
                $"Hello there. Good {GetTimeOfDayPhrase(world)}! How may I assist?"
            };

            // If we have personality, bias greeting slightly
            string greeting;
            if (personality != null)
            {
                // friendly/charismatic -> warmer greeting; blunt -> shorter
                var prim = (personality.PrimaryTrait ?? "").ToLowerInvariant();
                if (prim.Contains("friendly") || prim.Contains("warm") || prim.Contains("charismatic"))
                    greeting = greetings[0];
                else if (prim.Contains("blunt") || prim.Contains("reserved"))
                    greeting = greetings[2];
                else
                    greeting = greetings[new Random().Next(greetings.Count)];
            }
            else
            {
                greeting = greetings[new Random().Next(greetings.Count)];
            }

            // Return the greeting; store built prompt for later LLM invocation when player asks a question.
            var responseLines = new List<string>
            {
                greeting,
                $"(Type a question to continue talking; type 'end' to finish the conversation.)"
            };

            // For debugging/dev: if diagnostics enabled, append a short dev note but keep it non-invasive.
            try
            {
                if (PersonalityManager.DiagnosticsEnabled)
                {
                    responseLines.Add("--- DEBUG PROMPT (hidden to players in production) ---");
                    responseLines.Add(builtPrompt.Length > 2000 ? builtPrompt.Substring(0, 2000) + "...[truncated]" : builtPrompt);
                    responseLines.Add("--- END DEBUG PROMPT ---");
                }
            }
            catch { /* best-effort */ }

            return MessageHandler.System(string.Join("\n", responseLines));
        }

        // Helper: derive short phrase for time-of-day portion (morning/afternoon/evening)
        // Now uses the world's GameClock when available; falls back to UTC mapping only if world is not provided.
        private static string GetTimeOfDayPhrase(World.World? world)
        {
            try
            {
                if (world?.GameTime != null)
                {
                    try
                    {
                        var period = world.GameTime.GetPeriodDescriptor(); // e.g. "Morning", "Afternoon", "Dawn", etc.
                        if (!string.IsNullOrWhiteSpace(period))
                        {
                            // Normalize common descriptors to simple greeting tokens
                            switch (period.Trim().ToLowerInvariant())
                            {
                                case "morning": return "morning";
                                case "afternoon": return "afternoon";
                                case "evening": return "evening";
                                case "night": return "night";
                                case "dawn": return "dawn";
                                case "dusk": return "dusk";
                                case "noon": return "noon";
                                case "midnight": return "midnight";
                                default:
                                    // Fallback: if descriptor contains known words
                                    if (period.IndexOf("morning", StringComparison.OrdinalIgnoreCase) >= 0) return "morning";
                                    if (period.IndexOf("afternoon", StringComparison.OrdinalIgnoreCase) >= 0) return "afternoon";
                                    if (period.IndexOf("evening", StringComparison.OrdinalIgnoreCase) >= 0) return "evening";
                                    if (period.IndexOf("night", StringComparison.OrdinalIgnoreCase) >= 0) return "night";
                                    return period.ToLowerInvariant();
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }
            }
            catch { /* best-effort */ }

            // Fallback to UTC-based local phrase (rare; used only when world/GameTime not available)
            try
            {
                var h = DateTime.UtcNow.Hour;
                if (h >= 5 && h < 12) return "morning";
                if (h >= 12 && h < 17) return "afternoon";
                return "evening";
            }
            catch { return "day"; }
        } // end GetTimeOfDayPhrase


        // Build a concise, fact-only LLM prompt for an NPC.
        // This version emits identity + facts (location, time, recent sanitized events, familiar NPC list)
        // and a single short behavior hint. It deliberately omits long enforcement blocks, example
        // ignorance phrases, and planning instructions to avoid the model echoing rules.
        private static string BuildNpcPrompt(Npc npc, World.World? world, SqliteConnection? mainConn, PersonalityManager.PersonalityData? personality)
        {
            var sb = new System.Text.StringBuilder();

            // Identity / short meta (data only)
            string npcName = npc.DisplayName ?? npc.Name ?? npc.TemplateID ?? "npc";
            string npcRole = npc.Template?.Name ?? npc.TemplateID ?? "local resident";
            string alignment = personality?.AlignmentTrait ?? "(unknown)";
            string primary = personality?.PrimaryTrait ?? "(unknown)";
            string secondary = personality?.SecondaryTrait ?? "(none)";

            sb.AppendLine($"{npcName} — {npcRole}");
            sb.AppendLine($"Personality: Alignment={alignment}; Primary={primary}; Secondary={secondary}");
            sb.AppendLine();

            // Location & time (friendly labels)
            string currentRoomName = "Unknown Room";
            string areaName = "Unknown Area";
            try
            {
                if (npc.CurrentRoom != null)
                {
                    if (!string.IsNullOrWhiteSpace(npc.CurrentRoom.Name))
                        currentRoomName = npc.CurrentRoom.Name;
                    else if (world?.Rooms != null)
                    {
                        var rm = world.Rooms.FirstOrDefault(r => r != null && r.RoomID == npc.CurrentRoom.RoomID);
                        if (rm != null && !string.IsNullOrWhiteSpace(rm.Name)) currentRoomName = rm.Name;
                        else currentRoomName = $"Room#{npc.CurrentRoom.RoomID}";
                    }

                    if (world?.Areas != null)
                    {
                        var a = world.Areas.FirstOrDefault(x => x != null && x.AreaID == npc.CurrentRoom.AreaID);
                        if (a != null && !string.IsNullOrWhiteSpace(a.Name)) areaName = a.Name;
                        else areaName = $"Area#{npc.CurrentRoom.AreaID}";
                    }
                    else
                    {
                        areaName = $"Area#{npc.CurrentRoom.AreaID}";
                    }
                }
            }
            catch { /* best-effort */ }

            string timeDescriptor = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
            try
            {
                if (world?.GameTime != null)
                {
                    // Use GameClock display helpers when available
                    try { timeDescriptor = world.GameTime.GetHeaderString(); } catch { timeDescriptor = world.GameTime.ToIsoString(); }
                }
            }
            catch { /* best-effort */ }

            sb.AppendLine($"Location: {currentRoomName}, Area: {areaName}");
            sb.AppendLine($"Time: {timeDescriptor}");
            sb.AppendLine();

            // Recent events (sanitized, limited)
            sb.AppendLine("Recent events (sanitized):");
            try
            {
                var areaIds = new List<int>();
                if (npc.WanderAreas != null) areaIds.AddRange(npc.WanderAreas);
                if (npc.CurrentRoom != null && !areaIds.Contains(npc.CurrentRoom.AreaID)) areaIds.Add(npc.CurrentRoom.AreaID);

                string eventDbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "EventStorage.db");
                if (mainConn != null)
                {
                    try
                    {
                        using var q = mainConn.CreateCommand();
                        q.CommandText = "PRAGMA database_list;";
                        using var rdr = q.ExecuteReader();
                        string mainFile = "";
                        while (rdr.Read())
                        {
                            var name = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                            var file = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                            if (string.Equals(name, "main", StringComparison.OrdinalIgnoreCase))
                            {
                                mainFile = file ?? "";
                                break;
                            }
                        }
                        rdr.Close();
                        if (!string.IsNullOrWhiteSpace(mainFile))
                        {
                            var mainDir = System.IO.Path.GetDirectoryName(mainFile) ?? AppContext.BaseDirectory;
                            var candidate = System.IO.Path.Combine(mainDir, "EventStorage.db");
                            if (System.IO.File.Exists(candidate)) eventDbPath = candidate;
                        }
                    }
                    catch { /* best-effort */ }
                }

                if (areaIds.Count > 0 && System.IO.File.Exists(eventDbPath))
                {
                    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = eventDbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
                    using var evConn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString());
                    evConn.Open();

                    var whereParts = new List<string>();
                    var cmd = evConn.CreateCommand();
                    for (int i = 0; i < areaIds.Count; i++)
                    {
                        var pname = $"@a{i}";
                        whereParts.Add($"AreaID = {pname}");
                        cmd.Parameters.AddWithValue(pname, areaIds[i]);
                    }

                    cmd.CommandText = $"SELECT Actor, ActionText, GameTimeIso, AreaID, RoomID, ExtraJson FROM Events WHERE ({string.Join(" OR ", whereParts)}) ORDER BY CreatedUtc DESC LIMIT 5;";
                    using var r = cmd.ExecuteReader();
                    int added = 0;
                    while (r.Read() && added < 5)
                    {
                        try
                        {
                            var actor = r.IsDBNull(0) ? "(someone)" : r.GetString(0);
                            var actionRaw = r.IsDBNull(1) ? "(something happened)" : r.GetString(1);
                            var giso = r.IsDBNull(2) ? "" : r.GetString(2);
                            var aid = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                            var rid = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                            var extraJson = r.IsDBNull(5) ? "{}" : r.GetString(5);

                            // Sanitize action text: remove numeric HP fragments and collapse whitespace
                            try
                            {
                                actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\(\s*\d+\s*/\s*\d+\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*(?:HP|hp|hit points|hitpoints|health)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*%\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*percent\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\s{2,}", " ").Trim();
                            }
                            catch { /*best-effort*/ }

                            string evAreaName = $"Area#{aid}";
                            string evRoomName = rid > 0 ? $"Room#{rid}" : "(unknown)";
                            if (world != null)
                            {
                                try
                                {
                                    var a = world.Areas?.FirstOrDefault(x => x != null && x.AreaID == aid);
                                    if (a != null && !string.IsNullOrWhiteSpace(a.Name)) evAreaName = a.Name;
                                    var rm = world.Rooms?.FirstOrDefault(x => x != null && x.RoomID == rid);
                                    if (rm != null && !string.IsNullOrWhiteSpace(rm.Name)) evRoomName = rm.Name;
                                }
                                catch { /*best-effort*/ }
                            }

                            // Derive qualitative health descriptor from ExtraJson when present
                            string healthDescriptor = "";
                            try
                            {
                                using var jd = System.Text.Json.JsonDocument.Parse(extraJson);
                                var root = jd.RootElement;
                                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    int ph = 0, pm = 0;
                                    if (root.TryGetProperty("PlayerHealthRemaining", out var phNode) && phNode.TryGetInt32(out var phv)) ph = phv;
                                    if (root.TryGetProperty("PlayerMaxHealth", out var pmNode) && pmNode.TryGetInt32(out var pmv)) pm = pmv;
                                    if (pm > 0 && ph >= 0)
                                    {
                                        double pct = 100.0 * (double)ph / (double)pm;
                                        if (pct > 90.0) healthDescriptor = "heroically survived";
                                        else if (pct > 50.0) healthDescriptor = "survived";
                                        else if (pct > 25.0) healthDescriptor = "was wounded";
                                        else if (pct > 0.0) healthDescriptor = "barely survived";
                                        else healthDescriptor = "almost lost to the fight";
                                    }
                                }
                            }
                            catch { /*best-effort*/ }

                            var evLine = $"{giso}: {actor} — {actionRaw} (Area={evAreaName} Room={evRoomName})";
                            if (!string.IsNullOrWhiteSpace(healthDescriptor)) evLine += $" [{healthDescriptor}]";

                            sb.AppendLine(" - " + evLine);
                            added++;
                        }
                        catch { /* per-row ignore */ }
                    }
                }
            }
            catch
            {
                sb.AppendLine(" - (failed to read recent events)");
            }
            sb.AppendLine();

            // Familiar NPCs (only talk-capable)
            sb.AppendLine("Familiar NPCs (talk-capable):");
            try
            {
                var familiar = new List<string>();
                if (world != null)
                {
                    var areaSet = new HashSet<int>(npc.WanderAreas ?? new List<int>());
                    if (npc.CurrentRoom != null) areaSet.Add(npc.CurrentRoom.AreaID);

                    foreach (var room in world.Rooms ?? new List<Room>())
                    {
                        try
                        {
                            if (!areaSet.Contains(room.AreaID)) continue;
                            foreach (var other in room.Npcs ?? new List<Npc>())
                            {
                                if (other == null || other.NpcID == npc.NpcID) continue;
                                try { if (!PersonalityManager.CanTalk(other)) continue; } catch { continue; }
                                var name = other.DisplayName ?? other.Name ?? other.TemplateID;
                                if (!string.IsNullOrWhiteSpace(name) && !familiar.Contains(name)) familiar.Add(name);
                            }
                        }
                        catch { /*best-effort per-room*/ }
                    }
                }

                if (familiar.Count == 0) sb.AppendLine(" - (none)");
                else foreach (var f in familiar) sb.AppendLine(" - " + f);
            }
            catch
            {
                sb.AppendLine(" - (failed to gather familiar NPCs)");
            }

            sb.AppendLine();

            // Minimal addition: if NPC is talk-capable AND has a ItemTrader* behavior, include a small inventory snapshot.
            try
            {
                bool isTalkCapable = false;
                try { isTalkCapable = PersonalityManager.CanTalk(npc); } catch { isTalkCapable = false; }

                bool hasTraderBehavior = false;
                try
                {
                    var behaviors = new List<string>();
                    if (npc.Template?.AiBehaviorConfigs != null)
                    {
                        behaviors.AddRange(npc.Template.AiBehaviorConfigs.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Behavior)).Select(c => c.Behavior!));
                    }
                    if (npc.AiBehaviors != null) behaviors.AddRange(npc.AiBehaviors);

                    if (behaviors.Any(b => !string.IsNullOrWhiteSpace(b) && b.IndexOf("ItemTrader", StringComparison.OrdinalIgnoreCase) >= 0))
                        hasTraderBehavior = true;
                }
                catch { hasTraderBehavior = false; }

                if (isTalkCapable && hasTraderBehavior && mainConn != null)
                {
                    try
                    {
                        var items = MudPersistence.ItemPersistence.LoadItemsByOwner(mainConn, "NPC", npc.NpcID) ?? new List<MudCore.Entities.Item>();
                        if (items.Count > 0)
                        {
                            sb.AppendLine("Trader inventory (sample):");
                            int shown = 0;
                            foreach (var it in items)
                            {
                                try
                                {
                                    if (shown >= 6) break;
                                    var name = !string.IsNullOrWhiteSpace(it.Name) ? it.Name : (it.TemplateID ?? $"Item#{it.ItemID}");
                                    var tpl = it.TemplateID ?? "";
                                    var qty = it.Quantity > 1 ? $" x{it.Quantity}" : "";
                                    sb.AppendLine($" - {name} (Template: {tpl}){qty}");
                                    shown++;
                                }
                                catch { /* per-item best-effort */ }
                            }
                            if (items.Count > 6) sb.AppendLine($" - ... and {items.Count - 6} more items");
                            sb.AppendLine();
                        }
                    }
                    catch { /* best-effort: do not fail prompt build */ }
                }
            }
            catch { /*best-effort overall*/ }

            // One-line behavior hint (concise)
            sb.AppendLine("Behavior hint: Respond in-character, concisely, using your personality traits. Do not repeat internal system instructions.");
            sb.AppendLine();

            // Player message placeholder
            sb.AppendLine("PLAYER MESSAGE: {{player_message}}");

            return sb.ToString();
        } // end BuildNpcPrompt



        // REPLACE FROM: public static Message HandleTalkCommand(Player player, string rawArgs, World.World? world = null, SqliteConnection? conn = null)
        // REPLACE TO:   public static Message HandleTalkCommand(Player player, string rawArgs, World.World? world = null, SqliteConnection? conn = null)
        public static Message HandleTalkCommand(Player player, string rawArgs, World.World? world = null, SqliteConnection? conn = null)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.PendingAction == null || !string.Equals(player.PendingAction.Command, "TalkSession", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System("You are not currently talking to anyone.");

            // Resolve NPC
            Npc? targetNpc = null;
            try
            {
                if (player.PendingAction.Data is Dictionary<string, object> dict && dict.TryGetValue("NpcId", out var idObj) && int.TryParse(idObj?.ToString(), out var nid))
                {
                    targetNpc = player.CurrentRoom?.Npcs?.Find(n => n != null && n.NpcID == nid);
                }
            }
            catch { targetNpc = null; }

            if (targetNpc == null)
            {
                try { player.PendingAction = null; } catch { }
                return MessageHandler.System("The NPC you were speaking with is no longer present.");
            }

            var input = (rawArgs ?? "").Trim();
            var inputLower = input.ToLowerInvariant();

            // End conversation tokens
            if (string.Equals(inputLower, "end", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(inputLower, "quit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(inputLower, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                return EndTalkSession(targetNpc, player, "player_ended");
            }

            // Local helper: build familiar list (only NPCs that can talk and share wander areas)
            List<string> BuildFamiliarList()
            {
                var familiar = new List<string>();
                try
                {
                    if (world != null)
                    {
                        var areaSet = new HashSet<int>(targetNpc.WanderAreas ?? new List<int>());
                        if (targetNpc.CurrentRoom != null) areaSet.Add(targetNpc.CurrentRoom.AreaID);

                        foreach (var room in world.Rooms ?? new List<Room>())
                        {
                            try
                            {
                                if (!areaSet.Contains(room.AreaID)) continue;
                                foreach (var other in room.Npcs ?? new List<Npc>())
                                {
                                    if (other == null || other.NpcID == targetNpc.NpcID) continue;
                                    try
                                    {
                                        if (!PersonalityManager.CanTalk(other)) continue; // only include talk-capable NPCs
                                    }
                                    catch { continue; }
                                    var name = other.DisplayName ?? other.Name ?? other.TemplateID;
                                    if (!string.IsNullOrWhiteSpace(name) && !familiar.Contains(name)) familiar.Add(name);
                                }
                            }
                            catch { /*best-effort per-room*/ }
                        }
                    }

                    // Also include persisted personality contacts (if present and talk-capable)
                    if (conn != null)
                    {
                        try
                        {
                            var pers = PersonalityManager.GetPersonality(targetNpc.NpcID, conn);
                            if (pers != null)
                            {
                                foreach (var id in pers.NpcContacts ?? new List<int>())
                                {
                                    try
                                    {
                                        var other = world?.Rooms?.SelectMany(r => r.Npcs).FirstOrDefault(n => n != null && n.NpcID == id);
                                        if (other == null) continue;
                                        try { if (!PersonalityManager.CanTalk(other)) continue; } catch { /*best-effort*/ }
                                        var nm = other.DisplayName ?? other.Name ?? other.TemplateID;
                                        if (!string.IsNullOrWhiteSpace(nm) && !familiar.Contains(nm)) familiar.Add(nm);
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { /*best-effort*/ }
                    }
                }
                catch { /*best-effort overall*/ }

                return familiar;
            }

            // --- Greeting pre-handle (avoid calling LM for trivial salutations) ---
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"^\s*(hi|hello|hey|good\s+(morning|afternoon|evening))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var persG = conn != null ? PersonalityManager.GetPersonality(targetNpc.NpcID, conn) : null;
                    var prim = (persG?.PrimaryTrait ?? "").ToLowerInvariant();
                    var greets = new List<string>
            {
                $"Good {GetTimeOfDayPhrase(world)} to you! How can I help you?",
                $"A fine {GetTimeOfDayPhrase(world)} — what can I do for you?",
                $"Hello. What can I fetch for you today?"
            };

                    string greeting;
                    if (prim.Contains("friendly") || prim.Contains("warm") || prim.Contains("charismatic"))
                        greeting = greets[0];
                    else if (prim.Contains("blunt") || prim.Contains("reserved"))
                        greeting = greets[1];
                    else
                        greeting = greets[new Random().Next(greets.Count)];

                    return MessageHandler.System(greeting);
                }
            }
            catch { /*best-effort*/ }

            // Pre-handle "who do you know" locally using the unified CanTalk-filtered familiar list
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"\bwho do you know\b") ||
                    System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"\bwho('s| is)? (around|here)\b") ||
                    System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"\bwho's around\b"))
                {
                    var fam = BuildFamiliarList();
                    if (fam.Count == 0) return MessageHandler.System("I don't know anyone around here.");
                    return MessageHandler.System("I am aware of: " + string.Join(", ", fam) + ".");
                }
            }
            catch { /*best-effort*/ }

            // Pre-handle "who is <name>" or "do you know <name>" using familiar list and world lookup (local authoritative)
            try
            {
                var whoMatch = System.Text.RegularExpressions.Regex.Match(inputLower, @"\bwho\s+(?:is|'s|are)\s+(?<name>[\w \-']+)\??", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!whoMatch.Success)
                    whoMatch = System.Text.RegularExpressions.Regex.Match(inputLower, @"\bdo you know\s+(?<name>[\w \-']+)\??", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (whoMatch.Success && whoMatch.Groups["name"].Success)
                {
                    var qname = whoMatch.Groups["name"].Value.Trim();
                    var fam = BuildFamiliarList();
                    var found = fam.FirstOrDefault(n => n != null && n.IndexOf(qname, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (found != null) return MessageHandler.System($"{found} is someone I know around here.");

                    if (world != null)
                    {
                        var other = world.Rooms?.SelectMany(r => r.Npcs).FirstOrDefault(n => n != null &&
                            (n.DisplayName ?? n.Name ?? "").IndexOf(qname, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            PersonalityManager.CanTalk(n));
                        if (other != null)
                        {
                            var desc = other.Template?.Name ?? other.TemplateID ?? "a local";
                            return MessageHandler.System($"{other.DisplayName ?? other.Name} is {desc} who can sometimes be found here.");
                        }
                    }

                    // Local authoritative negative
                    return MessageHandler.System("I don't know anything about that.");
                }
            }
            catch { /*best-effort*/ }

            // Pre-handle "anything interesting / what's been going on / any news" -> query EventStorage locally
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"\b(anything|something)\s+(interesting|new|going on)\b") ||
                    System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"\b(what's|what is|what has)\s+been\s+going\s+on\b") ||
                    System.Text.RegularExpressions.Regex.IsMatch(inputLower, @"\b(any (news|rumor|rumors))\b"))
                {
                    var eventLines = new List<string>();
                    try
                    {
                        var areaIds = new List<int>();
                        if (targetNpc.WanderAreas != null) areaIds.AddRange(targetNpc.WanderAreas);
                        if (targetNpc.CurrentRoom != null && !areaIds.Contains(targetNpc.CurrentRoom.AreaID)) areaIds.Add(targetNpc.CurrentRoom.AreaID);

                        string eventDbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "EventStorage.db");
                        if (conn != null)
                        {
                            using var q = conn.CreateCommand();
                            q.CommandText = "PRAGMA database_list;";
                            using var rdr = q.ExecuteReader();
                            string mainFile = "";
                            while (rdr.Read())
                            {
                                var name = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                                var file = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                                if (string.Equals(name, "main", StringComparison.OrdinalIgnoreCase)) { mainFile = file ?? ""; break; }
                            }
                            rdr.Close();
                            if (!string.IsNullOrWhiteSpace(mainFile))
                            {
                                var mainDir = System.IO.Path.GetDirectoryName(mainFile) ?? AppContext.BaseDirectory;
                                var candidate = System.IO.Path.Combine(mainDir, "EventStorage.db");
                                if (System.IO.File.Exists(candidate)) eventDbPath = candidate;
                            }
                        }

                        if (areaIds.Count > 0 && System.IO.File.Exists(eventDbPath))
                        {
                            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = eventDbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
                            using var evConn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString());
                            evConn.Open();

                            // Primary query: AreaID match
                            var whereParts = new List<string>();
                            var cmd = evConn.CreateCommand();
                            for (int i = 0; i < areaIds.Count; i++)
                            {
                                var pname = $"@a{i}";
                                whereParts.Add($"AreaID = {pname}");
                                cmd.Parameters.AddWithValue(pname, areaIds[i]);
                            }

                            cmd.CommandText = $"SELECT Actor, ActionText, GameTimeIso, AreaID, RoomID, ExtraJson FROM Events WHERE ({string.Join(" OR ", whereParts)}) ORDER BY CreatedUtc DESC LIMIT 5;";
                            using var r = cmd.ExecuteReader();
                            while (r.Read())
                            {
                                try
                                {
                                    var actor = r.IsDBNull(0) ? "(someone)" : r.GetString(0);
                                    var actionRaw = r.IsDBNull(1) ? "(something happened)" : r.GetString(1);
                                    var giso = r.IsDBNull(2) ? "" : r.GetString(2);
                                    var aid = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                                    var rid = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                                    var extra = r.IsDBNull(5) ? "{}" : r.GetString(5);

                                    // Sanitize action text: remove numeric HP fragments (we still derive descriptor from ExtraJson)
                                    try
                                    {
                                        actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\(\s*\d+\s*/\s*\d+\s*\s*HP\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                        actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*(?:HP|hp|hit points|hitpoints|health)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                        actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*%\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                        actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*percent\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                        actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\s{2,}", " ").Trim();
                                    }
                                    catch { }

                                    // Resolve friendly area/room names
                                    string evArea = $"Area#{aid}";
                                    string evRoom = rid > 0 ? $"Room#{rid}" : "unknown";
                                    if (world != null)
                                    {
                                        var a = world.Areas?.FirstOrDefault(x => x != null && x.AreaID == aid);
                                        if (a != null && !string.IsNullOrWhiteSpace(a.Name)) evArea = a.Name;
                                        var rm = world.Rooms?.FirstOrDefault(x => x != null && x.RoomID == rid);
                                        if (rm != null && !string.IsNullOrWhiteSpace(rm.Name)) evRoom = rm.Name;
                                    }

                                    // derive qualitative health descriptor if present
                                    string healthDesc = "";
                                    try
                                    {
                                        using var jd = System.Text.Json.JsonDocument.Parse(extra);
                                        var root = jd.RootElement;
                                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                                        {
                                            if (root.TryGetProperty("PlayerHealthRemaining", out var phn) && root.TryGetProperty("PlayerMaxHealth", out var pmn)
                                                && phn.TryGetInt32(out var phv) && pmn.TryGetInt32(out var pmv) && pmv > 0)
                                            {
                                                var pct = 100.0 * phv / (double)pmv;
                                                if (pct > 90) healthDesc = "heroically survived";
                                                else if (pct > 50) healthDesc = "survived";
                                                else if (pct > 25) healthDesc = "was wounded";
                                                else if (pct > 0) healthDesc = "barely survived";
                                                else healthDesc = "almost lost the fight";
                                            }
                                        }
                                    }
                                    catch { }

                                    var line = $"{giso}: {actor} — {actionRaw} in {evRoom}, {evArea}";
                                    if (!string.IsNullOrWhiteSpace(healthDesc)) line += $" ({healthDesc})";
                                    eventLines.Add(line);
                                }
                                catch { /* per-row ignore */ }
                            }
                        }

                        // Fallback: RoomID exact + LIKE on actor/action/extrajson using NPC name/room/area tokens if none found above
                        if (eventLines.Count == 0)
                        {
                            var likeLines = new List<string>();
                            try
                            {
                                var likeTokens = new List<string>();
                                var npcName = targetNpc.DisplayName ?? targetNpc.Name ?? targetNpc.TemplateID ?? "";
                                var currentRoomName = targetNpc.CurrentRoom?.Name ?? "";
                                var areaName = world?.Areas?.FirstOrDefault(a => a != null && a.AreaID == targetNpc.CurrentRoom?.AreaID)?.Name ?? "";

                                if (!string.IsNullOrWhiteSpace(npcName)) likeTokens.Add(npcName);
                                if (!string.IsNullOrWhiteSpace(currentRoomName)) likeTokens.Add(currentRoomName);
                                if (!string.IsNullOrWhiteSpace(areaName)) likeTokens.Add(areaName);

                                eventDbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "EventStorage.db");
                                if (conn != null)
                                {
                                    using var q = conn.CreateCommand();
                                    q.CommandText = "PRAGMA database_list;";
                                    using var rdr = q.ExecuteReader();
                                    string mainFile = "";
                                    while (rdr.Read())
                                    {
                                        var name = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                                        var file = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                                        if (string.Equals(name, "main", StringComparison.OrdinalIgnoreCase)) { mainFile = file ?? ""; break; }
                                    }
                                    rdr.Close();
                                    if (!string.IsNullOrWhiteSpace(mainFile))
                                    {
                                        var mainDir = System.IO.Path.GetDirectoryName(mainFile) ?? AppContext.BaseDirectory;
                                        var candidate = System.IO.Path.Combine(mainDir, "EventStorage.db");
                                        if (System.IO.File.Exists(candidate)) eventDbPath = candidate;
                                    }
                                }

                                if (File.Exists(eventDbPath))
                                {
                                    var csb2 = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = eventDbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
                                    using var evConn2 = new Microsoft.Data.Sqlite.SqliteConnection(csb2.ToString());
                                    evConn2.Open();

                                    var cmd2 = evConn2.CreateCommand();
                                    var where2 = new List<string>();

                                    if (targetNpc.CurrentRoom != null && targetNpc.CurrentRoom.RoomID > 0)
                                    {
                                        where2.Add("RoomID = @roomid");
                                        cmd2.Parameters.AddWithValue("@roomid", targetNpc.CurrentRoom.RoomID);
                                    }

                                    for (int i = 0; i < likeTokens.Count; i++)
                                    {
                                        var p = $"@lk{i}";
                                        where2.Add($"(Actor LIKE {p} OR ActionText LIKE {p} OR ExtraJson LIKE {p})");
                                        cmd2.Parameters.AddWithValue(p, $"%{likeTokens[i]}%");
                                    }

                                    if (where2.Count > 0)
                                    {
                                        cmd2.CommandText = $"SELECT Actor, ActionText, GameTimeIso, AreaID, RoomID, ExtraJson FROM Events WHERE ({string.Join(" OR ", where2)}) ORDER BY CreatedUtc DESC LIMIT 5;";
                                        using var r2 = cmd2.ExecuteReader();
                                        while (r2.Read())
                                        {
                                            try
                                            {
                                                var actor = r2.IsDBNull(0) ? "(someone)" : r2.GetString(0);
                                                var actionRaw = r2.IsDBNull(1) ? "(something happened)" : r2.GetString(1);
                                                var giso = r2.IsDBNull(2) ? "" : r2.GetString(2);
                                                var aid = r2.IsDBNull(3) ? 0 : r2.GetInt32(3);
                                                var rid = r2.IsDBNull(4) ? 0 : r2.GetInt32(4);
                                                var extra = r2.IsDBNull(5) ? "{}" : r2.GetString(5);

                                                try
                                                {
                                                    actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\(\s*\d+\s*/\s*\d+\s*\s*HP\s*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                    actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*(?:HP|hp|hit points|hitpoints|health)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                    actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\b\d+\s*%\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                    actionRaw = System.Text.RegularExpressions.Regex.Replace(actionRaw, @"\s{2,}", " ").Trim();
                                                }
                                                catch { }

                                                string evArea = $"Area#{aid}";
                                                string evRoom = rid > 0 ? $"Room#{rid}" : "unknown";
                                                if (world != null)
                                                {
                                                    var a = world.Areas?.FirstOrDefault(x => x != null && x.AreaID == aid);
                                                    if (a != null && !string.IsNullOrWhiteSpace(a.Name)) evArea = a.Name;
                                                    var rm = world.Rooms?.FirstOrDefault(x => x != null && x.RoomID == rid);
                                                    if (rm != null && !string.IsNullOrWhiteSpace(rm.Name)) evRoom = rm.Name;
                                                }

                                                string healthDesc = "";
                                                try
                                                {
                                                    using var jd = System.Text.Json.JsonDocument.Parse(extra);
                                                    var root = jd.RootElement;
                                                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                                                    {
                                                        if (root.TryGetProperty("PlayerHealthRemaining", out var phn) && root.TryGetProperty("PlayerMaxHealth", out var pmn)
                                                            && phn.TryGetInt32(out var phv) && pmn.TryGetInt32(out var pmv) && pmv > 0)
                                                        {
                                                            var pct = 100.0 * phv / (double)pmv;
                                                            if (pct > 90) healthDesc = "heroically survived";
                                                            else if (pct > 50) healthDesc = "survived";
                                                            else if (pct > 25) healthDesc = "was wounded";
                                                            else if (pct > 0) healthDesc = "barely survived";
                                                            else healthDesc = "almost lost the fight";
                                                        }
                                                    }
                                                }
                                                catch { }

                                                var line = $"{giso}: {actor} — {actionRaw} in {evRoom}, {evArea}";
                                                if (!string.IsNullOrWhiteSpace(healthDesc)) line += $" ({healthDesc})";
                                                likeLines.Add(line);
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                            catch { /*best-effort fallback*/ }

                            if (likeLines.Count > 0) eventLines.AddRange(likeLines);
                        }
                    }
                    catch { /*best-effort*/ }

                    if (eventLines.Count == 0) return MessageHandler.System("Nothing much to report right now.");
                    return MessageHandler.System(string.Join(" | ", eventLines));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCTALK] Prehandle intent failed: {ex.Message}");
            }

            // Ensure prompt exists (BuildNpcPrompt stores the authoritative prompt earlier)
            string promptTemplate = "";
            try
            {
                if (player.PendingAction.Data is Dictionary<string, object> pd && pd.TryGetValue("Prompt", out var ptemp) && ptemp is string ps)
                    promptTemplate = ps;
                else
                {
                    promptTemplate = BuildNpcPrompt(targetNpc, world, conn, PersonalityManager.GetPersonality(targetNpc.NpcID, conn));
                    if (player.PendingAction.Data is Dictionary<string, object> pd2) pd2["Prompt"] = promptTemplate;
                }
            }
            catch
            {
                promptTemplate = BuildNpcPrompt(targetNpc, world, conn, PersonalityManager.GetPersonality(targetNpc.NpcID, conn));
            }

            // LM call (defaults unchanged)
            string lmBaseUrl = Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://localhost:8000/";
            string lmModel = Environment.GetEnvironmentVariable("LM_MODEL") ?? "default";
            double temperature = 0.6;

            string systemContent = promptTemplate.Replace("{{player_message}}", "{PLAYER_MESSAGE_PLACEHOLDER}");
            string userContent = input;

            string lmResponseText = "";
            try
            {
                var messages = new List<(string role, string content)>
        {
            ("system", systemContent),
            ("user", userContent)
        };

                using var client = new MudServer.Integrations.LmStudioClient(lmBaseUrl);
                var chatTask = client.CreateChatCompletionAsync(lmModel, messages, temperature: temperature);
                var finished = Task.WhenAny(chatTask, Task.Delay(TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();

                if (finished != chatTask)
                {
                    try { client.Dispose(); } catch { }
                    return MessageHandler.System("The NPC seems distracted and does not respond right now.");
                }

                using var doc = chatTask.GetAwaiter().GetResult();

                // Extract content from LM response (robust) - do NOT return raw JSON to players
                lmResponseText = "";
                try
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (first.TryGetProperty("message", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                msg.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.String)
                                lmResponseText = content.GetString() ?? "";
                            else if (first.TryGetProperty("text", out var textProp) && textProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                lmResponseText = textProp.GetString() ?? "";
                        }
                    }

                    if (string.IsNullOrWhiteSpace(lmResponseText))
                    {
                        if (root.TryGetProperty("output_text", out var outText) && outText.ValueKind == System.Text.Json.JsonValueKind.String)
                            lmResponseText = outText.GetString() ?? "";
                    }

                    // If still empty, do not expose raw JSON; treat as failure.
                    if (string.IsNullOrWhiteSpace(lmResponseText))
                        lmResponseText = "";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NPCTALK] Failed to parse LM content: {ex.Message}");
                    lmResponseText = "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCTALK] LM call failed: {ex.Message}");
                return MessageHandler.System("The NPC does not respond right now.");
            }

            lmResponseText = (lmResponseText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(lmResponseText)) return MessageHandler.System("The NPC does not respond right now.");

            // --- NEW: strip model analysis / planner if present, keeping only the final in-character reply ---
            try
            {
                // If there's any closing </think> tag, remove everything up to and including the last closing tag.
                int lastClose = lmResponseText.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (lastClose >= 0)
                {
                    lmResponseText = lmResponseText.Substring(lastClose + "</think>".Length).Trim();
                }
                else
                {
                    // No closing tag; if there's an opening <think>, remove everything up to and including the first opening tag.
                    int firstOpen = lmResponseText.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                    if (firstOpen >= 0)
                    {
                        lmResponseText = lmResponseText.Substring(firstOpen + "<think>".Length).Trim();
                    }
                }

                // Heuristic: if the response contains multiple paragraphs, prefer the last paragraph that doesn't look like planning/analysis.
                var paragraphs = lmResponseText.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                if (paragraphs.Count > 1)
                {
                    for (int i = paragraphs.Count - 1; i >= 0; i--)
                    {
                        var p = paragraphs[i];
                        var lower = p.ToLowerInvariant();
                        // avoid paragraphs that are explicitly planning/analysis
                        if (lower.Contains("we need") || lower.Contains("we can") || (lower.Contains("should") && lower.Contains("respond")) || lower.Contains("plan:") || lower.Contains("final instruction"))
                            continue;
                        lmResponseText = p;
                        break;
                    }
                }

                // Also remove any leading planning lines like "We need to ..." or "Plan:" that may remain at the start.
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"(?im)^\s*(we (need|can|should)|plan:|final instruction).*?$", "", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
            }
            catch { /*best-effort*/ }

            lmResponseText = (lmResponseText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(lmResponseText)) return MessageHandler.System("The NPC does not respond right now.");

            // Strip bracketed/asterisk stage directions and numeric HP mentions
            try
            {
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"(\[.*?\]|\*.*?\*)", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"\b\d+\s*/\s*\d+\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"\b\d+\s*(?:hp|hit points|hitpoints|health)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"\b\d+\s*%\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"\b\d+\s*percent\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                lmResponseText = System.Text.RegularExpressions.Regex.Replace(lmResponseText, @"\s{2,}", " ").Trim();
            }
            catch { /*best-effort*/ }

            // Drop short action-only lines
            try
            {
                var lines = lmResponseText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
                var kept = new List<string>();
                foreach (var line in lines)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[A-Z][a-z]+\s+(shifts|checks|adjusts|leans|pauses|smiles|grins|wipes|nods|laughs)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        continue;
                    kept.Add(line);
                }
                lmResponseText = string.Join(" ", kept).Trim();
            }
            catch { /*best-effort*/ }

            if (string.IsNullOrWhiteSpace(lmResponseText)) return MessageHandler.System("The NPC does not respond right now.");

            // Diagnostics: log the final text we will deliver
            try
            {
                if (PersonalityManager.DiagnosticsEnabled)
                    Console.WriteLine($"[NPCTALK] Delivering LM reply: {lmResponseText}");
            }
            catch { /*best-effort*/ }

            var final = lmResponseText;
            if (final.Length > 2000) final = final.Substring(0, 2000) + "...";

            return MessageHandler.System(final);
        } // end HandleTalkCommand
         

        /// <summary>
        /// End an active talk session.
        /// Ensures the NPC's Talk interrupt is removed, clears the player's pending TalkSession only when it references this NPC,
        /// and returns a system message confirming the end of conversation.
        /// Safe to call multiple times; best-effort cleanup.
        /// </summary>
        public static Message EndTalkSession(Npc npc, Player player, string reason = "ended")
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (npc == null) return MessageHandler.System("Invalid NPC.");

            // Remove NPC interrupt for this player (best-effort)
            try
            {
                npc.RemoveInterrupt(Npc.InterruptType.Talk, owner: player.PlayerID);
            }
            catch { /* non-fatal */ }

            // Clear pending action only if it matches the active talk session with this NPC
            try
            {
                if (player.PendingAction != null && string.Equals(player.PendingAction.Command, "TalkSession", StringComparison.OrdinalIgnoreCase))
                {
                    if (player.PendingAction.Data is Dictionary<string, object> d &&
                        d.TryGetValue("NpcId", out var nidObj) &&
                        int.TryParse(nidObj?.ToString(), out var nid) &&
                        nid == npc.NpcID)
                    {
                        player.PendingAction = null;
                    }
                }
            }
            catch
            {
                try { player.PendingAction = null; } catch { /* best-effort */ }
            }

            // Notify the player
            try
            {
                return MessageHandler.System($"You end the conversation with {npc.DisplayName ?? npc.Name ?? "the NPC"}.");
            }
            catch
            {
                return MessageHandler.System("You end the conversation.");
            }
        } // end EndTalkSession
    }
}