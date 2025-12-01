using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using MudCore.Templates;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;

namespace MudPersistence
{
    public static class NpcPersistence
    {
        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static int InsertNpc(SqliteConnection conn, Npc npc)
        {
            using var cmd = new SqliteCommand(
                @"INSERT INTO Npc (TemplateID, Name, Alignment, MaxHealth, Health, Description, IdleActions, WanderChance, CurrentRoomID, IsPersistent, Flags, Level, Mass, CombatSkill, EffCombatSkill, SizeEnum, BaseDmgDice, EffMass, IsHumanoid, BaseArmorClass, EffArmorClass, Debuffs, Buffs, VisualTrait, WanderAreas, AiBehaviors, AiTaskList, CurrentAiTask)
                  VALUES (@template, @name, @align, @maxh, @h, @desc, @idle, @wander, @room, @persist, @flags, @level, @mass, @cskill, @ecskill, @size, @bbdice, @effmass, @ishum, @baseac, @effac, @debuffs, @buffs, @visualTrait, @wanderAreas, @aiBehaviors, @aiTasks, @currentTask);
                  SELECT last_insert_rowid();", conn);

            cmd.Parameters.AddWithValue("@template", npc.TemplateID ?? "");
            cmd.Parameters.AddWithValue("@name", npc.Name ?? "");
            cmd.Parameters.AddWithValue("@align", npc.Alignment.ToString());
            cmd.Parameters.AddWithValue("@maxh", npc.HealthMax);
            cmd.Parameters.AddWithValue("@h", npc.Health);
            cmd.Parameters.AddWithValue("@desc", npc.Description ?? "");
            cmd.Parameters.AddWithValue("@idle", JsonSerializer.Serialize(npc.IdleActions ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@wander", npc.WanderChance);
            cmd.Parameters.AddWithValue("@room", npc.CurrentRoom?.RoomID ?? npc.CurrentRoomID);
            cmd.Parameters.AddWithValue("@persist", npc.IsPersistent ? 1 : 0);
            cmd.Parameters.AddWithValue("@flags", npc.FlagsJson ?? "[]");
            cmd.Parameters.AddWithValue("@level", npc.Level.HasValue ? (object)npc.Level.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@mass", npc.Mass.HasValue ? (object)npc.Mass.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@cskill", npc.CombatSkill.HasValue ? (object)npc.CombatSkill.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ecskill", npc.EffCombatSkill.HasValue ? (object)npc.EffCombatSkill.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@size", npc.SizeEnum.HasValue ? (object)npc.SizeEnum.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@bbdice", npc.BaseDmgDice ?? "");
            cmd.Parameters.AddWithValue("@effmass", npc.EffMass.HasValue ? (object)npc.EffMass.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ishum", npc.IsHumanoid ? 1 : 0);
            cmd.Parameters.AddWithValue("@baseac", npc.BaseArmorClass);
            cmd.Parameters.AddWithValue("@effac", npc.EffArmorClass);
            cmd.Parameters.AddWithValue("@debuffs", JsonSerializer.Serialize(npc.Debuffs ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@buffs", JsonSerializer.Serialize(npc.Buffs ?? new List<string>(), _jsonOpts));

            // New: VisualTrait parameter
            cmd.Parameters.AddWithValue("@visualTrait", npc.VisualTrait ?? "");

            // New AI / list fields
            cmd.Parameters.AddWithValue("@wanderAreas", JsonSerializer.Serialize(npc.WanderAreas ?? new List<int>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@aiBehaviors", JsonSerializer.Serialize(npc.AiBehaviors ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@aiTasks", JsonSerializer.Serialize(npc.AiTaskList ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@currentTask", npc.CurrentAiTask ?? "");

            long newId = (long)cmd.ExecuteScalar();
            npc.NpcID = (int)newId;
            return npc.NpcID;
        }

        public static void UpdateNpc(SqliteConnection conn, Npc npc)
        {
            using var cmd = new SqliteCommand(
                @"UPDATE Npc SET
                    TemplateID=@template,
                    Name=@name,
                    Alignment=@align,
                    MaxHealth=@maxh,
                    Health=@h,
                    Description=@desc,
                    IdleActions=@idle,
                    WanderChance=@wander,
                    CurrentRoomID=@room,
                    IsPersistent=@persist,
                    Flags=@flags,
                    Level=@level,
                    Mass=@mass,
                    CombatSkill=@cskill,
                    EffCombatSkill=@ecskill,
                    SizeEnum=@size,
                    BaseDmgDice=@bbdice,
                    EffMass=@effmass,
                    IsHumanoid=@ishum,
                    BaseArmorClass=@baseac,
                    EffArmorClass=@effac,
                    Debuffs=@debuffs,
                    Buffs=@buffs,
                    VisualTrait=@visualTrait,
                    WanderAreas=@wanderAreas,
                    AiBehaviors=@aiBehaviors,
                    AiTaskList=@aiTasks,
                    CurrentAiTask=@currentTask,
                    UpdatedAt = datetime('now')
                  WHERE NpcID = @id", conn);

            cmd.Parameters.AddWithValue("@template", npc.TemplateID ?? "");
            cmd.Parameters.AddWithValue("@name", npc.Name ?? "");
            cmd.Parameters.AddWithValue("@align", npc.Alignment.ToString());
            cmd.Parameters.AddWithValue("@maxh", npc.HealthMax);
            cmd.Parameters.AddWithValue("@h", npc.Health);
            cmd.Parameters.AddWithValue("@desc", npc.Description ?? "");
            cmd.Parameters.AddWithValue("@idle", JsonSerializer.Serialize(npc.IdleActions ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@wander", npc.WanderChance);
            cmd.Parameters.AddWithValue("@room", npc.CurrentRoom?.RoomID ?? npc.CurrentRoomID);
            cmd.Parameters.AddWithValue("@persist", npc.IsPersistent ? 1 : 0);
            cmd.Parameters.AddWithValue("@flags", npc.FlagsJson ?? "[]");
            cmd.Parameters.AddWithValue("@level", npc.Level.HasValue ? (object)npc.Level.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@mass", npc.Mass.HasValue ? (object)npc.Mass.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@cskill", npc.CombatSkill.HasValue ? (object)npc.CombatSkill.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ecskill", npc.EffCombatSkill.HasValue ? (object)npc.EffCombatSkill.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@size", npc.SizeEnum.HasValue ? (object)npc.SizeEnum.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@bbdice", npc.BaseDmgDice ?? "");
            cmd.Parameters.AddWithValue("@effmass", npc.EffMass.HasValue ? (object)npc.EffMass.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ishum", npc.IsHumanoid ? 1 : 0);
            cmd.Parameters.AddWithValue("@baseac", npc.BaseArmorClass);
            cmd.Parameters.AddWithValue("@effac", npc.EffArmorClass);
            cmd.Parameters.AddWithValue("@debuffs", JsonSerializer.Serialize(npc.Debuffs ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@buffs", JsonSerializer.Serialize(npc.Buffs ?? new List<string>(), _jsonOpts));

            // New: VisualTrait parameter for update
            cmd.Parameters.AddWithValue("@visualTrait", npc.VisualTrait ?? "");

            // New AI / list fields
            cmd.Parameters.AddWithValue("@wanderAreas", JsonSerializer.Serialize(npc.WanderAreas ?? new List<int>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@aiBehaviors", JsonSerializer.Serialize(npc.AiBehaviors ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@aiTasks", JsonSerializer.Serialize(npc.AiTaskList ?? new List<string>(), _jsonOpts));
            cmd.Parameters.AddWithValue("@currentTask", npc.CurrentAiTask ?? "");

            cmd.Parameters.AddWithValue("@id", npc.NpcID);

            cmd.ExecuteNonQuery();
        }

        public static List<Npc> LoadAllNpcs(SqliteConnection conn, World world)
        {
            var list = new List<Npc>();
            using var cmd = new SqliteCommand("SELECT * FROM Npc", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var npc = new Npc();

                npc.NpcID = reader.GetInt32(reader.GetOrdinal("NpcID"));
                npc.TemplateID = reader.IsDBNull(reader.GetOrdinal("TemplateID")) ? null : reader.GetString(reader.GetOrdinal("TemplateID"));
                npc.Name = reader.IsDBNull(reader.GetOrdinal("Name")) ? npc.TemplateID : reader.GetString(reader.GetOrdinal("Name"));

                var alignStr = reader.IsDBNull(reader.GetOrdinal("Alignment")) ? "Neutral" : reader.GetString(reader.GetOrdinal("Alignment"));
                if (Enum.TryParse<Alignment>(alignStr, true, out var align))
                    npc.Alignment = align;
                else
                    npc.Alignment = Alignment.Neutral;

                // read MaxHealth (column renamed from HealthMax)
                npc.HealthMax = reader.IsDBNull(reader.GetOrdinal("MaxHealth")) ? 0 : reader.GetInt32(reader.GetOrdinal("MaxHealth"));
                npc.Health = reader.IsDBNull(reader.GetOrdinal("Health")) ? npc.HealthMax : reader.GetInt32(reader.GetOrdinal("Health"));
                npc.Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description"));

                var idleJson = reader.IsDBNull(reader.GetOrdinal("IdleActions")) ? "[]" : reader.GetString(reader.GetOrdinal("IdleActions"));
                try { npc.IdleActions = JsonSerializer.Deserialize<List<string>>(idleJson, _jsonOpts) ?? new List<string>(); } catch { npc.IdleActions = new List<string>(); }

                npc.WanderChance = reader.IsDBNull(reader.GetOrdinal("WanderChance")) ? 0.0 : reader.GetDouble(reader.GetOrdinal("WanderChance"));
                npc.CurrentRoomID = reader.IsDBNull(reader.GetOrdinal("CurrentRoomID")) ? 0 : reader.GetInt32(reader.GetOrdinal("CurrentRoomID"));
                npc.IsPersistent = !reader.IsDBNull(reader.GetOrdinal("IsPersistent")) && reader.GetInt32(reader.GetOrdinal("IsPersistent")) != 0;
                npc.FlagsJson = reader.IsDBNull(reader.GetOrdinal("Flags")) ? "[]" : reader.GetString(reader.GetOrdinal("Flags"));

                npc.Level = reader.IsDBNull(reader.GetOrdinal("Level")) ? null : reader.GetInt32(reader.GetOrdinal("Level"));
                npc.Mass = reader.IsDBNull(reader.GetOrdinal("Mass")) ? null : reader.GetInt32(reader.GetOrdinal("Mass"));

                // Combat skills (read by name to avoid index drift)
                npc.CombatSkill = reader.IsDBNull(reader.GetOrdinal("CombatSkill")) ? null : reader.GetInt32(reader.GetOrdinal("CombatSkill"));
                npc.EffCombatSkill = reader.IsDBNull(reader.GetOrdinal("EffCombatSkill")) ? null : reader.GetInt32(reader.GetOrdinal("EffCombatSkill"));

                npc.SizeEnum = reader.IsDBNull(reader.GetOrdinal("SizeEnum")) ? null : reader.GetInt32(reader.GetOrdinal("SizeEnum"));
                npc.BaseDmgDice = reader.IsDBNull(reader.GetOrdinal("BaseDmgDice")) ? "" : reader.GetString(reader.GetOrdinal("BaseDmgDice"));
                npc.EffMass = reader.IsDBNull(reader.GetOrdinal("EffMass")) ? null : reader.GetInt32(reader.GetOrdinal("EffMass"));
                npc.IsHumanoid = !reader.IsDBNull(reader.GetOrdinal("IsHumanoid")) && reader.GetInt32(reader.GetOrdinal("IsHumanoid")) != 0;

                // Read BaseArmorClass / EffArmorClass by name to be robust to column ordering
                npc.BaseArmorClass = reader.IsDBNull(reader.GetOrdinal("BaseArmorClass")) ? 0 : reader.GetInt32(reader.GetOrdinal("BaseArmorClass"));
                npc.EffArmorClass = reader.IsDBNull(reader.GetOrdinal("EffArmorClass")) ? npc.BaseArmorClass : reader.GetInt32(reader.GetOrdinal("EffArmorClass"));

                var debJson = reader.IsDBNull(reader.GetOrdinal("Debuffs")) ? "[]" : reader.GetString(reader.GetOrdinal("Debuffs"));
                var buffJson = reader.IsDBNull(reader.GetOrdinal("Buffs")) ? "[]" : reader.GetString(reader.GetOrdinal("Buffs"));
                try { npc.Debuffs = JsonSerializer.Deserialize<List<string>>(debJson, _jsonOpts) ?? new List<string>(); } catch { npc.Debuffs = new List<string>(); }
                try { npc.Buffs = JsonSerializer.Deserialize<List<string>>(buffJson, _jsonOpts) ?? new List<string>(); } catch { npc.Buffs = new List<string>(); }

                int ordVisual = -1;
                try { ordVisual = reader.GetOrdinal("VisualTrait"); } catch { ordVisual = -1; }

                if (ordVisual >= 0)
                {
                    var vt = reader.IsDBNull(ordVisual) ? null : reader.GetString(ordVisual);
                    npc.VisualTrait = string.IsNullOrWhiteSpace(vt) ? null : vt.Trim();
                }
                else
                {
                    npc.VisualTrait = null; // column missing in DB
                }

                // New AI fields (JSON)
                var wanderJson = reader.IsDBNull(reader.GetOrdinal("WanderAreas")) ? "[]" : reader.GetString(reader.GetOrdinal("WanderAreas"));
                var aiBehJson = reader.IsDBNull(reader.GetOrdinal("AiBehaviors")) ? "[]" : reader.GetString(reader.GetOrdinal("AiBehaviors"));
                var aiTasksJson = reader.IsDBNull(reader.GetOrdinal("AiTaskList")) ? "[]" : reader.GetString(reader.GetOrdinal("AiTaskList"));
                npc.CurrentAiTask = reader.IsDBNull(reader.GetOrdinal("CurrentAiTask")) ? null : reader.GetString(reader.GetOrdinal("CurrentAiTask"));

                try { npc.WanderAreas = JsonSerializer.Deserialize<List<int>>(wanderJson, _jsonOpts) ?? new List<int>(); } catch { npc.WanderAreas = new List<int>(); }
                try { npc.AiBehaviors = JsonSerializer.Deserialize<List<string>>(aiBehJson, _jsonOpts) ?? new List<string>(); } catch { npc.AiBehaviors = new List<string>(); }
                try { npc.AiTaskList = JsonSerializer.Deserialize<List<string>>(aiTasksJson, _jsonOpts) ?? new List<string>(); } catch { npc.AiTaskList = new List<string>(); }

                // Resolve template reference
                try
                {
                    npc.Template = world.TemplateManager.Get(npc.TemplateID);
                    npc.ApplyTemplateDefaults();
                }
                catch
                {
                    npc.Template = null;
                }

                // Resolve room and add NPC to it
                var room = world.Rooms.FirstOrDefault(r => r.RoomID == npc.CurrentRoomID);
                if (room != null)
                {
                    npc.CurrentRoom = room;
                    room.Npcs.Add(npc);
                }

                list.Add(npc);
                world.Npcs.Add(npc);
            }

            return list;
        }
    }
}
