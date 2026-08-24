namespace WarehouseEPI.Core.Entities;

public enum LabelTemplateStatus { Draft, InValidation, Published, Retired }
public enum LabelSizePreset { SixByFourLandscape, FourBySixPortrait, ThreeByOneLandscape, FourByFourPointFivePortrait }
public enum LabelTemplateEventType { Created, Submitted, ReturnedToDraft, Published, Duplicated, Retired }

public sealed class LabelTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public Guid? CurrentPublishedVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public LabelTemplateVersion? CurrentPublishedVersion { get; set; }
    public ICollection<LabelTemplateVersion> Versions { get; set; } = [];
    public ICollection<LabelTemplateEvent> Events { get; set; } = [];
}

public sealed class LabelTemplateVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public required string Name { get; set; }
    public LabelSizePreset SizePreset { get; set; }
    public LabelTemplateStatus Status { get; set; }
    public required string DesignJson { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public Guid? RetiredByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public uint RowVersion { get; set; }
    public LabelTemplate Template { get; set; } = null!;
    public User? CreatedByUser { get; set; }
    public User? PublishedByUser { get; set; }
    public User? RetiredByUser { get; set; }
    public ICollection<LabelTemplateVersionAsset> Assets { get; set; } = [];
    public ICollection<LabelTemplateEvent> Events { get; set; } = [];
}

public sealed class LabelAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Content { get; set; }
    public required string Sha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsArchived { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public ICollection<LabelTemplateVersionAsset> Versions { get; set; } = [];
}

public sealed class LabelTemplateVersionAsset
{
    public Guid TemplateVersionId { get; set; }
    public Guid AssetId { get; set; }
    public LabelTemplateVersion TemplateVersion { get; set; } = null!;
    public LabelAsset Asset { get; set; } = null!;
}

public sealed class LabelTemplateEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public LabelTemplateEventType Type { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public Guid? AuthorizedByUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public LabelTemplate Template { get; set; } = null!;
    public LabelTemplateVersion TemplateVersion { get; set; } = null!;
    public User? RequestedByUser { get; set; }
    public User? AuthorizedByUser { get; set; }
}
