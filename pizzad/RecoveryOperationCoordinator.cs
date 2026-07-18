namespace pizzad;

public sealed class RecoveryOperationCoordinator
{
    private readonly object _sync = new();
    private string? _activeOperation;

    public IDisposable Acquire(string operation)
    {
        lock (_sync)
        {
            if (_activeOperation != null)
                throw new InvalidOperationException($"Recovery work is already in progress: {_activeOperation}. Wait for it to finish or cancel it before starting {operation}.");
            _activeOperation = operation;
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (_sync) _activeOperation = null;
    }

    private sealed class Lease(RecoveryOperationCoordinator owner) : IDisposable
    {
        private RecoveryOperationCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
