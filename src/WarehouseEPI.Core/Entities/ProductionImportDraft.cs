namespace WarehouseEPI.Core.Entities;

public enum ProductionImportDraftStatus { Reviewing, Ready, Confirmed, Discarded }

public sealed class ProductionImportDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public required byte[] FileBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ProductionImportDraftStatus Status { get; set; }
    // Application-managed version also works in the HTTP test provider. Updates use optimistic concurrency.
    public int Version { get; set; }
    public Guid? BatchId { get; set; }
    public User Owner { get; set; } = null!;
    public ProductionScheduleImportBatch? Batch { get; set; }
    public ICollection<ProductionImportRevision> Revisions { get; set; } = [];
}

public sealed class ProductionImportRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public int Number { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Action { get; set; }
    public required string ResolutionsJson { get; set; }
    public required string PreviewJson { get; set; }
    public required string Fingerprint { get; set; }
    public Guid? OperationId { get; set; }
    public ProductionImportDraft Draft { get; set; } = null!;
    public User Actor { get; set; } = null!;
}
