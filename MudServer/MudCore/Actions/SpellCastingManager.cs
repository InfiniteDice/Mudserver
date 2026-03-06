using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MudCore.Actions
{
    /// <summary>
    /// SpellCastingManager handles spell casting validation, cast timer, interruption, and completion.
    ///
    /// Cast flow:
    ///   1) CommandHandler parses "cast [spellId] [at/on target]" -> calls StartCasting
    ///   2) StartCasting: validates all requirements, resolves target, sets PendingAction or fires instantly
    ///   3) Effects.ProcessTimedEffects expires "IsCasting:[n]" -> calls CompleteCast
    ///   4) CompleteCast: deducts power, consumes components, fires effect stub, clears PendingAction
    ///
    /// Spell effects (Damage / Health / TimedEffect dispatch) are injected in a future step.
    /// The effectTarget and deliveryMethod values are stored in PendingAction.Data for future use.
    /// </summary>
    public static class SpellCastingManager
    {
        private const string CASTING_FLAG_PREFIX = "IsCasting:";
        // Effect name used in TimedEffectRegex — matches "Is[Casting]:[n]" in player Flags
        private const string CASTING_FLAG_PREFIX_EFFECT = "Casting";
        private const string PENDING_COMMAND = "CastSpell";

        // -------------------------------------------------------------------------
        // Public entry point: called by CommandHandler "cast" case
        // -------------------------------------------------------------------------

        /// <summary>
        /// Begin casting a spell. Validates all requirements then either fires instantly
        /// (castTimeSeconds == 0) or sets a timed PendingAction.
        /// </summary>
        public static Message StartCasting(
            Player player,
            string spellId,
            string? targetExpr,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            if (player == null || string.IsNullOrWhiteSpace(spellId))
                return MessageHandler.System("Invalid cast operation.");

            if (room == null)
                return MessageHandler.System("You are not in a valid room.");

            // Load spell template
            var template = LoadSpellTemplate(spellId);
            if (template == null)
                return MessageHandler.System($"Unknown spell '{spellId}'.");

            // Run full validation pipeline
            var validationResult = ValidateCast(player, template, room, world, conn);
            if (validationResult != null)
                return validationResult;

            // Resolve target
            var (targetNpcId, targetIsSelf, targetError) = ResolveTarget(player, template, targetExpr, room);
            if (targetError != null)
                return MessageHandler.System(targetError);

            string spellDisplayName = !string.IsNullOrWhiteSpace(template.Name)
                ? template.Name
                : (!string.IsNullOrWhiteSpace(template.Id) ? template.Id : spellId);

            string deliveryMethod = template.DeliveryMethod ?? "";
            string effectTarget = template.SpellEffects?.FirstOrDefault()?.EffectTarget ?? "Room";

            // Store data for CompleteCast
            var pendingData = new Dictionary<string, object>
            {
                { "SpellId",        spellId },
                { "SpellName",      spellDisplayName },
                { "TargetNpcId",    targetNpcId ?? (object)0 },
                { "TargetPlayerId", ResolvePlayerTarget(targetExpr, room) },
                { "TargetIsSelf",   targetIsSelf },
                { "RoomID",         room.RoomID },
                { "DeliveryMethod", deliveryMethod },
                { "EffectTarget",   effectTarget }
            };

            // NEW: Check if spell requires a choice before casting (e.g., Grow: +1 or +2 size)
            var effect = template.SpellEffects?.FirstOrDefault();
            if (effect?.HasChoicePrompt == true && !string.IsNullOrWhiteSpace(effect.ChoicePromptText))
            {
                // Store all necessary info for choice response handler
                pendingData["ChoicePromptText"] = effect.ChoicePromptText;
                pendingData["ChoiceOptions"] = effect.ChoiceOptions ?? new List<int> { 1 };
                pendingData["ChoiceFieldName"] = effect.ChoiceFieldName ?? "Choice";
                pendingData["DurationModifier"] = effect.DurationModifier;
                pendingData["CastTimeSeconds"] = template.CastTimeSeconds;

                player.PendingAction = new PendingAction
                {
                    Command = PENDING_COMMAND,
                    Step = "AwaitingChoice",
                    Data = pendingData
                };

                try { MudPersistence.PlayerPersistence.SavePlayer(conn, player); } catch { /* best-effort */ }

                // Send prompt to player
                MessageHandler.SendSystem(player, effect.ChoicePromptText);
                return null;
            }

            // Instantaneous cast (castTimeSeconds == 0 or not set)
            if (template.CastTimeSeconds <= 0)
            {
                player.PendingAction = new PendingAction
                {
                    Command = PENDING_COMMAND,
                    Step = "Complete",
                    Data = pendingData
                };
                return CompleteCast(player, conn, world);
            }

            // Timed cast — store cast duration directly in TimedEffectSeconds so it ticks
            // down at 3 real seconds per tick. Use 1 minute flag as the container but seed
            // the seconds counter to the actual castTimeSeconds value so short casts (3s, 6s)
            // fire after the correct number of ticks instead of waiting a full game minute.
            int castSeconds = Math.Max(3, template.CastTimeSeconds);

            player.PendingAction = new PendingAction
            {
                Command = PENDING_COMMAND,
                Step = "Casting",
                Data = pendingData
            };

            // Write the flag as 1 minute (minimum container) but override the seconds counter
            // so ProcessTimedEffects fires after castSeconds real seconds, not 60.
            var castFlags = player.Flags ?? new List<string>();
            string castFlagPrefix = $"Is{CASTING_FLAG_PREFIX_EFFECT}:";
            castFlags.RemoveAll(f => f.StartsWith(castFlagPrefix, StringComparison.OrdinalIgnoreCase));
            castFlags.Add($"{castFlagPrefix}1");
            player.Flags = castFlags;
            player.TimedEffectSeconds[CASTING_FLAG_PREFIX_EFFECT] = castSeconds;

            try { MudPersistence.PlayerPersistence.SavePlayer(conn, player); } catch { /* best-effort */ }

            // Broadcast cast start
            string spellNameForMsg = !string.IsNullOrWhiteSpace(template.Name) ? template.Name : spellId;
            MessageHandler.SendSystem(player, $"You begin weaving magic for '{spellNameForMsg}'... ({template.CastTimeSeconds}s)");
            foreach (var p in room.Players.Where(p => p != player))
                MessageHandler.SendSystem(p, $"{player.Name} begins weaving magic...");

            return null;
        }

        // -------------------------------------------------------------------------
        // Validation pipeline — returns a failure Message or null on success
        // -------------------------------------------------------------------------

        private static Message? ValidateCast(
            Player player,
            SpellTemplate spell,
            Room room,
            World.World world,
            SqliteConnection conn)
        {
            // 1) Spell memorized
            bool memorized = false;
            try
            {
                using var sel = conn.CreateCommand();
                sel.CommandText = "SELECT COUNT(*) FROM PlayerSpellMem WHERE PlayerID = @pid AND SpellId = @spell LIMIT 1;";
                sel.Parameters.AddWithValue("@pid", player.DbPlayerID);
                sel.Parameters.AddWithValue("@spell", spell.Id);
                var result = sel.ExecuteScalar();
                if (result != null && int.TryParse(result.ToString(), out int cnt))
                    memorized = cnt > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAST] Memorization check failed: {ex.Message}");
            }

            if (!memorized)
                return MessageHandler.System($"You do not have '{spell.Id}' memorized.");

            // 2) Skill level
            if (!string.IsNullOrWhiteSpace(spell.RequiredSkill) && spell.MinSkillLevel > 0)
            {
                int skillLevel = player.GetSkillLevel(spell.RequiredSkill, conn);
                if (skillLevel < spell.MinSkillLevel)
                    return MessageHandler.System(
                        $"You need {spell.RequiredSkill} level {spell.MinSkillLevel} to cast this. You have level {skillLevel}.");
            }

            // 3) Ritual only — reserved for future multi-player ritual system
            if (spell.RitualOnly)
                return MessageHandler.System("This spell requires a ritual and cannot be cast alone.");

            // 4) Alignment restriction
            if (!string.IsNullOrWhiteSpace(spell.AlignmentRestriction) &&
                !string.Equals(spell.AlignmentRestriction, "None", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(spell.AlignmentRestriction, player.Alignment.ToString(), StringComparison.OrdinalIgnoreCase))
                    return MessageHandler.System("Your alignment prevents you from casting this spell.");
            }

            // 5) Power cost
            if (spell.PowerCost > 0 && player.Power < spell.PowerCost)
                return MessageHandler.System(
                    $"You need {spell.PowerCost} power to cast this. You have {player.Power}.");

            // 6) notRoomFlags — block if room has any forbidden flag (e.g. "NoMagic")
            if (spell.NotRoomFlags != null && spell.NotRoomFlags.Count > 0 && room.Flags != null)
            {
                foreach (var forbidden in spell.NotRoomFlags)
                {
                    if (string.IsNullOrWhiteSpace(forbidden)) continue;
                    if (room.Flags.Any(f => string.Equals(f, forbidden, StringComparison.OrdinalIgnoreCase)))
                        return MessageHandler.System("You cannot cast magic here.");
                }
            }

            // 7) Required room flags — room must have ALL listed flags
            if (spell.RoomFlags != null && spell.RoomFlags.Count > 0)
            {
                foreach (var required in spell.RoomFlags)
                {
                    if (string.IsNullOrWhiteSpace(required)) continue;
                    bool hasFlag = room.Flags != null &&
                                   room.Flags.Any(f => string.Equals(f, required, StringComparison.OrdinalIgnoreCase));
                    if (!hasFlag)
                        return MessageHandler.System("The conditions here are not right for this spell.");
                }
            }

            // 8) Area climate
            if (spell.AreaClimate != null && spell.AreaClimate.Count > 0)
            {
                var climateRequirements = spell.AreaClimate.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                if (climateRequirements.Count > 0)
                {
                    try
                    {
                        var area = world?.Areas?.FirstOrDefault(a => a != null && a.AreaID == room.AreaID);
                        if (area != null)
                        {
                            bool climateMatch = climateRequirements.Any(c =>
                                string.Equals(c, area.Climate, StringComparison.OrdinalIgnoreCase));
                            if (!climateMatch)
                                return MessageHandler.System("The climate here prevents this spell.");
                        }
                        // If area not found, skip silently — climate data may not yet be populated
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CAST] Climate check failed: {ex.Message}");
                    }
                }
            }

            // 9) Tools required — player must have a held item matching each tool
            if (spell.ToolsRequired != null && spell.ToolsRequired.Count > 0)
            {
                foreach (var tool in spell.ToolsRequired)
                {
                    if (string.IsNullOrWhiteSpace(tool)) continue;
                    bool hasTool = player.Inventory?.Any(i =>
                        i != null &&
                        i.Flags != null &&
                        i.Flags.Any(f => string.Equals(f, "Held", StringComparison.OrdinalIgnoreCase)) &&
                        (string.Equals(i.TemplateID, tool, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(i.Name, tool, StringComparison.OrdinalIgnoreCase))) ?? false;

                    if (!hasTool)
                        return MessageHandler.System($"You need to be holding a {tool} to cast this spell.");
                }
            }

            // 10) Components + optional container requirement
            if (spell.CastingComponents != null && spell.CastingComponents.Count > 0)
            {
                var componentError = ValidateComponents(player, spell, conn);
                if (componentError != null)
                    return MessageHandler.System(componentError);
            }

            // 11) statModifier — silent, logged only (player stats not yet implemented)
            try
            {
                if (spell.SpellEffects != null)
                {
                    foreach (var effect in spell.SpellEffects)
                    {
                        if (!string.IsNullOrWhiteSpace(effect.StatModifier))
                            Console.WriteLine($"[CAST][INFO] statModifier '{effect.StatModifier}' noted for spell '{spell.Id}', not yet applied.");
                        if (!string.IsNullOrWhiteSpace(effect.StatModifies))
                            Console.WriteLine($"[CAST][INFO] statModifies '{effect.StatModifies}' noted for spell '{spell.Id}', not yet applied.");
                    }
                }
            }
            catch { /* best-effort */ }

            return null; // all checks passed
        }

        // -------------------------------------------------------------------------
        // Component validation helper
        // -------------------------------------------------------------------------

        private static string? ValidateComponents(Player player, SpellTemplate spell, SqliteConnection conn)
        {
            bool requiresContainer = spell.Container != null && spell.Container.Count > 0;

            if (!requiresContainer)
            {
                // Components can be anywhere in player inventory (direct or in any container)
                foreach (var component in spell.CastingComponents)
                {
                    if (string.IsNullOrWhiteSpace(component.Template)) continue;

                    int available = CountComponentInInventory(player, component.Template, conn);
                    if (available < component.Qty)
                        return $"You are missing {component.Template} (need {component.Qty}, have {available}).";
                }
                return null;
            }

            // Container required — find the matching container in player inventory
            // Match by TemplateID contains/starts-with the container name (e.g. "pouch_small" matches "Pouch")
            Item? matchedContainer = null;
            foreach (var containerName in spell.Container!)
            {
                if (string.IsNullOrWhiteSpace(containerName)) continue;
                matchedContainer = player.Inventory?.FirstOrDefault(i =>
                    i != null &&
                    string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(i.TemplateID) &&
                    i.TemplateID.IndexOf(containerName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (matchedContainer != null) break;
            }

            if (matchedContainer == null)
            {
                // Use the first container entry as a friendly word — strip underscores and take the
                // first meaningful token so "pouch_small" or "Pouch" both produce "pouch".
                string friendlyType = spell.Container!
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Split('_')[0].ToLowerInvariant())
                    .FirstOrDefault() ?? "container";

                return $"You must have your components in a {friendlyType} to cast this spell.";
            }

            // Verify each component exists in that container with sufficient qty
            List<Item> containerContents;
            try
            {
                containerContents = ItemPersistence.LoadItemsByOwner(conn, "Item", matchedContainer.ItemID) ?? new List<Item>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAST] Failed to load container contents for component check: {ex.Message}");
                return "Failed to check components due to a server error.";
            }

            foreach (var component in spell.CastingComponents)
            {
                if (string.IsNullOrWhiteSpace(component.Template)) continue;
                int inContainer = containerContents
                    .Where(i => i != null &&
                                string.Equals(i.TemplateID, component.Template, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => Math.Max(1, i.Quantity));

                if (inContainer < component.Qty)
                    return $"You are missing {component.Template} in your {matchedContainer.Name} (need {component.Qty}, have {inContainer}).";
            }

            return null;
        }

        private static int CountComponentInInventory(Player player, string templateId, SqliteConnection conn)
        {
            int count = 0;

            // Direct inventory
            count += player.Inventory?
                .Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase))
                .Sum(i => Math.Max(1, i.Quantity)) ?? 0;

            if (count > 0) return count;

            // Inside carried containers
            try
            {
                var containers = player.Inventory?
                    .Where(i => i != null && string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? new List<Item>();

                foreach (var container in containers)
                {
                    if (container.ItemID <= 0) continue;
                    var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID) ?? new List<Item>();
                    count += contents
                        .Where(i => i != null && string.Equals(i.TemplateID, templateId, StringComparison.OrdinalIgnoreCase))
                        .Sum(i => Math.Max(1, i.Quantity));
                    if (count > 0) return count;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAST] CountComponentInInventory failed for '{templateId}': {ex.Message}");
            }

            return count;
        }

        // -------------------------------------------------------------------------
        // Target resolution
        // -------------------------------------------------------------------------

        private static (int? targetNpcId, bool targetIsSelf, string? error) ResolveTarget(
            Player player,
            SpellTemplate spell,
            string? targetExpr,
            Room room)
        {
            string effectTarget = spell.SpellEffects?.FirstOrDefault()?.EffectTarget ?? "Room";

            switch (effectTarget.ToUpperInvariant())
            {
                case "SINGLE":
                case "NPC":
                    if (string.IsNullOrWhiteSpace(targetExpr))
                        return (null, false, $"Usage: cast {spell.Id} at/on <target>");

                    // "self" targets the player
                    if (string.Equals(targetExpr, "self", StringComparison.OrdinalIgnoreCase))
                        return (null, true, null);

                    // Resolve NPC target in room
                    var matches = NpcTargeting.FindMatches(room, targetExpr);
                    if (matches.Count == 0)
                        return (null, false, $"No target matching '{targetExpr}' here.");

                    if (matches.Count > 1)
                    {
                        // Multiple matches — for now return error; future can set PendingAction select
                        var list = string.Join(", ", matches.Select(m => m.DisplayName));
                        return (null, false, $"Multiple targets match '{targetExpr}': {list}. Be more specific.");
                    }

                    return (matches[0].NpcID, false, null);

                case "ROOM":
                case "":
                    // No target required — room is implicit
                    return (null, false, null);

                case "ALL":
                    // All NPCs in room — no explicit target required
                    return (null, false, null);

                case "AREA":
                case "WORLD":
                    return (null, false, "This spell's range is not yet implemented.");

                default:
                    // Unknown effectTarget — treat as room-scoped, no target needed
                    return (null, false, null);
            }
        }

        // -------------------------------------------------------------------------
        // Cast interruption — called by CombatManager when player takes damage > 0
        // -------------------------------------------------------------------------

        /// <summary>
        /// Interrupt an in-progress cast. No power is deducted (power only deducted on completion).
        /// </summary>
        public static void InterruptCast(Player player, string reason, SqliteConnection conn)
        {
            if (player?.PendingAction == null ||
                !string.Equals(player.PendingAction.Command, PENDING_COMMAND, StringComparison.OrdinalIgnoreCase))
                return;

            // Remove the IsCasting timed effect flag
            var flags = player.Flags ?? new List<string>();
            flags.RemoveAll(f => !string.IsNullOrWhiteSpace(f) &&
                                  f.StartsWith(CASTING_FLAG_PREFIX, StringComparison.OrdinalIgnoreCase));
            player.Flags = flags;
            player.TimedEffectSeconds.Remove("Casting");

            player.PendingAction = null;

            MessageHandler.SendSystem(player, $"Your spell is interrupted! {reason}");

            // Persist flag removal best-effort
            try { PlayerPersistence.SavePlayer(conn, player); } catch { /* best-effort */ }
        }

        // -------------------------------------------------------------------------
        // Cast completion — called from Effects.ProcessTimedEffects when "Casting" expires
        // -------------------------------------------------------------------------

        /// <summary>
        /// Complete the cast: deduct power, consume components, fire effect stub.
        /// Called either directly (instantaneous) or via Effects.ProcessTimedEffects (timed).
        /// </summary>
        public static Message CompleteCast(Player player, SqliteConnection conn, World.World world)
        {
            var room = player.CurrentRoom;

            if (player?.PendingAction == null ||
                !string.Equals(player.PendingAction.Command, PENDING_COMMAND, StringComparison.OrdinalIgnoreCase))
                return null;

            var data = player.PendingAction.Data as Dictionary<string, object>;
            if (data == null)
            {
                player.PendingAction = null;
                return MessageHandler.System("Cast data lost.");
            }

            string spellId = data.TryGetValue("SpellId", out var sid) ? sid?.ToString() ?? "" : "";
            string spellName = data.TryGetValue("SpellName", out var sn) ? sn?.ToString() ?? spellId : spellId;
            string effectTarget = data.TryGetValue("EffectTarget", out var et) ? et?.ToString() ?? "" : "";
            string deliveryMethod = data.TryGetValue("DeliveryMethod", out var dm) ? dm?.ToString() ?? "" : "";
            int roomId = data.TryGetValue("RoomID", out var rid) && int.TryParse(rid?.ToString(), out int parsedRid) ? parsedRid : 0;

            // Reload template for power cost and component data
            var template = LoadSpellTemplate(spellId);
            if (template == null)
            {
                player.PendingAction = null;
                return MessageHandler.System($"Spell template '{spellId}' could not be loaded at completion.");
            }

            // 0) Verify target still in room — abort if gone (power still deducted as cast interruption cost).
            // SKIP this check only for targetIsSelf — the caster is always present
            bool targetIsSelf = data.TryGetValue("TargetIsSelf", out var tis) && (bool)tis;

            Console.WriteLine($"[CAST] CompleteCast DEBUG: targetIsSelf={targetIsSelf}, data.keys={string.Join(",", data.Keys)}");
            if (data.ContainsKey("TargetIsSelf"))
                Console.WriteLine($"[CAST] TargetIsSelf value in data: {data["TargetIsSelf"]} (type: {data["TargetIsSelf"]?.GetType()})");


            if (!targetIsSelf)
            {
                // Validate NPC target is still in room
                if (data.TryGetValue("TargetNpcId", out var tnidRaw) &&
                    tnidRaw != null &&
                    !(tnidRaw is string str && string.IsNullOrEmpty(str)))
                {
                    try
                    {
                        int resolvedId = Convert.ToInt32(tnidRaw);
                        if (resolvedId > 0)
                        {
                            // Always use player.CurrentRoom — it is the live reference, not a stale world copy
                            bool targetPresent = player.CurrentRoom?.Npcs?.Any(n => n.NpcID == resolvedId) ?? false;
                            if (!targetPresent)
                            {
                                player.PendingAction = null;
                                if (template.PowerCost > 0)
                                    player.Power = Math.Max(0, player.Power - template.PowerCost);
                                MessageHandler.SendSystem(player, $"Your target is no longer here. {spellName} fizzles!");
                                return null;
                            }
                        }
                    }
                    catch { /* non-numeric TargetNpcId — no target to check */ }
                }
            }

            // 1) Deduct power
            if (template.PowerCost > 0)
            {
                player.Power = Math.Max(0, player.Power - template.PowerCost);
                try
                {
                    MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                    {
                        using var cmd = wConn.CreateCommand();
                        cmd.Transaction = wTx;
                        cmd.CommandText = "UPDATE Player SET Power = @power WHERE PlayerID = @pid";
                        cmd.Parameters.AddWithValue("@power", player.Power);
                        cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                        cmd.ExecuteNonQuery();
                        return (object?)null;
                    }, timeout: TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CAST] Failed to persist power deduction for {player.Name}: {ex.Message}");
                }
            }

            // 2) Consume components
            if (template.CastingComponents != null && template.CastingComponents.Count > 0)
            {
                ConsumeComponents(player, template, conn);
            }

            // 3) Remove spell from PlayerSpellMem — must use DbWriteQueue, same connection the INSERT used
            try
            {
                Console.WriteLine($"[CAST] About to DELETE: PlayerID={player.DbPlayerID}, SpellId='{spellId}' (length={spellId?.Length ?? 0})");

                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    using var del = wConn.CreateCommand();
                    del.Transaction = wTx;
                    del.CommandText = "DELETE FROM PlayerSpellMem WHERE PlayerID = @pid AND SpellId = @spell;";
                    del.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    del.Parameters.AddWithValue("@spell", spellId);

                    // Also log what's actually in the table before delete
                    using var checkCmd = wConn.CreateCommand();
                    checkCmd.Transaction = wTx;
                    checkCmd.CommandText = "SELECT SpellId FROM PlayerSpellMem WHERE PlayerID = @pid;";
                    checkCmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    using var reader = checkCmd.ExecuteReader();
                    var spellsInDb = new List<string>();
                    while (reader.Read()) spellsInDb.Add(reader.GetString(0));
                    Console.WriteLine($"[CAST] Spells in DB before DELETE: {string.Join(", ", spellsInDb)}");

                    int rows = del.ExecuteNonQuery();
                    Console.WriteLine($"[CAST] PlayerSpellMem DELETE rows affected: {rows} for spell='{spellId}' pid={player.DbPlayerID}");
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAST] FAILED to remove spell '{spellId}' from PlayerSpellMem for {player.Name}: {ex.Message}");
            }

            // 4) Execute spell effects
            targetIsSelf = data.TryGetValue("TargetIsSelf", out tis) && (bool)tis;
            int targetNpcId = 0;
            int targetPlayerId = 0;

            if (data.TryGetValue("TargetNpcId", out var tnid) && int.TryParse(tnid?.ToString(), out int parsedNpcId))
                targetNpcId = parsedNpcId;

            if (data.TryGetValue("TargetPlayerId", out var tpid) && int.TryParse(tpid?.ToString(), out int parsedPlayerId))
                targetPlayerId = parsedPlayerId;

            // NEW: Handle spells with choices (e.g., Grow)
            // Check if this spell had a choice prompt and extract the choice
            var effect = template.SpellEffects?.FirstOrDefault();
            if (effect?.HasChoicePrompt == true && effect.ChoiceFieldName != null)
            {
                // Spell required a choice - it should have been stored by HandleChoiceResponse
                int choice = data.TryGetValue(effect.ChoiceFieldName, out var choiceVal)
                    ? Convert.ToInt32(choiceVal)
                    : effect.ChoiceOptions?.FirstOrDefault() ?? 1;

                double durationModifier = effect.DurationModifier;

                // Dispatch to spell-specific handler based on spell ID
                switch (spellId.ToLowerInvariant())
                {
                    case "grow":
                        SpellEffects.DeliverGrow(player, template, effect, targetNpcId, targetPlayerId, targetIsSelf, room, world, conn, choice, durationModifier);
                        break;

                    // Add other choice-based spells here as they're implemented
                    default:
                        Console.WriteLine($"[CAST] No effect handler for choice spell '{spellId}'");
                        break;
                }
            }
            else
            {
                // Non-choice spells: dispatch by effect type
                SpellEffects.DeliverSpell(player, spellId, template, targetNpcId, targetPlayerId, targetIsSelf, room, world, conn);
            }

            // 5) Clear pending action
            player.PendingAction = null;

            // 6) Notify player and room
            MessageHandler.SendSystem(player, $"You cast {spellName}!");
            if (room != null)
            {
                foreach (var p in room.Players.Where(p => p != player))
                    MessageHandler.SendSystem(p, $"{player.Name} casts a spell!");
            }

            return null;
        }

        // -------------------------------------------------------------------------
        // Component consumption
        // -------------------------------------------------------------------------

        private static void ConsumeComponents(Player player, SpellTemplate spell, SqliteConnection conn)
        {
            if (spell.CastingComponents == null || spell.CastingComponents.Count == 0) return;

            bool requiresContainer = spell.Container != null && spell.Container.Count > 0;
            Item? matchedContainer = null;

            if (requiresContainer)
            {
                foreach (var containerName in spell.Container!)
                {
                    if (string.IsNullOrWhiteSpace(containerName)) continue;
                    matchedContainer = player.Inventory?.FirstOrDefault(i =>
                        i != null &&
                        string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(i.TemplateID) &&
                        i.TemplateID.IndexOf(containerName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (matchedContainer != null) break;
                }
            }

            foreach (var component in spell.CastingComponents)
            {
                if (string.IsNullOrWhiteSpace(component.Template)) continue;
                int remaining = component.Qty;

                try
                {
                    if (matchedContainer != null)
                    {
                        // Consume from specific container
                        var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", matchedContainer.ItemID)
                                       ?? new List<Item>();
                        foreach (var item in contents.Where(i =>
                                     i != null &&
                                     string.Equals(i.TemplateID, component.Template, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (remaining <= 0) break;
                            remaining = ConsumeFromItem(item, remaining, conn);
                        }
                    }
                    else
                    {
                        // Consume from direct inventory first
                        foreach (var item in player.Inventory?
                                     .Where(i => i != null &&
                                                 string.Equals(i.TemplateID, component.Template, StringComparison.OrdinalIgnoreCase))
                                     .ToList() ?? new List<Item>())
                        {
                            if (remaining <= 0) break;
                            remaining = ConsumeFromItem(item, remaining, conn);
                        }

                        // Then from any carried container
                        if (remaining > 0)
                        {
                            var containers = player.Inventory?
                                .Where(i => i != null &&
                                            string.Equals(i.ItemCategory, "Container", StringComparison.OrdinalIgnoreCase))
                                .ToList() ?? new List<Item>();

                            foreach (var container in containers)
                            {
                                if (remaining <= 0) break;
                                var contents = ItemPersistence.LoadItemsByOwner(conn, "Item", container.ItemID)
                                               ?? new List<Item>();
                                foreach (var item in contents.Where(i =>
                                             i != null &&
                                             string.Equals(i.TemplateID, component.Template, StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (remaining <= 0) break;
                                    remaining = ConsumeFromItem(item, remaining, conn);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CAST] ConsumeComponents failed for '{component.Template}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Consume up to 'needed' qty from a single item row. Returns remaining needed after consumption.
        /// </summary>
        private static int ConsumeFromItem(Item item, int needed, SqliteConnection conn)
        {
            if (item == null || needed <= 0) return needed;
            try
            {
                int consume = Math.Min(needed, Math.Max(1, item.Quantity));
                int newQty = item.Quantity - consume;
                if (newQty > 0)
                {
                    item.Quantity = newQty;
                    ItemPersistence.SaveItem(conn, item);
                }
                else
                {
                    if (item.ItemID > 0)
                        ItemPersistence.DeleteItem(conn, item.ItemID);
                }
                return needed - consume;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAST] ConsumeFromItem failed for ItemID={item.ItemID}: {ex.Message}");
                return needed;
            }
        }

        // -------------------------------------------------------------------------
        // Template loading — same pattern as MemorizationManager.GetSkillForSpell
        // -------------------------------------------------------------------------

        private static SpellTemplate? LoadSpellTemplate(string spellId)
        {
            try
            {
                string fileName = $"spell_{spellId.ToLowerInvariant()}.json";
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

                if (dataDir == null || !dataDir.Exists) return null;

                var files = Directory.GetFiles(dataDir.FullName, fileName, SearchOption.AllDirectories);
                if (files.Length == 0) return null;

                var json = File.ReadAllText(files[0]);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<SpellTemplate>(json, options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAST] LoadSpellTemplate failed for '{spellId}': {ex.Message}");
                return null;
            }
        }

        private static int ResolvePlayerTarget(string? targetExpr, Room room)
        {
            if (string.IsNullOrWhiteSpace(targetExpr) || room == null)
                return 0;

            var matches = room.Players.Where(p =>
                p.Name.Contains(targetExpr, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 1)
                return matches[0].DbPlayerID;

            return 0;
        }
    }
}