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
        public bool IsRunning => _worker != null && !_worker.IsCompleted;

        private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private readonly object _startLock = new();
        private string? _connectionString;
        private SqliteConnection? _writerConnection;

        // Metrics: total writes processed and rolling per-second window (last N seconds).
        // _totalWritesProcessed is incremented atomically so it can be read safely from other threads.
        // _perSecondCounts is updated by the single writer thread and used to compute per-second/minute rates.
        private long _totalWritesProcessed = 0;
        private readonly int _rateWindowSeconds = 60;
        private readonly int[] _perSecondCounts;
        private long _perSecondEpoch = 0; // seconds-since-epoch tracked for bucket alignment

        private DbWriteQueue()
        {
            // Initialize rolling window buffer
            _perSecondCounts = new int[_rateWindowSeconds];
            _perSecondEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

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

                // Best-effort: start a background reporter that observes the same cancellation token.
                // We don't store the Task reference so shutdown remains simple; reporter will stop when _cts is cancelled.
                try
                {
                    Task.Run(() => ReporterLoop(_cts.Token), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DbWriteQueue] Reporter failed to start: {ex.Message}");
                }
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
                if (_worker == null || _worker.IsCompleted)
                {
                    // still attempt to cancel reporter if present
                    try { _cts?.Cancel(); } catch { }
                    _queue.CompleteAdding();
                    return;
                }

                _cts?.Cancel();
                _queue.CompleteAdding();
            }

            var toWait = waitFor ?? TimeSpan.FromSeconds(5);

            // Wait for worker to finish (bounded)
            try
            {
                if (_worker != null)
                    await Task.WhenAny(_worker, Task.Delay(toWait)).ConfigureAwait(false);
            }
            catch { /* best-effort */ }

            // best-effort dispose writer connection
            try { _writerConnection?.Close(); _writerConnection?.Dispose(); } catch { }
        }

        // Background reporter loop (best-effort).
        // Logs aggregated metrics every 5 minutes. Observes the same cancellation token used by Start/Stop.
        private async Task ReporterLoop(CancellationToken ct)
        {
            // Reporter interval: change here if you want more/less frequent reports.
            var reporterInterval = TimeSpan.FromMinutes(5);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(reporterInterval, ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException) { break; }

                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var total = GetTotalWritesProcessed();
                        var perSec = GetWritesPerSecond();
                        var perMin = GetWritesPerMinute();
                        var queued = 0;
                        try { queued = _queue.Count; } catch { /* best-effort */ }

                        Console.WriteLine($"[DbWriteQueue][METRICS] totalWrites={total}, writes/sec(current)={perSec}, writes/min(last{_rateWindowSeconds}s)={perMin}, queued={queued}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DbWriteQueue] Reporter error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DbWriteQueue] Reporter loop fatal: {ex.Message}");
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
                        finally
                        {
                            // Record that a job has been processed (success or failure).
                            try { RecordWriteTick(); } catch { /* best-effort */ }
                        }
                    }
                    catch (Exception exOuter)
                    {
                        // If something fatal happens with connection/transaction, surface to caller and continue if possible
                        try { job.Completion.SetException(exOuter); } catch { }
                        try { RecordWriteTick(); } catch { /* best-effort */ }
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
            {
                try
                {
                    Console.WriteLine($"[DbWriteQueue] EnsureStarted failed. IsRunning={IsRunning}");
                }
                catch { /* best-effort logging */ }
                throw new InvalidOperationException("DbWriteQueue not started. Call DbWriteQueue.Instance.Start(connectionString) from startup (Program.Main).");
            }
        }

        // Public metrics accessors

        // Returns the total number of write jobs processed by the writer.
        public long GetTotalWritesProcessed() => Interlocked.Read(ref _totalWritesProcessed);

        // Returns writes observed in the current second (approx).
        public int GetWritesPerSecond()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var idx = (int)(now % _rateWindowSeconds);
                // If the epoch is stale (no recent writes), the bucket might be zero — that's acceptable.
                return _perSecondCounts[idx];
            }
            catch { return 0; }
        }

        // Sum over the last _rateWindowSeconds (default 60) to get writes per minute.
        public int GetWritesPerMinute()
        {
            try
            {
                int sum = 0;
                for (int i = 0; i < _rateWindowSeconds; i++)
                    sum += _perSecondCounts[i];
                return sum;
            }
            catch { return 0; }
        }

        // --- ADDED: expose queue length for diagnostics ---
        public int GetQueueLength()
        {
            try
            {
                return _queue.Count;
            }
            catch
            {
                return -1;
            }
        }

        // Internal helper: record a tick for a processed job.
        // Called from the single writer thread; updates total (atomic) and rolling per-second buckets.
        private void RecordWriteTick()
        {
            Interlocked.Increment(ref _totalWritesProcessed);

            try
            {
                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var currentEpoch = Interlocked.Read(ref _perSecondEpoch);

                if (nowSec != currentEpoch)
                {
                    // Advance epoch and zero any intermediate buckets.
                    // Since only the writer thread calls RecordWriteTick, we can safely mutate the array.
                    var delta = nowSec - currentEpoch;
                    if (delta >= _rateWindowSeconds)
                    {
                        Array.Clear(_perSecondCounts, 0, _perSecondCounts.Length);
                    }
                    else if (delta > 0)
                    {
                        for (long s = currentEpoch + 1; s <= nowSec; s++)
                        {
                            var idx = (int)(s % _rateWindowSeconds);
                            _perSecondCounts[idx] = 0;
                        }
                    }

                    Interlocked.Exchange(ref _perSecondEpoch, nowSec);
                }

                var idxNow = (int)(nowSec % _rateWindowSeconds);
                _perSecondCounts[idxNow]++;
            }
            catch
            {
                // best-effort; metrics are non-critical
            }
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