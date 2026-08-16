namespace PsfGuard.Nina.Sync.Protocol;

public sealed record SyncCatalogCapability
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool Readable { get; init; }

    public bool Writable { get; init; }
}

public sealed record SyncCapabilities
{
    public required int ProtocolVersion { get; init; }

    public required string Product { get; init; }

    public required string ProductVersion { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required IReadOnlyList<SyncCatalogCapability> Catalogs { get; init; }
}

public sealed record CreatePreviewRequest
{
    public int ProtocolVersion { get; init; } = CatalogBundle.CurrentProtocolVersion;

    public required string CatalogId { get; init; }

    public required SyncOperation Operation { get; init; }

    public required CatalogBundle Bundle { get; init; }
}

public sealed record SyncPreview
{
    public required string PreviewId { get; init; }

    public required string State { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public IReadOnlyDictionary<string, long>? Summary { get; init; }
}

public sealed record SyncPreviewJob
{
    public required string JobId { get; init; }

    public required string State { get; init; }

    public SyncPreview? Preview { get; init; }

    public string? Error { get; init; }
}

public sealed record SyncApplyResult
{
    public required string State { get; init; }

    public IReadOnlyDictionary<string, long>? Summary { get; init; }
}

public sealed record CreateExportRequest
{
    public int ProtocolVersion { get; init; } = CatalogBundle.CurrentProtocolVersion;

    public required string CatalogId { get; init; }

    public required SyncOperation Operation { get; init; }

    public bool ReviewedOnly { get; init; } = true;
}

public sealed record SyncExport
{
    public required string ExportId { get; init; }

    public required string State { get; init; }

    public CatalogBundle? Bundle { get; init; }

    public string? Error { get; init; }
}
