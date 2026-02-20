using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MudNetworking
{
    /// <summary>
    /// Centralized security event logging for suspicious activity detection.
    /// Logs to: D:\MudServer\MudServer\security_events.txt
    /// </summary>
    public static class SecurityLogger
    {
        private static readonly string LogPath = @"D:\MudServer\MudServer\security_events.txt";
        private static readonly object LockObj = new object();
        private static readonly string BlocklistPath = @"D:\MudServer\MudServer\ip_blocklist.txt";
        private static readonly HashSet<string> BlockedIPs = new HashSet<string>();
        private static readonly object BlocklistLock = new object();

        private static readonly Dictionary<string, List<DateTime>> ViolationTimestamps = new Dictionary<string, List<DateTime>>();
        private const int MaxViolationsPerHour = 10;

        /// <summary>
        /// Record a violation and check if IP should be auto-blocked
        /// </summary>
        public static bool ShouldBlockIP(string ipAddress)
        {
            lock (LockObj)
            {
                if (!ViolationTimestamps.ContainsKey(ipAddress))
                {
                    ViolationTimestamps[ipAddress] = new List<DateTime>();
                }

                var violations = ViolationTimestamps[ipAddress];
                var now = DateTime.UtcNow;
                var oneHourAgo = now.AddHours(-1);

                // Remove old violations (older than 1 hour)
                violations.RemoveAll(v => v < oneHourAgo);

                // Add new violation
                violations.Add(now);

                // Check if threshold exceeded
                if (violations.Count >= MaxViolationsPerHour)
                {
                    BlockIP(ipAddress, $"Auto-blocked: {violations.Count} violations in 1 hour");
                    return true;
                }

                return false;
            }
        }

        // Load blocklist at startup
        public static void LoadBlocklist()
        {
            lock (BlocklistLock)
            {
                try
                {
                    if (File.Exists(BlocklistPath))
                    {
                        var lines = File.ReadAllLines(BlocklistPath)
                            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                            .ToList();

                        foreach (var ip in lines)
                        {
                            BlockedIPs.Add(ip.Trim());
                        }

                        Console.WriteLine($"[SECURITY] Loaded {BlockedIPs.Count} blocked IPs from {BlocklistPath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to load blocklist: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Check if an IP is blocked
        /// </summary>
        public static bool IsIPBlocked(string ipAddress)
        {
            lock (BlocklistLock)
            {
                return BlockedIPs.Contains(ipAddress);
            }
        }

        /// <summary>
        /// Block an IP address permanently
        /// </summary>
        public static void BlockIP(string ipAddress, string reason)
        {
            lock (BlocklistLock)
            {
                try
                {
                    if (BlockedIPs.Add(ipAddress))
                    {
                        var timestamp = DateTime.UtcNow.ToString("O");
                        var logLine = $"[{timestamp}] [BLOCKED_IP] {ipAddress} | Reason: {reason}\n";

                        File.AppendAllText(BlocklistPath, $"{ipAddress} # {reason} ({timestamp})\n");
                        File.AppendAllText(LogPath, logLine);

                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[SECURITY] IP BLOCKED: {ipAddress} ({reason})");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to block IP: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Unblock an IP address
        /// </summary>
        public static void UnblockIP(string ipAddress)
        {
            lock (BlocklistLock)
            {
                try
                {
                    if (BlockedIPs.Remove(ipAddress))
                    {
                        var timestamp = DateTime.UtcNow.ToString("O");
                        var logLine = $"[{timestamp}] [UNBLOCKED_IP] {ipAddress}\n";

                        File.AppendAllText(LogPath, logLine);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[SECURITY] IP UNBLOCKED: {ipAddress}");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to unblock IP: {ex.Message}");
                }
            }
        }

        // Pattern detection for injection attempts
        // Refined to avoid false positives on common game commands like 'drop' (as in 'drop cup') or 'select'.
        // We now check for SQL-specific command sequences rather than naked keywords that double as game verbs.
        private static readonly Regex SqlInjectionPattern = new Regex(
            @"\b(DROP\s+(TABLE|DATABASE|INDEX|VIEW|PROC)|DELETE\s+FROM|INSERT\s+INTO|UPDATE\s+.*\s+SET|SELECT\s+.*\s+FROM|UNION\s+ALL|UNION\s+SELECT|ALTER\s+TABLE|EXEC\s*\()\b|--",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodeInjectionPattern = new Regex(
            @"(javascript:|onerror=|onclick=|<script|</script>|eval\(|System\.|Process\.|File\.)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BinaryPattern = new Regex(@"[\x00-\x08\x0B-\x0C\x0E-\x1F]");

        /// <summary>
        /// Log suspicious message content (SQL injection, code injection, etc.)
        /// </summary>
        public static void LogSuspiciousMessage(string playerName, int playerDbId, string messageContent, string reason)
        {
            lock (LockObj)
            {
                try
                {
                    var timestamp = DateTime.UtcNow.ToString("O");
                    var truncated = messageContent.Length > 200
                        ? messageContent.Substring(0, 200) + "..."
                        : messageContent;
                    var escaped = EscapeForLogging(truncated);

                    var logLine = $"[{timestamp}] [SUSPICIOUS] Player={playerName} (DbID={playerDbId}) | Reason={reason} | Content={escaped}\n";

                    File.AppendAllText(LogPath, logLine);
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SECURITY] {reason}: {playerName}");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to log suspicious message: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Log spam/rate limit violations
        /// </summary>
        public static void LogSpamViolation(string playerName, int playerDbId, int messagesInWindow, int windowSeconds)
        {
            lock (LockObj)
            {
                try
                {
                    var timestamp = DateTime.UtcNow.ToString("O");
                    var logLine = $"[{timestamp}] [SPAM] Player={playerName} (DbID={playerDbId}) | {messagesInWindow} messages in {windowSeconds}s\n";

                    File.AppendAllText(LogPath, logLine);
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($"[SECURITY] SPAM: {playerName} sent {messagesInWindow} messages in {windowSeconds}s");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to log spam: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Log failed authentication attempts
        /// </summary>
        public static void LogAuthFailure(string playerName, string reason, string ipAddress = "unknown")
        {
            lock (LockObj)
            {
                try
                {
                    var timestamp = DateTime.UtcNow.ToString("O");
                    var logLine = $"[{timestamp}] [AUTH_FAIL] Player={playerName} | IP={ipAddress} | Reason={reason}\n";

                    File.AppendAllText(LogPath, logLine);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[SECURITY] AUTH FAILURE: {playerName} ({reason})");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to log auth failure: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Log oversized messages
        /// </summary>
        public static void LogOversizeMessage(string playerName, int playerDbId, int messageSize, int maxAllowed)
        {
            lock (LockObj)
            {
                try
                {
                    var timestamp = DateTime.UtcNow.ToString("O");
                    var logLine = $"[{timestamp}] [OVERSIZE] Player={playerName} (DbID={playerDbId}) | Size={messageSize} bytes (max={maxAllowed})\n";

                    File.AppendAllText(LogPath, logLine);
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SECURITY] OVERSIZE MESSAGE: {playerName} ({messageSize} bytes)");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SECURITY][ERROR] Failed to log oversize: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Analyze message for suspicious patterns and return reason if found
        /// </summary>
        public static (bool isSuspicious, string reason) AnalyzeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return (false, "");

            // Check for SQL injection patterns
            if (SqlInjectionPattern.IsMatch(message))
            {
                var matches = SqlInjectionPattern.Matches(message).Cast<Match>().Select(m => m.Value).Distinct();
                return (true, $"SQL injection attempt detected: {string.Join(",", matches)}");
            }

            // Check for code injection patterns
            if (CodeInjectionPattern.IsMatch(message))
            {
                var matches = CodeInjectionPattern.Matches(message).Cast<Match>().Select(m => m.Value).Distinct();
                return (true, $"Code injection attempt detected: {string.Join(",", matches)}");
            }

            // Check for binary/control characters (except newlines/tabs)
            if (BinaryPattern.IsMatch(message))
            {
                return (true, "Binary/control characters detected");
            }

            // Check for excessive special characters (potential obfuscation)
            var specialCharCount = message.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            if (specialCharCount > message.Length * 0.5)
            {
                return (true, $"Excessive special characters ({specialCharCount}/{message.Length})");
            }

            return (false, "");
        }

        private static string EscapeForLogging(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            return text
                .Replace("\\", "\\\\")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\"", "\\\"");
        }
    }
}