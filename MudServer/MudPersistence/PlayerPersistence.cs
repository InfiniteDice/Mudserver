using Microsoft.Data.Sqlite;
using MudCore.Entities;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MudPersistence
{
    public static class PlayerPersistence
    {
        private static HashSet<string> _playerCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _colsLoaded = false;

        private static void EnsureColumns(SqliteConnection conn)
        {
            if (_colsLoaded || conn == null) return;
            try
            {
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

        public static void LoadExtendedCombat(SqliteConnection conn, Player player)
        {
            if (conn == null || player == null) return;
            EnsureColumns(conn);

            var wanted = new List<string>();
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
                                case "Mass": player.Mass = rdr.GetDouble(ordinal); break;
                                case "EffMass": player.EffMass = rdr.GetDouble(ordinal); break;
                                case "CombatSkill": player.CombatSkill = rdr.GetInt32(ordinal); break;
                                case "EffCombatSkill": player.EffCombatSkill = rdr.GetInt32(ordinal); break;
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
            EnsureColumns(conn);

            var colAssignments = new List<string>
            {
                "Health = @health",
                "MaxHealth = @maxHealth",
                "LastRoomID = @room"
            };

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

            var sql = $"UPDATE Player SET {string.Join(", ", colAssignments)} WHERE PlayerID = @pid;";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@pid", player.DbPlayerID);
            cmd.Parameters.AddWithValue("@health", player.Health);
            cmd.Parameters.AddWithValue("@maxHealth", player.MaxHealth);
            cmd.Parameters.AddWithValue("@room", player.CurrentRoom?.RoomID ?? 0);
            cmd.Parameters.AddWithValue("@lastAreaId", player.CurrentRoom?.AreaID ?? 0);

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
            cmd.Parameters.AddWithValue("@Experience", player.Experience);

            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerPersistence] SavePlayer failed (non-fatal): {ex.Message}");
            }
        }
    }
}