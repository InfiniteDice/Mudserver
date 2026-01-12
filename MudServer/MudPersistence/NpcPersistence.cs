using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudCore.World;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Data;
using MudCore.Actions;

namespace MudPersistence
{
    public static class NpcPersistence
    {
        private static HashSet<string> _npcCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _colsLoaded;

        private static void EnsureOpen(SqliteConnection conn)
        {
            if (conn.State != ConnectionState.Open)
                conn.Open();
        }

        private static void EnsureNpcColumns(SqliteConnection conn)
        {
            // Defensive: if no connection provided, nothing to do (caller must pass a live connection).
            if (conn == null) return;

            try
            {
                // If we've already successfully loaded a non-empty set of columns, nothing to do.
                if (_colsLoaded && _npcCols.Count > 0) return;

                EnsureOpen(conn);

                // Use a fresh local collector so partial failures won't corrupt the global cache.
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info('Npc');";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    try
                    {
                        // Column name is in ordinal 1 per PRAGMA table_info
                        if (!rdr.IsDBNull(1))
                        {
                            var name = rdr.GetString(1);
                            if (!string.IsNullOrWhiteSpace(name))
                                cols.Add(name);
                        }
                    }
                    catch
                    {
                        // Ignore per-row errors and continue reading other columns.
                    }
                }

                // Only replace the global set if we actually found columns.
                if (cols.Count > 0)
                {
                    _npcCols.Clear();
                    foreach (var c in cols) _npcCols.Add(c);
                    _colsLoaded = true;
                }
                else
                {
                    // If we found nothing, do not mark _colsLoaded true so callers will retry later.
                    _colsLoaded = false;
                }
            }
            catch (Exception ex)
            {
                // Log to aid debugging if PRAGMA fails — keep behavior conservative so future calls can retry.
                Console.WriteLine($"[NpcPersistence] EnsureNpcColumns failed: {ex.Message}");
                _colsLoaded = false;
            }
        }

        private static bool Has(string col) => _npcCols.Contains(col);

        // Include CurrentAreaID on insert/update
        public static int InsertNpc(SqliteConnection conn, Npc npc)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (npc == null) throw new ArgumentNullException(nameof(npc));

            // Prefer the single-writer queue when available so NPC creation (and its follow-up updates)
            // are atomic with other writer work. If the queue isn't started, fall back to direct insert.
            try
            {
                var newId = MudServer.DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                {
                    return InsertNpcInternal(wConn, npc, wTx);
                }, timeout: TimeSpan.FromSeconds(5));

