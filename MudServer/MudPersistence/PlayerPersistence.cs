using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Data;

namespace MudPersistence
{
    public static class PlayerPersistence
    {
        private static HashSet<string> _playerCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _colsLoaded = false;

        private static void EnsureOpen(SqliteConnection conn)
        {
            if (conn.State != ConnectionState.Open)
                conn.Open();
        }

        private static void EnsureColumns(SqliteConnection conn)
        {
            if (_colsLoaded || conn == null) return;
            try
            {
                EnsureOpen(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info('Player');";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    if (!rdr.IsDBNull(1))
                        _playerCols.Add(rdr.GetString(1));
                }
                _colsLoaded = true;
            }
            catch { }
        }

        private static bool Has(string col) => _playerCols.Contains(col);

        // Helper to read REAL/INTEGER/TEXT numeric fields robustly
        private static double GetDoubleFlexible(SqliteDataReader rdr, int ordinal, double fallback)
        {
            try
            {
                if (rdr.IsDBNull(ordinal)) return fallback;
                try { return rdr.GetDouble(ordinal); }
                catch
                {
                    try { return Convert.ToDouble(rdr.GetInt32(ordinal)); }
                    catch
                    {
                        try { return Convert.ToDouble(rdr.GetInt64(ordinal)); }
                        catch
                        {
                            var s = rdr.GetString(ordinal);
                            return double.TryParse(s, out var v) ? v : fallback;
                        }
                    }
                }
            }
            catch { return fallback; }
        }


