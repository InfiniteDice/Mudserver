using Microsoft.Data.Sqlite;
using System;

public static class DbPragmas
{
    public static void Configure(SqliteConnection conn)
    {
        if (conn == null) throw new ArgumentNullException(nameof(conn));
        try
        {
            using var cmd = conn.CreateCommand();

            // WAL enables concurrent readers while still allowing serialized writers
            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();

            // Good balance for many applications
            cmd.CommandText = "PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();

            // Determine cache size based on an estimated per-player budget (default 5 MB/player).
            // You can override:
            //  - MUD_EXPECTED_PLAYERS (int) to set how many concurrent players you plan for
            //  - MUD_SQLITE_CACHE_PER_PLAYER_MB (int) to change per-player MB
            //  - MUD_SQLITE_CACHE_MB_MAX (int) to cap the total cache size (MB)
            int expectedPlayers = 100; // safe default
            try
            {
                var ep = Environment.GetEnvironmentVariable("MUD_EXPECTED_PLAYERS");
                if (!string.IsNullOrWhiteSpace(ep) && int.TryParse(ep, out var epv) && epv > 0)
                    expectedPlayers = epv;
            }
            catch { /* best-effort */ }

            int perPlayerMb = 5; // new requested default: 5 MB per player
            try
            {
                var pp = Environment.GetEnvironmentVariable("MUD_SQLITE_CACHE_PER_PLAYER_MB");
                if (!string.IsNullOrWhiteSpace(pp) && int.TryParse(pp, out var ppv) && ppv > 0)
                    perPlayerMb = ppv;
            }
            catch { /* best-effort */ }

            long requestedCacheMb = (long)expectedPlayers * perPlayerMb;

            // Clamp to reasonable bounds to avoid accidental host exhaustion.
            int minMb = 16;               // minimum cache size
            int defaultMaxMb = 32 * 1024; // 32 GB
            int maxMb = defaultMaxMb;
            try
            {
                var envMax = Environment.GetEnvironmentVariable("MUD_SQLITE_CACHE_MB_MAX");
                if (!string.IsNullOrWhiteSpace(envMax) && int.TryParse(envMax, out var em) && em > 0)
                    maxMb = Math.Min(em, defaultMaxMb);
            }
            catch { /* best-effort */ }

            long cacheMb = Math.Max(minMb, Math.Min(requestedCacheMb, maxMb));

            Console.WriteLine($"[DB] Calculating SQLite cache: expectedPlayers={expectedPlayers}, perPlayerMB={perPlayerMb} => targetCacheMB={requestedCacheMb}. Clamped to {cacheMb} MB (min {minMb}, max {maxMb}).");

            // SQLite PRAGMA cache_size expects negative value = kilobytes
            long cacheKb = cacheMb * 1024L;
            cmd.CommandText = $"PRAGMA cache_size = -{cacheKb};";
            cmd.ExecuteNonQuery();

            // Optionally enable mmap for faster reads on supported platforms (bytes)
            long mmapBytes = 268_435_456; // default 256MB
            try
            {
                var env = Environment.GetEnvironmentVariable("MUD_SQLITE_MMAP_BYTES");
                if (!string.IsNullOrWhiteSpace(env) && long.TryParse(env, out var envBytes) && envBytes >= 0)
                {
                    mmapBytes = Math.Min(envBytes, 8L * 1024 * 1024 * 1024); // clamp to 8GB
                }
            }
            catch { /* best-effort */ }

            cmd.CommandText = $"PRAGMA mmap_size = {mmapBytes};";
            cmd.ExecuteNonQuery();

            // Set a reasonable WAL auto-checkpoint (in pages) to avoid unbounded WAL growth.
            // You can tune via MUD_SQLITE_WAL_AUTOCHECKPOINT (pages). Default 1000 pages.
            int walAutoCheckpoint = 1000;
            try
            {
                var env = Environment.GetEnvironmentVariable("MUD_SQLITE_WAL_AUTOCHECKPOINT");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var envPages) && envPages >= 0)
                {
                    walAutoCheckpoint = envPages;
                }
            }
            catch { /* best-effort */ }

            cmd.CommandText = $"PRAGMA wal_autocheckpoint = {walAutoCheckpoint};";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Failed to configure pragmas: {ex.Message}");
        }
    } // end Configure
}