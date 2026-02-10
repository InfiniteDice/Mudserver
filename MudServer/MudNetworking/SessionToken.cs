using System;

namespace MudNetworking
{
    /// <summary>
    /// Represents a player session token with expiry tracking.
    /// Allows players to reconnect without re-authenticating if token is still valid.
    /// </summary>
    public class SessionToken
    {
        public string Token { get; set; }
        public int DbPlayerID { get; set; }
        public string PlayerName { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiryUtc { get; set; }

        public SessionToken(int dbPlayerID, string playerName, int timeoutMinutes = 30)
        {
            DbPlayerID = dbPlayerID;
            PlayerName = playerName;
            CreatedUtc = DateTime.UtcNow;
            ExpiryUtc = DateTime.UtcNow.AddMinutes(timeoutMinutes); //set to 30 minutes by default

            // Generate token: MD5-like hash of timestamp + random + playerID
            Token = GenerateToken(dbPlayerID);
        }

        /// <summary>
        /// Check if this token is still valid (not expired).
        /// </summary>
        public bool IsValid => DateTime.UtcNow < ExpiryUtc;

        /// <summary>
        /// Refresh the token expiry (called on each command to prevent timeout).
        /// </summary>
        public void RefreshExpiry(int timeoutMinutes = 30)
        {
            ExpiryUtc = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        }

        /// <summary>
        /// Generate a unique session token.
        /// </summary>
        private static string GenerateToken(int dbPlayerID)
        {
            var timestamp = DateTime.UtcNow.Ticks.ToString();
            var random = Guid.NewGuid().ToString().Substring(0, 8);
            var combined = $"{timestamp}-{random}-{dbPlayerID}";

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
                return Convert.ToHexString(hash).Substring(0, 32); // Use first 32 chars
            }
        }
    }
}