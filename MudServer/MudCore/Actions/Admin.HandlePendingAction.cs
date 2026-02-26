using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MudCore.Actions;
using MudCore.Entities;
using MudCore.Templates;
using MudCore.World;
using MudNetworking;
using MudPersistence;
using MudServer;
using MudUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DbWriteQueue = global::MudServer.DbWriteQueue;


namespace MudCore.Actions
{
    // Extends AdminService with room editing commands. These are all prefixed with "editroom" to avoid conflicts with other edit* commands.
    public partial class AdminService
    {
        // Handles admin-related pending actions: EditRoom, CreateArea, CreateRoom.
        // Returns non-null Message if handled and the pending action consumed.
        public Message? HandlePendingAction(Player player, PendingAction pa, string input)
        {
            if (pa == null) return null;

            // --- EditExit pending action ---
            if (pa.Command == "EditExit")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;
                if (room == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("You are not inside a valid room.");
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Edit exit cancelled.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("HasDoor", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditHasDoor";
                        return MessageHandler.System("Set HasDoor: type 't' for true or 'f' for false.");
                    }

                    // New option: create an exit to an existing room
                    if (input == "2" || input.Equals("CreateExitToRoom", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ask admin for the direction to create (allow any valid direction)
                        pa.Step = "CreateExitToRoom_Dir";
                        return MessageHandler.System("Enter exit direction to create (N, S, E, W, NE, NW, SE, SW, UP, DOWN):");
                    }

                    // NEW: Option 3 - EditDoor (only when exit has a door)
                    if (input == "3" || input.Equals("EditDoor", StringComparison.OrdinalIgnoreCase))
                    {
                        // Find the exit by normalized direction
                        var exit = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(pa.Direction), StringComparison.OrdinalIgnoreCase));
                        if (exit == null)
                        {
                            player.PendingAction = null!;
                            return MessageHandler.System($"No exit found for direction '{pa.Direction}'.");
                        }

                        if (!exit.DoorId.HasValue)
                        {
                            return MessageHandler.System($"Exit '{exit.Direction}' does not have a door. Set HasDoor to true first (option 1).");
                        }

                        // Store DoorId in pa.Data for subsequent steps
                        var dict = new Dictionary<string, object>
                    {
                        { "DoorId", exit.DoorId.Value },
                        { "Direction", pa.Direction }
                    };
                        pa.Data = dict;
                        pa.Step = "EditDoorMenu";

                        // Load current door metadata from DB
                        string currentType = "Door";
                        List<string> currentFlags = new List<string>();
                        try
                        {
                            using var q = _db.CreateCommand();
                            q.CommandText = "SELECT DoorType, Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                            q.Parameters.AddWithValue("@did", exit.DoorId.Value);
                            using var rdr = q.ExecuteReader();
                            if (rdr.Read())
                            {
                                if (!rdr.IsDBNull(0))
                                {
                                    var dt = rdr.GetString(0);
                                    if (!string.IsNullOrWhiteSpace(dt))
                                        currentType = dt;
                                }
                                if (!rdr.IsDBNull(1))
                                {
                                    var fj = rdr.GetString(1);
                                    if (!string.IsNullOrWhiteSpace(fj))
                                    {
                                        try
                                        {
                                            var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                            if (parsed != null) currentFlags = parsed;
                                        }
                                        catch { /* fallback to empty */ }
                                    }
                                }
                            }
                        }
                        catch (Exception exLoad)
                        {
                            Console.WriteLine($"[ADMIN][EditDoor] Failed to load door metadata: {exLoad.Message}");
                        }

                        var flagsDisplay = currentFlags.Count == 0 ? "(none)" : string.Join(", ", currentFlags);
                        return MessageHandler.System(
                            $"Editing door for exit '{exit.Direction}' (DoorId={exit.DoorId.Value})\n" +
                            $"Current DoorType: {currentType}\n" +
                            $"Current Flags: {flagsDisplay}\n\n" +
                            "Options:\n" +
                            "1) Set DoorType\n" +
                            "2) Add Flag\n" +
                            "3) Remove Flag\n" +
                            "4) Done\n" +
                            "Type the option number or 'EndEdit' to cancel."
                        );
                    }


                    return MessageHandler.System("Invalid option. Type '1' or 'HasDoor' or '2' or 'CreateExitToRoom' or '3' or 'EditExit', or 'EndEdit'.");
                }

                // --- CreateExitToRoom: Step 1 = direction ---
                if (pa.Step == "CreateExitToRoom_Dir")
                {
                    var dirInput = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(dirInput))
                        return MessageHandler.System("Enter a direction (N, S, E, W, NE, NW, SE, SW, UP, DOWN).");

                    if (!IsValidDirection(dirInput))
                        return MessageHandler.System("Invalid direction. Use one of: N, S, E, W, NE, NW, SE, SW, UP, DOWN.");

                    // store new direction normalized in pa.Data
                    var dict = pa.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
                    dict["NewDir"] = NormalizeDirection(dirInput);
                    pa.Data = dict;

                    pa.Step = "CreateExitToRoom_RoomId";
                    // Added abort hint so admin can back out if they don't remember the RoomID.
                    return MessageHandler.System("Enter RoomID (integer) to link this exit to (or type 'EndEdit' to cancel):");
                }

