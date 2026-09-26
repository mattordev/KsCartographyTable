using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kaisentlaia.KsCartographyTableMod.API.Server;

namespace KsCartographyTable.test.Unit;

public class SerialDatabaseWorkerShould
{
    [Test]
    public async Task OwnResourceOnOneThreadAndFinishWritesBeforeTheFinalRead()
    {
        int mainThread = Environment.CurrentManagedThreadId;
        var callbacks = new ConcurrentQueue<Action>();
        var callbackThreads = new List<int>();
        ThreadBoundState state = null;
        string[] finalRows = null;
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() => state = new ThreadBoundState(), callbacks.Enqueue);
        foreach (string value in new[] { "first", "second" })
        {
            Assert.That(worker.TryEnqueue(resource =>
            {
                resource.CheckThread();
                resource.Rows.Add(value);
                return value;
            }, _ => callbackThreads.Add(Environment.CurrentManagedThreadId), AssertError), Is.True);
        }
        Assert.That(worker.TryEnqueue(resource => resource.Rows.ToArray(), result => finalRows = result, AssertError), Is.True);
        worker.Complete();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(finalRows, Is.Null, "The worker must not run game callbacks itself.");
        // Use the thread that actually pumps callbacks; async NUnit continuations may
        // resume on a different test thread than the one that constructed the worker.
        int callbackThread = Environment.CurrentManagedThreadId;
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.That(finalRows, Is.EqualTo(new[] { "first", "second" }));
        Assert.That(callbackThreads, Is.All.EqualTo(callbackThread));
        Assert.That(state.CreatedThread, Is.Not.EqualTo(mainThread));
        Assert.That(state.DisposedThread, Is.EqualTo(state.CreatedThread));
        Assert.That(worker.OutstandingJobs, Is.Zero);
        Assert.That(worker.TryEnqueue(_ => 1, _ => { }, AssertError), Is.False);
    }

    [Test]
    public async Task RejectExcessWorkWithoutWaitingAndRetainCapacityUntilCallbacksRun()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var twoCallbacksReady = new ManualResetEventSlim();
        int scheduled = 0;
        var callbacks = new ConcurrentQueue<Action>();
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() => new ThreadBoundState(), callback =>
        {
            callbacks.Enqueue(callback);
            if (Interlocked.Increment(ref scheduled) == 2) twoCallbacksReady.Set();
        }, maximumJobs: 2, maximumBytes: 100);
        try
        {
            Assert.That(worker.TryEnqueue(_ =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return 1;
            }, _ => { }, AssertError, 60), Is.True);
            Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(worker.TryEnqueue(_ => 2, _ => { }, AssertError, 41), Is.False, "Byte limit must be enforced.");
            Assert.That(worker.TryEnqueue(_ => 2, _ => { }, AssertError, 40), Is.True);
            Assert.That(worker.TryEnqueue(_ => 3, _ => { }, AssertError), Is.False, "Job limit must be enforced.");
            Assert.That(worker.OutstandingBytes, Is.EqualTo(100));
            release.Set();
            Assert.That(twoCallbacksReady.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(worker.TryEnqueue(_ => 3, _ => { }, AssertError), Is.False, "Unapplied results still consume capacity.");
            Assert.That(callbacks.TryDequeue(out var first), Is.True);
            first();
            Assert.That(worker.TryEnqueue(_ => 3, _ => { }, AssertError, 60), Is.True);
            worker.Complete();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            while (callbacks.TryDequeue(out var callback)) callback();
            Assert.That(worker.OutstandingJobs, Is.Zero);
            Assert.That(worker.OutstandingBytes, Is.Zero);
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task DispatchOperationErrorsAndContinueSubsequentWork()
    {
        var callbacks = new ConcurrentQueue<Action>();
        Exception reported = null;
        int final = 0;
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() => new ThreadBoundState(), callbacks.Enqueue);
        Assert.That(worker.TryEnqueue<int>(_ => throw new InvalidOperationException("disk failure"), _ => Assert.Fail(), error => reported = error), Is.True);
        Assert.That(worker.TryEnqueue(_ => 42, result => final = result, AssertError), Is.True);
        worker.Complete();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(reported, Is.Null);
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.That(reported, Is.TypeOf<InvalidOperationException>());
        Assert.That(reported.Message, Is.EqualTo("disk failure"));
        Assert.That(final, Is.EqualTo(42));
        Assert.That(worker.OutstandingJobs, Is.Zero);
    }

    [Test]
    public async Task CancelQueuedWorkButNeverCloseAResourceWhileItsOperationIsRunning()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var callbacks = new ConcurrentQueue<Action>();
        ThreadBoundState state = null;
        int queuedRuns = 0;
        int callbackRuns = 0;
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() => state = new ThreadBoundState(), callbacks.Enqueue);
        try
        {
            Assert.That(worker.TryEnqueue(resource =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                resource.CheckThread();
                return 1;
            }, _ => callbackRuns++, AssertError), Is.True);
            Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(worker.TryEnqueue(_ => ++queuedRuns, _ => callbackRuns++, AssertError), Is.True);
            worker.Dispose();
            Assert.That(state.DisposedThread, Is.Zero, "The running operation still owns the resource.");
            Assert.That(worker.Completion.IsCompleted, Is.False);
            Assert.That(worker.TryEnqueue(_ => 1, _ => { }, AssertError), Is.False);
            release.Set();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            while (callbacks.TryDequeue(out var callback)) callback();
            Assert.That(state.DisposedThread, Is.EqualTo(state.CreatedThread));
            Assert.That(queuedRuns, Is.Zero);
            Assert.That(callbackRuns, Is.Zero);
            Assert.That(worker.OutstandingJobs, Is.Zero);
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task SuppressCallbacksAlreadyQueuedWhenTheWorldIsDisposed()
    {
        var callbacks = new ConcurrentQueue<Action>();
        bool called = false;
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() => new ThreadBoundState(), callbacks.Enqueue);
        Assert.That(worker.TryEnqueue(_ => 1, _ => called = true, AssertError), Is.True);
        worker.Complete();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(callbacks, Has.Count.EqualTo(1));
        worker.Dispose();
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.That(called, Is.False);
        Assert.That(worker.OutstandingJobs, Is.Zero);
    }

    [Test]
    public async Task DeliverAnOpenFailureAndPermitALaterRetry()
    {
        var callbacks = new ConcurrentQueue<Action>();
        int attempts = 0;
        Exception error = null;
        int result = 0;
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() =>
        {
            if (++attempts == 1) throw new InvalidOperationException("open failed");
            return new ThreadBoundState();
        }, callbacks.Enqueue);
        Assert.That(worker.TryEnqueue(_ => 1, _ => Assert.Fail(), caught => error = caught), Is.True);
        Assert.That(worker.TryEnqueue(_ => 2, value => result = value, AssertError), Is.True);
        worker.Complete();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.That(error?.Message, Is.EqualTo("open failed"));
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(result, Is.EqualTo(2));
    }

    [Test]
    public void CloseResourceAndFaultCompletionIfTheMainThreadDispatcherFails()
    {
        ThreadBoundState state = null;
        using var worker = new SerialDatabaseWorker<ThreadBoundState>(() => state = new ThreadBoundState(), _ => throw new InvalidOperationException("world left"));
        Assert.That(worker.TryEnqueue(_ => 1, _ => { }, AssertError), Is.True);
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(error.Message, Is.EqualTo("world left"));
        Assert.That(state.DisposedThread, Is.EqualTo(state.CreatedThread));
        Assert.That(worker.OutstandingJobs, Is.Zero);
    }

    private static void AssertError(Exception error) => Assert.Fail(error.ToString());

    private sealed class ThreadBoundState : IDisposable
    {
        internal readonly int CreatedThread = Environment.CurrentManagedThreadId;
        internal int DisposedThread;
        internal readonly List<string> Rows = [];
        internal void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != CreatedThread || DisposedThread != 0)
                throw new InvalidOperationException("Resource used on the wrong thread or after disposal.");
        }
        public void Dispose()
        {
            CheckThread();
            DisposedThread = Environment.CurrentManagedThreadId;
        }
    }
}
