using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MudPersistence
{
    public static class NpcPersistence
    {
        private static HashSet<string> _npcCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _colsLoaded;

        private static void EnsureNpcColumns(SqliteConnection conn)
        {
            if (_colsLoaded || conn == null) return;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info('Npc');";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    if (!rdr.IsDBNull(1))
                        _npcCols.Add(rdr.GetString(1));
                }
                _colsLoaded = true;
            }
            catch { }
        }

        private static bool Has(string col) => _npcCols.Contains(col);

        // Include CurrentAreaID on insert/update
        public static int InsertNpc(SqliteConnection conn, Npc npc)
        {
            EnsureNpcColumns(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Npc (TemplateID, Name, Alignment, MaxHealth, Health, CurrentRoomID, IsPersistent, VisualTrait, CurrentAreaID)
                VALUES (@tid, @name, @align, @maxhp, @hp, @room, @persist, @trait, @area);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@tid", npc.TemplateID ?? "");
            cmd.Parameters.AddWithValue("@name", npc.Name ?? "");
            cmd.Parameters.AddWithValue("@align", (int)npc.Alignment);
            cmd.Parameters.AddWithValue("@maxhp", npc.HealthMax);
            cmd.Parameters.AddWithValue("@hp", npc.Health);
            cmd.Parameters.AddWithValue("@room", npc.CurrentRoomID);
            cmd.Parameters.AddWithValue("@persist", npc.IsPersistent ? 1 : 0);
            cmd.Parameters.AddWithValue("@trait", npc.VisualTrait ?? "");
            cmd.Parameters.AddWithValue("@area", npc.CurrentRoom?.AreaID ?? 0);

            int newId = (int)(long)cmd.ExecuteScalar();
            npc.NpcID = newId;

            SaveNpcCombat(conn, npc);
            return newId;
        }

        public static void UpdateNpc(SqliteConnection conn, Npc npc)
        {
            EnsureNpcColumns(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE Npc SET
                    Health = @hp,
                    MaxHealth = @maxhp,
                    CurrentRoomID = @room,
                    VisualTrait = @trait,
                    CurrentAreaID = @area
                WHERE NpcID = @id;";
            cmd.Parameters.AddWithValue("@hp", npc.Health);
            cmd.Parameters.AddWithValue("@maxhp", npc.HealthMax);
            cmd.Parameters.AddWithValue("@room", npc.CurrentRoomID);
            cmd.Parameters.AddWithValue("@trait", npc.VisualTrait ?? "");
            cmd.Parameters.AddWithValue("@area", npc.CurrentRoom?.AreaID ?? 0);
            cmd.Parameters.AddWithValue("@id", npc.NpcID);
            try { cmd.ExecuteNonQuery(); } catch { }

            SaveNpcCombat(conn, npc);
        }

        public static void DeleteNpc(SqliteConnection conn, int npcId)
        {
            if (conn == null || npcId <= 0) return;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Npc WHERE NpcID = @id;";
                cmd.Parameters.AddWithValue("@id", npcId);
                cmd.ExecuteNonQuery();
                Console.WriteLine($"[NpcPersistence] Deleted NPC NpcID={npcId} from DB.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NpcPersistence] Failed to delete NPC NpcID={npcId}: {ex.Message}");
            }
        }

        private static void SaveNpcCombat(SqliteConnection conn, Npc npc)
        {
            EnsureNpcColumns(conn);

            var assignments = new List<string>();
            if (Has("Mass")) assignments.Add("Mass = @Mass");
            if (Has("EffMass")) assignments.Add("EffMass = @EffMass");
            if (Has("CombatSkill")) assignments.Add("CombatSkill = @CombatSkill");
            if (Has("EffCombatSkill")) assignments.Add("EffCombatSkill = @EffCombatSkill");
            if (Has("SizeEnum")) assignments.Add("SizeEnum = @SizeEnum");
            if (Has("BaseDmgDice")) assignments.Add("BaseDmgDice = @BaseDmgDice");
            if (Has("BaseArmorClass")) assignments.Add("BaseArmorClass = @BaseArmorClass");
            if (Has("EffArmorClass")) assignments.Add("EffArmorClass = @EffArmorClass");
            if (Has("Buffs")) assignments.Add("Buffs = @Buffs");
            if (Has("Debuffs")) assignments.Add("Debuffs = @Debuffs");

            if (assignments.Count == 0) return;

            using var up = conn.CreateCommand();
            up.CommandText = $"UPDATE Npc SET {string.Join(", ", assignments)} WHERE NpcID = @id;";
            up.Parameters.AddWithValue("@id", npc.NpcID);

            up.Parameters.AddWithValue("@Mass", npc.Mass ?? 85.0);
            up.Parameters.AddWithValue("@EffMass", npc.EffMass ?? (npc.Mass ?? 85.0));
            up.Parameters.AddWithValue("@CombatSkill", npc.CombatSkill ?? 50);
            up.Parameters.AddWithValue("@EffCombatSkill", npc.EffCombatSkill ?? (npc.CombatSkill ?? 50));
            up.Parameters.AddWithValue("@SizeEnum", npc.SizeEnum ?? 4);
            up.Parameters.AddWithValue("@BaseDmgDice", npc.BaseDmgDice);
            up.Parameters.AddWithValue("@BaseArmorClass", npc.BaseArmorClass);
            up.Parameters.AddWithValue("@EffArmorClass", npc.EffArmorClass);

            var buffsJson = JsonSerializer.Serialize(npc.Buffs ?? new List<string>());
            var debuffsJson = JsonSerializer.Serialize(npc.Debuffs ?? new List<string>());
            up.Parameters.AddWithValue("@Buffs", buffsJson);
            up.Parameters.AddWithValue("@Debuffs", debuffsJson);

            try { up.ExecuteNonQuery(); } catch { }
        }

        // Add a harmless consistency warning in loader
        public static List<Npc> LoadAllNpcs(SqliteConnection conn, World world)
        {
            EnsureNpcColumns(conn);
            var npcs = new List<Npc>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Npc;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var npc = new Npc();
                try
                {
                    npc.NpcID = GetInt(rdr, "NpcID", 0);
                    npc.TemplateID = GetString(rdr, "TemplateID", "");
                    npc.Name = GetString(rdr, "Name", "");
                    npc.Alignment = (Alignment)GetInt(rdr, "Alignment", 1);
                    npc.HealthMax = GetInt(rdr, "MaxHealth", 100);
                    npc.Health = GetInt(rdr, "Health", npc.HealthMax);
                    npc.CurrentRoomID = GetInt(rdr, "CurrentRoomID", 0);
                    npc.IsPersistent = GetInt(rdr, "IsPersistent", 1) != 0;
                    npc.VisualTrait = GetStringOrNull(rdr, "VisualTrait");

                    // Read CurrentAreaID if present
                    npc.CurrentAreaID = Has("CurrentAreaID") ? GetInt(rdr, "CurrentAreaID", 0) : 0;

                    // Skip dead or invalid rows (Health <= 0 or HealthMax <= 0)
                    if (npc.Health <= 0 || npc.HealthMax <= 0)
                    {
                        Console.WriteLine($"[NpcPersistence] Skipping NPC NpcID={npc.NpcID} (Health={npc.Health}/{npc.HealthMax}).");
                        continue;
                    }

                    npc.Mass = Has("Mass") ? GetNullableDoubleFlexible(rdr, "Mass") : null;
                    npc.EffMass = Has("EffMass") ? GetNullableDoubleFlexible(rdr, "EffMass") : null;
                    npc.CombatSkill = Has("CombatSkill") ? GetNullableInt(rdr, "CombatSkill") : null;
                    npc.EffCombatSkill = Has("EffCombatSkill") ? GetNullableInt(rdr, "EffCombatSkill") : null;
                    npc.SizeEnum = Has("SizeEnum") ? GetNullableInt(rdr, "SizeEnum") : null;
                    if (Has("BaseDmgDice"))
                        npc.BaseDmgDice = GetDoubleFlexible(rdr, "BaseDmgDice", npc.BaseDmgDice);
                    if (Has("BaseArmorClass"))
                        npc.BaseArmorClass = GetDoubleFlexible(rdr, "BaseArmorClass", npc.BaseArmorClass);
                    if (Has("EffArmorClass"))
                        npc.EffArmorClass = GetDoubleFlexible(rdr, "EffArmorClass", npc.EffArmorClass);

                    if (Has("Buffs"))
                        npc.Buffs = TryDeserializeList(GetString(rdr, "Buffs", "[]"));
                    if (Has("Debuffs"))
                        npc.Debuffs = TryDeserializeList(GetString(rdr, "Debuffs", "[]"));

                    npc.CurrentRoom = world.Rooms.Find(r => r.RoomID == npc.CurrentRoomID);

                    // NEW: warn if area linkage mismatches (do not correct automatically)
                    if (npc.CurrentRoom != null && npc.CurrentRoom.AreaID > 0 && npc.CurrentAreaID > 0 && npc.CurrentAreaID != npc.CurrentRoom.AreaID)
                    {
                        Console.WriteLine($"[NpcPersistence][WARN] Area mismatch for NpcID={npc.NpcID}: CurrentAreaID={npc.CurrentAreaID}, Room.AreaID={npc.CurrentRoom.AreaID}.");
                        // Keep loading as-is
                    }

                    MudServer.Combat.InitializeSizeDefaults(npc);
                    MudServer.Combat.UpdateEffectiveStats(npc);

                    if (npc.CurrentRoom != null)
                        npc.CurrentRoom.Npcs.Add(npc);
                    npcs.Add(npc);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcPersistence] Failed to load NPC row: {ex.Message}");
                }
            }
            return npcs;
        }

        // Helpers
        private static int GetInt(SqliteDataReader rdr, string name, int def)
        {
            try { var i = rdr.GetOrdinal(name); return rdr.IsDBNull(i) ? def : rdr.GetInt32(i); }
            catch { return def; }
        }
        private static int? GetNullableInt(SqliteDataReader rdr, string name)
        {
            try { var i = rdr.GetOrdinal(name); return rdr.IsDBNull(i) ? (int?)null : rdr.GetInt32(i); }
            catch { return null; }
        }
        private static double GetDoubleFlexible(SqliteDataReader rdr, string name, double def)
        {
            try
            {
                var i = rdr.GetOrdinal(name);
                if (rdr.IsDBNull(i)) return def;
                // SQLite will coerce if column is numeric; if stored as text, try parse
                try { return rdr.GetDouble(i); }
                catch
                {
                    var s = rdr.GetString(i);
                    return double.TryParse(s, out var v) ? v : def;
                }
            }
            catch { return def; }
        }
        private static double? GetNullableDoubleFlexible(SqliteDataReader rdr, string name)
        {
            try
            {
                var i = rdr.GetOrdinal(name);
                if (rdr.IsDBNull(i)) return null;
                try { return rdr.GetDouble(i); }
                catch
                {
                    var s = rdr.GetString(i);
                    return double.TryParse(s, out var v) ? v : (double?)null;
                }
            }
            catch { return null; }
        }
        private static string GetString(SqliteDataReader rdr, string name, string def)
        {
            try { var i = rdr.GetOrdinal(name); return rdr.IsDBNull(i) ? def : rdr.GetString(i); }
            catch { return def; }
        }
        private static string? GetStringOrNull(SqliteDataReader rdr, string name)
        {
            try { var i = rdr.GetOrdinal(name); return rdr.IsDBNull(i) ? null : rdr.GetString(i); }
            catch { return null; }
        }
        private static List<string> TryDeserializeList(string json)
        {
            try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
            catch { return new List<string>(); }
        }
    }
}
