using System;
using System.Collections.Generic;
using MudCore.Entities;
using MudNetworking;

namespace MudCore.Actions
{
    /// <summary>
    /// Placeholder skeleton for NPC "talk" interactions.
    /// Responsibilities (planned):
    ///  - Start / manage a short-lived talk session between a Player and an Npc.
    ///  - Apply an owner-scoped InterruptType.Talk so NPCs do not wander while conversing.
    ///  - Provide a single entry-point for the command handler to route talk-related text while session active.
    ///  - Provide safe termination and cleanup (removes interrupt, refunds/cleans pending state).
    /// 
    /// Implementation notes:
    ///  - Methods below intentionally contain placeholder logic and explanatory TODOs.
    ///  - Keep session state lightweight: store a small PendingAction on the Player (Command="TalkSession").
    ///  - Use owner = player.PlayerID when adding interrupts so only the talker may clear the session.
    ///  - Persist conversation state or dialog trees elsewhere when ready; this file should remain the runtime session manager.
    /// </summary>
    public static class NpcTalk
    {
        // Start a talk session. Applies owner-scoped Talk interrupt on the NPC and places a PendingAction on the player.
        // Returns a system Message suitable for immediate client display.
        public static Message BeginTalkSession(Player player, Npc npc, string? topic = null)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (npc == null) return MessageHandler.System("Invalid NPC.");
            if (player.CurrentRoom == null || npc.CurrentRoom == null || player.CurrentRoom != npc.CurrentRoom)
                return MessageHandler.System("You must be in the same room to talk to that NPC.");

            // If player already talking to someone, refuse or end existing session (policy decision).
            try
            {
                if (player.PendingAction != null && string.Equals(player.PendingAction.Command, "TalkSession", StringComparison.OrdinalIgnoreCase))
                    return MessageHandler.System("You are already in a conversation. Type 'end' to finish it first.");
            }
            catch { /* best-effort */ }

            // Mark session on player so subsequent talk-related commands route here.
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
            catch
            {
                // If setting pending action fails, still attempt to add interrupt (best-effort).
            }

            // Apply interrupt to keep NPC in place while talking. Owner = player.PlayerID
            try
            {
                npc.AddInterrupt(Npc.InterruptType.Talk, owner: player.PlayerID, note: topic ?? "TalkSession", ttl: null);
            }
            catch { /* best-effort */ }

            // TODO: hook into dialog engine / script system here. For now return a friendly prompt.
            string greeting = $"You begin speaking with {npc.DisplayName}.";
            if (!string.IsNullOrWhiteSpace(topic))
                greeting += $" Topic: {topic}";

            greeting += " (Type 'say <text>' to speak, 'ask <question>' to ask, or 'end' to finish.)";
            return MessageHandler.System(greeting);
        }

        // Handle talk-session commands while a TalkSession pending action is active on the player.
        // This is the single entrypoint the CommandHandler should call when a player's PendingAction.Command == "TalkSession".
        // Placeholder: implements basic parsing and leaves TODO hooks for dialog logic.
        public static Message HandleTalkCommand(Player player, string rawArgs)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (player.PendingAction == null || !string.Equals(player.PendingAction.Command, "TalkSession", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System("You are not currently talking to anyone.");

            // Resolve NPC from pending data (best-effort)
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
                // Attempt to clear stale pending action and notify player
                try { player.PendingAction = null; } catch { }
                return MessageHandler.System("The NPC you were speaking with is no longer present.");
            }

            var input = (rawArgs ?? "").Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return MessageHandler.System("During a conversation you can: 'say <text>', 'ask <question>', or 'end'.");
            }

            // Basic command parse
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var verb = parts[0].ToLowerInvariant();
            var payload = parts.Length > 1 ? parts[1].Trim() : "";

            switch (verb)
            {
                case "say":
                    if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: say <text>");
                    // TODO: route player's spoken text to dialog engine / simple echo for now
                    // Placeholder response: NPC echos or provides trivial reply
                    try
                    {
                        foreach (var p in player.CurrentRoom.Players)
                            MessageHandler.SendSystem(p, $"{player.Name} says to {targetNpc.DisplayName}: \"{payload}\"");
                    }
                    catch { /* best-effort */ }

                    // TODO: integrate with NPC response generator; for now a canned reply
                    return MessageHandler.System($"{targetNpc.DisplayName} listens and nods.");

                case "ask":
                    if (string.IsNullOrWhiteSpace(payload)) return MessageHandler.System("Usage: ask <question>");
                    // TODO: call question/intent parser and dialog tree. Placeholder: NPC gives a canned answer.
                    try
                    {
                        foreach (var p in player.CurrentRoom.Players)
                            MessageHandler.SendSystem(p, $"{player.Name} asks {targetNpc.DisplayName}: \"{payload}\"");
                    }
                    catch { /* best-effort */ }

                    return MessageHandler.System($"{targetNpc.DisplayName} responds: \"I don't have an answer for that yet.\"");

                case "end":
                case "quit":
                case "cancel":
                    return EndTalkSession(targetNpc, player, "player_ended");

                default:
                    return MessageHandler.System("Unknown talk command. Valid: say, ask, end.");
            }
        }

        // End an active talk session. Removes owner-scoped interrupt and clears the player's PendingAction if it references this NPC.
        // Returns a friendly message for the player.
        public static Message EndTalkSession(Npc npc, Player player, string reason = "ended")
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (npc == null) return MessageHandler.System("Invalid NPC.");

            // Remove owner-scoped Talk interrupt (best-effort)
            try
            {
                npc.RemoveInterrupt(Npc.InterruptType.Talk, owner: player.PlayerID);
            }
            catch { /* best-effort */ }

            // Clear player's pending talk action if it references this NPC
            try
            {
                if (player.PendingAction != null && string.Equals(player.PendingAction.Command, "TalkSession", StringComparison.OrdinalIgnoreCase))
                {
                    bool referencesNpc = false;
                    try
                    {
                        if (player.PendingAction.Data is Dictionary<string, object> d && d.TryGetValue("NpcId", out var nidObj) && int.TryParse(nidObj?.ToString(), out var nid))
                            referencesNpc = nid == npc.NpcID;
                    }
                    catch { referencesNpc = false; }

                    if (referencesNpc) player.PendingAction = null;
                }
            }
            catch { /* best-effort */ }

            // TODO: optionally persist conversation logs or trigger NPC follow-up actions.
            return MessageHandler.System($"{npc.DisplayName} ends the conversation. ({reason})");
        }

        // Query helper: is NPC currently engaged in a talk session?
        public static bool IsTalking(Npc npc)
        {
            if (npc == null) return false;
            try { return npc.HasInterrupt(Npc.InterruptType.Talk); } catch { return false; }
        }
    }
}