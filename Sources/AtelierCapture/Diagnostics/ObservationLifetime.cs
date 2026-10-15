namespace AtelierCapture;

internal sealed class ObservationLifetime
{
    private readonly object gate = new();
    private readonly ManualResetEventSlim completed = new();
    private readonly ThreadLocal<int> nativeDepth = new();
    private int active;
    private int closingThread;
    private bool closing;

    internal bool IsClosing => Volatile.Read(ref closing);
    internal bool IsClosed => completed.IsSet;
    internal bool OwnsNativeCallback => nativeDepth.Value != 0;

    internal void Enter(bool diagnostic = false)
    {
        lock (gate)
        {
            if (closing && !OwnsNativeCallback && !diagnostic)
                throw new InvalidOperationException(
                    "Capture is closing; observation is not admitted."
                );
            active++;
        }
    }

    internal void Exit()
    {
        lock (gate)
        {
            if (active <= 0)
                throw new InvalidOperationException("Capture observation lifetime is unbalanced.");
            active--;
            if (active == 0)
                Monitor.PulseAll(gate);
        }
    }

    internal IDisposable? BeginNativeCallback()
    {
        lock (gate)
        {
            var depth = nativeDepth.Value;
            if (closing && depth == 0)
                return null;
            var lease = new NativeLease(this, Environment.CurrentManagedThreadId);
            nativeDepth.Value = depth + 1;
            active++;
            return lease;
        }
    }

    private void EndNativeCallback()
    {
        if (!OwnsNativeCallback)
            throw new InvalidOperationException("Native callback lifetime is unbalanced.");
        nativeDepth.Value--;
        Exit();
    }

    internal void Close(Action finish)
    {
        if (OwnsNativeCallback)
        {
            _ = Task.Run(() => Close(finish));
            return;
        }
        var owner = false;
        lock (gate)
        {
            if (!closing)
            {
                closing = true;
                closingThread = Environment.CurrentManagedThreadId;
                owner = true;
                while (active != 0)
                    Monitor.Wait(gate);
            }
        }
        if (!owner)
        {
            if (Volatile.Read(ref closingThread) != Environment.CurrentManagedThreadId)
                completed.Wait();
            return;
        }
        try
        {
            finish();
        }
        finally
        {
            completed.Set();
        }
    }

    private sealed class NativeLease(ObservationLifetime owner, int thread) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            if (thread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException(
                    "Native callback lease must close on its original thread."
                );
            disposed = true;
            owner.EndNativeCallback();
        }
    }
}
