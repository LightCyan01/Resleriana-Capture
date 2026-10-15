using AtelierCapture.Storage;

namespace AtelierCapture.Archive;

internal sealed record VerifiedArchiveEntry(string Name, string Sha256, long Bytes, long Records);

internal sealed record BootstrapOwnership(
    string RunId,
    string Environment,
    string CheckpointHash,
    string DiagnosticsHash,
    long Records,
    long TransferredRecords,
    long ReferenceRecords
);

internal sealed class VerifiedCapture : IDisposable
{
    private readonly FileStream archive;
    internal string ArchivePath { get; }
    internal string RunId { get; }
    internal string Environment { get; }
    internal string CheckpointHash { get; }
    internal long? CheckpointGeneration { get; }
    internal IReadOnlyList<StorageStreamStamp> Streams { get; }
    internal IReadOnlyList<VerifiedArchiveEntry> Entries { get; }
    internal bool NewlyCreated { get; }
    internal bool NormalShutdown { get; }
    internal bool Recovered { get; }
    internal bool Aborted { get; }
    internal long TailBytes { get; }
    internal BootstrapOwnership? BootstrapOwnership { get; }
    internal long SourceBytesExpanded { get; }
    internal long ZipBytesExpanded { get; }

    internal VerifiedCapture(
        FileStream archive,
        string archivePath,
        string runId,
        string environment,
        string checkpointHash,
        long? checkpointGeneration,
        StorageStreamStamp[] streams,
        VerifiedArchiveEntry[] entries,
        bool newlyCreated,
        bool normalShutdown,
        bool recovered,
        bool aborted,
        long tailBytes,
        BootstrapOwnership? bootstrapOwnership,
        long sourceBytesExpanded,
        long zipBytesExpanded
    )
    {
        this.archive = archive;
        ArchivePath = Path.GetFullPath(archivePath);
        RunId = runId;
        Environment = environment;
        CheckpointHash = checkpointHash;
        CheckpointGeneration = checkpointGeneration;
        Streams = Array.AsReadOnly(streams);
        Entries = Array.AsReadOnly(entries);
        NewlyCreated = newlyCreated;
        NormalShutdown = normalShutdown;
        Recovered = recovered;
        Aborted = aborted;
        TailBytes = tailBytes;
        BootstrapOwnership = bootstrapOwnership;
        SourceBytesExpanded = sourceBytesExpanded;
        ZipBytesExpanded = zipBytesExpanded;
    }

    public void Dispose() => archive.Dispose();
}