                npc.NpcID = newId;
                return newId;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct insert on provided connection.
            }
            catch
            {
                // best-effort: on enqueue error, fall back to direct insert to preserve prior behavior.
            }

            // Direct insert path (no external transaction)
            var id = InsertNpcInternal(conn, npc, null);
            npc.NpcID = id;
            return id;
        }

        // Internal transactional insert used by the writer queue or direct callers.
        // Accepts an optional transaction; when tx != null it will attach commands to it.
        private static int InsertNpcInternal(SqliteConnection conn, Npc npc, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (npc == null) throw new ArgumentNullException(nameof(npc));

            EnsureOpen(conn);
            EnsureNpcColumns(conn);

            // Enable verbose diagnostic logging by setting environment variable NPC_PERSISTENCE_DEBUG=1
            bool verbose = false;
            try
            {
                var ev = Environment.GetEnvironmentVariable("NPC_PERSISTENCE_DEBUG");
                verbose = !string.IsNullOrEmpty(ev) && ev.Trim() == "1";
            }
            catch { verbose = false; }

            if (verbose)
            {
                try
                {
                    Console.WriteLine("[NpcPersistence][DBG] BEGIN InsertNpcInternal");
                    Console.WriteLine($"[NpcPersistence][DBG] Detected columns: {string.Join(", ", _npcCols.OrderBy(s => s))}");
                    Console.WriteLine($"[NpcPersistence][DBG] NPC (pre-insert) TemplateID='{npc.TemplateID}' Name='{npc.Name}' NpcID={npc.NpcID}");
                    Console.WriteLine($"[NpcPersistence][DBG] HealthMax={npc.HealthMax} Health={npc.Health} RoomID={npc.CurrentRoomID} AreaID={npc.CurrentAreaID} IsPersistent={npc.IsPersistent}");
                    Console.WriteLine($"[NpcPersistence][DBG] VisualTrait='{npc.VisualTrait}' DescriptionPresent={(string.IsNullOrEmpty(npc.Description) ? "no" : "yes")}");
                    Console.WriteLine($"[NpcPersistence][DBG] IsHumanoid={npc.IsHumanoid} WanderChance={npc.WanderChance} Level={npc.Level}");
                    try
                    {
                        Console.WriteLine($"[NpcPersistence][DBG] Flags(list) count={(npc.Flags?.Count ?? 0)} FlagsJson={(npc.FlagsJson ?? "")}");
                    }
                    catch { }
                    try
                    {
                        Console.WriteLine($"[NpcPersistence][DBG] WanderAreas count={(npc.WanderAreas?.Count ?? 0)} WanderAreasJson={(System.Text.Json.JsonSerializer.Serialize(npc.WanderAreas ?? new System.Collections.Generic.List<int>()))}");
                    }
                    catch { }
                    try
                    {
                        Console.WriteLine($"[NpcPersistence][DBG] Mass={npc.Mass} EffMass={npc.EffMass} BaseDmgDice={npc.BaseDmgDice} BaseArmorClass={npc.BaseArmorClass} EffArmorClass={npc.EffArmorClass}");
                    }
                    catch { }
                    Console.WriteLine("[NpcPersistence][DBG] END header dump");
                }
                catch { /* best-effort only */ }
            }

            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            // Include optional columns when present
            cmd.CommandText = @"
        INSERT INTO Npc (TemplateID, Name, Alignment, MaxHealth, Health, CurrentRoomID, IsPersistent, VisualTrait, CurrentAreaID"
                                + (Has("Description") ? ", Description" : "")
                                + (Has("IsHumanoid") ? ", IsHumanoid" : "")
                                + (Has("WanderChance") ? ", WanderChance" : "")
                                + (Has("Level") ? ", Level" : "")
                                + (Has("CombatSkill") ? ", CombatSkill" : "")
                                + (Has("EffCombatSkill") ? ", EffCombatSkill" : "")
                                + (Has("Flags") ? ", Flags" : "")
                                + (Has("Hunger") ? ", Hunger" : "")
                                + (Has("AiBehaviors") ? ", AiBehaviors" : "") + @")
        VALUES (@tid, @name, @align, @maxhp, @hp, @room, @persist, @trait, @area"
                                + (Has("Description") ? ", @desc" : "")
                                + (Has("IsHumanoid") ? ", @isHumanoid" : "")
                                + (Has("WanderChance") ? ", @wc" : "")
                                + (Has("Level") ? ", @lvl" : "")
                                + (Has("CombatSkill") ? ", @cs" : "")
                                + (Has("EffCombatSkill") ? ", @ecs" : "")
                                + (Has("Flags") ? ", @flags" : "")
                                + (Has("Hunger") ? ", @hunger" : "")
                                + (Has("AiBehaviors") ? ", @aibehaviors" : "") + @");
        SELECT last_insert_rowid();";

            cmd.Parameters.AddWithValue("@tid", npc.TemplateID ?? "");
            cmd.Parameters.AddWithValue("@name", npc.Name ?? "");

            try
            {
                cmd.Parameters.AddWithValue("@align", npc.Alignment.ToString());
            }
            catch
            {
                cmd.Parameters.AddWithValue("@align", Alignment.Neutral.ToString());
            }

            cmd.Parameters.AddWithValue("@maxhp", npc.HealthMax);
            cmd.Parameters.AddWithValue("@hp", npc.Health);
            cmd.Parameters.AddWithValue("@room", npc.CurrentRoomID);
            cmd.Parameters.AddWithValue("@persist", npc.IsPersistent ? 1 : 0);
            cmd.Parameters.AddWithValue("@trait", npc.VisualTrait ?? "");
            cmd.Parameters.AddWithValue("@area", npc.CurrentAreaID);

            if (Has("Description")) cmd.Parameters.AddWithValue("@desc", npc.Description ?? "");
            if (Has("IsHumanoid")) cmd.Parameters.AddWithValue("@isHumanoid", npc.IsHumanoid ? 1 : 0);
            if (Has("WanderChance")) cmd.Parameters.AddWithValue("@wc", npc.WanderChance);
            if (Has("Level")) cmd.Parameters.AddWithValue("@lvl", Math.Max(1, npc.Level ?? 1));
            if (Has("CombatSkill")) cmd.Parameters.AddWithValue("@cs", npc.CombatSkill ?? 0.0);
            if (Has("EffCombatSkill")) cmd.Parameters.AddWithValue("@ecs", npc.EffCombatSkill ?? (npc.CombatSkill ?? 0.0));

            if (Has("Flags"))
            {
                try
                {
                    var flagsList = npc.Flags ?? new System.Collections.Generic.List<string>();
                    flagsList.Sort(System.StringComparer.OrdinalIgnoreCase);
                    var flagsJson = System.Text.Json.JsonSerializer.Serialize(flagsList);
                    cmd.Parameters.AddWithValue("@flags", flagsJson);
                }
                catch
                {
                    cmd.Parameters.AddWithValue("@flags", "[]");
                }
            }

            if (Has("Hunger"))
            {
                try
                {
                    cmd.Parameters.AddWithValue("@hunger", npc.Hunger);
                }
                catch
                {
                    cmd.Parameters.AddWithValue("@hunger", 0);
                }
            }

            // Persist per-NPC AiBehaviors (JSON serialized) when DB supports it.
            if (Has("AiBehaviors"))
            {
                try
                {
                    string ajson = "[]";
                    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    if (npc.Template != null)
                    {
                        // Prefer the raw JSON representation (AiBehaviorsRaw) to preserve nested arrays exactly
                        if (npc.Template.AiBehaviorsRaw != null && npc.Template.AiBehaviorsRaw.Count > 0)
                        {
                            ajson = System.Text.Json.JsonSerializer.Serialize(npc.Template.AiBehaviorsRaw, opts);
                        }
                        else
                        {
                            // Fallback: serialize structured configs (keeps nested lists too)
                            var ab = npc.Template.AiBehaviorConfigs ?? new System.Collections.Generic.List<NpcTemplate.AiBehaviorConfig>();
                            ajson = System.Text.Json.JsonSerializer.Serialize(ab, opts);
                        }
                    }
                    cmd.Parameters.AddWithValue("@aibehaviors", ajson);
                }
                catch
                {
                    cmd.Parameters.AddWithValue("@aibehaviors", "[]");
                }
            }

            long scalar;
            try
            {
                if (verbose)
                {
                    try
                    {
                        // Dump parameter names/values to help identify what's actually being bound
                        var names = new System.Text.StringBuilder();
                        foreach (Microsoft.Data.Sqlite.SqliteParameter p in cmd.Parameters)
                        {
                            names.Append($"{p.ParameterName}='{p.Value}' ");
                        }
                        Console.WriteLine($"[NpcPersistence][DBG] Executing INSERT with params: {names}");
                    }
                    catch { }
                }

                scalar = (long)cmd.ExecuteScalar();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NpcPersistence][ERROR] InsertNpcInternal failed: {ex.Message}");
                throw;
            }

            int newId = (int)scalar;
            npc.NpcID = newId;

            if (verbose)
            {
                Console.WriteLine($"[NpcPersistence][DBG] Inserted NpcID={newId}");
            }

            // Persist WanderAreas JSON if column exists
            if (Has("WanderAreas"))
            {
                try
                {
                    using var upWander = conn.CreateCommand();
                    if (tx != null) upWander.Transaction = tx;
                    upWander.CommandText = "UPDATE Npc SET WanderAreas = @wander WHERE NpcID = @id;";
                    var wanderJson = System.Text.Json.JsonSerializer.Serialize(npc.WanderAreas ?? new System.Collections.Generic.List<int>());
                    upWander.Parameters.AddWithValue("@wander", wanderJson);
                    upWander.Parameters.AddWithValue("@id", npc.NpcID);
                    upWander.ExecuteNonQuery();
                    if (verbose) Console.WriteLine($"[NpcPersistence][DBG] Persisted WanderAreas for NpcID={npc.NpcID}: {wanderJson}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcPersistence][WARN] Failed to persist WanderAreas for NpcID={npc.NpcID}: {ex.Message}");
                }
            }

            // Persist WanderChance if column exists
            if (Has("WanderChance"))
            {
                try
                {
                    using var upChance = conn.CreateCommand();
                    if (tx != null) upChance.Transaction = tx;
                    upChance.CommandText = "UPDATE Npc SET WanderChance = @wc WHERE NpcID = @id;";
                    upChance.Parameters.AddWithValue("@wc", npc.WanderChance);
                    upChance.Parameters.AddWithValue("@id", npc.NpcID);
                    upChance.ExecuteNonQuery();
                    if (verbose) Console.WriteLine($"[NpcPersistence][DBG] Persisted WanderChance for NpcID={npc.NpcID}: {npc.WanderChance}");
                }
                catch { }
            }

            // Persist combat-related fields; use the provided transaction if any
            SaveNpcCombat(conn, npc, tx);

            if (verbose)
            {
                Console.WriteLine($"[NpcPersistence][DBG] SaveNpcCombat completed for NpcID={npc.NpcID}");
            }

            return newId;
        } //end InsertNpcInternal

        // Updated UpdateNpc to call SaveNpcCombat with optional transaction null to preserve behavior.
        // Internal transactional update used by the writer queue or direct callers.
        // Accepts optional transaction so it can run inside the writer's transaction.
        private static void UpdateNpcInternal(SqliteConnection conn, Npc npc, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (npc == null) throw new ArgumentNullException(nameof(npc));

            EnsureOpen(conn);
            EnsureNpcColumns(conn);

            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            // NOTE: Name column WAS missing from UPDATE previously which prevented admin name edits
            // from persisting. Add Name = @name into the SET list and bind @name below.
            cmd.CommandText = @"
        UPDATE Npc SET
            Health = @hp,
            MaxHealth = @maxhp,
            Name = @name,
            CurrentRoomID = @room,
            VisualTrait = @trait,
            CurrentAreaID = @area"
                        + (Has("Description") ? ", Description = @desc" : "")
                        + (Has("IsHumanoid") ? ", IsHumanoid = @isHumanoid" : "")
                        + (Has("WanderChance") ? ", WanderChance = @wc" : "")
                        + (Has("Level") ? ", Level = @lvl" : "")
                        + (Has("CombatSkill") ? ", CombatSkill = @cs" : "")
                        + (Has("EffCombatSkill") ? ", EffCombatSkill = @ecs" : "")
                        + (Has("Flags") ? ", Flags = @flags" : "")
                        + (Has("AiBehaviors") ? ", AiBehaviors = @aibehaviors" : "") + @"
        WHERE NpcID = @id;";

            cmd.Parameters.AddWithValue("@hp", npc.Health);
            cmd.Parameters.AddWithValue("@maxhp", npc.HealthMax);
            // Bind new @name parameter so Name updates persist
            cmd.Parameters.AddWithValue("@name", npc.Name ?? "");
            cmd.Parameters.AddWithValue("@room", npc.CurrentRoomID);
            cmd.Parameters.AddWithValue("@trait", npc.VisualTrait ?? "");
            // Preserve existing behavior: use current room's AreaID if available
            cmd.Parameters.AddWithValue("@area", npc.CurrentRoom?.AreaID ?? 0);
            cmd.Parameters.AddWithValue("@id", npc.NpcID);
            if (Has("Description")) cmd.Parameters.AddWithValue("@desc", npc.Description ?? "");
            if (Has("IsHumanoid")) cmd.Parameters.AddWithValue("@isHumanoid", npc.IsHumanoid ? 1 : 0);
            if (Has("WanderChance")) cmd.Parameters.AddWithValue("@wc", npc.WanderChance);
            if (Has("Level")) cmd.Parameters.AddWithValue("@lvl", Math.Max(1, npc.Level ?? 1));
            if (Has("CombatSkill")) cmd.Parameters.AddWithValue("@cs", npc.CombatSkill ?? 0.0);
            if (Has("EffCombatSkill")) cmd.Parameters.AddWithValue("@ecs", npc.EffCombatSkill ?? (npc.CombatSkill ?? 0.0));

            if (Has("Flags"))
            {
                try
                {
                    var flagsList = npc.Flags ?? new System.Collections.Generic.List<string>();
                    flagsList.Sort(System.StringComparer.OrdinalIgnoreCase);
                    var flagsJson = System.Text.Json.JsonSerializer.Serialize(flagsList);
                    cmd.Parameters.AddWithValue("@flags", flagsJson);
                }
                catch
                {
                    cmd.Parameters.AddWithValue("@flags", "[]");
                }
            }

            // Persist per-NPC AiBehaviors (JSON serialized) when DB supports it.
            if (Has("AiBehaviors"))
            {
                try
                {
                    string ajson = "[]";
                    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    if (npc.Template != null)
                    {
                        // Prefer the raw JSON representation (AiBehaviorsRaw) to preserve nested arrays exactly
                        if (npc.Template.AiBehaviorsRaw != null && npc.Template.AiBehaviorsRaw.Count > 0)
                        {
                            ajson = System.Text.Json.JsonSerializer.Serialize(npc.Template.AiBehaviorsRaw, opts);
                        }
                        else
                        {
                            // Fallback: serialize structured configs (keeps nested lists too)
                            var ab = npc.Template.AiBehaviorConfigs ?? new System.Collections.Generic.List<NpcTemplate.AiBehaviorConfig>();
                            ajson = System.Text.Json.JsonSerializer.Serialize(ab, opts);
                        }
                    }
                    cmd.Parameters.AddWithValue("@aibehaviors", ajson);
                }
                catch
                {
                    cmd.Parameters.AddWithValue("@aibehaviors", "[]");
                }
            }

            try { cmd.ExecuteNonQuery(); } catch { }

            // Persist WanderAreas if column exists
            if (Has("WanderAreas"))
            {
                try
                {
                    using var upWander = conn.CreateCommand();
                    if (tx != null) upWander.Transaction = tx;
                    upWander.CommandText = "UPDATE Npc SET WanderAreas = @wander WHERE NpcID = @id;";
                    var wanderJson = System.Text.Json.JsonSerializer.Serialize(npc.WanderAreas ?? new System.Collections.Generic.List<int>());
                    upWander.Parameters.AddWithValue("@wander", wanderJson);
                    upWander.Parameters.AddWithValue("@id", npc.NpcID);
                    upWander.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcPersistence][WARN] Failed to update WanderAreas for NpcID={npc.NpcID}: {ex.Message}");
                }
            }

            // Persist WanderChance if column exists
            if (Has("WanderChance"))
            {
                try
                {
                    using var upChance = conn.CreateCommand();
                    if (tx != null) upChance.Transaction = tx;
                    upChance.CommandText = "UPDATE Npc SET WanderChance = @wc WHERE NpcID = @id;";
                    upChance.Parameters.AddWithValue("@wc", npc.WanderChance);
                    upChance.Parameters.AddWithValue("@id", npc.NpcID);
                    upChance.ExecuteNonQuery();
                }
                catch { }
            }

            // Persist combat-related fields inside provided tx (if any)
            SaveNpcCombat(conn, npc, tx);
        } // end UpdateNpcInternal

        public static void UpdateNpc(SqliteConnection conn, Npc npc)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (npc == null) throw new ArgumentNullException(nameof(npc));

            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    UpdateNpcInternal(wConn, npc, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));

                // After a queued update, ensure transient runtime fields derived from Flags are refreshed
                try { HydrateRuntimeFlags(npc); } catch { /* best-effort */ }

                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct update on provided connection.
            }
            catch
            {
                // best-effort: on enqueue error, fall back to direct update to preserve prior behavior.
            }

            // Direct update path (no external transaction)
            UpdateNpcInternal(conn, npc, null);

            // Refresh transient runtime fields so callers (AI, rendering) immediately see new flag-derived state.
            try { HydrateRuntimeFlags(npc); } catch { /* best-effort */ }
        } // end UpdateNpc


        
        // NEW: Rehydrate runtime-only fields (Sleep/Move) from the instance's Flags list.
        // This mirrors the parsing logic used at load-time so runtime edits take effect immediately.
        // IMPORTANT: Do NOT set transient "IsOnMove" simply because Move: flags exist. That must be
        // controlled by runtime executor logic. Similarly, IsSleeping is owned by runtime executor.
        public static void HydrateRuntimeFlags(Npc npc)
        {
            if (npc == null) return;

            try
            {
                // Sleep defaults
                npc.SleepRoomId = -1;
                npc.SleepStartPeriod = "Midnight";
                npc.SleepEndPeriod = "Dawn";
                // Do NOT mark IsSleeping true here; executor owns sleep lifecycle.
                npc.IsSleeping = false;
                npc.SleepBlockedAtRoomId = null;

                // Move defaults
                npc.MoveRoomId = 0;
                npc.MovePeriod = "";
                // Do NOT set IsOnMove true simply because Move: schedules exist; executor will set it when movement actually starts.
                npc.IsOnMove = false;
                npc.MoveBlockedAtRoomId = null;

                // Clear existing schedule list
                try { npc.MoveSchedules = new System.Collections.Generic.List<Npc.MoveSchedule>(); } catch { npc.MoveSchedules = new System.Collections.Generic.List<Npc.MoveSchedule>(); }

                if (npc.Flags == null) return;

                // Parse Sleep: flag (Sleep:<roomId>:<start>:<end>)
                string sleepFlag = null!;
                foreach (var f in npc.Flags)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(f)) continue;
                        if (f.StartsWith("Sleep:", StringComparison.OrdinalIgnoreCase))
                        {
                            sleepFlag = f;
                            break;
                        }
                    }
                    catch { /* best-effort */ }
                }

                if (!string.IsNullOrWhiteSpace(sleepFlag))
                {
                    var parts = sleepFlag.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var rid))
                        npc.SleepRoomId = Math.Max(0, rid);
                    if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                        npc.SleepStartPeriod = parts[2].Trim();
                    if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
                        npc.SleepEndPeriod = parts[3].Trim();
                }

                // IMPORTANT: Do NOT set IsSleeping based solely on a "Sleeping" flag during hydration.
                // The runtime executor should control entering/exiting sleep so that behavior is consistent with clock.
                // If you have a special persistent 'Sleeping' marker you truly want respected across restarts,
                // implement that explicitly. For now, leave IsSleeping=false to let the scheduler/executor decide.

                // Parse Move: flags - collect all occurrences into MoveSchedules
                try
                {
                    var moveFlags = new System.Collections.Generic.List<string>();
                    foreach (var f in npc.Flags)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(f)) continue;
                            if (f.StartsWith("Move:", StringComparison.OrdinalIgnoreCase))
                                moveFlags.Add(f);
                        }
                        catch { /* per-flag best-effort */ }
                    }

                    if (moveFlags.Count > 0)
                    {
                        foreach (var mf in moveFlags)
                        {
                            try
                            {
                                var parts = mf.Split(':');
                                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var rid))
                                {
                                    var period = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : "";
                                    npc.MoveSchedules.Add(new Npc.MoveSchedule { RoomId = Math.Max(0, rid), Period = period });
                                }
                            }
                            catch { /* best-effort per entry */ }
                        }

                        // Backwards compatibility: set primary MoveRoomId/MovePeriod to first schedule when available
                        if (npc.MoveSchedules.Count > 0)
                        {
                            var first = npc.MoveSchedules[0];
                            npc.MoveRoomId = first.RoomId;
                            npc.MovePeriod = first.Period ?? "";
                        }
                    }
                }
                catch { /* best-effort */ }

                // Hydrate IsOnMove transient marker:
                // Only set IsOnMove true here when an explicit "Moving" runtime flag is present.
                // Do NOT set it merely because MoveSchedules exists.
                try
                {
                    bool movingFound = false;
                    foreach (var f in npc.Flags)
                    {
                        if (string.IsNullOrWhiteSpace(f)) continue;
                        if (string.Equals(f, "Moving", StringComparison.OrdinalIgnoreCase))
                        {
                            movingFound = true;
                            break;
                        }
                    }
                    npc.IsOnMove = movingFound;
                }
                catch
                {
                    npc.IsOnMove = false;
                }
            }
            catch
            {
                // best-effort; do not throw while trying to hydrate runtime fields
            }
        } // end HydrateRuntimeFlags

        public static void DeleteNpc(SqliteConnection conn, int npcId)
        {
            if (conn == null || npcId <= 0) return;

            // Prefer the single-writer queue so deletes are serialized and don't race with other writer operations.
            try
            {
                MudServer.DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                {
                    DeleteNpcInternal(wConn, npcId, wTx);
                    return (object?)null;
                }, timeout: TimeSpan.FromSeconds(5));
                return;
            }
            catch (InvalidOperationException)
            {
                // Queue not started — fall through to direct delete on provided connection.
            }
            catch
            {
                // best-effort: if enqueue fails, fall back to direct delete.
            }

            // Direct delete path
            DeleteNpcInternal(conn, npcId, null);
        }

        // Internal transactional delete used by writer or direct callers.
        private static void DeleteNpcInternal(SqliteConnection conn, int npcId, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (npcId <= 0) return;

            try
            {
                EnsureOpen(conn);
                using var cmd = conn.CreateCommand();
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Npc WHERE NpcID = @id;";
                cmd.Parameters.AddWithValue("@id", npcId);
                cmd.ExecuteNonQuery();
                Console.WriteLine($"[NpcPersistence] Deleted NPC NpcID={npcId} from DB.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NpcPersistence] Failed to delete NPC NpcID={npcId}: {ex.Message}");
                throw;
            }
        } // end DeleteNpc

        private static void SaveNpcCombat(SqliteConnection conn, Npc npc, SqliteTransaction? tx = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (npc == null) throw new ArgumentNullException(nameof(npc));

            EnsureOpen(conn);
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
            if (Has("Level")) assignments.Add("Level = @Level");
            if (Has("Flags")) assignments.Add("Flags = @Flags");
            if (Has("Hunger")) assignments.Add("Hunger = @Hunger");

            if (assignments.Count == 0) return;

            using var up = conn.CreateCommand();
            if (tx != null) up.Transaction = tx;

            up.CommandText = $"UPDATE Npc SET {string.Join(", ", assignments)} WHERE NpcID = @id;";
            up.Parameters.AddWithValue("@id", npc.NpcID);

            up.Parameters.AddWithValue("@Mass", npc.Mass ?? 85.0);
            up.Parameters.AddWithValue("@EffMass", npc.EffMass ?? (npc.Mass ?? 85.0));
            up.Parameters.AddWithValue("@CombatSkill", npc.CombatSkill ?? 50.0);
            up.Parameters.AddWithValue("@EffCombatSkill", npc.EffCombatSkill ?? (npc.CombatSkill ?? 50.0));
            up.Parameters.AddWithValue("@SizeEnum", npc.SizeEnum ?? 4);
            up.Parameters.AddWithValue("@BaseDmgDice", npc.BaseDmgDice);
            up.Parameters.AddWithValue("@BaseArmorClass", npc.BaseArmorClass);
            up.Parameters.AddWithValue("@EffArmorClass", npc.EffArmorClass);
            up.Parameters.AddWithValue("@Level", Math.Max(1, npc.Level ?? 1));

            var buffsJson = JsonSerializer.Serialize(npc.Buffs ?? new List<string>());
            var debuffsJson = JsonSerializer.Serialize(npc.Debuffs ?? new List<string>());
            up.Parameters.AddWithValue("@Buffs", buffsJson);
            up.Parameters.AddWithValue("@Debuffs", debuffsJson);

            if (Has("Flags"))
            {
                try
                {
                    var flagsList = npc.Flags ?? new List<string>();
                    flagsList.Sort(System.StringComparer.OrdinalIgnoreCase);
                    var flagsJson = System.Text.Json.JsonSerializer.Serialize(flagsList);
                    up.Parameters.AddWithValue("@Flags", flagsJson);
                }
                catch
                {
                    up.Parameters.AddWithValue("@Flags", "[]");
                }
            }

            if (Has("Hunger"))
            {
                try
                {
                    up.Parameters.AddWithValue("@Hunger", npc.Hunger);
                }
                catch
                {
                    up.Parameters.AddWithValue("@Hunger", 0);
                }
            }

            try { up.ExecuteNonQuery(); } catch { }
        } // end SaveNpcCombat

        // Add a harmless consistency warning in loader
        public static List<Npc> LoadAllNpcs(SqliteConnection conn, World world)
        {
            EnsureOpen(conn);
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

                    // Read Alignment flexibly: prefer textual enum names, fallback to numeric values for backwards compatibility.
                    try
                    {
                        string? alignText = GetStringOrNull(rdr, "Alignment");
                        if (!string.IsNullOrWhiteSpace(alignText))
                        {
                            if (System.Enum.TryParse<Alignment>(alignText.Trim(), true, out var a))
                            {
                                npc.Alignment = a;
                            }
                            else if (int.TryParse(alignText.Trim(), out var ai) && ai >= 0 && ai <= 2)
                            {
                                npc.Alignment = (Alignment)ai;
                            }
                            else
                            {
                                npc.Alignment = Alignment.Neutral;
                            }
                        }
                        else
                        {
                            // Column may be numeric in some DBs
                            npc.Alignment = (Alignment)GetInt(rdr, "Alignment", 1);
                        }
                    }
                    catch
                    {
                        npc.Alignment = Alignment.Neutral;
                    }

                    npc.HealthMax = GetInt(rdr, "MaxHealth", 100);
                    npc.Health = GetInt(rdr, "Health", npc.HealthMax);
                    npc.CurrentRoomID = GetInt(rdr, "CurrentRoomID", 0);
                    npc.IsPersistent = GetInt(rdr, "IsPersistent", 1) != 0;
                    npc.VisualTrait = GetStringOrNull(rdr, "VisualTrait");

                    // Read CurrentAreaID if present
                    npc.CurrentAreaID = Has("CurrentAreaID") ? GetInt(rdr, "CurrentAreaID", 0) : 0;

                    // Read WanderChance (REAL)
                    npc.WanderChance = Has("WanderChance")
                        ? GetDoubleFlexible(rdr, "WanderChance", npc.WanderChance)
                        : npc.WanderChance;

                    // Read WanderAreas (prefer JSON TEXT; fallback to current area)
                    if (Has("WanderAreas"))
                    {
                        try
                        {
                            var idx = rdr.GetOrdinal("WanderAreas");
                            if (!rdr.IsDBNull(idx))
                            {
                                string? s = null;
                                try { s = rdr.GetString(idx); } catch { }
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                    try
                                    {
                                        var list = JsonSerializer.Deserialize<List<int>>(s);
                                        if (list != null) npc.WanderAreas = list;
                                    }
                                    catch { /* malformed JSON, ignore */ }
                                }
                                // If not text, fallback later
                            }
                        }
                        catch { }
                    }

                    // Read Hunger if present; default 0 (meaning never hungry).
                    npc.Hunger = Has("Hunger") ? GetInt(rdr, "Hunger", 0) : 0;

                    // Skip dead or invalid rows (Health <= 0 or HealthMax <= 0)
                    if (npc.Health <= 0 || npc.HealthMax <= 0)
                    {
                        Console.WriteLine($"[NpcPersistence] Skipping NPC NpcID={npc.NpcID} (Health={npc.Health}/{npc.HealthMax}).");
                        continue;
                    }

                    npc.Mass = Has("Mass") ? GetNullableDoubleFlexible(rdr, "Mass") : null;
                    npc.EffMass = Has("EffMass") ? GetNullableDoubleFlexible(rdr, "EffMass") : null;

                    // CHANGED: read REAL/int flexibly for combat skills
                    npc.CombatSkill = Has("CombatSkill") ? GetNullableDoubleFlexible(rdr, "CombatSkill") : null;
                    npc.EffCombatSkill = Has("EffCombatSkill") ? GetNullableDoubleFlexible(rdr, "EffCombatSkill") : null;

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

                    // NEW: read Flags column when present
                    if (Has("Flags"))
                    {
                        try
                        {
                            npc.Flags = TryDeserializeList(GetString(rdr, "Flags", "[]"));
                            npc.FlagsJson = string.IsNullOrEmpty(npc.FlagsJson) ? (npc.Flags != null ? JsonSerializer.Serialize(npc.Flags) : "[]") : npc.FlagsJson;
                        }
                        catch
                        {
                            npc.Flags = new List<string>();
                            npc.FlagsJson = "[]";
                        }
                    }

                    // NEW: Parse Sleep: flag into runtime fields (SleepRoomId, SleepStartPeriod, SleepEndPeriod).
                    try
                    {
                        npc.SleepRoomId = -1;
                        npc.SleepStartPeriod = "Midnight";
                        npc.SleepEndPeriod = "Dawn";
                        npc.IsSleeping = false;
                        npc.SleepBlockedAtRoomId = null;

                        if (npc.Flags != null)
                        {
                            string sleepFlag = null!;
                            foreach (var f in npc.Flags)
                            {
                                try
                                {
                                    if (string.IsNullOrWhiteSpace(f)) continue;
                                    if (f.StartsWith("Sleep:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        sleepFlag = f;
                                        break;
                                    }
                                }
                                catch { /* best-effort */ }
                            }

                            if (!string.IsNullOrWhiteSpace(sleepFlag))
                            {
                                var parts = sleepFlag.Split(':');
                                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var rid))
                                    npc.SleepRoomId = Math.Max(0, rid);
                                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                                    npc.SleepStartPeriod = parts[2].Trim();
                                if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
                                    npc.SleepEndPeriod = parts[3].Trim();
                            }

                            try
                            {
                                bool sleepingFound = false;
                                foreach (var f in npc.Flags)
                                {
                                    if (string.IsNullOrWhiteSpace(f)) continue;
                                    if (string.Equals(f, "Sleeping", StringComparison.OrdinalIgnoreCase))
                                    {
                                        sleepingFound = true;
                                        break;
                                    }
                                }
                                npc.IsSleeping = sleepingFound;
                            }
                            catch { npc.IsSleeping = false; }
                        }
                    }
                    catch (Exception exSleep)
                    {
                        Console.WriteLine($"[NpcPersistence][WARN] Failed to parse Sleep flag for NpcID={npc.NpcID}: {exSleep.Message}");
                        npc.SleepRoomId = -1;
                        npc.SleepStartPeriod = "Midnight";
                        npc.SleepEndPeriod = "Dawn";
                        npc.IsSleeping = false;
                        npc.SleepBlockedAtRoomId = null;
                    }

                    // NEW: Parse Move: flags into runtime MoveSchedules (collect all Move: entries).
                    try
                    {
                        npc.MoveRoomId = 0;
                        npc.MovePeriod = "";
                        npc.IsOnMove = false;
                        npc.MoveBlockedAtRoomId = null;
                        npc.MoveSchedules = new List<Npc.MoveSchedule>();

                        if (npc.Flags != null)
                        {
                            var moveFlagList = new List<string>();
                            foreach (var f in npc.Flags)
                            {
                                try
                                {
                                    if (string.IsNullOrWhiteSpace(f)) continue;
                                    if (f.StartsWith("Move:", StringComparison.OrdinalIgnoreCase))
                                        moveFlagList.Add(f);
                                }
                                catch { /* best-effort per-move */ }
                            }

                            if (moveFlagList.Count > 0)
                            {
                                foreach (var mf in moveFlagList)
                                {
                                    try
                                    {
                                        var parts = mf.Split(':');
                                        if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var rid))
                                        {
                                            var period = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : "";
                                            npc.MoveSchedules.Add(new Npc.MoveSchedule { RoomId = Math.Max(0, rid), Period = period });
                                        }
                                    }
                                    catch { /* best-effort per-move */ }
                                }

                                if (npc.MoveSchedules.Count > 0)
                                {
                                    var first = npc.MoveSchedules[0];
                                    npc.MoveRoomId = first.RoomId;
                                    npc.MovePeriod = first.Period ?? "";
                                }
                            }
                        }

                        // Hydrate IsOnMove only when explicit "Moving" runtime flag is present.
                        try
                        {
                            bool movingFound = false;
                            if (npc.Flags != null)
                            {
                                foreach (var f in npc.Flags)
                                {
                                    if (string.IsNullOrWhiteSpace(f)) continue;
                                    if (string.Equals(f, "Moving", StringComparison.OrdinalIgnoreCase))
                                    {
                                        movingFound = true;
                                        break;
                                    }
                                }
                            }
                            npc.IsOnMove = movingFound;
                        }
                        catch
                        {
                            npc.IsOnMove = false;
                        }
                    }
                    catch (Exception exMove)
                    {
                        Console.WriteLine($"[NpcPersistence][WARN] Failed to parse Move flag(s) for NpcID={npc.NpcID}: {exMove.Message}");
                        npc.MoveRoomId = 0;
                        npc.MovePeriod = "";
                        npc.IsOnMove = false;
                        npc.MoveBlockedAtRoomId = null;
                        npc.MoveSchedules = new List<Npc.MoveSchedule>();
                    }

                    npc.Description = Has("Description") ? GetString(rdr, "Description", "") : npc.Description;

                    npc.CurrentRoom = world.Rooms.Find(r => r.RoomID == npc.CurrentRoomID);

                    // -------------------------
                    // Attach template and apply overrides from DB if present (robust AiBehaviors parsing)
                    // -------------------------
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(npc.TemplateID) && world?.TemplateManager != null)
                        {
                            if (world.TemplateManager.TryGet(npc.TemplateID, out var tmpl))
                            {
                                // Attempt to read per-NPC AiBehavior override column (supports either 'AiBehaviors' or 'AiBehaviorConfigs')
                                string? aiBehJson = null;
                                try
                                {
                                    if (Has("AiBehaviors")) aiBehJson = GetStringOrNull(rdr, "AiBehaviors");
                                    if (string.IsNullOrWhiteSpace(aiBehJson) && Has("AiBehaviorConfigs"))
                                        aiBehJson = GetStringOrNull(rdr, "AiBehaviorConfigs");
                                }
                                catch { aiBehJson = null; }

                                if (!string.IsNullOrWhiteSpace(aiBehJson))
                                {
                                    try
                                    {
                                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                        List<NpcTemplate.AiBehaviorConfig>? configs = null;

                                        // Try direct deserialization into structured configs first
                                        try
                                        {
                                            configs = JsonSerializer.Deserialize<List<NpcTemplate.AiBehaviorConfig>>(aiBehJson, opts);
                                        }
                                        catch
                                        {
                                            configs = null;
                                        }

                                        // If direct failed, parse manually via JsonDocument to handle mixed forms robustly
                                        if (configs == null)
                                        {
                                            try
                                            {
                                                var docs = JsonDocument.Parse(aiBehJson);
                                                if (docs.RootElement.ValueKind == JsonValueKind.Array)
                                                {
                                                    configs = new List<NpcTemplate.AiBehaviorConfig>();
                                                    foreach (var el in docs.RootElement.EnumerateArray())
                                                    {
                                                        try
                                                        {
                                                            if (el.ValueKind == JsonValueKind.Object)
                                                            {
                                                                var raw = el.GetRawText();
                                                                var cfg = JsonSerializer.Deserialize<NpcTemplate.AiBehaviorConfig>(raw, opts);
                                                                if (cfg != null) configs.Add(cfg);
                                                            }
                                                            else if (el.ValueKind == JsonValueKind.String)
                                                            {
                                                                var s = el.GetString();
                                                                if (!string.IsNullOrWhiteSpace(s))
                                                                {
                                                                    // Some templates may have stringified JSON inside the array; try to parse it
                                                                    var trimmed = s.Trim();
                                                                    if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) || (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
                                                                    {
                                                                        try
                                                                        {
                                                                            var inner = JsonDocument.Parse(trimmed);
                                                                            if (inner.RootElement.ValueKind == JsonValueKind.Object)
                                                                            {
                                                                                var cfg = JsonSerializer.Deserialize<NpcTemplate.AiBehaviorConfig>(trimmed, opts);
                                                                                if (cfg != null) configs.Add(cfg);
                                                                            }
                                                                            // else: skip arrays of primitives as legacy – not supported to structured config
                                                                        }
                                                                        catch
                                                                        {
                                                                            // ignore inner parse failures
                                                                        }
                                                                    }
                                                                    else
                                                                    {
                                                                        // Legacy simple string behavior token (e.g., "ItemMover") — synthesize minimal config
                                                                        var cfg = new NpcTemplate.AiBehaviorConfig { Behavior = s };
                                                                        configs.Add(cfg);
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        catch { /* best-effort per-element */ }
                                                    }
                                                }
                                            }
                                            catch { configs = null; }
                                        }

                                        // If we got structured configs, clone template and attach them; else fallback to shared template
                                        if (configs != null && configs.Count > 0)
                                        {
                                            // Deep-clone the template so we don't mutate the shared template instance in TemplateManager.
                                            var cloneJson = JsonSerializer.Serialize(tmpl, new JsonSerializerOptions { WriteIndented = false });
                                            var tmplClone = JsonSerializer.Deserialize<NpcTemplate>(cloneJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                            if (tmplClone != null)
                                            {
                                                tmplClone.AiBehaviorConfigs = configs;
                                                // Also preserve raw AiBehaviorsRaw if db contained objects/strings – keep both representations for fidelity
                                                try
                                                {
                                                    var rawList = JsonSerializer.Deserialize<List<JsonElement>>(aiBehJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                                    if (rawList != null && rawList.Count > 0)
                                                        tmplClone.AiBehaviorsRaw = rawList;
                                                }
                                                catch { /* best-effort */ }

                                                npc.Template = tmplClone;
                                            }
                                            else
                                            {
                                                npc.Template = tmpl; // fallback
                                            }
                                        }
                                        else
                                        {
                                            // Could not parse structured configs – keep shared template and log for diagnosis
                                            Console.WriteLine($"[NpcPersistence][WARN] AiBehaviors JSON present but could not be parsed into configs for NpcID={npc.NpcID}. Raw length={aiBehJson.Length}");
                                            // preserve raw on the shared template only for visibility (do not mutate global object)
                                            npc.Template = tmpl;
                                        }
                                    }
                                    catch (Exception exDes)
                                    {
                                        Console.WriteLine($"[NpcPersistence][WARN] Failed to deserialize AiBehaviors for NpcID={npc.NpcID}: {exDes.Message}");
                                        npc.Template = tmpl;
                                    }
                                }
                                else
                                {
                                    // No per-NPC override → use shared template
                                    npc.Template = tmpl;
                                }

                                if (string.IsNullOrWhiteSpace(npc.Name) && npc.Template != null && !string.IsNullOrWhiteSpace(npc.Template.Name))
                                    npc.Name = npc.Template.Name;

                                // If DB had 0 WanderChance, hydrate from template
                                if (npc.WanderChance <= 0 && npc.Template != null)
                                    npc.WanderChance = npc.Template.WanderChance;

                                // NEW: hydrate Level from template if missing/invalid
                                if (npc.Level == null || npc.Level <= 0)
                                    npc.Level = npc.Template?.Level ?? 1;

                                // Apply template defaults so Description, IdleActions, etc. are populated when DB row omitted them.
                                try { npc.ApplyTemplateDefaults(); } catch { /* best-effort */ }
                            }
                            else
                            {
                                Console.WriteLine($"[NpcPersistence][WARN] Template not found for TemplateID='{npc.TemplateID}' NpcID={npc.NpcID}.");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[NpcPersistence][WARN] Missing TemplateID or TemplateManager for NpcID={npc.NpcID}.");
                        }
                    }
                    catch (Exception tex)
                    {
                        Console.WriteLine($"[NpcPersistence][ERROR] Failed to attach template for NpcID={npc.NpcID}: {tex.Message}");
                    }

                    // Ensure WanderAreas fallback to current room area when empty
                    if ((npc.WanderAreas == null || npc.WanderAreas.Count == 0) && npc.CurrentRoom != null && npc.CurrentRoom.AreaID > 0)
                        npc.WanderAreas = new List<int> { npc.CurrentRoom.AreaID };

                    Combat.InitializeSizeDefaults(npc);
                    Combat.UpdateEffectiveStats(npc);

                    // --- after parsing Move flags into npc.MoveSchedules (existing code) ---
                    // Add conditional debug output so we can confirm DB->runtime mapping for Move schedules.
                    try
                    {
                        var dbg = Environment.GetEnvironmentVariable("NPC_PERSISTENCE_DEBUG");
                        if (!string.IsNullOrEmpty(dbg) && dbg.Trim() == "1")
                        {
                            try
                            {
                                if (npc.MoveSchedules != null && npc.MoveSchedules.Count > 0)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    sb.Append($"[NpcPersistence][DBG-MOVE] NpcID={npc.NpcID} MoveSchedules:");
                                    foreach (var ms in npc.MoveSchedules)
                                    {
                                        sb.Append($" {{RoomId={ms.RoomId},Period='{ms.Period}'}}");
                                    }
                                    Console.WriteLine(sb.ToString());
                                }
                            }
                            catch { /* best-effort */ }
                        }
                    }
                    catch { /* best-effort */ }

                    if (npc.CurrentRoom != null)
                        npc.CurrentRoom.Npcs.Add(npc);
                    npcs.Add(npc);

                    // Register ItemTrader if template includes behavior config (best-effort)
                    try
                    {
                        var trader = MudCore.AI.ItemTrader.CreateIfConfigured(npc, world);
                        if (trader != null)
                        {
                            MudCore.AI.ItemTraderManager.Register(npc, trader);
                        }
                    }
                    catch (Exception exReg)
                    {
                        Console.WriteLine($"[NpcPersistence] Failed to create/register ItemTrader for NpcID={npc.NpcID}: {exReg.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NpcPersistence] Failed to load NPC row: {ex.Message}");
                }
            }
            return npcs;
        } // end LoadAllNpcs

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
