using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MudNetworking
{
    /// <summary>
    /// Manages player session tokens to prevent disconnection on browser blur/phone sleep.
    /// Maintains token → player mapping with automatic expiry cleanup.
    /// </summary>
    public sealed class SessionManager
    {
        private static readonly Lazy<SessionManager> _instance = new Lazy<SessionManager>(() => new SessionManager());
        public static SessionManager Instance => _instance.Value;

        // Token string -> SessionToken mapping
        private readonly ConcurrentDictionary<string, SessionToken> _sessions = new ConcurrentDictionary<string, SessionToken>();
        private readonly object _cleanupLock = new object();
        private System.Threading.Timer _cleanupTimer;

        private SessionManager() { }

        /// <summary>
        /// Start the cleanup background task (removes expired tokens).
        /// Call this once at server startup.
        /// </summary>
        public void Start()
        {
            _cleanupTimer = new System.Threading.Timer(_ => CleanupExpiredTokens(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Console.WriteLine("[SESSION] SessionManager started.");
        }

        /// <summary>
        /// Create a new session token for a player after login.
        /// </summary>
        public string CreateSession(int dbPlayerID, string playerName)
        {
            var sessionToken = new SessionToken(dbPlayerID, playerName);
            _sessions[sessionToken.Token] = sessionToken;
            Console.WriteLine($"[SESSION] Created session for player '{playerName}' (DbID={dbPlayerID}): {sessionToken.Token}");
            return sessionToken.Token;
        }

        /// <summary>
        /// Validate and retrieve a session by token.
        /// Returns null if token is invalid or expired.
        /// </summary>
        public SessionToken GetSession(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            if (_sessions.TryGetValue(token, out var session))
            {
                if (session.IsValid)
                    return session;
                else
                    _sessions.TryRemove(token, out _); // Clean up expired
            }

            return null;
        }

        /// <summary>
        /// Refresh a session's expiry time (called on each command).
        /// </summary>
        public bool RefreshSession(string token, int timeoutMinutes = 5)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;

            if (_sessions.TryGetValue(token, out var session))
            {
                if (session.IsValid)
                {
                    session.RefreshExpiry(timeoutMinutes);
                    return true;
                }
                else
                    _sessions.TryRemove(token, out _);
            }

            return false;
        }

        /// <summary>
        /// Invalidate a session (logout).
        /// </summary>
        public void DestroySession(string token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                _sessions.TryRemove(token, out _);
                Console.WriteLine($"[SESSION] Destroyed session: {token}");
            }
        }

        /// <summary>
        /// Clean up expired tokens (called periodically).
        /// </summary>
        private void CleanupExpiredTokens()
        {
            lock (_cleanupLock)
            {
                try
                {
                    var expired = _sessions.Where(kvp => !kvp.Value.IsValid).ToList();
                    int count = 0;
                    foreach (var kvp in expired)
                    {
                        if (_sessions.TryRemove(kvp.Key, out _))
                            count++;
                    }

                    if (count > 0)
                        Console.WriteLine($"[SESSION] Cleaned up {count} expired session(s). Active sessions: {_sessions.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SESSION] Cleanup failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Shutdown the session manager (stops cleanup timer).
        /// </summary>
        public void Stop()
        {
            try
            {
                _cleanupTimer?.Dispose();
                _sessions.Clear();
                Console.WriteLine("[SESSION] SessionManager stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSION] Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current session count (for diagnostics).
        /// </summary>
        public int GetActiveSessionCount => _sessions.Count;
    }
}