using AQ.Denials.Ingest;

namespace AQ.Denials.Api.State;

/// <summary>Supplies the canonical state every read endpoint is built on.</summary>
public interface IStateProvider
{
    /// <summary>
    /// Derived directly from the data pack by the same pipeline the tests assert against, so an
    /// endpoint can never present a figure the test suite has not seen.
    /// </summary>
    IngestOutcome Current { get; }
}

/// <summary>
/// Runs the pure ingestion pipeline on demand. Nothing is cached, because a cache would be a
/// second source of truth and this pack parses in well under a second; when that stops being
/// true the cache should key on the pack's file timestamps, not on a timer.
/// </summary>
public sealed class DerivedStateProvider : IStateProvider
{
    private readonly string? _dataDir;

    public DerivedStateProvider(IConfiguration configuration) =>
        _dataDir = configuration["DATA_DIR"];

    public IngestOutcome Current => IngestPipeline.Run(DataPack.Read(_dataDir));
}
