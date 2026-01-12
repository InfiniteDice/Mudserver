using Microsoft.Data.Sqlite;
using MudCore.Entities;
using MudNetworking;
using MudPersistence;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MudCore.Actions
{
    /// <summary>
    /// Centralized lock & key mechanics.
    /// - Containers must include the 'Lockable' flag to be lockable.
    /// - Locks bind to a specific key instance via flag: "LockedByKey:<ItemID>"
    /// - Locking requires a key item (ItemCategory == "Key") and prompts the player for a label;
    ///   the key's Name is updated to the label and both items are persisted atomically.
    /// - Unlocking requires possessing the bound key instance.
    /// </summary>
    public static class KeyLocks
    {
        // Pending data used by CommandHandler when asking player to name a key.
        public class LockPendingData
        {
            public Item Container { get; set; } = null!;
            public Item Key { get; set; } = null!;
        }

        // Flag prefix used to bind a key instance to a container
        private const string LockedByKeyPrefix = "LockedByKey:";

        // Marker flag required on an item to allow locking
        private const string LockableFlag = "Lockable";

        // Detect whether an item/template explicitly supports locking
        public static bool IsLockable(Item item)
        {
            if (item == null) return false;
            if (item.Flags != null && item.Flags.Any(f => string.Equals(f, LockableFlag, StringComparison.OrdinalIgnoreCase))) return true;
            if (!string.IsNullOrEmpty(item.FlagsJson) && item.FlagsJson.IndexOf($"\"{LockableFlag}\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Return locked key item id if present, otherwise null
        public static int? GetLockedKeyItemId(Item container)
        {
            if (container == null) return null;
            var flags = container.Flags ?? new List<string>();
            foreach (var f in flags)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (f.StartsWith(LockedByKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = f.Substring(LockedByKeyPrefix.Length);
                    if (int.TryParse(rest, out var id)) return id;
                }
            }
            // fallback to FlagsJson inspection (best-effort)
            if (!string.IsNullOrEmpty(container.FlagsJson))
            {
                // naive parse: look for LockedByKey:<digits>
                var idx = container.FlagsJson.IndexOf(LockedByKeyPrefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = idx + LockedByKeyPrefix.Length;
                    int end = start;
                    while (end < container.FlagsJson.Length && char.IsDigit(container.FlagsJson[end])) end++;
                    var substr = container.FlagsJson.Substring(start, end - start);
                    if (int.TryParse(substr, out var id2)) return id2;
                }
            }
            return null;
        }

        public static bool IsLocked(Item container) => GetLockedKeyItemId(container).HasValue;

        // Return null if accessible, otherwise a user-friendly denial message.
        // Player is allowed if they hold the bound key instance.
        public static string? EnsureAccessible(SqliteConnection conn, Player player, Item container)
        {
            if (container == null) return "Invalid container.";
            var lockedId = GetLockedKeyItemId(container);
            if (!lockedId.HasValue) return null; // not locked — accessible

            // If player carries the key (by ItemID) they are allowed
            var hasKey = player?.Inventory?.Any(i => i != null && i.ItemID == lockedId.Value) ?? false;
            if (hasKey) return null;

            // Deny
            return $"{container.Name} is locked. Use `unlock {container.Name} with <key>`.";
        }

        // Lock the container with a specific key instance and label the key.
        // Returns a user-facing Message (success/failure).
        public static Message LockContainer(SqliteConnection conn, Player player, Item container, Item key, string label)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (container == null) return MessageHandler.System("Invalid container.");
            if (key == null) return MessageHandler.System("Invalid key.");
            if (string.IsNullOrWhiteSpace(label)) return MessageHandler.System("Key label cannot be empty.");

            if (!IsLockable(container))
                return MessageHandler.System($"You cannot lock {container.Name}.");

            if (IsLocked(container))
                return MessageHandler.System($"{container.Name} is already locked.");

            if (!string.Equals(key.ItemCategory ?? "", "Key", StringComparison.OrdinalIgnoreCase))
                return MessageHandler.System($"'{key.Name}' is not a key.");

            // Prevent self-lock: key inside the container
            if (string.Equals(key.OwnerType, "Item", StringComparison.OrdinalIgnoreCase) && key.OwnerID == container.ItemID)
                return MessageHandler.System("You cannot lock a container with a key that is inside it.");

            SqliteTransaction? tx = null;
            try
            {
                if (conn == null) return MessageHandler.System("Server DB unavailable.");
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();

                tx = conn.BeginTransaction();

                // update container flags -> add LockedByKey:<keyId>
                var cflags = container.Flags ?? new List<string>();
                cflags.RemoveAll(f => f != null && f.StartsWith(LockedByKeyPrefix, StringComparison.OrdinalIgnoreCase));
                cflags.Add($"{LockedByKeyPrefix}{key.ItemID}");
                container.Flags = cflags;

                // update key name label -> append " Key" unless user already included it
                var trimmed = label.Trim();
                string newName;
                if (trimmed.EndsWith("key", StringComparison.OrdinalIgnoreCase))
                    newName = trimmed; // user already included 'key'
                else
                    newName = $"{trimmed} Key";

                key.Name = newName;

                // Persist both atomically using the provided transaction
                ItemPersistence.SaveItem(conn, container, tx);
                ItemPersistence.SaveItem(conn, key, tx);

                tx.Commit();

                // Notify in-memory objects already updated
                return MessageHandler.System($"You lock {container.Name} with '{key.Name}'.");
            }
            catch (Exception ex)
            {
                try { tx?.Rollback(); } catch { /* best-effort */ }
                Console.WriteLine($"[LOCK] Exception locking container {container?.ItemID} with key {key?.ItemID}: {ex}");
                return MessageHandler.System("Failed to lock the container due to a server error.");
            }
            finally
            {
                try { tx?.Dispose(); } catch { /* best-effort */ }
                // DO NOT close the connection here — connection lifecycle is managed by caller or writer queue.
            }
        } // end LockContainer

        // Unlock the container using the provided key instance
        public static Message UnlockContainer(SqliteConnection conn, Player player, Item container, Item key)
        {
            if (player == null) return MessageHandler.System("Invalid player.");
            if (container == null) return MessageHandler.System("Invalid container.");
            if (key == null) return MessageHandler.System("Invalid key.");

            var lockedId = GetLockedKeyItemId(container);
            if (!lockedId.HasValue)
                return MessageHandler.System($"{container.Name} is not locked.");

            if (lockedId.Value != key.ItemID)
                return MessageHandler.System("That key does not match the lock.");

            SqliteTransaction? tx = null;
            try
            {
                if (conn == null) return MessageHandler.System("Server DB unavailable.");
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();

                tx = conn.BeginTransaction();

                // remove LockedByKey flag(s)
                var cflags = container.Flags ?? new List<string>();
                cflags.RemoveAll(f => f != null && f.StartsWith(LockedByKeyPrefix, StringComparison.OrdinalIgnoreCase));
                container.Flags = cflags;

                // If the key's name was the generated "<label> Key" form, revert it back to "Key".
                // Only change when the name ends with " Key" (case-insensitive) to avoid clobbering custom names.
                if (key != null && !string.IsNullOrEmpty(key.Name) &&
                    key.Name.EndsWith(" Key", StringComparison.OrdinalIgnoreCase))
                {
                    key.Name = "Key";
                    // Persist key change as part of same transaction so DB remains consistent.
                    ItemPersistence.SaveItem(conn, key, tx);
                }

                ItemPersistence.SaveItem(conn, container, tx);

                tx.Commit();

                return MessageHandler.System($"You unlock {container.Name} with '{key.Name}'.");
            }
            catch (Exception ex)
            {
                try { tx?.Rollback(); } catch { /* best-effort */ }
                Console.WriteLine($"[UNLOCK] Exception unlocking container {container?.ItemID} with key {key?.ItemID}: {ex}");
                return MessageHandler.System("Failed to unlock the container due to a server error.");
            }
            finally
            {
                try { tx?.Dispose(); } catch { /* best-effort */ }
                // DO NOT close the connection here — connection lifecycle is managed by caller or writer queue.
            }
        } // end UnlockContainer
    }
}