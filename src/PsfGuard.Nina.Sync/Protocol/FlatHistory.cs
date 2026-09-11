using System.Text.Json.Serialization;

namespace PsfGuard.Nina.Sync.Protocol;

public sealed record FlatHistoryRecord
{
    public required long SourceRowId { get; init; }
    public required string Fingerprint { get; init; }
    public string? TargetGuid { get; init; }
    public string? TargetName { get; init; }
    public required string ProfileId { get; init; }
    public long? LightSessionDate { get; init; }
    public long LightSessionId { get; init; }
    public long? FlatsTakenDate { get; init; }
    public string? FlatsType { get; init; }
    public string? FilterName { get; init; }
    public long? Gain { get; init; }
    public long? Offset { get; init; }
    public long? Bin { get; init; }
    public long? ReadoutMode { get; init; }
    public double? Rotation { get; init; }
    public double? Roi { get; init; }
}

public sealed record FlatHistorySnapshot
{
    public int ProtocolVersion { get; init; } = 1;
    public required string CatalogId { get; init; }
    public required string OriginId { get; init; }
    public required string SourceName { get; init; }
    public required IReadOnlyList<FlatHistoryRecord> Records { get; init; }

    [JsonIgnore]
    public int SkippedRecords { get; init; }
}

public sealed record FlatHistorySnapshotResponse
{
    public required string CatalogId { get; init; }
    public required string OriginId { get; init; }
    public int Received { get; init; }
}

public sealed record FlatHistoryDecision
{
    public required string RecordId { get; init; }
    public required long SourceRowId { get; init; }
    public required string Fingerprint { get; init; }
    public string? TargetGuid { get; init; }
    public required string Reason { get; init; }
}

public sealed record FlatHistoryPendingRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public required string CatalogId { get; init; }
    public required string OriginId { get; init; }
    public string? TargetGuid { get; init; }
}

public sealed record FlatHistoryPendingResponse
{
    public required string CatalogId { get; init; }
    public required string OriginId { get; init; }
    public required IReadOnlyList<FlatHistoryDecision> Decisions { get; init; }
}

public sealed record FlatHistoryAcknowledgment
{
    public required string RecordId { get; init; }
    public required long SourceRowId { get; init; }
    public required string Fingerprint { get; init; }
    public string? TargetGuid { get; init; }
    public required string Status { get; init; }
    public string? Detail { get; init; }
}

public sealed record FlatHistoryAcknowledgeRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public required string CatalogId { get; init; }
    public required string OriginId { get; init; }
    public required IReadOnlyList<FlatHistoryAcknowledgment> Results { get; init; }
}

public sealed record FlatHistoryAcknowledgeResponse
{
    public required string CatalogId { get; init; }
    public required string OriginId { get; init; }
    public int Acknowledged { get; init; }
}
