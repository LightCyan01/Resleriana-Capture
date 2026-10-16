namespace AtelierCapture.Storage;

internal enum StorageAction
{
    Initialize,
    Append,
    DataFlush,
    CheckpointWrite,
    CheckpointFlush,
    Acknowledge,
    CleanupDelete,
    CleanupOpen,
    Reserve,
}

internal sealed class StorageHooks
{
    internal DateTime? Timestamp;
    internal Action<StorageAction, string, long>? Before;
    internal Action<StorageAction, string, long>? After;
    internal Action<FileStream, ReadOnlyMemory<byte>>? Append;
}

internal sealed class CaptureAbortedException(string reason) : OperationCanceledException(reason);
