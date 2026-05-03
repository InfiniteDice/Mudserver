using MudCore.World;
using MudNetworking;
using System;
using System.Threading;

namespace MudServer
{
    public static class ShutdownManager
    {
        private static Timer? _countdownTimer = null;
        private static int _remainingMinutes = 0;
        private static string _reason = "";
        private static int _estimatedDowntime = 0;
        private static Action? _onShutdownCallback = null;
        private static World? _world = null;
        private static readonly object _lock = new object();

        public static bool IsShutdownPending
        {
            get
            {
                lock (_lock)
                {
                    return _countdownTimer != null;
                }
            }
        }

        /// <summary>
        /// Schedule a server shutdown with countdown notifications.
        /// </summary>
        /// <param name="minutes">Minutes until shutdown (0 = immediate)</param>
        /// <param name="reason">Reason for shutdown</param>
        /// <param name="downtime">Expected downtime in minutes</param>
        /// <param name="world">World instance for broadcasting</param>
        /// <param name="onShutdown">Callback to execute when shutdown begins</param>
        /// <returns>True if scheduled successfully, false if already pending</returns>
        public static bool ScheduleShutdown(int minutes, string reason, int downtime,
                                           World world, Action onShutdown)
        {
            lock (_lock)
            {
                if (_countdownTimer != null)
                {
                    Console.WriteLine("[SHUTDOWN] Shutdown already pending; cannot schedule another.");
                    return false;
                }

                _remainingMinutes = Math.Max(0, minutes);
                _reason = reason ?? "maintenance";
                _estimatedDowntime = Math.Max(0, downtime);
                _world = world;
                _onShutdownCallback = onShutdown;

                Console.WriteLine($"[SHUTDOWN] Scheduled: {_remainingMinutes}min, reason='{_reason}', downtime={_estimatedDowntime}min");

                // Immediate shutdown (0 minutes)
                if (_remainingMinutes == 0)
                {
                    SendFinalMessage();
                    ExecuteShutdown();
                    return true;
                }

                // Send initial notification
                SendCountdownMessage();

                // Start countdown timer (fires every 60 seconds)
                _countdownTimer = new Timer(OnCountdownTick, null,
                                           TimeSpan.FromMinutes(1),
                                           TimeSpan.FromMinutes(1));

                return true;
            }
        }

        /// <summary>
        /// Abort a pending shutdown.
        /// </summary>
        /// <returns>True if aborted successfully, false if no shutdown was pending</returns>
        public static bool AbortShutdown()
        {
            lock (_lock)
            {
                if (_countdownTimer == null)
                {
                    Console.WriteLine("[SHUTDOWN] No shutdown pending to abort.");
                    return false;
                }

                Console.WriteLine($"[SHUTDOWN] Aborted: was {_remainingMinutes}min remaining.");

                // Stop timer
                _countdownTimer?.Dispose();
                _countdownTimer = null;

                // Notify all players
                if (_world != null)
                {
                    try
                    {
                        // _world.Players is not populated at runtime; players live in room.Players across all rooms.
                        foreach (var room in _world.Rooms)
                        {
                            if (room?.Players == null) continue;
                            foreach (var p in room.Players)
                            {
                                if (p != null && p.IsAuthenticated)
                                    MessageHandler.SendSystem(p, "Server shutdown has been cancelled.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SHUTDOWN] Failed to broadcast abort: {ex.Message}");
                    }
                }

                // Reset state
                _remainingMinutes = 0;
                _reason = "";
                _estimatedDowntime = 0;
                _world = null;
                _onShutdownCallback = null;

                return true;
            }
        }

        private static void OnCountdownTick(object? state)
        {
            lock (_lock)
            {
                if (_countdownTimer == null) return; // Already aborted

                _remainingMinutes--;

                if (_remainingMinutes <= 0)
                {
                    // Final minute: send shutdown message and execute
                    SendFinalMessage();
                    ExecuteShutdown();
                }
                else
                {
                    // Send countdown update
                    SendCountdownMessage();
                }
            }
        }

        private static void SendCountdownMessage()
        {
            if (_world == null) return;

            string message = $"Server will shut down in {_remainingMinutes} minute{(_remainingMinutes != 1 ? "s" : "")}, reason: {_reason}, expected down time: {_estimatedDowntime} minutes.";

            try
            {
                // _world.Players is not populated at runtime; players live in room.Players across all rooms.
                foreach (var room in _world.Rooms)
                {
                    if (room?.Players == null) continue;
                    foreach (var p in room.Players)
                    {
                        if (p != null && p.IsAuthenticated)
                            MessageHandler.SendSystem(p, message);
                    }
                }

                Console.WriteLine($"[SHUTDOWN] Broadcast: {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SHUTDOWN] Failed to broadcast countdown: {ex.Message}");
            }
        }

        private static void SendFinalMessage()
        {
            if (_world == null) return;

            string message = $"Server is now shutting down, reason: {_reason}, expected down time: {_estimatedDowntime} minutes.";

            try
            {
                // _world.Players is not populated at runtime; players live in room.Players across all rooms.
                foreach (var room in _world.Rooms)
                {
                    if (room?.Players == null) continue;
                    foreach (var p in room.Players)
                    {
                        if (p != null && p.IsAuthenticated)
                            MessageHandler.SendSystem(p, message);
                    }
                }

                Console.WriteLine($"[SHUTDOWN] Final message: {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SHUTDOWN] Failed to broadcast final message: {ex.Message}");
            }
        }

        private static void ExecuteShutdown()
        {
            // Stop timer
            _countdownTimer?.Dispose();
            _countdownTimer = null;

            Console.WriteLine("[SHUTDOWN] Executing graceful shutdown...");

            // Give messages time to send (small delay)
            Thread.Sleep(1000);

            // Invoke shutdown callback
            try
            {
                _onShutdownCallback?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SHUTDOWN] Callback failed: {ex.Message}");
            }

            // Reset state
            _remainingMinutes = 0;
            _reason = "";
            _estimatedDowntime = 0;
            _world = null;
            _onShutdownCallback = null;
        }
    }
}
