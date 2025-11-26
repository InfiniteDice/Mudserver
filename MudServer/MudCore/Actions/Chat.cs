using MudCore.Entities;
using MudCore.World;
using MudNetworking;
using System.Linq;

namespace MudServer
{
    public static class Chat
    {
        public static void Say(Player player, string text)
        {
            if (player.CurrentRoom == null)
            {
                player.PendingMessageQueue.Add(MessageHandler.System("You are lost in the void."));
                return;
            }

            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                player.PendingMessageQueue.Add(MessageHandler.System("You must say something."));
                return;
            }

            string messageText = $"{player.Name} says: {text}";

            // Broadcast to all players in the same room
            foreach (var p in player.CurrentRoom.Players)
            {
                p.PendingMessageQueue.Add(MessageHandler.System(messageText));
            }
        }
    }
}