                // --- CreateExitToRoom: Step 2 = room id ---
                if (pa.Step == "CreateExitToRoom_RoomId")
                {
                    var dict = pa.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
                    if (!dict.TryGetValue("NewDir", out var ndObj) || ndObj == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: missing direction. Restart edit and try again.");
                    }

                    string newDir = ndObj.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(newDir))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: invalid direction stored. Restart edit and try again.");
                    }

                    if (!int.TryParse((input ?? "").Trim(), out var targetRoomId))
                        return MessageHandler.System("Invalid RoomID. Enter an integer RoomID.");

                    // Validate target room exists
                    var targetRoom = _world.Rooms.FirstOrDefault(r => r != null && r.RoomID == targetRoomId);
                    if (targetRoom == null)
                        return MessageHandler.System($"RoomID {targetRoomId} not found.");

                    // Perform DB upsert for both sides (src -> target, and reverse target -> src) within writer
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            // Helper: upsert by RoomID+ToRoomID (preserve metadata)
                            void UpsertExit(int rRoomId, string dir, int toRoomId)
                            {
                                using var upd = conn.CreateCommand();
                                upd.Transaction = tx;
                                upd.CommandText = @"
                                    UPDATE RoomExit
                                    SET Direction = @dir,
                                        IsHidden = @hidden,
                                        DiscoverKeyword = @keyword,
                                        DoorId = @doorId
                                    WHERE RoomID = @rid AND ToRoomID = @toRid;";
                                upd.Parameters.AddWithValue("@rid", rRoomId);
                                upd.Parameters.AddWithValue("@dir", dir ?? "");
                                upd.Parameters.AddWithValue("@toRid", toRoomId);
                                upd.Parameters.AddWithValue("@hidden", 0);
                                upd.Parameters.AddWithValue("@keyword", "");
                                upd.Parameters.AddWithValue("@doorId", 0);

                                var affected = upd.ExecuteNonQuery();
                                if (affected == 0)
                                {
                                    using var ins = conn.CreateCommand();
                                    ins.Transaction = tx;
                                    ins.CommandText = @"
                                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                                    ins.Parameters.AddWithValue("@rid", rRoomId);
                                    ins.Parameters.AddWithValue("@dir", dir ?? "");
                                    ins.Parameters.AddWithValue("@toRid", toRoomId);
                                    ins.ExecuteNonQuery();
                                }
                            }

                            // Upsert target->src first (reverse), using opposite direction
                            var opp = OppositeDirection(newDir);
                            UpsertExit(targetRoomId, opp, room.RoomID);

                            // Upsert src->target using provided newDir
                            UpsertExit(room.RoomID, newDir, targetRoomId);

                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(8));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditExit][CreateExitToRoom] DB write failed: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to create exit due to a server error.");
                    }

                    // Update in-memory exits (avoid duplicates)
                    try
                    {
                        // Add src -> target
                        if (!room.Exits.Any(x => x.ToRoomID == targetRoomId && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(newDir), StringComparison.OrdinalIgnoreCase)))
                        {
                            room.Exits.Add(new Room.RoomExit(newDir, targetRoomId));
                            room.IsDirty = true;
                        }

                        // Add reverse exit in target room
                        var reverseDir = OppositeDirection(newDir);
                        if (!targetRoom.Exits.Any(x => x.ToRoomID == room.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(reverseDir), StringComparison.OrdinalIgnoreCase)))
                        {
                            targetRoom.Exits.Add(new Room.RoomExit(reverseDir, room.RoomID));
                            targetRoom.IsDirty = true;
                        }

                        // Persist both rooms' exits to DB via RoomPersistence (best-effort)
                        try
                        {
                            RoomPersistence.SaveRoomExits(_db, targetRoom);
                        }
                        catch { /* best-effort */ }
                        try
                        {
                            RoomPersistence.SaveRoomExits(_db, room);
                        }
                        catch { /* best-effort */ }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditExit][CreateExitToRoom] In-memory update failed: {ex.Message}");
                    }

                    // Broadcast updated room(s)
                    try
                    {
                        foreach (var p in room.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                room,
                                room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }

                        foreach (var p in targetRoom.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                targetRoom,
                                targetRoom.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                targetRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Created exit '{newDir}' linking Room {room.RoomID} -> Room {targetRoomId} (reverse '{OppositeDirection(newDir)}' also created).");
                }

                // --- EditHasDoor preserved unchanged (existing logic) ---
                if (pa.Step == "EditHasDoor")
                {
                    var v = input?.Trim().ToLowerInvariant();
                    if (v != "t" && v != "f" && v != "true" && v != "false")
                        return MessageHandler.System("Invalid value. Type 't' (true) or 'f' (false).");

                    bool newVal = (v == "t" || v == "true");

                    // Find the exit by normalized direction
                    var exit = room.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(pa.Direction), StringComparison.OrdinalIgnoreCase));
                    if (exit == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System($"No exit found for direction '{pa.Direction}'.");
                    }

                    try
                    {
                        if (newVal)
                        {
                            if (!exit.DoorId.HasValue)
                            {
                                int newDoorId = DbWriteQueue.Instance.RunWriteAndWait<int>((conn, tx) =>
                                {
                                    using var ins = conn.CreateCommand();
                                    ins.Transaction = tx;
                                    ins.CommandText = "INSERT INTO Doors (IsOpen, DoorType, Flags) VALUES (0, 'Door', '[\"Close:30\"]');";
                                    ins.ExecuteNonQuery();

                                    using var last = conn.CreateCommand();
                                    last.Transaction = tx;
                                    last.CommandText = "SELECT last_insert_rowid();";
                                    var vobj = last.ExecuteScalar();
                                    var createdId = Convert.ToInt32(vobj);

                                    void UpsertExitInternal(int rRoomId, string dir, int toRoomId, int doorIdVal, bool isHidden, string? keyword)
                                    {
                                        using var upd = conn.CreateCommand();
                                        upd.Transaction = tx;
                                        upd.CommandText = @"
                                    UPDATE RoomExit
                                    SET Direction = @dir,
                                        IsHidden = @hidden,
                                        DiscoverKeyword = @keyword,
                                        DoorId = @doorId
                                    WHERE RoomID = @rid AND ToRoomID = @toRid;";
                                        upd.Parameters.AddWithValue("@rid", rRoomId);
                                        upd.Parameters.AddWithValue("@dir", dir ?? "");
                                        upd.Parameters.AddWithValue("@toRid", toRoomId);
                                        upd.Parameters.AddWithValue("@hidden", isHidden ? 1 : 0);
                                        upd.Parameters.AddWithValue("@keyword", keyword ?? "");
                                        upd.Parameters.AddWithValue("@doorId", doorIdVal);

                                        var affected = upd.ExecuteNonQuery();
                                        if (affected == 0)
                                        {
                                            using var insCmd = conn.CreateCommand();
                                            insCmd.Transaction = tx;
                                            insCmd.CommandText = @"
                                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                                        VALUES (@rid, @dir, @toRid, @hidden, @keyword, @doorId);";
                                            insCmd.Parameters.AddWithValue("@rid", rRoomId);
                                            insCmd.Parameters.AddWithValue("@dir", dir ?? "");
                                            insCmd.Parameters.AddWithValue("@toRid", toRoomId);
                                            insCmd.Parameters.AddWithValue("@hidden", isHidden ? 1 : 0);
                                            insCmd.Parameters.AddWithValue("@keyword", keyword ?? "");
                                            insCmd.Parameters.AddWithValue("@doorId", doorIdVal);
                                            insCmd.ExecuteNonQuery();
                                        }
                                    }

                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && mateRoom != null)
                                    {
                                        UpsertExitInternal(mateRoom.RoomID, mateExit.Direction, room.RoomID, createdId, mateExit.IsHidden, mateExit.DiscoverKeyword);
                                    }

                                    UpsertExitInternal(room.RoomID, exit.Direction, exit.ToRoomID, createdId, exit.IsHidden, exit.DiscoverKeyword);

                                    return createdId;
                                }, timeout: TimeSpan.FromSeconds(5));

                                exit.DoorId = newDoorId;
                                exit.DoorIsOpen = false;
                                room.IsDirty = true;

                                try
                                {
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && !mateExit.DoorId.HasValue)
                                    {
                                        mateExit.DoorId = newDoorId;
                                        mateExit.DoorIsOpen = false;
                                        mateRoom!.IsDirty = true;
                                    }
                                }
                                catch { /* best-effort in-memory sync */ }
                            }
                        }
                        else
                        {
                            int? oldDoor = exit.DoorId;
                            exit.DoorId = null;

                            if (oldDoor.HasValue)
                            {
                                DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                                {
                                    try
                                    {
                                        var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                        Room.RoomExit? mateExit = null;
                                        if (mateRoom != null && mateRoom.Exits != null)
                                        {
                                            string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                            mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                                e.ToRoomID == room.RoomID &&
                                                string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                                ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                        }

                                        if (mateRoom != null && mateExit != null)
                                        {
                                            using var cmd = conn.CreateCommand();
                                            cmd.Transaction = tx;
                                            cmd.CommandText = @"
                                        UPDATE RoomExit
                                        SET DoorId = 0
                                        WHERE RoomID = @mrid AND ToRoomID = @toRid AND DoorId = @door;";
                                            cmd.Parameters.AddWithValue("@mrid", mateRoom.RoomID);
                                            cmd.Parameters.AddWithValue("@toRid", room.RoomID);
                                            cmd.Parameters.AddWithValue("@door", oldDoor.Value);
                                            cmd.ExecuteNonQuery();
                                        }

                                        using var cmd2 = conn.CreateCommand();
                                        cmd2.Transaction = tx;
                                        cmd2.CommandText = @"
                                    UPDATE RoomExit
                                    SET DoorId = 0
                                    WHERE RoomID = @rid AND ToRoomID = @toRid AND DoorId = @door;";
                                        cmd2.Parameters.AddWithValue("@rid", room.RoomID);
                                        cmd2.Parameters.AddWithValue("@toRid", exit.ToRoomID);
                                        cmd2.Parameters.AddWithValue("@door", oldDoor.Value);
                                        cmd2.ExecuteNonQuery();
                                    }
                                    catch (Exception ex)
                                    {
                                        throw new Exception("Failed to clear DoorId in DB: " + ex.Message, ex);
                                    }
                                    return (object?)null;
                                }, timeout: TimeSpan.FromSeconds(5));

                                try
                                {
                                    var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                                    Room.RoomExit? mateExit = null;
                                    if (mateRoom != null && mateRoom.Exits != null)
                                    {
                                        string opp = OppositeDirection(NormalizeDirection(exit.Direction));
                                        mateExit = mateRoom.Exits.FirstOrDefault(e =>
                                            e.ToRoomID == room.RoomID &&
                                            string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase))
                                            ?? mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == room.RoomID);
                                    }

                                    if (mateExit != null && mateExit.DoorId.HasValue && mateExit.DoorId.Value == oldDoor.Value)
                                    {
                                        mateExit.DoorId = null;
                                        mateExit.DoorIsOpen = null;
                                        mateRoom!.IsDirty = true;
                                    }
                                }
                                catch { /* best-effort */ }
                            }
                        }

                        room.IsDirty = true;
                        _world.SaveDirtyRooms(_db);

                        try
                        {
                            var mateRoomForSave = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                            if (mateRoomForSave != null)
                                RoomPersistence.SaveRoomExits(_db, mateRoomForSave);

                            RoomPersistence.SaveRoomExits(_db, room);
                        }
                        catch (Exception exSave)
                        {
                            Console.WriteLine($"[ADMIN][EditExit] SaveRoomExits failed: {exSave.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EDITEXIT] Failed saving exit change: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save exit changes due to a server error.");
                    }

                    try
                    {
                        foreach (var p in room.Players)
                        {
                            p.PendingMessageQueue.Add(MessageHandler.Room(
                                room,
                                room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                _world, _db));
                        }

                        try
                        {
                            var mateRoomNotify = _world.Rooms.FirstOrDefault(r => r.RoomID == exit.ToRoomID);
                            if (mateRoomNotify != null)
                            {
                                foreach (var p in mateRoomNotify.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        mateRoomNotify,
                                        mateRoomNotify.Npcs.Select(n => n.DisplayName).ToList(),
                                        mateRoomNotify.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world, _db));
                                }
                            }
                        }
                        catch { /* best-effort */ }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Exit '{exit.Direction}' HasDoor set to {(exit.DoorId.HasValue ? "true" : "false")}.");
                }

                // NEW: EditDoorMenu step
                if (pa.Step == "EditDoorMenu")
                {
                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("4", StringComparison.OrdinalIgnoreCase) || input.Equals("Done", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Door editing complete.");
                    }

                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found. Restart edit.");
                    }

                    if (input.Equals("1", StringComparison.OrdinalIgnoreCase) || input.Equals("Set DoorType", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "SetDoorType";
                        return MessageHandler.System("Enter new DoorType (e.g., Door, Gate, Trapdoor, Portcullis):");
                    }

                    if (input.Equals("2", StringComparison.OrdinalIgnoreCase) || input.Equals("Add Flag", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "AddDoorFlag";
                        return MessageHandler.System("Enter flag to add (e.g., 'Close:30' for 30-second auto-close, 'Lockable' to enable locking):");
                    }

                    if (input.Equals("3", StringComparison.OrdinalIgnoreCase) || input.Equals("Remove Flag", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "RemoveDoorFlag";
                        // Load current flags and display numbered list
                        List<string> flags = new List<string>();
                        try
                        {
                            using var q = _db.CreateCommand();
                            q.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                            q.Parameters.AddWithValue("@did", doorId);
                            var obj = q.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                var fj = obj.ToString();
                                if (!string.IsNullOrWhiteSpace(fj))
                                {
                                    var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                    if (parsed != null) flags = parsed;
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        if (flags.Count == 0)
                            return MessageHandler.System("No flags to remove. Type 'back' to return to menu.");

                        var list = string.Join("\n", flags.Select((f, i) => $"{i + 1}) {f}"));
                        return MessageHandler.System($"Current flags:\n{list}\n\nType the flag number to remove, or 'back':");
                    }

                    return MessageHandler.System("Invalid option. Type 1, 2, 3, 4, or 'EndEdit'.");
                }

                // NEW: SetDoorType step
                if (pa.Step == "SetDoorType")
                {
                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found.");
                    }

                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    var newType = input.Trim();
                    if (string.IsNullOrWhiteSpace(newType) || newType.Length > 50)
                        return MessageHandler.System("Invalid DoorType. Enter a reasonable name (max 50 chars).");

                    // Persist to DB via DbWriteQueue
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Doors SET DoorType = @dtype WHERE DoorId = @did;";
                            cmd.Parameters.AddWithValue("@dtype", newType);
                            cmd.Parameters.AddWithValue("@did", doorId);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditDoor] Failed to update DoorType: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save DoorType due to a server error.");
                    }

                    // Update in-memory cache on both exits
                    try
                    {
                        var currentRoom = player.CurrentRoom;
                        if (currentRoom != null && dict.TryGetValue("Direction", out var dirObj))
                        {
                            var dir = dirObj.ToString();
                            var exit = currentRoom.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                            if (exit != null && exit.DoorId == doorId)
                                exit.DoorType = newType;

                            // Update mate exit
                            var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit?.ToRoomID);
                            if (mateRoom != null && exit != null)
                            {
                                var opp = OppositeDirection(NormalizeDirection(dir));
                                var mateExit = mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == currentRoom.RoomID && string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase));
                                if (mateExit != null && mateExit.DoorId == doorId)
                                    mateExit.DoorType = newType;
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // Broadcast room updates
                    try
                    {
                        BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"DoorType updated to '{newType}'.");
                }

                // NEW: AddDoorFlag step
                if (pa.Step == "AddDoorFlag")
                {
                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found.");
                    }

                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    var newFlag = input.Trim();
                    if (string.IsNullOrWhiteSpace(newFlag))
                        return MessageHandler.System("Flag cannot be empty. Enter a flag or 'EndEdit'.");

                    // Load current flags, remove any existing Close:* flags if adding a new Close:N
                    List<string> flags = new List<string>();
                    try
                    {
                        using var q = _db.CreateCommand();
                        q.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                        q.Parameters.AddWithValue("@did", doorId);
                        var obj = q.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                        {
                            var fj = obj.ToString();
                            if (!string.IsNullOrWhiteSpace(fj))
                            {
                                var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                if (parsed != null) flags = parsed;
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // If adding Close:N, remove existing Close:* flags (only one allowed)
                    if (newFlag.StartsWith("Close:", StringComparison.OrdinalIgnoreCase))
                    {
                        flags.RemoveAll(f => f.StartsWith("Close:", StringComparison.OrdinalIgnoreCase));
                    }

                    // Add new flag (avoid duplicates)
                    if (!flags.Contains(newFlag, StringComparer.OrdinalIgnoreCase))
                        flags.Add(newFlag);

                    // Persist via DbWriteQueue
                    try
                    {
                        var flagsJson = JsonSerializer.Serialize(flags);
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Doors SET Flags = @flags WHERE DoorId = @did;";
                            cmd.Parameters.AddWithValue("@flags", flagsJson);
                            cmd.Parameters.AddWithValue("@did", doorId);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditDoor] Failed to add flag: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save flag due to a server error.");
                    }

                    // Update in-memory cache on both exits
                    try
                    {
                        var currentRoom = player.CurrentRoom;
                        if (currentRoom != null && dict.TryGetValue("Direction", out var dirObj))
                        {
                            var dir = dirObj.ToString();
                            var exit = currentRoom.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                            if (exit != null && exit.DoorId == doorId)
                                exit.DoorFlags = new List<string>(flags);

                            // Update mate exit
                            var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit?.ToRoomID);
                            if (mateRoom != null && exit != null)
                            {
                                var opp = OppositeDirection(NormalizeDirection(dir));
                                var mateExit = mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == currentRoom.RoomID && string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase));
                                if (mateExit != null && mateExit.DoorId == doorId)
                                    mateExit.DoorFlags = new List<string>(flags);
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // Broadcast room updates
                    try
                    {
                        BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Flag '{newFlag}' added.");
                }

                // NEW: RemoveDoorFlag step
                if (pa.Step == "RemoveDoorFlag")
                {
                    var dict = pa.Data as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue("DoorId", out var didObj) || !int.TryParse(didObj.ToString(), out var doorId))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Internal error: DoorId not found.");
                    }

                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase) || input.Equals("cancel", StringComparison.OrdinalIgnoreCase) || input.Equals("back", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    if (!int.TryParse(input.Trim(), out var idx) || idx < 1)
                        return MessageHandler.System("Invalid index. Enter a flag number or 'back'.");

                    // Load current flags
                    List<string> flags = new List<string>();
                    try
                    {
                        using var q = _db.CreateCommand();
                        q.CommandText = "SELECT Flags FROM Doors WHERE DoorId = @did LIMIT 1;";
                        q.Parameters.AddWithValue("@did", doorId);
                        var obj = q.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                        {
                            var fj = obj.ToString();
                            if (!string.IsNullOrWhiteSpace(fj))
                            {
                                var parsed = JsonSerializer.Deserialize<List<string>>(fj);
                                if (parsed != null) flags = parsed;
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    if (idx > flags.Count)
                        return MessageHandler.System($"Invalid index. Enter 1..{flags.Count} or 'back'.");

                    var removed = flags[idx - 1];
                    flags.RemoveAt(idx - 1);

                    // Persist via DbWriteQueue
                    try
                    {
                        var flagsJson = JsonSerializer.Serialize(flags);
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((conn, tx) =>
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = "UPDATE Doors SET Flags = @flags WHERE DoorId = @did;";
                            cmd.Parameters.AddWithValue("@flags", flagsJson);
                            cmd.Parameters.AddWithValue("@did", doorId);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditDoor] Failed to remove flag: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save flags due to a server error.");
                    }

                    // Update in-memory cache on both exits
                    try
                    {
                        var currentRoom = player.CurrentRoom;
                        if (currentRoom != null && dict.TryGetValue("Direction", out var dirObj))
                        {
                            var dir = dirObj.ToString();
                            var exit = currentRoom.Exits.FirstOrDefault(e => string.Equals(NormalizeDirection(e.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase));
                            if (exit != null && exit.DoorId == doorId)
                                exit.DoorFlags = new List<string>(flags);

                            // Update mate exit
                            var mateRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == exit?.ToRoomID);
                            if (mateRoom != null && exit != null)
                            {
                                var opp = OppositeDirection(NormalizeDirection(dir));
                                var mateExit = mateRoom.Exits.FirstOrDefault(e => e.ToRoomID == currentRoom.RoomID && string.Equals(NormalizeDirection(e.Direction), opp, StringComparison.OrdinalIgnoreCase));
                                if (mateExit != null && mateExit.DoorId == doorId)
                                    mateExit.DoorFlags = new List<string>(flags);
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    // Broadcast room updates
                    try
                    {
                        BroadcastRoomUpdates(player.CurrentRoom, _world, _db);
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Flag '{removed}' removed.");
                }



                return null;
            }//End EditExit

            // --- Kill pending action ---
            if (pa.Command == "Kill")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                if (pa.Step == "SpecifyTarget")
                {
                    // input contains the target string the admin typed after the prompt
                    player.PendingAction = null!; // consume pending
                    return HandleKill(player, "kill " + (input ?? ""));
                }

                return null;
            }

            // --- EditArea pending action ---
            if (pa.Command == "EditArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Area editing ended.");
                }

                // Resolve area from pa.Data
                var area = _world.Areas?.FirstOrDefault(a => a?.AreaID == player.CurrentRoom?.AreaID);
                if (area == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Area not found.");
                }

                if (pa.Step == "SelectField")
                {
                    var choice = (input ?? "").Trim();
                    if (choice.Equals("1") || choice.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System($"Current name is '{area.Name}'. Enter new name (or 'EndEdit'):");
                    }
                    if (choice.Equals("2") || choice.Equals("IsPvPAllowed", StringComparison.OrdinalIgnoreCase) || choice.Equals("PvP", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditPvP";
                        return MessageHandler.System($"Current IsPvPAllowed is '{area.IsPvPAllowed}'. Enter 'true' or 'false' (or 'EndEdit'):");
                    }
                    if (choice.Equals("3") || choice.Equals("Climate", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditClimate";
                        return MessageHandler.System($"Current Climate is '{area.Climate}'. Enter new climate (e.g., Temperate, Tropical, Arid, Polar, Swampland, Highland, Indoor) or 'EndEdit':");
                    }
                    if (choice.Equals("4") || choice.Equals("Owner", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditOwner";
                        return MessageHandler.System($"Current Owner is '{area.Owner}'. Enter new owner (or 'EndEdit'):");
                    }
                    if (choice.Equals("5") || choice.Equals("MaxNpcs", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditMaxNpcs";
                        return MessageHandler.System($"Current MaxNpcs is '{area.MaxNpcs}'. Enter new max NPCs (or 'EndEdit'):");
                    }

                    return MessageHandler.System("Invalid option. Choose 1..5 or type the field name, or 'EndEdit'.");
                }

                if (pa.Step == "EditName")
                {
                    var newName = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newName))
                        return MessageHandler.System("Name cannot be empty. Enter a valid name or 'EndEdit'.");

                    area.Name = newName;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET Name = @name WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@name", newName);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist Name for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save area name due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Area name updated to '{area.Name}'.");
                }

                if (pa.Step == "EditPvP")
                {
                    var val = (input ?? "").Trim().ToLowerInvariant();
                    if (val != "true" && val != "false" && val != "t" && val != "f")
                        return MessageHandler.System("Invalid value. Enter 'true' or 'false'.");

                    bool newVal = val == "true" || val == "t";
                    area.IsPvPAllowed = newVal;

                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET IsPvPAllowed = @pvp WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@pvp", newVal ? 1 : 0);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist IsPvPAllowed for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save PvP setting due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"IsPvPAllowed updated to '{area.IsPvPAllowed}'.");
                }

                if (pa.Step == "EditClimate")
                {
                    var newClimate = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newClimate))
                        return MessageHandler.System("Climate cannot be empty. Enter a valid climate or 'EndEdit'.");

                    area.Climate = newClimate;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET Climate = @climate WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@climate", newClimate);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist Climate for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save climate due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Climate updated to '{area.Climate}'.");
                }

                if (pa.Step == "EditOwner")
                {
                    var newOwner = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newOwner))
                        return MessageHandler.System("Owner cannot be empty. Enter a valid owner or 'EndEdit'.");

                    area.Owner = newOwner;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET Owner = @owner WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@owner", newOwner);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist Owner for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save owner due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Owner updated to '{area.Owner}'.");
                }

                if (pa.Step == "EditMaxNpcs")
                {
                    if (!int.TryParse((input ?? "").Trim(), out int newMax) || newMax < 1)
                        return MessageHandler.System("MaxNpcs must be an integer >= 1.");

                    area.MaxNpcs = newMax;
                    try
                    {
                        DbWriteQueue.Instance.RunWriteAndWait<object?>((wConn, wTx) =>
                        {
                            using var cmd = wConn.CreateCommand();
                            cmd.Transaction = wTx;
                            cmd.CommandText = "UPDATE Area SET MaxNpcs = @max WHERE AreaID = @aid;";
                            cmd.Parameters.AddWithValue("@max", newMax);
                            cmd.Parameters.AddWithValue("@aid", area.AreaID);
                            cmd.ExecuteNonQuery();
                            return (object?)null;
                        }, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditArea] Failed to persist MaxNpcs for AreaID={area.AreaID}: {ex.Message}");
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to save MaxNpcs due to a server error.");
                    }

                    player.PendingAction = null!;
                    return MessageHandler.System($"MaxNpcs updated to {area.MaxNpcs}.");
                }

                return null;
            } // end EditArea pending action

            // --- EditNpc pending action ---
            if (pa.Command == "EditNpc")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                // helper to resolve npc by id stored in pa.Data
                Npc ResolveNpcFromPa()
                {
                    try
                    {
                        if (pa.Data is Dictionary<string, object> dict && dict.TryGetValue("NpcId", out var nidObj) && int.TryParse(nidObj.ToString(), out var nid))
                        {
                            return _world.Npcs.FirstOrDefault(n => n != null && n.NpcID == nid);
                        }
                    }
                    catch { }
                    return null!;
                }

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    // If we were editing a specific npc, remove the admin edit interrupt (owner-scoped)
                    try
                    {
                        var maybe = ResolveNpcFromPa();
                        if (maybe != null) try { maybe.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System("NPC editing cancelled.");
                }

                // Step: selecting target from a prior presented list
                if (pa.Step == "SelectTarget")
                {
                    if (!int.TryParse(input, out var idx) || idx < 1)
                        return MessageHandler.System("Invalid selection. Type the number of the NPC to edit or 'EndEdit' to cancel.");

                    if (!(pa.Data is Dictionary<string, object> data) || !data.TryGetValue("Matches", out var matchesObj) || !(matchesObj is List<Npc> matches) || matches.Count == 0)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Pending selection list no longer available. Restart edit.");
                    }

                    if (idx > matches.Count)
                        return MessageHandler.System($"Invalid selection. Enter a number between 1 and {matches.Count}.");

                    var chosen = matches[idx - 1];
                    if (chosen == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Selected NPC no longer available.");
                    }

                    // Store selected npc id and present edit menu
                    var dict = new Dictionary<string, object> { { "NpcId", chosen.NpcID } };
                    pa.Data = dict;
                    pa.Step = "SelectField";

                    // Add AdminEdit interrupt on chosen NPC so movement is suppressed while admin edits it.
                    try { chosen.AddInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name, note: "AdminEdit", ttl: null); } catch { }

                    string summary = $"Editing NPC: Template='{chosen.TemplateID}', Name='{chosen.Name}', Flags=[{string.Join(", ", chosen.Flags ?? new List<string>())}], AiBehaviors=[{(chosen.Template?.AiBehaviorConfigs != null ? string.Join(", ", chosen.Template.AiBehaviorConfigs.Select(b => b.Behavior)) : string.Join(", ", chosen.AiBehaviors ?? new List<string>()))}]";
                    var menu = summary + "\n\nChoose a field to edit:\n1) NPC Name\n2) NPC Flags\n3) NPC AiBehaviors\n4) Visual Trait\n5) Buffs/Debuffs\n6) Hunger\n7) WanderAreas\n8) Toggle NoAttack flag\nType the option number or name, or 'EndEdit' to finish.";
                    return MessageHandler.System(menu);
                }

                // Step: choose which field to edit
                if (pa.Step == "SelectField")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("NPC not found. It may have been removed.");
                    }

                    var choice = (input ?? "").Trim();
                    if (choice.Equals("1") || choice.Equals("NPC Name", StringComparison.OrdinalIgnoreCase) || choice.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System($"Current name is '{npc.Name}'. Enter new name (or 'EndEdit'):");
                    }
                    if (choice.Equals("2") || choice.Equals("NPC Flags", StringComparison.OrdinalIgnoreCase) || choice.Equals("Flags", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditFlags";
                        // present flags numbered
                        var flags = npc.Flags ?? new List<string>();
                        if (flags.Count == 0)
                            return MessageHandler.System("NPC has no flags. Type 'add <flag>' to add one, or 'EndEdit' to cancel.");
                        var list = string.Join("\n", flags.Select((f, i) => $"{i + 1}) {f}"));
                        return MessageHandler.System($"Current NPC Flags:\n{list}\n\nCommands: 'add <flag>' or 'remove <n>' or 'edit <n> <newflag>' or 'EndEdit'.");
                    }
                    if (choice.Equals("3") || choice.Equals("NPC AiBehaviors", StringComparison.OrdinalIgnoreCase) || choice.Equals("AiBehaviors", StringComparison.OrdinalIgnoreCase) || choice.Equals("AI", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditAi";
                        var behaviors = npc.Template?.AiBehaviorConfigs != null && npc.Template.AiBehaviorConfigs.Count > 0
                            ? npc.Template.AiBehaviorConfigs.Select(b => b.Behavior).ToList()
                            : (npc.AiBehaviors ?? new List<string>());
                        if (behaviors == null || behaviors.Count == 0)
                            return MessageHandler.System("NPC has no AI behaviors. Type 'add <behavior>' to add one, or 'EndEdit' to cancel.");
                        var list = string.Join("\n", behaviors.Select((b, i) => $"{i + 1}) {b}"));
                        return MessageHandler.System($"Current AI Behaviors:\n{list}\n\nCommands: 'add <behavior>' or 'remove <n>' or 'edit <n> <newbehavior>' or 'EndEdit'.");
                    }
                    if (choice.Equals("4") || choice.Equals("Visual Trait", StringComparison.OrdinalIgnoreCase) || choice.Equals("VisualTrait", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditVisual";
                        var current = string.IsNullOrWhiteSpace(npc.VisualTrait) ? "(none)" : npc.VisualTrait;
                        return MessageHandler.System($"Current visual trait is: {current}\nEnter new visual trait (or 'clear' to remove, 'EndEdit' to cancel):");
                    }
                    if (choice.Equals("5") || choice.Equals("Buffs", StringComparison.OrdinalIgnoreCase) || choice.Equals("Debuffs", StringComparison.OrdinalIgnoreCase) || choice.Equals("Buffs/Debuffs", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditEffects";
                        var buffs = npc.Buffs ?? new List<string>();
                        var debuffs = npc.Debuffs ?? new List<string>();
                        var all = new List<string>();
                        all.AddRange(buffs.Select(b => $"Buff: {b}"));
                        all.AddRange(debuffs.Select(d => $"Debuff: {d}"));
                        var text = all.Count == 0 ? "(none)" : string.Join("\n", all.Select((s, i) => $"{i + 1}) {s}"));
                        return MessageHandler.System($"Current Buffs/Debuffs:\n{text}\n\nCommands: 'add buff <token>', 'add debuff <token>', 'remove buff <token>', 'remove debuff <token>' or 'EndEdit'.");
                    }
                    if (choice.Equals("6") || choice.Equals("Hunger", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditHunger";
                        return MessageHandler.System($"Current Hunger is {npc.Hunger}. Enter new hunger value (0-10) or 'EndEdit':");
                    }
                    if (choice.Equals("7") || choice.Equals("WanderAreas", StringComparison.OrdinalIgnoreCase) || choice.Equals("Wander", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current wander areas and transition to WanderAreas step
                        var areas = npc.WanderAreas ?? new List<int>();
                        string list = areas.Count == 0 ? "(none)" : string.Join(" ", areas.Select(a => $"[{a}]"));
                        pa.Step = "WanderAreas";
                        // Ensure NpcId remains present in pa.Data
                        try
                        {
                            if (!(pa.Data is Dictionary<string, object> dict)) pa.Data = new Dictionary<string, object> { { "NpcId", npc.NpcID } };
                            else dict["NpcId"] = npc.NpcID;
                        }
                        catch { pa.Data = new Dictionary<string, object> { { "NpcId", npc.NpcID } }; }

                        return MessageHandler.System($"Current wander areas are: {list}\nWould you like to Add, or Remove an AreaID? Type: 'Add [AreaID]' or 'Remove [AreaID]', or 'EndEdit' to cancel.");
                    }
                    if (choice.Equals("8") || choice.Equals("NoAttack", StringComparison.OrdinalIgnoreCase))
                    {
                        var hasFlag = npc.Flags != null &&
                            npc.Flags.Any(f => string.Equals(f, "NoAttack", StringComparison.OrdinalIgnoreCase));

                        if (hasFlag)
                        {
                            npc.Flags.RemoveAll(f => string.Equals(f, "NoAttack", StringComparison.OrdinalIgnoreCase));
                            try { MudPersistence.NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN] UpdateNpc failed: {ex.Message}"); }

                            // Redisplay menu after toggle
                            var flagSummary = $"NPC: Template='{npc.TemplateID}', Name='{npc.Name}', Flags=[{string.Join(", ", npc.Flags ?? new List<string>())}]";
                            return MessageHandler.System($"NoAttack flag removed from {npc.DisplayName}.\n\n{flagSummary}\n\nChoose a field to edit:\n1) NPC Name\n2) NPC Flags\n3) NPC AiBehaviors\n4) Visual Trait\n5) Buffs/Debuffs\n6) Hunger\n7) WanderAreas\n8) Toggle NoAttack flag\nType the option number or name, or 'EndEdit' to finish.");
                        }
                        else
                        {
                            if (npc.Flags == null) npc.Flags = new List<string>();
                            npc.Flags.Add("NoAttack");
                            try { MudPersistence.NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN] UpdateNpc failed: {ex.Message}"); }

                            // Redisplay menu after toggle
                            var flagSummary = $"NPC: Template='{npc.TemplateID}', Name='{npc.Name}', Flags=[{string.Join(", ", npc.Flags ?? new List<string>())}]";
                            return MessageHandler.System($"NoAttack flag added to {npc.DisplayName}. This NPC cannot now be attacked.\n\n{flagSummary}\n\nChoose a field to edit:\n1) NPC Name\n2) NPC Flags\n3) NPC AiBehaviors\n4) Visual Trait\n5) Buffs/Debuffs\n6) Hunger\n7) WanderAreas\n8) Toggle NoAttack flag\nType the option number or name, or 'EndEdit' to finish.");
                        }
                    }

                    return MessageHandler.System("Invalid option. Choose 1..8 or type the field name, or 'EndEdit'.");
                }

                // Step: Edit Name
                if (pa.Step == "EditName")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    var newName = (input ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newName)) return MessageHandler.System("Name cannot be empty. Enter a valid name or 'EndEdit'.");

                    npc.Name = newName;
                    try
                    {
                        NpcPersistence.UpdateNpc(_db, npc);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN][EditNpc] Failed to persist name for NpcID={npc.NpcID}: {ex.Message}");
                        return MessageHandler.System("Failed to save NPC name due to a server error.");
                    }

                    // Remove admin-edit interrupt now that admin completed editing
                    try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                    // Notify room occupants
                    try
                    {
                        var room = npc.CurrentRoom;
                        if (room != null)
                        {
                            foreach (var p in room.Players)
                                p.PendingMessageQueue.Add(MessageHandler.Room(room, room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(), room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), _world));
                        }
                    }
                    catch { }

                    player.PendingAction = null!;
                    return MessageHandler.System($"NPC name updated to '{npc.Name}'.");
                }

                // Step: Edit Flags
                if (pa.Step == "EditFlags")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    // add <flag>
                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var flag = cmd.Substring(4).Trim();
                        if (string.IsNullOrWhiteSpace(flag)) return MessageHandler.System("Usage: add <flag>");
                        if (npc.Flags == null) npc.Flags = new List<string>();
                        if (!npc.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)))
                            npc.Flags.Add(flag);

                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Added flag '{flag}' to NPC.");
                    }

                    // remove <n> or remove <flag>
                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = cmd.Substring(7).Trim();
                        if (int.TryParse(arg, out var idx))
                        {
                            var flags = npc.Flags ?? new List<string>();
                            if (idx < 1 || idx > flags.Count) return MessageHandler.System($"Invalid index. Enter 1..{flags.Count} or 'remove <flag>'.");
                            var removed = flags[idx - 1];
                            flags.RemoveAt(idx - 1);
                            npc.Flags = flags;
                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed flag '{removed}'.");
                        }
                        else
                        {
                            var flags = npc.Flags ?? new List<string>();
                            var found = flags.FirstOrDefault(f => string.Equals(f, arg, StringComparison.OrdinalIgnoreCase));
                            if (found == null) return MessageHandler.System($"Flag '{arg}' not found on NPC.");
                            flags.RemoveAll(f => string.Equals(f, arg, StringComparison.OrdinalIgnoreCase));
                            npc.Flags = flags;
                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed flag '{arg}'.");
                        }
                    }

                    // edit <n> <newflag>
                    if (cmd.StartsWith("edit ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = cmd.Substring(5).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: edit <n> <newflag>");
                        if (!int.TryParse(parts[0], out var idx)) return MessageHandler.System("Usage: edit <n> <newflag>");
                        var flags = npc.Flags ?? new List<string>();
                        if (idx < 1 || idx > flags.Count) return MessageHandler.System($"Invalid index. Enter 1..{flags.Count}.");
                        flags[idx - 1] = parts[1].Trim();
                        npc.Flags = flags;
                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist flags edit for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC flags."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Updated flag {idx} to '{parts[1].Trim()}'.");
                    }

                    return MessageHandler.System("Invalid flags command. Use 'add <flag>', 'remove <n>' or 'remove <flag>' or 'edit <n> <newflag>' or 'EndEdit'.");
                }

                // Step: Edit AI behaviors
                if (pa.Step == "EditAi")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    // Behaviors list - support editing template-based AiBehaviorConfigs only as simple string fallback
                    var behaviors = npc.Template?.AiBehaviorConfigs != null && npc.Template.AiBehaviorConfigs.Count > 0
                        ? npc.Template.AiBehaviorConfigs.Select(b => b.Behavior).ToList()
                        : (npc.AiBehaviors ?? new List<string>());

                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var b = cmd.Substring(4).Trim();
                        if (string.IsNullOrWhiteSpace(b)) return MessageHandler.System("Usage: add <behavior>");
                        if (npc.AiBehaviors == null) npc.AiBehaviors = new List<string>();
                        npc.AiBehaviors.Add(b);
                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Added AI behavior '{b}'.");
                    }

                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = cmd.Substring(7).Trim();
                        if (int.TryParse(arg, out var idx))
                        {
                            var list = npc.AiBehaviors ?? new List<string>();
                            if (idx < 1 || idx > list.Count) return MessageHandler.System($"Invalid index. Enter 1..{list.Count}.");
                            var removed = list[idx - 1];
                            list.RemoveAt(idx - 1);
                            npc.AiBehaviors = list;
                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed AI behavior '{removed}'.");
                        }
                        else
                        {
                            var list = npc.AiBehaviors ?? new List<string>();
                            if (!list.RemoveAll(s => string.Equals(s, arg, StringComparison.OrdinalIgnoreCase)).Equals(0))
                            {
                                try { NpcPersistence.UpdateNpc(_db, npc); }
                                catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                                // Remove admin-edit interrupt now that admin completed editing
                                try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                                player.PendingAction = null!;
                                return MessageHandler.System($"Removed AI behavior '{arg}'.");
                            }
                            return MessageHandler.System($"Behavior '{arg}' not found.");
                        }
                    }

                    if (cmd.StartsWith("edit ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = cmd.Substring(5).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: edit <n> <newbehavior>");
                        if (!int.TryParse(parts[0], out var idx)) return MessageHandler.System("Usage: edit <n> <newbehavior>");
                        var list = npc.AiBehaviors ?? new List<string>();
                        if (idx < 1 || idx > list.Count) return MessageHandler.System($"Invalid index. Enter 1..{list.Count}.");
                        list[idx - 1] = parts[1].Trim();
                        npc.AiBehaviors = list;
                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist ai edit for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC behaviors."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Updated AI behavior {idx} to '{parts[1].Trim()}'.");
                    }

                    return MessageHandler.System("Invalid AI command. Use 'add <behavior>', 'remove <n>', 'edit <n> <newbehavior>' or 'EndEdit'.");
                }

                // Step: Edit Visual Trait
                if (pa.Step == "EditVisual")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    if (string.Equals(cmd, "clear", StringComparison.OrdinalIgnoreCase))
                        npc.VisualTrait = "";
                    else
                        npc.VisualTrait = cmd;

                    try { NpcPersistence.UpdateNpc(_db, npc); }
                    catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist visual trait for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save visual trait."); }

                    // Remove admin-edit interrupt now that admin completed editing
                    try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                    // Notify room occupants
                    try
                    {
                        var room = npc.CurrentRoom;
                        if (room != null)
                        {
                            foreach (var p in room.Players)
                                p.PendingMessageQueue.Add(MessageHandler.Room(room, room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(), room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(), _world));
                        }
                    }
                    catch { }

                    player.PendingAction = null!;
                    return MessageHandler.System($"Visual trait updated to '{npc.VisualTrait}'.");
                }

                // Step: Edit Buffs/Debuffs
                if (pa.Step == "EditEffects")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    // add buff <token> / add debuff <token>
                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = cmd.Substring(4).Trim();
                        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: add buff <token> or add debuff <token>");
                        var which = parts[0].Trim().ToLowerInvariant();
                        var token = parts[1].Trim();
                        if (which == "buff")
                        {
                            if (npc.Buffs == null) npc.Buffs = new List<string>();
                            npc.Buffs.Add(token);
                        }
                        else if (which == "debuff")
                        {
                            if (npc.Debuffs == null) npc.Debuffs = new List<string>();
                            npc.Debuffs.Add(token);
                        }
                        else return MessageHandler.System("Specify 'buff' or 'debuff'. Usage: add buff <token>");

                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist effects add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC effects."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Added {which} '{token}' to NPC.");
                    }

                    // remove buff <token> or remove debuff <token>
                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = cmd.Substring(7).Trim();
                        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) return MessageHandler.System("Usage: remove buff <token> or remove debuff <token>");
                        var which = parts[0].Trim().ToLowerInvariant();
                        var token = parts[1].Trim();
                        if (which == "buff")
                        {
                            npc.Buffs?.RemoveAll(b => string.Equals(b, token, StringComparison.OrdinalIgnoreCase));
                        }
                        else if (which == "debuff")
                        {
                            npc.Debuffs?.RemoveAll(d => string.Equals(d, token, StringComparison.OrdinalIgnoreCase));
                        }
                        else return MessageHandler.System("Specify 'buff' or 'debuff'. Usage: remove buff <token>");

                        try { NpcPersistence.UpdateNpc(_db, npc); }
                        catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist effects remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC effects."); }

                        // Remove admin-edit interrupt now that admin completed editing
                        try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Removed {which} '{token}' from NPC.");
                    }

                    return MessageHandler.System("Invalid effects command. Use 'add buff <token>', 'add debuff <token>', 'remove buff <token>', 'remove debuff <token>' or 'EndEdit'.");
                }

                // Step: Edit Hunger
                if (pa.Step == "EditHunger")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }
                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase)) { player.PendingAction = null!; return MessageHandler.System("Edit cancelled."); }

                    if (!int.TryParse(cmd, out var val) || val < 0 || val > 10)
                        return MessageHandler.System("Invalid hunger. Enter an integer between 0 and 10.");

                    npc.Hunger = val;
                    try { NpcPersistence.UpdateNpc(_db, npc); }
                    catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist hunger for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC hunger."); }

                    // Remove admin-edit interrupt now that admin completed editing
                    try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                    player.PendingAction = null!;
                    return MessageHandler.System($"NPC Hunger set to {npc.Hunger}.");
                }

                // Step: WanderAreas - accept "Add <AreaID>" or "Remove <AreaID>"
                if (pa.Step == "WanderAreas")
                {
                    var npc = ResolveNpcFromPa();
                    if (npc == null) { player.PendingAction = null!; return MessageHandler.System("NPC not found."); }

                    var cmd = (input ?? "").Trim();
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase) || string.Equals(cmd, "cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return MessageHandler.System("Usage: Add <AreaID> or Remove <AreaID>");

                    var verb = parts[0].Trim().ToLowerInvariant();
                    if (!int.TryParse(parts[1], out int areaId) || areaId <= 0)
                        return MessageHandler.System("Invalid AreaID. Provide a positive integer.");

                    try
                    {
                        if (npc.WanderAreas == null) npc.WanderAreas = new List<int>();

                        if (verb == "add")
                        {
                            if (!npc.WanderAreas.Contains(areaId))
                                npc.WanderAreas.Add(areaId);
                            else
                                return MessageHandler.System($"AreaID {areaId} is already present in NPC wander areas.");

                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist WanderAreas add for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC wander areas."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            var list = string.Join(" ", npc.WanderAreas.Select(a => $"[{a}]"));
                            return MessageHandler.System($"Added AreaID {areaId}. Current wander areas: {list}");
                        }
                        else if (verb == "remove" || verb == "rm")
                        {
                            if (!npc.WanderAreas.Contains(areaId))
                                return MessageHandler.System($"AreaID {areaId} is not present in NPC wander areas.");

                            npc.WanderAreas.RemoveAll(a => a == areaId);

                            try { NpcPersistence.UpdateNpc(_db, npc); }
                            catch (Exception ex) { Console.WriteLine($"[ADMIN][EditNpc] Failed to persist WanderAreas remove for NpcID={npc.NpcID}: {ex.Message}"); return MessageHandler.System("Failed to save NPC wander areas."); }

                            // Remove admin-edit interrupt now that admin completed editing
                            try { npc.RemoveInterrupt(Npc.InterruptType.AdminEdit, owner: player.Name); } catch { }

                            player.PendingAction = null!;
                            var list = npc.WanderAreas.Count == 0 ? "(none)" : string.Join(" ", npc.WanderAreas.Select(a => $"[{a}]"));
                            return MessageHandler.System($"Removed AreaID {areaId}. Current wander areas: {list}");
                        }
                        else
                        {
                            return MessageHandler.System("Unknown action. Use 'Add <AreaID>' or 'Remove <AreaID>'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        player.PendingAction = null!;
                        Console.WriteLine($"[ADMIN][EditNpc] Error processing WanderAreas edit: {ex.Message}");
                        return MessageHandler.System("Failed to update wander areas due to a server error.");
                    }
                }

                return null;
            } // end EditNpc pending action

            // EditRoom pending action
            if (pa.Command == "EditRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                var room = player.CurrentRoom;

                if (input.Equals("EndEdit", StringComparison.OrdinalIgnoreCase))
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Room editing ended.");
                }

                if (pa.Step == "SelectField")
                {
                    if (input == "1" || input.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "EditName";
                        return MessageHandler.System("Enter new name for this room:");
                    }
                    if (input == "2" || input.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current description to make editing easier, then prompt for new value.
                        pa.Step = "EditDescription";
                        var current = string.IsNullOrWhiteSpace(room?.Description) ? "(no current description)" : room.Description;
                        return MessageHandler.System($"Current description is:\n{current}\n\nEnter new description for this room:");
                    }
                    // NEW: allow editing distant description
                    if (input == "3" || input.Equals("DistantDescription", StringComparison.OrdinalIgnoreCase))
                    {
                        // Show current distant description to make editing easier, then prompt for new value.
                        pa.Step = "EditDistantDescription";
                        var currentD = string.IsNullOrWhiteSpace(room?.DistantDescription) ? "(no current distant description)" : room.DistantDescription;
                        return MessageHandler.System($"Current distant description is:\n{currentD}\n\nEnter new distant description for this room:");
                    }

                    // NEW: LLM generation option
                    if (input == "4" || input.Equals("GenerateDescription", StringComparison.OrdinalIgnoreCase) ||
                        input.Equals("Generate Description", StringComparison.OrdinalIgnoreCase) ||
                        input.Equals("Generate Description from Prompt", StringComparison.OrdinalIgnoreCase))
                    {
                        // Transition to collecting operator keywords for AI generation
                        pa.Step = "CollectKeywords";
                        // Store room id for reference in pa.Data
                        pa.Data = new Dictionary<string, object> { { "RoomID", room?.RoomID ?? 0 } };
                        return MessageHandler.System("Enter comma-separated keywords to guide the AI (or type 'none' to skip). Type 'EndEdit' to cancel.");
                    }

                    // NEW: SetRoomFlag option (accept '5' or 'SetRoomFlag')
                    if (input == "5" || input.Equals("SetRoomFlag", StringComparison.OrdinalIgnoreCase))
                    {
                        pa.Step = "SetRoomFlag";
                        var flags = room.Flags ?? new List<string>();
                        if (flags.Count == 0)
                            return MessageHandler.System("Room has no flags. Type 'add <flag>' to add one, or 'EndEdit' to cancel.");
                        var list = string.Join("\n", flags.Select((f, i) => $"{i + 1}) {f}"));
                        return MessageHandler.System($"Current Room Flags:\n{list}\n\nCommands: 'add <flag>' or 'remove <n>' or 'edit <n> <newflag>' or 'EndEdit'.");
                    }

                    return MessageHandler.System("Invalid option. Type Name, Description, DistantDescription, GenerateDescription, SetRoomFlag, 1, 2, 3, 4, 5, or EndEdit.");
                }

                if (pa.Step == "EditName")
                {
                    room.Name = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System($"Room name updated to '{room.Name}'.");
                }

                if (pa.Step == "EditDescription")
                {
                    room.Description = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room description updated.");
                }

                /// handle generate-from-prompt flow - NEW: CollectKeywords -> GenerateDescriptionPreview -> GenerateDescriptionConfirm
                if (pa.Step == "CollectKeywords")
                {
                    // input contains comma-separated keywords (or 'none')
                    var keywordsRaw = (input ?? "").Trim();
                    string[] keywordArray;
                    if (string.Equals(keywordsRaw, "none", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(keywordsRaw))
                        keywordArray = new string[0];
                    else
                    {
                        keywordArray = keywordsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(12).ToArray();
                    }

                    // save into pa.Data dictionary
                    try
                    {
                        if (!(pa.Data is Dictionary<string, object> dict)) dict = new Dictionary<string, object>();
                        dict["Keywords"] = keywordArray;
                        pa.Data = dict;
                    }
                    catch { pa.Data = new Dictionary<string, object> { { "Keywords", keywordArray } }; }

                    // Build structured payload for LLM
                    var lmUrl = Environment.GetEnvironmentVariable("LMSTUDIO_URL") ?? "http://127.0.0.1:8000/";
                    var lmModel = Environment.GetEnvironmentVariable("LMSTUDIO_MODEL") ?? "default";
                    var systemPrompt = Environment.GetEnvironmentVariable("LMSTUDIO_SYSTEM_PROMPT") ??
                        "You are a concise immersive worldbuilding assistant for a text MUD. Produce exactly one JSON object with keys 'Description' and 'DistantDescription'. Do not include any extra text. Use neutral, evocative language; no markup; avoid profanity. Description should be 60-800 characters. DistantDescription should be one or two brief sentences.";

                    try
                    {
                        // Assemble exits info
                        var exitsList = new List<string>();
                        try
                        {
                            if (room != null && room.Exits != null)
                            {
                                foreach (var ex in room.Exits)
                                {
                                    // toRoomName best-effort
                                    var toRoom = _world.Rooms.FirstOrDefault(r => r.RoomID == ex.ToRoomID);
                                    var toName = toRoom?.Name ?? "";
                                    var dirWord = Movement.DirectionDisplayName(ex.Direction);
                                    var label = ex.DiscoverKeyword ?? "";
                                    // e.g. north: the gate -> Gatehouse
                                    var piece = $"{dirWord}:{(string.IsNullOrWhiteSpace(label) ? ex.Direction : label)}->{toName}";
                                    exitsList.Add(piece);
                                }
                            }
                        }
                        catch { /* best-effort */ }

                        var userBuilder = new System.Text.StringBuilder();
                        userBuilder.AppendLine($"Title: {room?.Name ?? "Unknown"}");
                        userBuilder.AppendLine($"Exits: {string.Join(", ", exitsList)}");
                        if (keywordArray.Length > 0) userBuilder.AppendLine($"Keywords: {string.Join(", ", keywordArray)}");
                        userBuilder.AppendLine($"ExistingDescription: {room?.Description ?? ""}");
                        userBuilder.AppendLine();
                        userBuilder.AppendLine("Produce a JSON object exactly like: {\"Description\":\"<full text>\",\"DistantDescription\":\"<short text>\"} with Description and DistantDescription only.");

                        using var lmClient = new MudServer.Integrations.LmStudioClient(lmUrl);
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));

                        var messages = new List<(string role, string content)>
            {
                ("system", systemPrompt),
                ("user", userBuilder.ToString())
            };

                        using var respDoc = lmClient.CreateChatCompletionAsync(lmModel, messages, temperature: 0.7, ct: cts.Token)
                                                   .GetAwaiter().GetResult();

                        string generated = "";

                        try
                        {
                            var root = respDoc.RootElement;

                            // chat/completions: choices[0].message.content (preferred)
                            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
                            {
                                var first = choices[0];
                                if (first.TryGetProperty("message", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.Object && msg.TryGetProperty("content", out var content))
                                {
                                    generated = content.GetString() ?? "";
                                }
                                else if (first.TryGetProperty("text", out var textProp))
                                {
                                    generated = textProp.GetString() ?? "";
                                }
                            }

                            // fallback: responses style
                            if (string.IsNullOrWhiteSpace(generated) && root.TryGetProperty("output_text", out var outText) && outText.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                generated = outText.GetString() ?? "";
                            }

                            if (string.IsNullOrWhiteSpace(generated) && root.TryGetProperty("output", out var outNode))
                            {
                                if (outNode.ValueKind == System.Text.Json.JsonValueKind.String)
                                    generated = outNode.GetString() ?? "";
                                else if (outNode.ValueKind == System.Text.Json.JsonValueKind.Array && outNode.GetArrayLength() > 0)
                                    generated = string.Join("\n", outNode.EnumerateArray().Select(e => e.GetString() ?? ""));
                            }
                        }
                        catch (Exception exParse)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse LM response container: {exParse.Message}");
                            generated = "";
                        }

                        generated = (generated ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(generated))
                        {
                            return MessageHandler.System("LLM returned empty description. Try different keywords or try again later.");
                        }

                        // Attempt to extract JSON object from generated string
                        string jsonText = generated;
                        try
                        {
                            // If output contains surrounding text, find first { and last } and parse substring
                            int firstIdx = jsonText.IndexOf('{');
                            int lastIdx = jsonText.LastIndexOf('}');
                            if (firstIdx >= 0 && lastIdx > firstIdx)
                                jsonText = jsonText.Substring(firstIdx, lastIdx - firstIdx + 1);

                            using var jd = JsonDocument.Parse(jsonText);
                            var root = jd.RootElement;

                            string desc = "";
                            string distant = "";

                            if (root.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String)
                                desc = d.GetString() ?? "";
                            if (root.TryGetProperty("DistantDescription", out var dd) && dd.ValueKind == JsonValueKind.String)
                                distant = dd.GetString() ?? "";

                            if (string.IsNullOrWhiteSpace(desc))
                            {
                                return MessageHandler.System("LLM returned JSON but Description was empty. Try different keywords.");
                            }

                            // store draft in pa.Data
                            if (!(pa.Data is Dictionary<string, object> dct)) dct = new Dictionary<string, object>();
                            dct["DraftDescription"] = desc.Trim();
                            dct["DraftDistant"] = distant.Trim();
                            dct["Keywords"] = keywordArray;
                            pa.Data = dct;
                            pa.Step = "GenerateDescriptionConfirm";

                            // Present preview and prompt for confirmation
                            var preview = $"Preview - Description:\n{desc}\n\nDistantDescription:\n{distant}\n\nType 'confirm' to save, 'edit' to enter new keywords, or 'cancel'.";
                            return MessageHandler.System(preview);
                        }
                        catch (Exception exJson)
                        {
                            Console.WriteLine($"[LLM][WARN] Failed to parse JSON from LM output: {exJson.Message}");
                            Console.WriteLine($"[LLM][WARN] Raw LM output: {generated}");
                            return MessageHandler.System("LLM returned output that could not be parsed as JSON. Try different keywords or try again later.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LLM] Generation failed: {ex.Message}");
                        return MessageHandler.System($"LLM generation failed: {ex.Message}");
                    }
                }

                if (pa.Step == "GenerateDescriptionConfirm")
                {
                    // Expecting confirm / edit / cancel
                    var dct = pa.Data as Dictionary<string, object>;
                    string draftDesc = dct != null && dct.TryGetValue("DraftDescription", out var ddObj) ? (ddObj as string ?? "") : "";
                    string draftDistant = dct != null && dct.TryGetValue("DraftDistant", out var drObj) ? (drObj as string ?? "") : "";

                    var cmd = (input ?? "").Trim().ToLowerInvariant();
                    if (cmd == "confirm")
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(draftDesc))
                            {
                                room.Description = draftDesc;
                                room.DistantDescription = draftDistant;
                                room.IsDirty = true;
                                _world.SaveDirtyRooms(_db);

                                // Broadcast updated room payloads to occupants
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world));
                                }

                                player.PendingAction = null!;
                                return MessageHandler.System("Generated description saved and broadcast to room.");
                            }
                            else
                            {
                                player.PendingAction = null!;
                                return MessageHandler.System("No draft description available to save.");
                            }
                        }
                        catch (Exception exSave)
                        {
                            Console.WriteLine($"[LLM][ERROR] Failed to save generated description: {exSave.Message}");
                            player.PendingAction = null!;
                            return MessageHandler.System("Failed to save generated description due to a server error.");
                        }
                    }
                    else if (cmd == "edit")
                    {
                        pa.Step = "CollectKeywords";
                        return MessageHandler.System("Enter comma-separated keywords to guide the AI (or 'none' to skip). Type 'EndEdit' to cancel.");
                    }
                    else if (cmd == "cancel" || cmd == "endedit")
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Generation cancelled.");
                    }
                    else
                    {
                        return MessageHandler.System("Invalid option. Type 'confirm' to save, 'edit' to re-enter keywords, or 'cancel'.");
                    }
                }

                // NEW: persist DistantDescription edits
                if (pa.Step == "EditDistantDescription")
                {
                    room.DistantDescription = input;
                    room.IsDirty = true;
                    _world.SaveDirtyRooms(_db);
                    player.PendingAction = null!;
                    return MessageHandler.System("Room distant description updated.");
                }

                // NEW: SetRoomFlag step (handles input after selecting option 5)
                if (pa.Step == "SetRoomFlag")
                {
                    var flags = room.Flags ?? new List<string>();
                    var cmd = (input ?? "").Trim();

                    // If no input yet, present the menu showing current flags
                    if (string.IsNullOrWhiteSpace(cmd))
                    {
                        if (flags.Count == 0)
                            return MessageHandler.System("Room has no flags. Type 'add <flag>' to add one, or 'EndEdit' to cancel.");
                        var list = string.Join("\n", flags.Select((f, i) => $"{i + 1}) {f}"));
                        return MessageHandler.System($"Current Room Flags:\n{list}\n\nCommands: 'add <flag>' or 'remove <n>' or 'edit <n> <newflag>' or 'EndEdit'.");
                    }

                    
                    if (string.Equals(cmd, "EndEdit", StringComparison.OrdinalIgnoreCase))
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Edit cancelled.");
                    }

                    // add <flag>
                    if (cmd.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        var flag = cmd.Substring(4).Trim();
                        if (string.IsNullOrWhiteSpace(flag))
                            return MessageHandler.System("Usage: add <flag>");

                        if (room.Flags == null)
                            room.Flags = new List<string>();

                        if (!room.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)))
                            room.Flags.Add(flag);

                        try
                        {
                            var fj = JsonSerializer.Serialize(room.Flags);
                            var prop = room.GetType().GetProperty("FlagsJson");
                            if (prop != null && prop.CanWrite)
                                prop.SetValue(room, fj);
                        }
                        catch { /* best-effort */ }

                        room.IsDirty = true;
                        _world.SaveDirtyRooms(_db);

                        try
                        {
                            foreach (var p in room.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    room,
                                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world, _db));
                            }
                        }
                        catch { /* best-effort */ }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Flag '{flag}' added to room.");
                    }

                    // remove <n> or remove <flag>
                    if (cmd.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = cmd.Substring(7).Trim();
                        if (int.TryParse(arg, out var idx))
                        {
                            if (idx < 1 || idx > flags.Count)
                                return MessageHandler.System($"Invalid index. Enter 1..{flags.Count} or 'remove <flag>'.");
                            var removed = flags[idx - 1];
                            flags.RemoveAt(idx - 1);
                            room.Flags = flags;

                            try
                            {
                                var fj = JsonSerializer.Serialize(room.Flags);
                                var prop = room.GetType().GetProperty("FlagsJson");
                                if (prop != null && prop.CanWrite)
                                    prop.SetValue(room, fj);
                            }
                            catch { /* best-effort */ }

                            room.IsDirty = true;
                            _world.SaveDirtyRooms(_db);

                            try
                            {
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world, _db));
                                }
                            }
                            catch { /* best-effort */ }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed flag '{removed}'.");
                        }
                        else
                        {
                            var found = flags.FirstOrDefault(f => string.Equals(f, arg, StringComparison.OrdinalIgnoreCase));
                            if (found == null)
                                return MessageHandler.System($"Flag '{arg}' not found on room.");

                            flags.RemoveAll(f => string.Equals(f, arg, StringComparison.OrdinalIgnoreCase));
                            room.Flags = flags;

                            try
                            {
                                var fj = JsonSerializer.Serialize(room.Flags);
                                var prop = room.GetType().GetProperty("FlagsJson");
                                if (prop != null && prop.CanWrite)
                                    prop.SetValue(room, fj);
                            }
                            catch { /* best-effort */ }

                            room.IsDirty = true;
                            _world.SaveDirtyRooms(_db);

                            try
                            {
                                foreach (var p in room.Players)
                                {
                                    p.PendingMessageQueue.Add(MessageHandler.Room(
                                        room,
                                        room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                        room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                        _world, _db));
                                }
                            }
                            catch { /* best-effort */ }

                            player.PendingAction = null!;
                            return MessageHandler.System($"Removed flag '{arg}'.");
                        }
                    }

                    // edit <n> <newflag>
                    if (cmd.StartsWith("edit ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = cmd.Substring(5).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                            return MessageHandler.System("Usage: edit <n> <newflag>");
                        if (!int.TryParse(parts[0], out var idx))
                            return MessageHandler.System("Usage: edit <n> <newflag>");
                        if (idx < 1 || idx > flags.Count)
                            return MessageHandler.System($"Invalid index. Enter 1..{flags.Count}.");

                        flags[idx - 1] = parts[1].Trim();
                        room.Flags = flags;

                        try
                        {
                            var fj = JsonSerializer.Serialize(room.Flags);
                            var prop = room.GetType().GetProperty("FlagsJson");
                            if (prop != null && prop.CanWrite)
                                prop.SetValue(room, fj);
                        }
                        catch { /* best-effort */ }

                        room.IsDirty = true;
                        _world.SaveDirtyRooms(_db);

                        try
                        {
                            foreach (var p in room.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    room,
                                    room.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                    room.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world, _db));
                            }
                        }
                        catch { /* best-effort */ }

                        player.PendingAction = null!;
                        return MessageHandler.System($"Updated flag {idx} to '{parts[1].Trim()}'.");
                    }

                    return MessageHandler.System("Invalid flags command. Use 'add <flag>', 'remove <n>' or 'remove <flag>' or 'edit <n> <newflag>' or 'EndEdit'.");
                }

                return null;
            }//End EditRoom

            // CreateArea pending action
            if (pa.Command == "CreateArea")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var newArea = new Area(input, 0);

                try
                {
                    // capture current room id and normalized directions for the writer
                    int srcRoomId = player.CurrentRoom?.RoomID ?? 0;
                    string dirNorm = dir;
                    string oppDir = OppositeDirection(dirNorm);

                    // Perform both Area insert and starting Room creation inside single-writer so they are atomic and serialized.
                    var result = MudServer.DbWriteQueue.Instance.RunWriteAndWait<(int areaId, int roomId)>((wConn, wTx) =>
                    {
                        // Insert Area row
                        using var ins = wConn.CreateCommand();
                        ins.Transaction = wTx;
                        ins.CommandText = @"
                    INSERT INTO Area (Name, Description, WorldID, Theme, LevelRangeMin, LevelRangeMax, IsPvPAllowed, Owner)
                    VALUES (@name, @desc, @wid, @theme, @min, @max, @pvp, @owner);
                    SELECT last_insert_rowid();";
                        ins.Parameters.AddWithValue("@name", newArea.Name ?? "");
                        ins.Parameters.AddWithValue("@desc", newArea.Description ?? "");
                        ins.Parameters.AddWithValue("@wid", _world.WorldID);
                        ins.Parameters.AddWithValue("@theme", newArea.Theme ?? "Generic");
                        ins.Parameters.AddWithValue("@min", newArea.LevelRangeMin);
                        ins.Parameters.AddWithValue("@max", newArea.LevelRangeMax);
                        ins.Parameters.AddWithValue("@pvp", newArea.IsPvPAllowed ? 1 : 0);
                        ins.Parameters.AddWithValue("@owner", newArea.Owner ?? "System");

                        long newAreaIdLong = (long)ins.ExecuteScalar();
                        int newAreaId = (int)newAreaIdLong;
                        newArea.AreaID = newAreaId;

                        // Create a starting room for the area using the writer connection/tx (atomic)
                        var newRoom = new Room($"{input} - Undefined Room", "Starting room for the area")
                        {
                            AreaID = newArea.AreaID
                        };

                        int roomDbId = _world.CreateRoomInDB(newRoom, wConn, wTx);

                        // Persist two RoomExit rows linking srcRoom <-> newRoom so reverse exit exists in DB.
                        // Insert src -> new
                        using (var e1 = wConn.CreateCommand())
                        {
                            e1.Transaction = wTx;
                            e1.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e1.Parameters.AddWithValue("@rid", srcRoomId);
                            e1.Parameters.AddWithValue("@dir", dirNorm ?? "");
                            e1.Parameters.AddWithValue("@toRid", roomDbId);
                            e1.ExecuteNonQuery();
                        }

                        // Insert new -> src (reverse)
                        using (var e2 = wConn.CreateCommand())
                        {
                            e2.Transaction = wTx;
                            e2.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e2.Parameters.AddWithValue("@rid", roomDbId);
                            e2.Parameters.AddWithValue("@dir", oppDir ?? "");
                            e2.Parameters.AddWithValue("@toRid", srcRoomId);
                            e2.ExecuteNonQuery();
                        }

                        return (newAreaId, roomDbId);
                    }, timeout: TimeSpan.FromSeconds(10));

                    // Update in-memory world with new area and room
                    newArea.AreaID = result.areaId;

                    // Try to find the room instance that CreateRoomInDB likely added (writer may have added to _world.Rooms).
                    Room createdRoomInstance = _world.Rooms.FirstOrDefault(r => r.RoomID == result.roomId);
                    if (createdRoomInstance == null)
                    {
                        // If not present, create a local instance and add to world's Rooms.
                        createdRoomInstance = new Room($"{input} - Undefined Room", "Starting room for the area") { AreaID = newArea.AreaID, RoomID = result.roomId };
                        _world.Rooms.Add(createdRoomInstance);
                    }
                    else
                    {
                        // Ensure AreaID is set correctly
                        createdRoomInstance.AreaID = newArea.AreaID;
                    }

                    newArea.Rooms.Add(createdRoomInstance);
                    _world.Areas.Add(newArea);

                    var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                    //CODE REMOVED - 2025-12-21 12:00:00 // 'currentArea.Rooms.Add(createdRoomInstance);' // Rationale: Do not add the new-area room into the previous area's Rooms collection — this caused the client header to resolve the wrong area via Rooms.Contains(...) leading to a flicker.
                    // ensure we didn't accidentally duplicate the room into the wrong area's Rooms list

                    // Link exits in-memory using the authoritative room instance from world.Rooms
                    // ensure we don't duplicate exits
                    if (!player.CurrentRoom.Exits.Any(x => x.ToRoomID == createdRoomInstance.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase)))
                        player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, createdRoomInstance.RoomID));

                    if (!createdRoomInstance.Exits.Any(x => x.ToRoomID == player.CurrentRoom.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(OppositeDirection(dir)), StringComparison.OrdinalIgnoreCase)))
                        createdRoomInstance.Exits.Add(new Room.RoomExit(OppositeDirection(dir), player.CurrentRoom.RoomID));

                    player.CurrentRoom.IsDirty = true;
                    createdRoomInstance.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    player.PendingAction = null!;
                    return MessageHandler.System("Area created.");
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEAREA][ERROR] Failed to insert Area '{input}': {ex.Message}");
                    return MessageHandler.System($"Error creating area {input}: {ex.Message}");
                }
            }

            // CreateRoom pending action
            if (pa.Command == "CreateRoom")
            {
                if (!player.IsAdmin)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Permission denied. Admins only.");
                }

                string dir = pa.Direction;
                var currentArea = _world.Areas.FirstOrDefault(a => a.Rooms.Contains(player.CurrentRoom));
                if (currentArea == null)
                {
                    player.PendingAction = null!;
                    return MessageHandler.System("Current room is not in an area.");
                }

                var newRoom = new Room(input, "Newly created room")
                {
                    AreaID = currentArea.AreaID
                };

                try
                {
                    int srcRoomId = player.CurrentRoom?.RoomID ?? 0;
                    string dirNorm = dir;
                    string oppDir = OppositeDirection(dirNorm);

                    // Create the room using writer queue so the DB insert and related writes are serialized.
                    int createdRoomId = MudServer.DbWriteQueue.Instance.RunWriteAndWait<int>((wConn, wTx) =>
                    {
                        int id = _world.CreateRoomInDB(newRoom, wConn, wTx);

                        // Persist exits linking both sides inside writer tx
                        using (var e1 = wConn.CreateCommand())
                        {
                            e1.Transaction = wTx;
                            e1.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e1.Parameters.AddWithValue("@rid", srcRoomId);
                            e1.Parameters.AddWithValue("@dir", dirNorm ?? "");
                            e1.Parameters.AddWithValue("@toRid", id);
                            e1.ExecuteNonQuery();
                        }

                        using (var e2 = wConn.CreateCommand())
                        {
                            e2.Transaction = wTx;
                            e2.CommandText = @"
                        INSERT INTO RoomExit (RoomID, Direction, ToRoomID, IsHidden, DiscoverKeyword, DoorId)
                        VALUES (@rid, @dir, @toRid, 0, '', 0);";
                            e2.Parameters.AddWithValue("@rid", id);
                            e2.Parameters.AddWithValue("@dir", oppDir ?? "");
                            e2.Parameters.AddWithValue("@toRid", srcRoomId);
                            e2.ExecuteNonQuery();
                        }

                        return id;
                    }, timeout: TimeSpan.FromSeconds(10));

                    if (createdRoomId == 0)
                    {
                        player.PendingAction = null!;
                        return MessageHandler.System("Failed to create room in database.");
                    }

                    // Prefer to locate the authoritative room instance that may have been added by CreateRoomInDB.
                    Room createdRoomInstance = _world.Rooms.FirstOrDefault(r => r.RoomID == createdRoomId);
                    if (createdRoomInstance == null)
                    {
                        createdRoomInstance = new Room(input, "Newly created room") { AreaID = currentArea.AreaID, RoomID = createdRoomId };
                        _world.Rooms.Add(createdRoomInstance);
                    }
                    else
                    {
                        // ensure name/area set
                        createdRoomInstance.Name = input;
                        createdRoomInstance.AreaID = currentArea.AreaID;
                    }

                    currentArea.Rooms.Add(createdRoomInstance);

                    // Link exits in-memory (avoid duplicates)
                    if (!player.CurrentRoom.Exits.Any(x => x.ToRoomID == createdRoomInstance.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(dir), StringComparison.OrdinalIgnoreCase)))
                        player.CurrentRoom.Exits.Add(new Room.RoomExit(dir, createdRoomInstance.RoomID));

                    if (!createdRoomInstance.Exits.Any(x => x.ToRoomID == player.CurrentRoom.RoomID && string.Equals(NormalizeDirection(x.Direction), NormalizeDirection(oppDir), StringComparison.OrdinalIgnoreCase)))
                        createdRoomInstance.Exits.Add(new Room.RoomExit(oppDir, player.CurrentRoom.RoomID));

                    player.CurrentRoom.IsDirty = true;
                    createdRoomInstance.IsDirty = true;
                    _world.SaveDirtyRooms(_db);

                    // Broadcast updated room views so clients immediately see the new exit
                    try
                    {
                        var srcRoom = player.CurrentRoom;
                        if (srcRoom != null)
                        {
                            foreach (var p in srcRoom.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    srcRoom,
                                    srcRoom.Npcs.Select(n => MessageHandler.GetNpcDisplayName(n)).ToList(),
                                    srcRoom.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }

                        if (createdRoomInstance != null)
                        {
                            foreach (var p in createdRoomInstance.Players)
                            {
                                p.PendingMessageQueue.Add(MessageHandler.Room(
                                    createdRoomInstance,
                                    createdRoomInstance.Npcs.Select(n => n.DisplayName).ToList(),
                                    createdRoomInstance.Players.Where(pl => pl != p).Select(pl => pl.Name).ToList(),
                                    _world));
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    player.PendingAction = null!;
                    return MessageHandler.System("Room created.");
                }
                catch (Exception ex)
                {
                    player.PendingAction = null!;
                    Console.WriteLine($"[CREATEROOM][ERROR] Failed to create room '{input}': {ex.Message}");
                    return MessageHandler.System("Failed to create room due to a server error.");
                }
            }

            return null;
        } // end HandlePendingAction
    }
}