        public static void LoadExtendedCombat(SqliteConnection conn, Player player)
        {
            if (conn == null || player == null) return;
            EnsureOpen(conn);
            EnsureColumns(conn);

            var wanted = new List<string>();
            if (Has("Level")) wanted.Add("Level");
            if (Has("Mass")) wanted.Add("Mass");
            if (Has("EffMass")) wanted.Add("EffMass");
            if (Has("CombatSkill")) wanted.Add("CombatSkill");
            if (Has("EffCombatSkill")) wanted.Add("EffCombatSkill");
            if (Has("SizeEnum")) wanted.Add("SizeEnum");
            if (Has("BaseDmgDice")) wanted.Add("BaseDmgDice");
            if (Has("EffDmgDice")) wanted.Add("EffDmgDice");
            if (Has("BaseArmorClass")) wanted.Add("BaseArmorClass");
            if (Has("EffArmorClass")) wanted.Add("EffArmorClass");
            if (Has("Buffs")) wanted.Add("Buffs");
            if (Has("Debuffs")) wanted.Add("Debuffs");
            if (Has("Experience")) wanted.Add("Experience");

            // NEW: Flags column support
            if (Has("Flags")) wanted.Add("Flags");

            if (wanted.Count == 0) return;

            var sql = $"SELECT {string.Join(",", wanted)} FROM Player WHERE PlayerID = @pid;";
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    int ordinal = 0;
                    foreach (var col in wanted)
                    {
                        if (rdr.IsDBNull(ordinal)) { ordinal++; continue; }
                        try
                        {
                            switch (col)
                            {
                                case "Level": player.Level = rdr.GetInt32(ordinal); break;
                                case "Mass": player.Mass = rdr.GetDouble(ordinal); break;
                                case "EffMass": player.EffMass = rdr.GetDouble(ordinal); break;
                                case "CombatSkill": player.CombatSkill = GetDoubleFlexible(rdr, ordinal, player.CombatSkill); break;
                                case "EffCombatSkill": player.EffCombatSkill = GetDoubleFlexible(rdr, ordinal, player.EffCombatSkill); break;
                                case "SizeEnum": player.SizeEnum = rdr.GetInt32(ordinal); break;
                                case "BaseDmgDice": player.BaseDmgDice = rdr.GetDouble(ordinal); break;
                                case "EffDmgDice": player.EffDmgDice = rdr.GetDouble(ordinal); break;
                                case "BaseArmorClass": player.BaseArmorClass = rdr.GetDouble(ordinal); break;
                                case "EffArmorClass": player.EffArmorClass = rdr.GetDouble(ordinal); break;
                                case "Buffs":
                                    player.Buffs = JsonSerializer.Deserialize<List<string>>(rdr.GetString(ordinal)) ?? new List<string>();
                                    break;
                                case "Debuffs":
                                    player.Debuffs = JsonSerializer.Deserialize<List<string>>(rdr.GetString(ordinal)) ?? new List<string>();
                                    break;
                                case "Experience":
                                    player.Experience = rdr.GetInt32(ordinal);
                                    break;
                                case "Flags":
                                    try
                                    {
                                        var fjson = rdr.GetString(ordinal);
                                        player.Flags = JsonSerializer.Deserialize<List<string>>(fjson) ?? new List<string>();
                                        player.FlagsJson = string.IsNullOrEmpty(fjson) ? "[]" : fjson;
                                    }
                                    catch
                                    {
                                        player.Flags = new List<string>();
                                        player.FlagsJson = "[]";
                                    }
                                    break;
                            }
                        }
                        catch { }
                        ordinal++;
                    }
                }
            }
            catch { }
        }

        // Add read/write support for LastAreaID (if present)
        public static void SavePlayer(SqliteConnection conn, Player player)
        {
            if (conn == null || player == null) return;

            // Prefer using the single-writer queue so Player writes are serialized with other writer work.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    SavePlayerInternal(wConn, player, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct save on provided connection.
            }
            catch
            {
                // best-effort: if enqueue fails, fall back to direct save.
            }

            // Direct save path (no external transaction)
            SavePlayerInternal(conn, player, null);
        }

        // Internal transactional implementation of SavePlayer.
        // When tx != null, commands will be attached to the provided transaction to allow atomic writer jobs.
        private static void SavePlayerInternal(SqliteConnection conn, Player player, SqliteTransaction? tx = null)
        {
            if (conn == null || player == null) return;

            EnsureOpen(conn);
            EnsureColumns(conn);

            var colAssignments = new List<string>
    {
        "Health = @health",
        "MaxHealth = @maxHealth",
        "LastRoomID = @room"
    };

            if (Has("Level")) colAssignments.Add("Level = @Level");
            if (Has("LastAreaID")) colAssignments.Add("LastAreaID = @lastAreaId");
            if (Has("Mass")) colAssignments.Add("Mass = @Mass");
            if (Has("EffMass")) colAssignments.Add("EffMass = @EffMass");
            if (Has("CombatSkill")) colAssignments.Add("CombatSkill = @CombatSkill");
            if (Has("EffCombatSkill")) colAssignments.Add("EffCombatSkill = @EffCombatSkill");
            if (Has("SizeEnum")) colAssignments.Add("SizeEnum = @SizeEnum");
            if (Has("BaseDmgDice")) colAssignments.Add("BaseDmgDice = @BaseDmgDice");
            if (Has("EffDmgDice")) colAssignments.Add("EffDmgDice = @EffDmgDice");
            if (Has("BaseArmorClass")) colAssignments.Add("BaseArmorClass = @BaseArmorClass");
            if (Has("EffArmorClass")) colAssignments.Add("EffArmorClass = @EffArmorClass");
            if (Has("Buffs")) colAssignments.Add("Buffs = @Buffs");
            if (Has("Debuffs")) colAssignments.Add("Debuffs = @Debuffs");
            if (Has("Experience")) colAssignments.Add("Experience = @Experience");
            if (Has("Flags")) colAssignments.Add("Flags = @Flags");

            var sql = $"UPDATE Player SET {string.Join(", ", colAssignments)} WHERE PlayerID = @pid;";

            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = sql;

            cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
            cmd.Parameters.AddWithValue("@health", player.Health);
            cmd.Parameters.AddWithValue("@maxHealth", player.MaxHealth);
            cmd.Parameters.AddWithValue("@room", player.CurrentRoom?.RoomID ?? 0);
            cmd.Parameters.AddWithValue("@lastAreaId", player.CurrentRoom?.AreaID ?? 0);

            cmd.Parameters.AddWithValue("@Level", player.Level);
            cmd.Parameters.AddWithValue("@Mass", player.Mass);
            cmd.Parameters.AddWithValue("@EffMass", player.EffMass);
            cmd.Parameters.AddWithValue("@CombatSkill", player.CombatSkill);
            cmd.Parameters.AddWithValue("@EffCombatSkill", player.EffCombatSkill);
            cmd.Parameters.AddWithValue("@SizeEnum", player.SizeEnum);
            cmd.Parameters.AddWithValue("@BaseDmgDice", player.BaseDmgDice);
            cmd.Parameters.AddWithValue("@EffDmgDice", player.EffDmgDice);
            cmd.Parameters.AddWithValue("@BaseArmorClass", player.BaseArmorClass);
            cmd.Parameters.AddWithValue("@EffArmorClass", player.EffArmorClass);
            cmd.Parameters.AddWithValue("@Buffs", JsonSerializer.Serialize(player.Buffs ?? new List<string>()));
            cmd.Parameters.AddWithValue("@Debuffs", JsonSerializer.Serialize(player.Debuffs ?? new List<string>()));

            // Protect against accidental overwrite of persisted Experience when in-memory Experience is still default (0).
            int expToSave = player.Experience;
            if (Has("Experience") && expToSave == 0 && player.DbPlayerID != 0)
            {
                try
                {
                    using var q = conn.CreateCommand();
                    if (tx != null) q.Transaction = tx;
                    q.CommandText = "SELECT Experience FROM Player WHERE PlayerID = @pid";
                    q.Parameters.AddWithValue("@pid", player.DbPlayerID);
                    var o = q.ExecuteScalar();
                    if (o != null && o != DBNull.Value)
                    {
                        if (int.TryParse(o.ToString(), out var dbExp))
                            expToSave = dbExp;
                    }
                }
                catch { /* non-fatal: fallback to in-memory value */ }
            }

            cmd.Parameters.AddWithValue("@Experience", expToSave);

            // NEW: Flags parameter (serialize deterministically)
            try
            {
                var flagsList = player.Flags ?? new List<string>();
                flagsList.Sort(System.StringComparer.OrdinalIgnoreCase);
                var flagsJson = JsonSerializer.Serialize(flagsList);
                cmd.Parameters.AddWithValue("@Flags", flagsJson);
            }
            catch
            {
                cmd.Parameters.AddWithValue("@Flags", "[]");
            }

            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerPersistence] SavePlayer failed (non-fatal): {ex.Message}");
                throw;
            }
        } // end SavePlayerInternal
    }
}