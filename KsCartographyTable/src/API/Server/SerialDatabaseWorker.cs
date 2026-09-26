using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kaisentlaia.KsCartographyTableMod.API.Server
{
    /// <summary>
    /// Owns its resource on one background thread. Work must use detached snapshots;
    /// game objects and the resource must never escape into the other thread.
    /// Completion callbacks are dispatched to the supplied main-thread scheduler.
    /// </summary>
    internal sealed class SerialDatabaseWorker<TState> : IDisposable where TState : class, IDisposable
    {
        private readonly object gate = new();
        private readonly Queue<Job> pending = new();
        private readonly Func<TState> createState;
        private readonly Action<Action> dispatch;
        private readonly int maximumJobs;
        private readonly long maximumBytes;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool accepting = true;
        private int disposed;
        private int outstandingJobs;
        private long outstandingBytes;

        internal Task Completion => completion.Task;
        internal int OutstandingJobs { get { lock (gate) return outstandingJobs; } }
        internal long OutstandingBytes { get { lock (gate) return outstandingBytes; } }

        internal SerialDatabaseWorker(Func<TState> createState, Action<Action> dispatch,
            int maximumJobs = 64, long maximumBytes = 16 * 1024 * 1024,
            string threadName = "Cartography database")
        {
            ArgumentNullException.ThrowIfNull(createState);
            ArgumentNullException.ThrowIfNull(dispatch);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumJobs);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            this.createState = createState;
            this.dispatch = dispatch;
            this.maximumJobs = maximumJobs;
            this.maximumBytes = maximumBytes;
            new Thread(Run) { IsBackground = true, Name = threadName }.Start();
        }

        /// <summary>
        /// Never waits for SQL or available capacity. False means nothing was queued;
        /// the caller must retry or explicitly reject the request. Capacity includes
        /// operations whose results are still waiting for their main-thread callback.
        /// FIFO operations allow a final read after writes to act as a commit fence.
        /// </summary>
        internal bool TryEnqueue<TResult>(Func<TState, TResult> operation, Action<TResult> onSuccess,
            Action<Exception> onError, long estimatedBytes = 0)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(onSuccess);
            ArgumentNullException.ThrowIfNull(onError);
            ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
            lock (gate)
            {
                if (!accepting || outstandingJobs >= maximumJobs || estimatedBytes > maximumBytes - outstandingBytes)
                {
                    return false;
                }
                var job = new Job(this, estimatedBytes, getState =>
                {
                    try
                    {
                        TResult result = operation(getState());
                        return () => onSuccess(result);
                    }
                    catch (Exception error)
                    {
                        return () => onError(error);
                    }
                });
                outstandingJobs++;
                outstandingBytes += estimatedBytes;
                pending.Enqueue(job);
                Monitor.Pulse(gate);
                return true;
            }
        }

        /// <summary>
        /// Reject new work, drain all accepted operations, then close the resource on
        /// its owning thread. Completion does not require pumping main-thread callbacks.
        /// A failed operation does not cancel later work; session code must track errors
        /// and must not report a successful upload after one of its writes failed.
        /// </summary>
        internal void Complete()
        {
            lock (gate)
            {
                accepting = false;
                Monitor.Pulse(gate);
            }
        }

        /// <summary>
        /// Cancel queued work and suppress outstanding callbacks without blocking the
        /// game thread. A running transaction is allowed to finish before its connection
        /// is disposed by the worker. Await Completion when shutdown must wait for cleanup.
        /// Use Complete and await Completion before Dispose to drain accepted writes.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (gate)
            {
                accepting = false;
                while (pending.TryDequeue(out var job)) job.Release();
                Monitor.Pulse(gate);
            }
        }

        private void Run()
        {
            TState state = null;
            Exception failure = null;
            try
            {
                while (true)
                {
                    Job job;
                    lock (gate)
                    {
                        while (pending.Count == 0 && accepting) Monitor.Wait(gate);
                        if (!pending.TryDequeue(out job)) break;
                    }
                    Action callback = job.Execute(() => state ??= createState());
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        job.Release();
                        continue;
                    }
                    try
                    {
                        dispatch(() =>
                        {
                            try
                            {
                                if (Volatile.Read(ref disposed) == 0) callback();
                            }
                            finally { job.Release(); }
                        });
                    }
                    catch
                    {
                        job.Release();
                        throw;
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                lock (gate)
                {
                    accepting = false;
                    while (pending.TryDequeue(out var job)) job.Release();
                }
                try { state?.Dispose(); }
                catch (Exception error) { failure ??= error; }
                if (failure == null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }

        private sealed class Job(SerialDatabaseWorker<TState> owner, long bytes, Func<Func<TState>, Action> execute)
        {
            private int released;
            internal Action Execute(Func<TState> getState) => execute(getState);
            internal void Release()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                lock (owner.gate)
                {
                    owner.outstandingJobs--;
                    owner.outstandingBytes -= bytes;
                }
            }
        }
    }
}
