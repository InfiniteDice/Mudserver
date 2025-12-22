using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MudServer
{
    /// <summary>
    /// Single‑writer queue for serializing all DB write work on a dedicated background thread/connection.
    /// - Start(...) creates and configures a dedicated writer connection (WAL + pragmas).
    /// - EnqueueAsync/RunWriteAndWait allow callers to schedule work that receives the writer connection + transaction.
    /// - Jobs execute inside a transaction; commit or rollback is handled centrally.
    /// </summary>
    public sealed class DbWriteQueue : IDisposable
    {
        private static readonly Lazy<DbWriteQueue> _lazy = new(() => new DbWriteQueue());
        public static DbWriteQueue Instance => _lazy.Value;

        private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private readonly object _startLock = new();
        private string? _connectionString;
        private SqliteConnection? _writerConnection;

        private DbWriteQueue() { }

        /// <summary>
        /// Start the writer with a connection string (e.g. "Data Source=...").
        /// Idempotent; calling Start when already started is a no-op.
        /// </summary>
        public void Start(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));
            lock (_startLock)
            {
                if (_worker != null && !_worker.IsCompleted) return;

                _connectionString = connectionString;
                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => WorkerLoop(_cts.Token), CancellationToken.None);
            }
        }

        /// <summary>
        /// Enqueue a write that returns a value asynchronously.
        /// Caller should await the returned Task.
        /// </summary>
        public Task<T> EnqueueAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, TimeSpan? timeout = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            EnsureStarted();

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new Job
            {
                Work = async (conn, tx) => (object?)(await work(conn, tx).ConfigureAwait(false)),
                Completion = tcs
            };
            _queue.Add(job);
            return WaitForResultAsync<T>(tcs.Task, timeout);
        }

        /// <summary>
        /// Enqueue a write that does not return a value.
        /// </summary>
        public Task EnqueueAsync(Func<SqliteConnection, SqliteTransaction, Task> work, TimeSpan? timeout = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            EnsureStarted();

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new Job
            {
                Work = async (conn, tx) =>
                {
                    await work(conn, tx).ConfigureAwait(false);
                    return (object?)null;
                },
                Completion = tcs
            };
            _queue.Add(job);
            return WaitForResultAsync<object?>(tcs.Task, timeout);
        }

        /// <summary>
        /// Synchronous helper for legacy callers: enqueue and block waiting for completion.
        /// Throws on error or timeout.
        /// </summary>
        public T RunWriteAndWait<T>(Func<SqliteConnection, SqliteTransaction, T> work, TimeSpan? timeout = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            // wrap synchronous work into Task-returning delegate
            return EnqueueAsync<T>((conn, tx) =>
            {
                T res;
                try { res = work(conn, tx); }
                catch (Exception ex)
                { // rethrow inside task so queue handles it
                    var tcs = new TaskCompletionSource<T>();
                    tcs.SetException(ex);
                    return tcs.Task;
                }
                return Task.FromResult(res);
            }, timeout).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous helper for void work.
        /// </summary>
        public void RunWriteAndWait(Action<SqliteConnection, SqliteTransaction> work, TimeSpan? timeout = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            RunWriteAndWait<object?>((conn, tx) =>
            {
                work(conn, tx);
                return null;
            }, timeout);
        }

        /// <summary>
        /// Stop the writer and wait for the worker to finish processing queued work.
        /// </summary>
        public async Task StopAsync(TimeSpan? waitFor = null)
        {
            lock (_startLock)
            {
                if (_worker == null || _worker.IsCompleted) return;
                _cts?.Cancel();
                _queue.CompleteAdding();
            }

            if (_worker != null)
            {
                var toWait = waitFor ?? TimeSpan.FromSeconds(5);
                var finished = await Task.WhenAny(_worker, Task.Delay(toWait)).ConfigureAwait(false);
                // best-effort dispose writer connection
                try { _writerConnection?.Close(); _writerConnection?.Dispose(); } catch { }
            }
        }

        private async Task WorkerLoop(CancellationToken ct)
        {
            try
            {
                // create and configure dedicated writer connection
                _writerConnection = new SqliteConnection(_connectionString);
                _writerConnection.Open();
                // apply pragmas on writer connection
                try { DbPragmas.Configure(_writerConnection); } catch { /* best-effort */ }

                while (!_queue.IsCompleted && !ct.IsCancellationRequested)
                {
                    Job? job = null;
                    try
                    {
                        job = _queue.Take(ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (InvalidOperationException) { break; } // collection completed

                    if (job == null) continue;

                    try
                    {
                        using var tx = _writerConnection.BeginTransaction();
                        object? result = null;
                        try
                        {
                            var task = job.Work(_writerConnection, tx);
                            // Work delegate may return a completed task or async task
                            result = await task.ConfigureAwait(false);
                            tx.Commit();
                            job.Completion.SetResult(result);
                        }
                        catch (Exception ex)
                        {
                            try { tx.Rollback(); } catch { }
                            job.Completion.SetException(ex);
                        }
                    }
                    catch (Exception exOuter)
                    {
                        // If something fatal happens with connection/transaction, surface to caller and continue if possible
                        try { job.Completion.SetException(exOuter); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DbWriteQueue] Worker loop fatal: {ex.Message}");
            }
            finally
            {
                try { _writerConnection?.Close(); _writerConnection?.Dispose(); } catch { }
            }
        }

        private static async Task<T> WaitForResultAsync<T>(Task<object?> task, TimeSpan? timeout)
        {
            if (timeout.HasValue)
            {
                var delay = Task.Delay(timeout.Value);
                var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (finished == delay) throw new TimeoutException("DbWriteQueue timed out waiting for result.");
            }
            var resObj = await task.ConfigureAwait(false);
            if (resObj == null) return default!;
            return (T)resObj!;
        }

        private void EnsureStarted()
        {
            if (_worker == null || _worker.IsCompleted)
                throw new InvalidOperationException("DbWriteQueue not started. Call DbWriteQueue.Instance.Start(connectionString) from startup (Program.Main).");
        }

        public void Dispose()
        {
            try { StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
            _queue.Dispose();
            _cts?.Dispose();
        }

        private class Job
        {
            public Func<SqliteConnection, SqliteTransaction, Task<object?>> Work { get; init; } = (_, _) => Task.FromResult<object?>(null);
            public TaskCompletionSource<object?> Completion { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}