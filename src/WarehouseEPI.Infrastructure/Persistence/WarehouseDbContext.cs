using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Persistence;

public sealed class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options)
    : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BusinessSettings> BusinessSettings => Set<BusinessSettings>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<ProductType> ProductTypes => Set<ProductType>();
    public DbSet<ProductClass> ProductClasses => Set<ProductClass>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<LocationRackRevision> LocationRackRevisions => Set<LocationRackRevision>();
    public DbSet<ProductLocationAssignment> ProductLocationAssignments => Set<ProductLocationAssignment>();
    public DbSet<ProductLot> ProductLots => Set<ProductLot>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryMovementLine> InventoryMovementLines => Set<InventoryMovementLine>();
    public DbSet<InventoryBalanceChange> InventoryBalanceChanges => Set<InventoryBalanceChange>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<InventoryMovementCorrection> InventoryMovementCorrections => Set<InventoryMovementCorrection>();
    public DbSet<WipDisposition> WipDispositions => Set<WipDisposition>();
    public DbSet<ProductLotDateChange> ProductLotDateChanges => Set<ProductLotDateChange>();
    public DbSet<WarehouseMapLayout> WarehouseMapLayouts => Set<WarehouseMapLayout>();
    public DbSet<WarehouseMapElement> WarehouseMapElements => Set<WarehouseMapElement>();
    public DbSet<WarehouseMapLayer> WarehouseMapLayers => Set<WarehouseMapLayer>();
    public DbSet<WarehouseMapArchitecturalElement> WarehouseMapArchitecturalElements => Set<WarehouseMapArchitecturalElement>();
    public DbSet<WarehouseMapReferenceImage> WarehouseMapReferenceImages => Set<WarehouseMapReferenceImage>();
    public DbSet<WarehouseMapRevision> WarehouseMapRevisions => Set<WarehouseMapRevision>();
    public DbSet<CycleCountCampaign> CycleCountCampaigns => Set<CycleCountCampaign>();
    public DbSet<CycleCountLocation> CycleCountLocations => Set<CycleCountLocation>();
    public DbSet<CycleCountAttempt> CycleCountAttempts => Set<CycleCountAttempt>();
    public DbSet<CycleCountEntry> CycleCountEntries => Set<CycleCountEntry>();
    public DbSet<CycleCountAction> CycleCountActions => Set<CycleCountAction>();
    public DbSet<CycleCountReviewBatch> CycleCountReviewBatches => Set<CycleCountReviewBatch>();
    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();
    public DbSet<LabelTemplateVersion> LabelTemplateVersions => Set<LabelTemplateVersion>();
    public DbSet<LabelAsset> LabelAssets => Set<LabelAsset>();
    public DbSet<LabelTemplateVersionAsset> LabelTemplateVersionAssets => Set<LabelTemplateVersionAsset>();
    public DbSet<LabelTemplateEvent> LabelTemplateEvents => Set<LabelTemplateEvent>();
    public DbSet<OperationalExceptionCase> OperationalExceptionCases => Set<OperationalExceptionCase>();
    public DbSet<OperationalExceptionEvent> OperationalExceptionEvents => Set<OperationalExceptionEvent>();
    public DbSet<ReceivingDocument> ReceivingDocuments => Set<ReceivingDocument>();
    public DbSet<ReceivingDocumentLine> ReceivingDocumentLines => Set<ReceivingDocumentLine>();
    public DbSet<ReceivingConfirmation> ReceivingConfirmations => Set<ReceivingConfirmation>();
    public DbSet<ReceivingConfirmationLine> ReceivingConfirmationLines => Set<ReceivingConfirmationLine>();
    public DbSet<ReceivingDocumentEvent> ReceivingDocumentEvents => Set<ReceivingDocumentEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureRole(modelBuilder);
        ConfigureUser(modelBuilder);
        ConfigureBusinessSettings(modelBuilder);
        ConfigureUnit(modelBuilder);
        ConfigureProductType(modelBuilder);
        ConfigureProductClass(modelBuilder);
        ConfigureProduct(modelBuilder);
        ConfigureProductBarcode(modelBuilder);
        ConfigureLocation(modelBuilder);
        ConfigureLocationRackRevision(modelBuilder);
        ConfigureProductLocationAssignment(modelBuilder);
        ConfigureProductLot(modelBuilder);
        ConfigureInventoryMovement(modelBuilder);
        ConfigureInventoryMovementLine(modelBuilder);
        ConfigureInventoryBalanceChange(modelBuilder);
        ConfigureInventoryBalance(modelBuilder);
        ConfigureInventoryMovementCorrection(modelBuilder);
        ConfigureWipDisposition(modelBuilder);
        ConfigureProductLotDateChange(modelBuilder);
        ConfigureWarehouseMap(modelBuilder);
        ConfigureCycleCounts(modelBuilder);
        ConfigureLabels(modelBuilder);
        ConfigureOperationalExceptions(modelBuilder);
        ConfigureReceiving(modelBuilder);
    }

    private static void ConfigureReceiving(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<ReceivingDocument>();
        document.ToTable("receiving_documents", table =>
        {
            table.HasCheckConstraint("ck_receiving_documents_type", "type IN ('PURCHASE_ORDER','DELIVERY_NOTE','PACKING_LIST','PRODUCTION_ORDER','OTHER')");
            table.HasCheckConstraint("ck_receiving_documents_status", "status IN ('OPEN','PARTIALLY_RECEIVED','COMPLETED','CLOSED_WITH_DIFFERENCES','CANCELLED')");
            table.HasCheckConstraint("ck_receiving_documents_number", "length(btrim(number)) > 0 AND normalized_number = upper(btrim(number))");
            table.HasCheckConstraint("ck_receiving_documents_origin", "length(btrim(origin)) > 0 AND normalized_origin = upper(btrim(origin))");
            table.HasCheckConstraint("ck_receiving_documents_terminal_shape", "(status = 'COMPLETED' AND completed_at IS NOT NULL AND closed_at IS NULL AND cancelled_at IS NULL) OR (status = 'CLOSED_WITH_DIFFERENCES' AND closed_at IS NOT NULL AND close_reason IS NOT NULL AND cancelled_at IS NULL) OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL AND closed_at IS NULL) OR (status IN ('OPEN','PARTIALLY_RECEIVED') AND completed_at IS NULL AND closed_at IS NULL AND cancelled_at IS NULL)");
        });
        document.HasKey(item => item.Id);
        document.Property(item => item.Id).HasColumnName("id");
        document.Property(item => item.OperationId).HasColumnName("operation_id");
        document.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        document.Property(item => item.Type).HasColumnName("type").HasMaxLength(30).HasConversion(value => ReceivingDocumentTypeToDatabase(value), value => ReceivingDocumentTypeFromDatabase(value));
        document.Property(item => item.Number).HasColumnName("number").HasMaxLength(120).IsRequired();
        document.Property(item => item.NormalizedNumber).HasColumnName("normalized_number").HasMaxLength(120).IsRequired();
        document.Property(item => item.Origin).HasColumnName("origin").HasMaxLength(160).IsRequired();
        document.Property(item => item.NormalizedOrigin).HasColumnName("normalized_origin").HasMaxLength(160).IsRequired();
        document.Property(item => item.DocumentDate).HasColumnName("document_date");
        document.Property(item => item.Status).HasColumnName("status").HasMaxLength(30).HasConversion(value => ReceivingDocumentStatusToDatabase(value), value => ReceivingDocumentStatusFromDatabase(value));
        document.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(500);
        document.Property(item => item.OpenedByUserId).HasColumnName("opened_by_user_id");
        document.Property(item => item.OpenedAt).HasColumnName("opened_at").HasDefaultValueSql("now()");
        document.Property(item => item.CompletedAt).HasColumnName("completed_at");
        document.Property(item => item.ClosedByUserId).HasColumnName("closed_by_user_id");
        document.Property(item => item.ClosedAt).HasColumnName("closed_at");
        document.Property(item => item.CloseReason).HasColumnName("close_reason").HasMaxLength(500);
        document.Property(item => item.CancelledByUserId).HasColumnName("cancelled_by_user_id");
        document.Property(item => item.CancelledAt).HasColumnName("cancelled_at");
        document.Property(item => item.CancelReason).HasColumnName("cancel_reason").HasMaxLength(500);
        document.Property(item => item.Version).HasColumnName("xmin").IsRowVersion();
        document.HasIndex(item => item.OperationId).IsUnique();
        document.HasIndex(item => new { item.Type, item.NormalizedNumber, item.NormalizedOrigin }).IsUnique().HasFilter("status <> 'CANCELLED'");
        document.HasIndex(item => new { item.Status, item.OpenedAt });
        document.HasOne(item => item.OpenedByUser).WithMany().HasForeignKey(item => item.OpenedByUserId).OnDelete(DeleteBehavior.Restrict);
        document.HasOne(item => item.ClosedByUser).WithMany().HasForeignKey(item => item.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
        document.HasOne(item => item.CancelledByUser).WithMany().HasForeignKey(item => item.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);

        var line = modelBuilder.Entity<ReceivingDocumentLine>();
        line.ToTable("receiving_document_lines", table => { table.HasCheckConstraint("ck_receiving_document_lines_number", "line_number > 0"); table.HasCheckConstraint("ck_receiving_document_lines_quantity", "expected_quantity > 0"); });
        line.HasKey(item => item.Id);
        line.Property(item => item.Id).HasColumnName("id");
        line.Property(item => item.ReceivingDocumentId).HasColumnName("receiving_document_id");
        line.Property(item => item.LineNumber).HasColumnName("line_number");
        line.Property(item => item.ProductId).HasColumnName("product_id");
        line.Property(item => item.UnitId).HasColumnName("unit_id");
        line.Property(item => item.ExpectedQuantity).HasColumnName("expected_quantity").HasPrecision(18, 4);
        line.HasIndex(item => new { item.ReceivingDocumentId, item.LineNumber }).IsUnique();
        line.HasIndex(item => new { item.ReceivingDocumentId, item.ProductId }).IsUnique();
        line.HasIndex(item => item.ProductId);
        line.HasOne(item => item.ReceivingDocument).WithMany(item => item.Lines).HasForeignKey(item => item.ReceivingDocumentId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne(item => item.Product).WithMany().HasForeignKey(item => item.ProductId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne(item => item.Unit).WithMany().HasForeignKey(item => item.UnitId).OnDelete(DeleteBehavior.Restrict);

        var confirmation = modelBuilder.Entity<ReceivingConfirmation>();
        confirmation.ToTable("receiving_confirmations");
        confirmation.HasKey(item => item.Id);
        confirmation.Property(item => item.Id).HasColumnName("id");
        confirmation.Property(item => item.OperationId).HasColumnName("operation_id");
        confirmation.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        confirmation.Property(item => item.ReceivingDocumentId).HasColumnName("receiving_document_id");
        confirmation.Property(item => item.InventoryMovementId).HasColumnName("inventory_movement_id");
        confirmation.Property(item => item.ResponsibleUserId).HasColumnName("responsible_user_id");
        confirmation.Property(item => item.DifferenceAcknowledged).HasColumnName("difference_acknowledged");
        confirmation.Property(item => item.DifferenceNotes).HasColumnName("difference_notes").HasMaxLength(500);
        confirmation.Property(item => item.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");
        confirmation.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        confirmation.HasIndex(item => item.OperationId).IsUnique();
        confirmation.HasIndex(item => item.InventoryMovementId).IsUnique();
        confirmation.HasIndex(item => new { item.ReceivingDocumentId, item.OccurredAt });
        confirmation.HasOne(item => item.ReceivingDocument).WithMany(item => item.Confirmations).HasForeignKey(item => item.ReceivingDocumentId).OnDelete(DeleteBehavior.Restrict);
        confirmation.HasOne(item => item.InventoryMovement).WithMany().HasForeignKey(item => item.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
        confirmation.HasOne(item => item.ResponsibleUser).WithMany().HasForeignKey(item => item.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);

        var confirmationLine = modelBuilder.Entity<ReceivingConfirmationLine>();
        confirmationLine.ToTable("receiving_confirmation_lines");
        confirmationLine.HasKey(item => item.Id);
        confirmationLine.Property(item => item.Id).HasColumnName("id");
        confirmationLine.Property(item => item.ReceivingConfirmationId).HasColumnName("receiving_confirmation_id");
        confirmationLine.Property(item => item.ReceivingDocumentLineId).HasColumnName("receiving_document_line_id");
        confirmationLine.Property(item => item.InventoryMovementLineId).HasColumnName("inventory_movement_line_id");
        confirmationLine.Property(item => item.ExternalLotReference).HasColumnName("external_lot_reference").HasMaxLength(120);
        confirmationLine.HasIndex(item => item.InventoryMovementLineId).IsUnique();
        confirmationLine.HasIndex(item => item.ReceivingDocumentLineId);
        confirmationLine.HasIndex(item => item.ExternalLotReference).HasFilter("external_lot_reference IS NOT NULL");
        confirmationLine.HasOne(item => item.ReceivingConfirmation).WithMany(item => item.Lines).HasForeignKey(item => item.ReceivingConfirmationId).OnDelete(DeleteBehavior.Restrict);
        confirmationLine.HasOne(item => item.ReceivingDocumentLine).WithMany(item => item.ConfirmationLines).HasForeignKey(item => item.ReceivingDocumentLineId).OnDelete(DeleteBehavior.Restrict);
        confirmationLine.HasOne(item => item.InventoryMovementLine).WithMany().HasForeignKey(item => item.InventoryMovementLineId).OnDelete(DeleteBehavior.Restrict);

        var auditEvent = modelBuilder.Entity<ReceivingDocumentEvent>();
        auditEvent.ToTable("receiving_document_events", table => table.HasCheckConstraint("ck_receiving_document_events_type", "type IN ('OPENED','RECEIPT_CONFIRMED','AUTOMATICALLY_COMPLETED','CLOSED_WITH_DIFFERENCES','CANCELLED','RECEIPT_CORRECTED','REOPENED_AFTER_CORRECTION')"));
        auditEvent.HasKey(item => item.Id);
        auditEvent.Property(item => item.Id).HasColumnName("id");
        auditEvent.Property(item => item.OperationId).HasColumnName("operation_id");
        auditEvent.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();
        auditEvent.Property(item => item.ReceivingDocumentId).HasColumnName("receiving_document_id");
        auditEvent.Property(item => item.Type).HasColumnName("type").HasMaxLength(40).HasConversion(value => ReceivingDocumentEventTypeToDatabase(value), value => ReceivingDocumentEventTypeFromDatabase(value));
        auditEvent.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
        auditEvent.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(500);
        auditEvent.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        auditEvent.HasIndex(item => item.OperationId).IsUnique().HasFilter("operation_id IS NOT NULL");
        auditEvent.HasIndex(item => new { item.ReceivingDocumentId, item.RecordedAt });
        auditEvent.HasOne(item => item.ReceivingDocument).WithMany(item => item.Events).HasForeignKey(item => item.ReceivingDocumentId).OnDelete(DeleteBehavior.Restrict);
        auditEvent.HasOne(item => item.ActorUser).WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureLabels(ModelBuilder modelBuilder)
    {
        var template = modelBuilder.Entity<LabelTemplate>();
        template.ToTable("label_templates", table =>
        {
            table.HasCheckConstraint("ck_label_templates_code", "code = upper(btrim(code)) AND code ~ '^[A-Z0-9][A-Z0-9-]{2,59}$'");
            table.HasCheckConstraint("ck_label_templates_kind", "kind IN ('PRODUCT_LABEL','PALLET_LICENSE_PLATE')");
        });
        template.HasKey(item => item.Id);
        template.Property(item => item.Id).HasColumnName("id");
        template.Property(item => item.Code).HasColumnName("code").HasMaxLength(60).IsRequired();
        template.Property(item => item.Kind).HasColumnName("kind").HasMaxLength(30).HasDefaultValue(LabelTemplateKind.ProductLabel)
            .HasConversion(value => value == LabelTemplateKind.PalletLicensePlate ? "PALLET_LICENSE_PLATE" : "PRODUCT_LABEL",
                value => value == "PALLET_LICENSE_PLATE" ? LabelTemplateKind.PalletLicensePlate : LabelTemplateKind.ProductLabel);
        template.Property(item => item.CurrentPublishedVersionId).HasColumnName("current_published_version_id");
        template.Property(item => item.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        template.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        template.HasIndex(item => item.Code).IsUnique();

        var version = modelBuilder.Entity<LabelTemplateVersion>();
        version.ToTable("label_template_versions", table =>
        {
            table.HasCheckConstraint("ck_label_template_versions_number", "version > 0");
            table.HasCheckConstraint("ck_label_template_versions_status", "status IN ('DRAFT','IN_VALIDATION','PUBLISHED','RETIRED')");
            table.HasCheckConstraint("ck_label_template_versions_size", "size_preset IN ('6X4_L','4X6_P','3X1_L','4X45_P','11X85_L')");
        });
        version.HasKey(item => item.Id);
        version.Property(item => item.Id).HasColumnName("id");
        version.Property(item => item.TemplateId).HasColumnName("template_id");
        version.Property(item => item.Version).HasColumnName("version");
        version.Property(item => item.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        version.Property(item => item.SizePreset).HasColumnName("size_preset").HasMaxLength(12)
            .HasConversion(value => LabelSizeToDatabase(value), value => LabelSizeFromDatabase(value));
        version.Property(item => item.Status).HasColumnName("status").HasMaxLength(20).HasConversion(
            value => value == LabelTemplateStatus.InValidation ? "IN_VALIDATION" : value.ToString().ToUpperInvariant(),
            value => value == "IN_VALIDATION" ? LabelTemplateStatus.InValidation : Enum.Parse<LabelTemplateStatus>(value, true));
        version.Property(item => item.DesignJson).HasColumnName("design_json").HasColumnType("jsonb").IsRequired();
        version.Property(item => item.CreatedByUserId).HasColumnName("created_by_user_id");
        version.Property(item => item.PublishedByUserId).HasColumnName("published_by_user_id");
        version.Property(item => item.RetiredByUserId).HasColumnName("retired_by_user_id");
        version.Property(item => item.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        version.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        version.Property(item => item.PublishedAt).HasColumnName("published_at");
        version.Property(item => item.RetiredAt).HasColumnName("retired_at");
        version.Property(item => item.RowVersion).IsRowVersion().HasColumnName("xmin");
        version.HasIndex(item => new { item.TemplateId, item.Version }).IsUnique();
        version.HasIndex(item => item.TemplateId).IsUnique().HasFilter("status IN ('DRAFT','IN_VALIDATION')");
        version.HasOne(item => item.Template).WithMany(item => item.Versions).HasForeignKey(item => item.TemplateId).OnDelete(DeleteBehavior.Restrict);
        version.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        version.HasOne(item => item.PublishedByUser).WithMany().HasForeignKey(item => item.PublishedByUserId).OnDelete(DeleteBehavior.Restrict);
        version.HasOne(item => item.RetiredByUser).WithMany().HasForeignKey(item => item.RetiredByUserId).OnDelete(DeleteBehavior.Restrict);
        template.HasOne(item => item.CurrentPublishedVersion).WithMany().HasForeignKey(item => item.CurrentPublishedVersionId).OnDelete(DeleteBehavior.Restrict);

        var asset = modelBuilder.Entity<LabelAsset>();
        asset.ToTable("label_assets", table =>
        {
            table.HasCheckConstraint("ck_label_assets_size", "octet_length(content) BETWEEN 1 AND 1048576");
            table.HasCheckConstraint("ck_label_assets_dimensions", "width BETWEEN 1 AND 4096 AND height BETWEEN 1 AND 4096");
            table.HasCheckConstraint("ck_label_assets_type", "content_type IN ('image/png','image/jpeg')");
        });
        asset.HasKey(item => item.Id);
        asset.Property(item => item.Id).HasColumnName("id");
        asset.Property(item => item.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        asset.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(20).IsRequired();
        asset.Property(item => item.Content).HasColumnName("content").IsRequired();
        asset.Property(item => item.Sha256).HasColumnName("sha256").HasMaxLength(64).IsFixedLength().IsRequired();
        asset.Property(item => item.Width).HasColumnName("width");
        asset.Property(item => item.Height).HasColumnName("height");
        asset.Property(item => item.IsArchived).HasColumnName("is_archived");
        asset.Property(item => item.CreatedByUserId).HasColumnName("created_by_user_id");
        asset.Property(item => item.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        asset.HasIndex(item => item.Sha256).IsUnique();
        asset.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var versionAsset = modelBuilder.Entity<LabelTemplateVersionAsset>();
        versionAsset.ToTable("label_template_version_assets");
        versionAsset.HasKey(item => new { item.TemplateVersionId, item.AssetId });
        versionAsset.Property(item => item.TemplateVersionId).HasColumnName("template_version_id");
        versionAsset.Property(item => item.AssetId).HasColumnName("asset_id");
        versionAsset.HasOne(item => item.TemplateVersion).WithMany(item => item.Assets).HasForeignKey(item => item.TemplateVersionId).OnDelete(DeleteBehavior.Cascade);
        versionAsset.HasOne(item => item.Asset).WithMany(item => item.Versions).HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.Restrict);

        var auditEvent = modelBuilder.Entity<LabelTemplateEvent>();
        auditEvent.ToTable("label_template_events");
        auditEvent.HasKey(item => item.Id);
        auditEvent.Property(item => item.Id).HasColumnName("id");
        auditEvent.Property(item => item.TemplateId).HasColumnName("template_id");
        auditEvent.Property(item => item.TemplateVersionId).HasColumnName("template_version_id");
        auditEvent.Property(item => item.Type).HasColumnName("type").HasMaxLength(30).HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<LabelTemplateEventType>(value, true));
        auditEvent.Property(item => item.RequestedByUserId).HasColumnName("requested_by_user_id");
        auditEvent.Property(item => item.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        auditEvent.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(500);
        auditEvent.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        auditEvent.HasIndex(item => new { item.TemplateId, item.RecordedAt });
        auditEvent.HasOne(item => item.Template).WithMany(item => item.Events).HasForeignKey(item => item.TemplateId).OnDelete(DeleteBehavior.Restrict);
        auditEvent.HasOne(item => item.TemplateVersion).WithMany(item => item.Events).HasForeignKey(item => item.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);
        auditEvent.HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        auditEvent.HasOne(item => item.AuthorizedByUser).WithMany().HasForeignKey(item => item.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRole(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Role>();

        entity.ToTable("roles");
        entity.HasKey(role => role.Id);
        entity.Property(role => role.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(role => role.Code).HasColumnName("code").HasMaxLength(30).IsRequired();
        entity.Property(role => role.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        entity.Property(role => role.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.HasIndex(role => role.Code).IsUnique();

        entity.HasData(
            new Role
            {
                Id = 1,
                Code = "ADMIN",
                Name = "Administrador",
                CreatedAt = DateTimeOffset.UnixEpoch
            },
            new Role
            {
                Id = 2,
                Code = "OPERATOR",
                Name = "Operador",
                CreatedAt = DateTimeOffset.UnixEpoch
            });
    }

    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<User>();

        entity.ToTable("users");
        entity.HasKey(user => user.Id);
        entity.Property(user => user.Id).HasColumnName("id");
        entity.Property(user => user.FullName).HasColumnName("full_name").HasMaxLength(160).IsRequired();
        entity.Property(user => user.RoleId).HasColumnName("role_id");
        entity.Property(user => user.PinLookup).HasColumnName("pin_lookup").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(user => user.PinHash).HasColumnName("pin_hash").IsRequired();
        entity.Property(user => user.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(user => user.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(user => user.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.HasIndex(user => user.PinLookup).IsUnique();
        entity.HasOne(user => user.Role)
            .WithMany(role => role.Users)
            .HasForeignKey(user => user.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureBusinessSettings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BusinessSettings>();
        entity.HasKey(settings => settings.Id);
        entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(settings => settings.BusinessName).HasColumnName("business_name").HasMaxLength(160).IsRequired();
        entity.Property(settings => settings.WarehouseName).HasColumnName("warehouse_name").HasMaxLength(120).IsRequired();
        entity.Property(settings => settings.WarehouseCode).HasColumnName("warehouse_code").HasMaxLength(30).IsRequired();
        entity.Property(settings => settings.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(100).IsRequired();
        entity.Property(settings => settings.WipReminderDays).HasColumnName("wip_reminder_days").HasDefaultValue(7);
        entity.ToTable("business_settings", table =>
        {
            table.HasCheckConstraint("ck_business_settings_singleton", "id = 1");
            table.HasCheckConstraint("ck_business_settings_wip_reminder_days", "wip_reminder_days BETWEEN 1 AND 365");
        });
        entity.Property(settings => settings.LogoFileName).HasColumnName("logo_file_name").HasMaxLength(100);
        entity.Property(settings => settings.LogoContentType).HasColumnName("logo_content_type").HasMaxLength(30);
        entity.Property(settings => settings.LogoHash).HasColumnName("logo_hash").HasMaxLength(64);
        entity.Property(settings => settings.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.Property(settings => settings.UpdatedByUserId).HasColumnName("updated_by_user_id");
        entity.HasOne(settings => settings.UpdatedByUser).WithMany().HasForeignKey(settings => settings.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUnit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Unit>();

        entity.ToTable("units", table =>
            table.HasCheckConstraint("ck_units_code_normalized", "code = upper(btrim(code)) AND code <> ''"));
        entity.HasKey(unit => unit.Id);
        entity.Property(unit => unit.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(unit => unit.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        entity.Property(unit => unit.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        entity.Property(unit => unit.AllowsDecimals).HasColumnName("allows_decimals").HasDefaultValue(true);
        entity.Property(unit => unit.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.HasIndex(unit => unit.Code).IsUnique();

        entity.HasData(
            new Unit { Id = 1, Code = "EA", Name = "Pieza", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 2, Code = "BX", Name = "Caja", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 3, Code = "BDL", Name = "Bulto", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 4, Code = "CTN", Name = "Caja de embarque", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 5, Code = "FT", Name = "Pie", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 6, Code = "GAL", Name = "Galón", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 7, Code = "5GAL", Name = "Recipiente de 5 galones", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 8, Code = "3GANG", Name = "Grupo de 3", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 9, Code = "KT", Name = "Kit", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 10, Code = "PR", Name = "Par", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 11, Code = "LB", Name = "Libra", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 12, Code = "RL", Name = "Rollo", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 13, Code = "SQFT", Name = "Pie cuadrado", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 14, Code = "MSI", Name = "Mil pulgadas cuadradas", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 15, Code = "YD", Name = "Yarda", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 16, Code = "IN", Name = "Pulgada", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 17, Code = "OZ", Name = "Onza", AllowsDecimals = true, IsActive = true },
            new Unit { Id = 18, Code = CatalogDefaults.UnassignedUnitCode, Name = CatalogDefaults.UnassignedUnitName, AllowsDecimals = true, IsActive = true });
    }

    private static void ConfigureProductType(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductType>();
        entity.ToTable("product_types", table =>
            table.HasCheckConstraint("ck_product_types_code_normalized", "code = upper(btrim(code)) AND code <> ''"));
        entity.HasKey(type => type.Id);
        entity.Property(type => type.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(type => type.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        entity.Property(type => type.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        entity.Property(type => type.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.HasIndex(type => type.Code).IsUnique();
        entity.HasData(
            new ProductType { Id = 1, Code = "FG", Name = "Producto terminado", IsActive = true },
            new ProductType { Id = 2, Code = "RAW", Name = "Materia prima", IsActive = true });
    }

    private static void ConfigureProductClass(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductClass>();
        entity.ToTable("product_classes", table =>
            table.HasCheckConstraint("ck_product_classes_code_normalized", "code = upper(btrim(code)) AND code <> ''"));
        entity.HasKey(productClass => productClass.Id);
        entity.Property(productClass => productClass.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(productClass => productClass.Code).HasColumnName("code").HasMaxLength(60).IsRequired();
        entity.Property(productClass => productClass.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        entity.Property(productClass => productClass.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.HasIndex(productClass => productClass.Code).IsUnique();

        var codes = new[]
        {
            "2-BBAGS", "AQUA", "AQUATANK", "BAYRIG", "BCF", "BERMS", "BIOSEAL", "BIOSEAL-EPI",
            "BODY BAGS", "BODY BAGS-HARD BAGS", "CBF", "DUMPSTER LINER", "FC-HARDBAGS", "IWM", "KPA",
            "MISC", "NUCLEAR BAGS", "PACKAGING", "RAD BAGS", "RM", "SPOUTED BAGS", "SUB", "SUB-COMP",
            "SUBASS", "SUBASSEMBLY", "UNDERGARMENTS"
        };
        entity.HasData(codes.Select((code, index) => new ProductClass
        {
            Id = checked((short)(index + 1)),
            Code = code,
            Name = code,
            IsActive = true
        }));
    }

    private static void ConfigureProduct(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Product>();

        entity.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_minimum_stock", "minimum_stock >= 0");
            table.HasCheckConstraint("ck_products_sku_normalized", "sku = upper(btrim(sku)) AND sku <> ''");
            table.HasCheckConstraint(
                "ck_products_external_reference_trimmed",
                "external_reference IS NULL OR (external_reference = btrim(external_reference) AND external_reference <> '')");
        });
        entity.HasKey(product => product.Id);
        entity.Property(product => product.Id).HasColumnName("id");
        entity.Property(product => product.Sku).HasColumnName("sku").HasMaxLength(60).IsRequired();
        entity.Property(product => product.Description).HasColumnName("description");
        entity.Property(product => product.ExternalReference).HasColumnName("external_reference").HasMaxLength(120);
        entity.Property(product => product.ProductTypeId).HasColumnName("product_type_id");
        entity.Property(product => product.ProductClassId).HasColumnName("product_class_id");
        entity.Property(product => product.BaseUnitId).HasColumnName("base_unit_id");
        entity.Property(product => product.MinimumStock).HasColumnName("minimum_stock").HasPrecision(18, 4);
        entity.Property(product => product.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(product => product.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(product => product.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.HasIndex(product => product.Sku).IsUnique();
        entity.HasOne(product => product.BaseUnit)
            .WithMany(unit => unit.Products)
            .HasForeignKey(product => product.BaseUnitId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(product => product.ProductType)
            .WithMany(type => type.Products)
            .HasForeignKey(product => product.ProductTypeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(product => product.ProductClass)
            .WithMany(productClass => productClass.Products)
            .HasForeignKey(product => product.ProductClassId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProductBarcode(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductBarcode>();

        entity.ToTable("product_barcodes", table =>
            table.HasCheckConstraint(
                "ck_product_barcodes_format",
                "format IN ('CODE_128', 'EAN_13', 'EAN_8', 'UPC_A', 'UPC_E', 'QR', 'OTHER')"));
        entity.HasKey(barcode => barcode.Id);
        entity.Property(barcode => barcode.Id).HasColumnName("id");
        entity.Property(barcode => barcode.ProductId).HasColumnName("product_id");
        entity.Property(barcode => barcode.Barcode).HasColumnName("barcode").HasMaxLength(100).IsRequired();
        entity.Property(barcode => barcode.Format).HasColumnName("format").HasMaxLength(30).HasDefaultValue("CODE_128");
        entity.Property(barcode => barcode.IsPrimary).HasColumnName("is_primary").HasDefaultValue(false);
        entity.Property(barcode => barcode.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(barcode => barcode.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.HasIndex(barcode => barcode.Barcode).IsUnique();
        entity.HasIndex(barcode => barcode.ProductId)
            .IsUnique()
            .HasFilter("is_primary = TRUE");
        entity.HasOne(barcode => barcode.Product)
            .WithMany(product => product.Barcodes)
            .HasForeignKey(barcode => barcode.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureLocation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Location>();

        entity.ToTable("locations", table =>
        {
            table.HasCheckConstraint("ck_locations_kind", "kind IN ('RACK', 'AREA')");
            table.HasCheckConstraint("ck_locations_operational_role", "operational_role IN ('STORAGE', 'WIP', 'OTHER')");
            table.HasCheckConstraint("ck_locations_wip_area", "operational_role <> 'WIP' OR kind = 'AREA'");
            table.HasCheckConstraint("ck_locations_code_normalized", "code = upper(btrim(code)) AND code <> ''");
            table.HasCheckConstraint("ck_locations_structure", "(kind = 'RACK' AND row_code ~ '^[A-Z]$' AND rack_number > 0 AND pallet_number BETWEEN 1 AND 9 AND code = row_code || '-' || rack_number::text || '-' || pallet_number::text) OR (kind = 'AREA' AND row_code IS NULL AND rack_number IS NULL AND pallet_number IS NULL AND code ~ '^[A-Z0-9]([A-Z0-9-]*[A-Z0-9])?$')");
            table.HasCheckConstraint("ck_locations_block", "(is_blocked = FALSE AND block_reason IS NULL) OR (is_active = TRUE AND is_blocked = TRUE AND block_reason IS NOT NULL AND btrim(block_reason) <> '')");
        });
        entity.HasKey(location => location.Id);
        entity.Property(location => location.Id).HasColumnName("id");
        entity.Property(location => location.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        entity.Property(location => location.Kind).HasColumnName("kind").HasMaxLength(10)
            .HasConversion(value => value == LocationKind.Rack ? "RACK" : "AREA",
                value => value == "RACK" ? LocationKind.Rack : LocationKind.Area);
        entity.Property(location => location.OperationalRole).HasColumnName("operational_role").HasMaxLength(10)
            .HasDefaultValue(LocationOperationalRole.Storage)
            .HasConversion(value => LocationRoleToDatabase(value), value => LocationRoleFromDatabase(value));
        entity.Property(location => location.RowCode).HasColumnName("row_code").HasMaxLength(1);
        entity.Property(location => location.RackNumber).HasColumnName("rack_number");
        entity.Property(location => location.PalletNumber).HasColumnName("pallet_number");
        entity.Property(location => location.Description).HasColumnName("description").HasMaxLength(200);
        entity.Property(location => location.IsBlocked).HasColumnName("is_blocked").HasDefaultValue(false);
        entity.Property(location => location.BlockReason).HasColumnName("block_reason").HasMaxLength(200);
        entity.Property(location => location.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(location => location.IsPhysicallyPresent).HasColumnName("is_physically_present").HasDefaultValue(true);
        entity.Property(location => location.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(location => location.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.Ignore(location => location.IsOperational);
        entity.Ignore(location => location.TracksInventory);
        entity.Ignore(location => location.IsWip);
        entity.Ignore(location => location.LevelNumber);
        entity.Ignore(location => location.HorizontalPosition);
        entity.HasIndex(location => location.Code).IsUnique();
        entity.HasIndex(location => new { location.RowCode, location.RackNumber, location.PalletNumber })
            .IsUnique().HasFilter("kind = 'RACK'");
    }

    private static void ConfigureLocationRackRevision(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<LocationRackRevision>();
        entity.ToTable("location_rack_revisions", table =>
        {
            table.HasCheckConstraint("ck_location_rack_revisions_row", "row_code ~ '^[A-Z]$'");
            table.HasCheckConstraint("ck_location_rack_revisions_rack", "rack_number > 0");
            table.HasCheckConstraint("ck_location_rack_revisions_reason", "reason = btrim(reason) AND reason <> ''");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id");
        entity.Property(item => item.OperationId).HasColumnName("operation_id");
        entity.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(item => item.RowCode).HasColumnName("row_code").HasMaxLength(1).IsRequired();
        entity.Property(item => item.RackNumber).HasColumnName("rack_number");
        entity.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        entity.Property(item => item.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb").IsRequired();
        entity.Property(item => item.AfterJson).HasColumnName("after_json").HasColumnType("jsonb").IsRequired();
        entity.Property(item => item.RequestedByUserId).HasColumnName("requested_by_user_id");
        entity.Property(item => item.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        entity.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        entity.HasIndex(item => item.OperationId).IsUnique();
        entity.HasIndex(item => new { item.RowCode, item.RackNumber, item.RecordedAt });
        entity.HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.AuthorizedByUser).WithMany().HasForeignKey(item => item.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProductLocationAssignment(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductLocationAssignment>();

        entity.ToTable("product_location_assignments");
        entity.HasKey(assignment => new { assignment.ProductId, assignment.LocationId });
        entity.Property(assignment => assignment.ProductId).HasColumnName("product_id");
        entity.Property(assignment => assignment.LocationId).HasColumnName("location_id");
        entity.Property(assignment => assignment.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(assignment => assignment.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(assignment => assignment.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.HasIndex(assignment => assignment.LocationId);
        entity.HasOne(assignment => assignment.Product)
            .WithMany(product => product.LocationAssignments)
            .HasForeignKey(assignment => assignment.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(assignment => assignment.Location)
            .WithMany(location => location.ProductAssignments)
            .HasForeignKey(assignment => assignment.LocationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProductLot(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductLot>();

        entity.ToTable("product_lots", table =>
            table.HasCheckConstraint(
                "ck_product_lots_normalized_number",
                "normalized_number = upper(btrim(normalized_number)) AND normalized_number <> ''"));
        entity.HasKey(lot => lot.Id);
        entity.Property(lot => lot.Id).HasColumnName("id");
        entity.Property(lot => lot.ProductId).HasColumnName("product_id");
        entity.Property(lot => lot.Number).HasColumnName("number").HasMaxLength(100).IsRequired();
        entity.Property(lot => lot.NormalizedNumber).HasColumnName("normalized_number").HasMaxLength(100).IsRequired();
        entity.Property(lot => lot.LotDate).HasColumnName("lot_date");
        entity.HasIndex(lot => new { lot.ProductId, lot.LotDate });
        entity.Property(lot => lot.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.HasIndex(lot => new { lot.ProductId, lot.NormalizedNumber }).IsUnique();
        entity.HasOne(lot => lot.Product)
            .WithMany(product => product.Lots)
            .HasForeignKey(lot => lot.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInventoryMovement(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InventoryMovement>();

        entity.ToTable("inventory_movements", table =>
        {
            table.HasCheckConstraint(
                "ck_inventory_movements_type",
                "type IN ('ENTRY', 'EXIT', 'TRANSFER', 'ADJUSTMENT')");
            table.HasCheckConstraint("ck_inventory_movements_purpose",
                "purpose IN ('STANDARD', 'GENERAL_EXIT', 'PRODUCTION_ISSUE', 'WIP_WAREHOUSE_RETURN', 'WIP_CONSUMPTION', 'WIP_SUPPLIER_RETURN', 'CYCLE_COUNT_ADJUSTMENT', 'DOCUMENT_RECEIPT')");
            table.HasCheckConstraint("ck_inventory_movements_operational_shape",
                "(purpose = 'PRODUCTION_ISSUE' AND type IN ('ENTRY', 'EXIT', 'TRANSFER') AND operational_area_id IS NOT NULL) OR " +
                "(purpose = 'GENERAL_EXIT' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR " +
                "(purpose = 'WIP_WAREHOUSE_RETURN' AND ((type IN ('ENTRY', 'EXIT') AND operational_area_id IS NULL) OR (type = 'TRANSFER' AND operational_area_id IS NOT NULL))) OR " +
                "(purpose = 'WIP_CONSUMPTION' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL) OR " +
                "(purpose = 'WIP_SUPPLIER_RETURN' AND type IN ('ENTRY', 'EXIT') AND operational_area_id IS NOT NULL AND NULLIF(BTRIM(reference), '') IS NOT NULL) OR " +
                "(purpose = 'STANDARD' AND operational_area_id IS NULL) OR " +
                "(purpose = 'CYCLE_COUNT_ADJUSTMENT' AND type = 'ADJUSTMENT' AND operational_area_id IS NULL) OR " +
                "(purpose = 'DOCUMENT_RECEIPT' AND type = 'ENTRY' AND operational_area_id IS NULL)");
        });
        entity.HasKey(movement => movement.Id);
        entity.Property(movement => movement.Id).HasColumnName("id");
        entity.Property(movement => movement.OperationId).HasColumnName("operation_id");
        entity.Property(movement => movement.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(movement => movement.Type).HasColumnName("type").HasMaxLength(20)
            .HasConversion(
                value => MovementTypeToDatabase(value),
                value => MovementTypeFromDatabase(value));
        entity.Property(movement => movement.Purpose).HasColumnName("purpose").HasMaxLength(30)
            .HasDefaultValue(InventoryMovementPurpose.Standard)
            .HasConversion(value => MovementPurposeToDatabase(value), value => MovementPurposeFromDatabase(value));
        entity.Property(movement => movement.OperationalAreaId).HasColumnName("operational_area_id");
        entity.Property(movement => movement.ResponsibleUserId).HasColumnName("responsible_user_id");
        entity.Property(movement => movement.Reference).HasColumnName("reference").HasMaxLength(120);
        entity.Property(movement => movement.Notes).HasColumnName("notes").HasMaxLength(500);
        entity.Property(movement => movement.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");
        entity.Property(movement => movement.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        entity.HasIndex(movement => movement.OperationId).IsUnique();
        entity.HasIndex(movement => movement.OccurredAt);
        entity.HasIndex(movement => new { movement.ResponsibleUserId, movement.OccurredAt });
        entity.HasIndex(movement => new { movement.Purpose, movement.OccurredAt });
        entity.HasIndex(movement => movement.OperationalAreaId);
        entity.HasOne(movement => movement.ResponsibleUser)
            .WithMany()
            .HasForeignKey(movement => movement.ResponsibleUserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(movement => movement.OperationalArea)
            .WithMany()
            .HasForeignKey(movement => movement.OperationalAreaId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInventoryMovementLine(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InventoryMovementLine>();

        entity.ToTable("inventory_movement_lines", table =>
            table.HasCheckConstraint("ck_inventory_movement_lines_number", "line_number > 0"));
        entity.HasKey(line => line.Id);
        entity.Property(line => line.Id).HasColumnName("id");
        entity.Property(line => line.MovementId).HasColumnName("movement_id");
        entity.Property(line => line.LineNumber).HasColumnName("line_number");
        entity.Property(line => line.ProductId).HasColumnName("product_id");
        entity.Property(line => line.UnitId).HasColumnName("unit_id");
        entity.Property(line => line.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        entity.Property(line => line.SourceLocationId).HasColumnName("source_location_id");
        entity.Property(line => line.DestinationLocationId).HasColumnName("destination_location_id");
        entity.Property(line => line.LotId).HasColumnName("lot_id");
        entity.Property(line => line.LotAllocationMode).HasColumnName("lot_allocation_mode").HasDefaultValue(InventoryLotAllocationMode.None)
            .HasConversion(value => LotAllocationModeToDatabase(value), value => LotAllocationModeFromDatabase(value));
        entity.Property(line => line.PreviousQuantity).HasColumnName("previous_quantity").HasPrecision(18, 4);
        entity.Property(line => line.AdjustmentDelta).HasColumnName("adjustment_delta").HasPrecision(18, 4);
        entity.HasIndex(line => new { line.MovementId, line.LineNumber }).IsUnique();
        entity.HasIndex(line => line.ProductId);
        entity.HasIndex(line => line.SourceLocationId);
        entity.HasIndex(line => line.DestinationLocationId);
        entity.HasOne(line => line.Movement).WithMany(movement => movement.Lines)
            .HasForeignKey(line => line.MovementId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(line => line.Product).WithMany()
            .HasForeignKey(line => line.ProductId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(line => line.Unit).WithMany()
            .HasForeignKey(line => line.UnitId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(line => line.SourceLocation).WithMany()
            .HasForeignKey(line => line.SourceLocationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(line => line.DestinationLocation).WithMany()
            .HasForeignKey(line => line.DestinationLocationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(line => line.Lot).WithMany()
            .HasForeignKey(line => line.LotId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInventoryBalanceChange(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InventoryBalanceChange>();

        entity.ToTable("inventory_balance_changes", table =>
            table.HasCheckConstraint(
                "ck_inventory_balance_changes_arithmetic",
                "previous_quantity + delta_quantity = resulting_quantity"));
        entity.HasKey(change => change.Id);
        entity.Property(change => change.Id).HasColumnName("id");
        entity.Property(change => change.MovementLineId).HasColumnName("movement_line_id");
        entity.Property(change => change.LocationId).HasColumnName("location_id");
        entity.Property(change => change.LotId).HasColumnName("lot_id");
        entity.Property(change => change.LotNumberSnapshot).HasColumnName("lot_number_snapshot").HasMaxLength(100);
        entity.Property(change => change.LotDateSnapshot).HasColumnName("lot_date_snapshot");
        entity.Property(change => change.DeltaQuantity).HasColumnName("delta_quantity").HasPrecision(18, 4);
        entity.Property(change => change.PreviousQuantity).HasColumnName("previous_quantity").HasPrecision(18, 4);
        entity.Property(change => change.ResultingQuantity).HasColumnName("resulting_quantity").HasPrecision(18, 4);
        entity.HasIndex(change => change.MovementLineId);
        entity.HasIndex(change => change.LocationId);
        entity.HasOne(change => change.MovementLine).WithMany(line => line.BalanceChanges)
            .HasForeignKey(change => change.MovementLineId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(change => change.Location).WithMany()
            .HasForeignKey(change => change.LocationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(change => change.Lot).WithMany()
            .HasForeignKey(change => change.LotId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProductLotDateChange(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProductLotDateChange>();
        entity.ToTable("product_lot_date_changes");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id");
        entity.Property(item => item.OperationId).HasColumnName("operation_id");
        entity.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(item => item.ProductLotId).HasColumnName("product_lot_id");
        entity.Property(item => item.PreviousLotDate).HasColumnName("previous_lot_date");
        entity.Property(item => item.NewLotDate).HasColumnName("new_lot_date");
        entity.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        entity.Property(item => item.RequestedByUserId).HasColumnName("requested_by_user_id");
        entity.Property(item => item.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        entity.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        entity.HasIndex(item => item.OperationId).IsUnique();
        entity.HasIndex(item => new { item.ProductLotId, item.RecordedAt });
        entity.HasOne(item => item.ProductLot).WithMany().HasForeignKey(item => item.ProductLotId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.AuthorizedByUser).WithMany().HasForeignKey(item => item.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInventoryBalance(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InventoryBalance>();

        entity.ToTable("inventory_balances");
        entity.HasKey(balance => balance.Id);
        entity.Property(balance => balance.Id).HasColumnName("id");
        entity.Property(balance => balance.ProductId).HasColumnName("product_id");
        entity.Property(balance => balance.LocationId).HasColumnName("location_id");
        entity.Property(balance => balance.LotId).HasColumnName("lot_id");
        entity.Property(balance => balance.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        entity.Property(balance => balance.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.Property(balance => balance.Version).IsRowVersion().HasColumnName("xmin");
        entity.HasIndex(balance => new { balance.ProductId, balance.LocationId })
            .IsUnique().HasFilter("lot_id IS NULL");
        entity.HasIndex(balance => new { balance.ProductId, balance.LocationId, balance.LotId })
            .IsUnique().HasFilter("lot_id IS NOT NULL");
        entity.HasIndex(balance => balance.LocationId);
        entity.HasIndex(balance => balance.Quantity).HasFilter("quantity < 0");
        entity.HasOne(balance => balance.Product).WithMany()
            .HasForeignKey(balance => balance.ProductId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(balance => balance.Location).WithMany()
            .HasForeignKey(balance => balance.LocationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(balance => balance.Lot).WithMany()
            .HasForeignKey(balance => balance.LotId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWarehouseMap(ModelBuilder modelBuilder)
    {
        var layout = modelBuilder.Entity<WarehouseMapLayout>();
        layout.ToTable("warehouse_map_layouts", table =>
        {
            table.HasCheckConstraint("ck_warehouse_map_layout_singleton", "id = 1");
            table.HasCheckConstraint("ck_warehouse_map_layout_scale", "scale_units_per_inch IS NULL OR scale_units_per_inch > 0");
            table.HasCheckConstraint("ck_warehouse_map_layout_measurement", "measurement_system IN ('IMPERIAL', 'METRIC')");
        });
        layout.HasKey(item => item.Id);
        layout.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        layout.Property(item => item.Version).HasColumnName("version");
        layout.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        layout.Property(item => item.UpdatedByUserId).HasColumnName("updated_by_user_id");
        layout.Property(item => item.ScaleUnitsPerInch).HasColumnName("scale_units_per_inch").HasPrecision(12, 6);
        layout.Property(item => item.MeasurementSystem).HasColumnName("measurement_system").HasMaxLength(10)
            .HasConversion(value => value == WarehouseMapMeasurementSystem.Imperial ? "IMPERIAL" : "METRIC",
                value => value == "METRIC" ? WarehouseMapMeasurementSystem.Metric : WarehouseMapMeasurementSystem.Imperial)
            .HasDefaultValue(WarehouseMapMeasurementSystem.Imperial);
        layout.Ignore(item => item.RowVersion);
        layout.HasOne(item => item.UpdatedByUser).WithMany().HasForeignKey(item => item.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var element = modelBuilder.Entity<WarehouseMapElement>();
        element.ToTable("warehouse_map_elements", table =>
        {
            table.HasCheckConstraint("ck_warehouse_map_element_identity", "(kind = 'RACK' AND row_code ~ '^[A-Z]$' AND rack_number > 0 AND location_id IS NULL) OR (kind = 'AREA' AND row_code IS NULL AND rack_number IS NULL AND location_id IS NOT NULL)");
            table.HasCheckConstraint("ck_warehouse_map_element_geometry", "x >= 0 AND y >= 0 AND width > 0 AND height > 0 AND rotation IN (0, 90, 180, 270)");
        });
        element.HasKey(item => item.Id);
        element.Property(item => item.Id).HasColumnName("id");
        element.Property(item => item.LayoutId).HasColumnName("layout_id");
        element.Property(item => item.Kind).HasColumnName("kind").HasMaxLength(10).HasConversion(value => value == WarehouseMapElementKind.Rack ? "RACK" : "AREA", value => value == "RACK" ? WarehouseMapElementKind.Rack : WarehouseMapElementKind.Area);
        element.Property(item => item.RowCode).HasColumnName("row_code").HasMaxLength(1);
        element.Property(item => item.RackNumber).HasColumnName("rack_number");
        element.Property(item => item.LocationId).HasColumnName("location_id");
        element.Property(item => item.X).HasColumnName("x").HasPrecision(8, 2);
        element.Property(item => item.Y).HasColumnName("y").HasPrecision(8, 2);
        element.Property(item => item.Width).HasColumnName("width").HasPrecision(8, 2);
        element.Property(item => item.Height).HasColumnName("height").HasPrecision(8, 2);
        element.Property(item => item.Rotation).HasColumnName("rotation");
        element.Property(item => item.ZIndex).HasColumnName("z_index");
        element.Property(item => item.IsVisible).HasColumnName("is_visible").HasDefaultValue(true);
        element.HasIndex(item => new { item.LayoutId, item.RowCode, item.RackNumber }).IsUnique().HasFilter("kind = 'RACK'");
        element.HasIndex(item => item.LocationId).IsUnique().HasFilter("kind = 'AREA'");
        element.HasOne(item => item.Layout).WithMany(item => item.Elements).HasForeignKey(item => item.LayoutId).OnDelete(DeleteBehavior.Cascade);
        element.HasOne(item => item.Location).WithMany().HasForeignKey(item => item.LocationId).OnDelete(DeleteBehavior.Restrict);

        var layer = modelBuilder.Entity<WarehouseMapLayer>();
        layer.ToTable("warehouse_map_layers", table =>
            table.HasCheckConstraint("ck_warehouse_map_layer_code", "code IN ('STRUCTURE', 'AISLES', 'ZONES', 'TEXT', 'DIMENSIONS', 'OPERATIONS')"));
        layer.HasKey(item => item.Id);
        layer.Property(item => item.Id).HasColumnName("id");
        layer.Property(item => item.LayoutId).HasColumnName("layout_id");
        layer.Property(item => item.Code).HasColumnName("code").HasMaxLength(16).HasConversion(
            value => value == WarehouseMapLayerCode.Structure ? "STRUCTURE" :
                value == WarehouseMapLayerCode.Aisles ? "AISLES" :
                value == WarehouseMapLayerCode.Zones ? "ZONES" :
                value == WarehouseMapLayerCode.Text ? "TEXT" :
                value == WarehouseMapLayerCode.Dimensions ? "DIMENSIONS" : "OPERATIONS",
            value => value == "STRUCTURE" ? WarehouseMapLayerCode.Structure :
                value == "AISLES" ? WarehouseMapLayerCode.Aisles :
                value == "ZONES" ? WarehouseMapLayerCode.Zones :
                value == "TEXT" ? WarehouseMapLayerCode.Text :
                value == "DIMENSIONS" ? WarehouseMapLayerCode.Dimensions : WarehouseMapLayerCode.Operations);
        layer.Property(item => item.Name).HasColumnName("name").HasMaxLength(40).IsRequired();
        layer.Property(item => item.SortOrder).HasColumnName("sort_order");
        layer.Property(item => item.IsLocked).HasColumnName("is_locked").HasDefaultValue(true);
        layer.HasIndex(item => new { item.LayoutId, item.Code }).IsUnique();
        layer.HasOne(item => item.Layout).WithMany(item => item.Layers).HasForeignKey(item => item.LayoutId).OnDelete(DeleteBehavior.Cascade);

        var architectural = modelBuilder.Entity<WarehouseMapArchitecturalElement>();
        architectural.ToTable("warehouse_map_architectural_elements", table =>
        {
            table.HasCheckConstraint("ck_warehouse_map_architectural_kind", "kind IN ('RECTANGLE', 'POLYLINE', 'TEXT')");
            table.HasCheckConstraint("ck_warehouse_map_architectural_style", "stroke_token IN ('NONE', 'SECONDARY', 'PRIMARY', 'INFO', 'WARNING', 'SUCCESS') AND fill_token IN ('NONE', 'SECONDARY', 'PRIMARY', 'INFO', 'WARNING', 'SUCCESS') AND stroke_width >= 0 AND stroke_width <= 12");
        });
        architectural.HasKey(item => item.Id);
        architectural.Property(item => item.Id).HasColumnName("id");
        architectural.Property(item => item.LayoutId).HasColumnName("layout_id");
        architectural.Property(item => item.LayerId).HasColumnName("layer_id");
        architectural.Property(item => item.GroupId).HasColumnName("group_id");
        architectural.Property(item => item.Kind).HasColumnName("kind").HasMaxLength(12).HasConversion(
            value => value == WarehouseMapArchitecturalElementKind.Rectangle ? "RECTANGLE" :
                value == WarehouseMapArchitecturalElementKind.Polyline ? "POLYLINE" : "TEXT",
            value => value == "RECTANGLE" ? WarehouseMapArchitecturalElementKind.Rectangle :
                value == "POLYLINE" ? WarehouseMapArchitecturalElementKind.Polyline : WarehouseMapArchitecturalElementKind.Text);
        architectural.Property(item => item.Label).HasColumnName("label").HasMaxLength(120);
        architectural.Property(item => item.GeometryJson).HasColumnName("geometry_json").HasColumnType("jsonb").IsRequired();
        architectural.Property(item => item.StrokeToken).HasColumnName("stroke_token").HasMaxLength(16).IsRequired();
        architectural.Property(item => item.FillToken).HasColumnName("fill_token").HasMaxLength(16).IsRequired();
        architectural.Property(item => item.StrokeWidth).HasColumnName("stroke_width").HasPrecision(5, 2);
        architectural.Property(item => item.IsDashed).HasColumnName("is_dashed");
        architectural.Property(item => item.ZIndex).HasColumnName("z_index");
        architectural.Property(item => item.IsLocked).HasColumnName("is_locked");
        architectural.Property(item => item.IsArchived).HasColumnName("is_archived").HasDefaultValue(false);
        architectural.HasIndex(item => new { item.LayoutId, item.LayerId, item.ZIndex });
        architectural.HasIndex(item => new { item.LayoutId, item.GroupId }).HasFilter("group_id IS NOT NULL");
        architectural.HasIndex(item => new { item.LayoutId, item.IsArchived });
        architectural.HasOne(item => item.Layout).WithMany(item => item.ArchitecturalElements).HasForeignKey(item => item.LayoutId).OnDelete(DeleteBehavior.Cascade);
        architectural.HasOne(item => item.Layer).WithMany(item => item.ArchitecturalElements).HasForeignKey(item => item.LayerId).OnDelete(DeleteBehavior.Restrict);

        var reference = modelBuilder.Entity<WarehouseMapReferenceImage>();
        reference.ToTable("warehouse_map_reference_images", table =>
        {
            table.HasCheckConstraint("ck_warehouse_map_reference_file", "content_type IN ('image/png', 'image/jpeg', 'image/webp') AND pixel_width BETWEEN 1 AND 4096 AND pixel_height BETWEEN 1 AND 4096");
            table.HasCheckConstraint("ck_warehouse_map_reference_geometry", "x >= 0 AND y >= 0 AND width > 0 AND height > 0 AND rotation IN (0, 90, 180, 270) AND opacity BETWEEN 0.05 AND 1");
            table.HasCheckConstraint("ck_warehouse_map_reference_calibration", "(calibration_a_x IS NULL AND calibration_a_y IS NULL AND calibration_b_x IS NULL AND calibration_b_y IS NULL AND calibration_distance_inches IS NULL) OR (calibration_a_x BETWEEN 0 AND 1 AND calibration_a_y BETWEEN 0 AND 1 AND calibration_b_x BETWEEN 0 AND 1 AND calibration_b_y BETWEEN 0 AND 1 AND calibration_distance_inches > 0)");
        });
        reference.HasKey(item => item.Id);
        reference.Property(item => item.Id).HasColumnName("id");
        reference.Property(item => item.LayoutId).HasColumnName("layout_id");
        reference.Property(item => item.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(160).IsRequired();
        reference.Property(item => item.StoredFileName).HasColumnName("stored_file_name").HasMaxLength(80).IsRequired();
        reference.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(20).IsRequired();
        reference.Property(item => item.Sha256).HasColumnName("sha256").HasMaxLength(64).IsFixedLength().IsRequired();
        reference.Property(item => item.PixelWidth).HasColumnName("pixel_width");
        reference.Property(item => item.PixelHeight).HasColumnName("pixel_height");
        reference.Property(item => item.X).HasColumnName("x").HasPrecision(9, 3);
        reference.Property(item => item.Y).HasColumnName("y").HasPrecision(9, 3);
        reference.Property(item => item.Width).HasColumnName("width").HasPrecision(9, 3);
        reference.Property(item => item.Height).HasColumnName("height").HasPrecision(9, 3);
        reference.Property(item => item.Rotation).HasColumnName("rotation");
        reference.Property(item => item.Opacity).HasColumnName("opacity").HasPrecision(4, 3).HasDefaultValue(0.35m);
        reference.Property(item => item.IsLocked).HasColumnName("is_locked").HasDefaultValue(true);
        reference.Property(item => item.IsArchived).HasColumnName("is_archived").HasDefaultValue(false);
        reference.Property(item => item.CalibrationAX).HasColumnName("calibration_a_x").HasPrecision(8, 6);
        reference.Property(item => item.CalibrationAY).HasColumnName("calibration_a_y").HasPrecision(8, 6);
        reference.Property(item => item.CalibrationBX).HasColumnName("calibration_b_x").HasPrecision(8, 6);
        reference.Property(item => item.CalibrationBY).HasColumnName("calibration_b_y").HasPrecision(8, 6);
        reference.Property(item => item.CalibrationDistanceInches).HasColumnName("calibration_distance_inches").HasPrecision(12, 4);
        reference.Property(item => item.CreatedByUserId).HasColumnName("created_by_user_id");
        reference.Property(item => item.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        reference.HasIndex(item => new { item.LayoutId, item.IsArchived }).IsUnique().HasFilter("is_archived = false");
        reference.HasIndex(item => new { item.LayoutId, item.Sha256 }).IsUnique();
        reference.HasOne(item => item.Layout).WithMany(item => item.ReferenceImages).HasForeignKey(item => item.LayoutId).OnDelete(DeleteBehavior.Restrict);
        reference.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var revision = modelBuilder.Entity<WarehouseMapRevision>();
        revision.ToTable("warehouse_map_revisions");
        revision.HasKey(item => item.Id);
        revision.Property(item => item.Id).HasColumnName("id");
        revision.Property(item => item.OperationId).HasColumnName("operation_id");
        revision.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        revision.Property(item => item.PreviousVersion).HasColumnName("previous_version");
        revision.Property(item => item.NewVersion).HasColumnName("new_version");
        revision.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(500);
        revision.Property(item => item.ChangesJson).HasColumnName("changes_json").HasColumnType("jsonb").IsRequired();
        revision.Property(item => item.RequestedByUserId).HasColumnName("requested_by_user_id");
        revision.Property(item => item.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        revision.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        revision.HasIndex(item => item.OperationId).IsUnique();
        revision.HasIndex(item => item.RecordedAt);
        revision.HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne(item => item.AuthorizedByUser).WithMany().HasForeignKey(item => item.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCycleCounts(ModelBuilder modelBuilder)
    {
        var campaign = modelBuilder.Entity<CycleCountCampaign>();
        campaign.ToTable("cycle_count_campaigns");
        campaign.HasKey(item => item.Id);
        campaign.Property(item => item.Id).HasColumnName("id");
        campaign.Property(item => item.OperationId).HasColumnName("operation_id");
        campaign.Property(item => item.Number).HasColumnName("number").ValueGeneratedOnAdd();
        campaign.Property(item => item.Title).HasColumnName("title").HasMaxLength(160);
        campaign.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(500);
        campaign.Property(item => item.Status).HasColumnName("status").HasMaxLength(30)
            .HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<CycleCountCampaignStatus>(value, true));
        campaign.Property(item => item.CreatedByUserId).HasColumnName("created_by_user_id");
        campaign.Property(item => item.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        campaign.Property(item => item.ReleasedAt).HasColumnName("released_at");
        campaign.Property(item => item.CompletedAt).HasColumnName("completed_at");
        campaign.Property(item => item.CancelledAt).HasColumnName("cancelled_at");
        campaign.Property(item => item.LastActionByUserId).HasColumnName("last_action_by_user_id");
        campaign.HasIndex(item => item.Number).IsUnique();
        campaign.HasIndex(item => item.OperationId).IsUnique();
        campaign.HasIndex(item => new { item.Status, item.CreatedAt });
        campaign.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        campaign.HasOne(item => item.LastActionByUser).WithMany().HasForeignKey(item => item.LastActionByUserId).OnDelete(DeleteBehavior.Restrict);

        var location = modelBuilder.Entity<CycleCountLocation>();
        location.ToTable("cycle_count_locations");
        location.HasKey(item => item.Id);
        location.Property(item => item.Id).HasColumnName("id");
        location.Property(item => item.CampaignId).HasColumnName("campaign_id");
        location.Property(item => item.LocationId).HasColumnName("location_id");
        location.Property(item => item.SortOrder).HasColumnName("sort_order");
        location.Property(item => item.Status).HasColumnName("status").HasMaxLength(30)
            .HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<CycleCountLocationStatus>(value, true));
        location.Property(item => item.AdjustmentMovementId).HasColumnName("adjustment_movement_id");
        location.Property(item => item.AdjustmentReason).HasColumnName("adjustment_reason").HasMaxLength(40)
            .HasConversion(value => value == null ? null : value.ToString()!.ToUpperInvariant(), value => string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<CycleCountAdjustmentReason>(value, true));
        location.Property(item => item.AdjustmentReasonNotes).HasColumnName("adjustment_reason_notes").HasMaxLength(500);
        location.Property(item => item.LastActionByUserId).HasColumnName("last_action_by_user_id");
        location.Property(item => item.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        location.Property(item => item.CompletedAt).HasColumnName("completed_at");
        location.HasIndex(item => new { item.CampaignId, item.LocationId }).IsUnique();
        location.HasIndex(item => new { item.LocationId, item.Status });
        location.HasIndex(item => item.AdjustmentMovementId).IsUnique().HasFilter("adjustment_movement_id IS NOT NULL");
        location.HasOne(item => item.Campaign).WithMany(item => item.Locations).HasForeignKey(item => item.CampaignId).OnDelete(DeleteBehavior.Restrict);
        location.HasOne(item => item.Location).WithMany().HasForeignKey(item => item.LocationId).OnDelete(DeleteBehavior.Restrict);
        location.HasOne(item => item.AdjustmentMovement).WithMany().HasForeignKey(item => item.AdjustmentMovementId).OnDelete(DeleteBehavior.Restrict);
        location.HasOne(item => item.LastActionByUser).WithMany().HasForeignKey(item => item.LastActionByUserId).OnDelete(DeleteBehavior.Restrict);

        var attempt = modelBuilder.Entity<CycleCountAttempt>();
        attempt.ToTable("cycle_count_attempts");
        attempt.HasKey(item => item.Id);
        attempt.Property(item => item.Id).HasColumnName("id");
        attempt.Property(item => item.OperationId).HasColumnName("operation_id");
        attempt.Property(item => item.SubmissionOperationId).HasColumnName("submission_operation_id");
        attempt.Property(item => item.CycleCountLocationId).HasColumnName("cycle_count_location_id");
        attempt.Property(item => item.AttemptNumber).HasColumnName("attempt_number");
        attempt.Property(item => item.Status).HasColumnName("status").HasMaxLength(20)
            .HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<CycleCountAttemptStatus>(value, true));
        attempt.Property(item => item.StartedByUserId).HasColumnName("started_by_user_id");
        attempt.Property(item => item.StartedAt).HasColumnName("started_at").HasDefaultValueSql("now()");
        attempt.Property(item => item.SubmittedByUserId).HasColumnName("submitted_by_user_id");
        attempt.Property(item => item.SubmittedAt).HasColumnName("submitted_at");
        attempt.HasIndex(item => item.OperationId).IsUnique();
        attempt.HasIndex(item => item.SubmissionOperationId).IsUnique().HasFilter("submission_operation_id IS NOT NULL");
        attempt.HasIndex(item => new { item.CycleCountLocationId, item.AttemptNumber }).IsUnique();
        attempt.HasOne(item => item.CycleCountLocation).WithMany(item => item.Attempts).HasForeignKey(item => item.CycleCountLocationId).OnDelete(DeleteBehavior.Restrict);
        attempt.HasOne(item => item.StartedByUser).WithMany().HasForeignKey(item => item.StartedByUserId).OnDelete(DeleteBehavior.Restrict);
        attempt.HasOne(item => item.SubmittedByUser).WithMany().HasForeignKey(item => item.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);

        var entry = modelBuilder.Entity<CycleCountEntry>();
        entry.ToTable("cycle_count_entries");
        entry.HasKey(item => item.Id);
        entry.Property(item => item.Id).HasColumnName("id");
        entry.Property(item => item.CycleCountAttemptId).HasColumnName("cycle_count_attempt_id");
        entry.Property(item => item.ProductId).HasColumnName("product_id");
        entry.Property(item => item.UnitId).HasColumnName("unit_id");
        entry.Property(item => item.ExpectedQuantity).HasColumnName("expected_quantity").HasPrecision(18, 4);
        entry.Property(item => item.ExpectedBalanceVersion).HasColumnName("expected_balance_version");
        entry.Property(item => item.CountedQuantity).HasColumnName("counted_quantity").HasPrecision(18, 4);
        entry.Property(item => item.IsUnexpectedProduct).HasColumnName("is_unexpected_product").HasDefaultValue(false);
        entry.HasIndex(item => new { item.CycleCountAttemptId, item.ProductId }).IsUnique();
        entry.HasOne(item => item.CycleCountAttempt).WithMany(item => item.Entries).HasForeignKey(item => item.CycleCountAttemptId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne(item => item.Product).WithMany().HasForeignKey(item => item.ProductId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne(item => item.Unit).WithMany().HasForeignKey(item => item.UnitId).OnDelete(DeleteBehavior.Restrict);

        var action = modelBuilder.Entity<CycleCountAction>();
        action.ToTable("cycle_count_actions");
        action.HasKey(item => item.Id);
        action.Property(item => item.Id).HasColumnName("id");
        action.Property(item => item.OperationId).HasColumnName("operation_id");
        action.Property(item => item.CampaignId).HasColumnName("campaign_id");
        action.Property(item => item.CycleCountLocationId).HasColumnName("cycle_count_location_id");
        action.Property(item => item.CycleCountAttemptId).HasColumnName("cycle_count_attempt_id");
        action.Property(item => item.ReviewBatchId).HasColumnName("review_batch_id");
        action.Property(item => item.Type).HasColumnName("type").HasMaxLength(30)
            .HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<CycleCountActionType>(value, true));
        action.Property(item => item.ResponsibleUserId).HasColumnName("responsible_user_id");
        action.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(500);
        action.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        action.HasIndex(item => new { item.CampaignId, item.RecordedAt });
        action.HasIndex(item => item.OperationId).IsUnique().HasFilter("operation_id IS NOT NULL");
        action.HasOne(item => item.Campaign).WithMany().HasForeignKey(item => item.CampaignId).OnDelete(DeleteBehavior.Restrict);
        action.HasOne(item => item.CycleCountLocation).WithMany().HasForeignKey(item => item.CycleCountLocationId).OnDelete(DeleteBehavior.Restrict);
        action.HasOne(item => item.CycleCountAttempt).WithMany().HasForeignKey(item => item.CycleCountAttemptId).OnDelete(DeleteBehavior.Restrict);
        action.HasOne(item => item.ReviewBatch).WithMany(item => item.Actions).HasForeignKey(item => item.ReviewBatchId).OnDelete(DeleteBehavior.Restrict);

        var reviewBatch = modelBuilder.Entity<CycleCountReviewBatch>();
        reviewBatch.ToTable("cycle_count_review_batches");
        reviewBatch.HasKey(item => item.Id);
        reviewBatch.Property(item => item.Id).HasColumnName("id");
        reviewBatch.Property(item => item.OperationId).HasColumnName("operation_id");
        reviewBatch.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
        reviewBatch.Property(item => item.CampaignId).HasColumnName("campaign_id");
        reviewBatch.Property(item => item.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        reviewBatch.Property(item => item.AuthorizedAt).HasColumnName("authorized_at").HasDefaultValueSql("now()");
        reviewBatch.HasIndex(item => item.OperationId).IsUnique();
        reviewBatch.HasIndex(item => new { item.CampaignId, item.AuthorizedAt });
        reviewBatch.HasOne(item => item.Campaign).WithMany().HasForeignKey(item => item.CampaignId).OnDelete(DeleteBehavior.Restrict);
        reviewBatch.HasOne(item => item.AuthorizedByUser).WithMany().HasForeignKey(item => item.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
        action.HasOne(item => item.ResponsibleUser).WithMany().HasForeignKey(item => item.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOperationalExceptions(ModelBuilder modelBuilder)
    {
        var exceptionCase = modelBuilder.Entity<OperationalExceptionCase>();
        exceptionCase.ToTable("operational_exception_cases", table =>
        {
            table.HasCheckConstraint("ck_operational_exception_cases_category", "category IN ('NEGATIVE_INVENTORY','BELOW_MINIMUM','UNASSIGNED_BALANCE','RESTRICTED_INVENTORY','STAGNANT_INVENTORY','CYCLE_COUNT_STALE','CYCLE_COUNT_PENDING','AGED_WIP')");
            table.HasCheckConstraint("ck_operational_exception_cases_severity", "severity IN ('CRITICAL','WARNING','INFORMATION')");
            table.HasCheckConstraint("ck_operational_exception_cases_status", "status IN ('NEW','IN_PROGRESS','WAITING','RESOLVED')");
            table.HasCheckConstraint("ck_operational_exception_cases_resolution", "(status = 'RESOLVED' AND resolved_at IS NOT NULL) OR (status <> 'RESOLVED' AND resolved_at IS NULL)");
        });
        exceptionCase.HasKey(item => item.Id);
        exceptionCase.Property(item => item.Id).HasColumnName("id");
        exceptionCase.Property(item => item.Category).HasColumnName("category").HasMaxLength(30)
            .HasConversion(value => ExceptionCategoryToDatabase(value), value => ExceptionCategoryFromDatabase(value));
        exceptionCase.Property(item => item.Severity).HasColumnName("severity").HasMaxLength(20)
            .HasConversion(value => value.ToString().ToUpperInvariant(), value => Enum.Parse<OperationalExceptionSeverity>(value, true));
        exceptionCase.Property(item => item.ConditionKey).HasColumnName("condition_key").HasMaxLength(220).IsRequired();
        exceptionCase.Property(item => item.Status).HasColumnName("status").HasMaxLength(20)
            .HasConversion(value => value == OperationalExceptionStatus.InProgress ? "IN_PROGRESS" : value.ToString().ToUpperInvariant(), value => value == "IN_PROGRESS" ? OperationalExceptionStatus.InProgress : Enum.Parse<OperationalExceptionStatus>(value, true));
        exceptionCase.Property(item => item.ProductId).HasColumnName("product_id");
        exceptionCase.Property(item => item.LocationId).HasColumnName("location_id");
        exceptionCase.Property(item => item.CycleCountLocationId).HasColumnName("cycle_count_location_id");
        exceptionCase.Property(item => item.PrimaryText).HasColumnName("primary_text").HasMaxLength(160).IsRequired();
        exceptionCase.Property(item => item.SecondaryText).HasColumnName("secondary_text").HasMaxLength(200).IsRequired();
        exceptionCase.Property(item => item.ValueText).HasColumnName("value_text").HasMaxLength(200);
        exceptionCase.Property(item => item.TargetUrl).HasColumnName("target_url").HasMaxLength(1000).IsRequired();
        exceptionCase.Property(item => item.AssignedUserId).HasColumnName("assigned_user_id");
        exceptionCase.Property(item => item.FirstDetectedAt).HasColumnName("first_detected_at").HasDefaultValueSql("now()");
        exceptionCase.Property(item => item.LastDetectedAt).HasColumnName("last_detected_at").HasDefaultValueSql("now()");
        exceptionCase.Property(item => item.ResolvedAt).HasColumnName("resolved_at");
        exceptionCase.Property(item => item.Version).HasColumnName("xmin").IsRowVersion();
        exceptionCase.HasIndex(item => new { item.Category, item.ConditionKey }).IsUnique().HasFilter("resolved_at IS NULL");
        exceptionCase.HasIndex(item => new { item.Status, item.Severity, item.FirstDetectedAt });
        exceptionCase.HasIndex(item => new { item.AssignedUserId, item.Status });
        exceptionCase.HasOne(item => item.Product).WithMany().HasForeignKey(item => item.ProductId).OnDelete(DeleteBehavior.Restrict);
        exceptionCase.HasOne(item => item.Location).WithMany().HasForeignKey(item => item.LocationId).OnDelete(DeleteBehavior.Restrict);
        exceptionCase.HasOne(item => item.CycleCountLocation).WithMany().HasForeignKey(item => item.CycleCountLocationId).OnDelete(DeleteBehavior.Restrict);
        exceptionCase.HasOne(item => item.AssignedUser).WithMany().HasForeignKey(item => item.AssignedUserId).OnDelete(DeleteBehavior.Restrict);

        var exceptionEvent = modelBuilder.Entity<OperationalExceptionEvent>();
        exceptionEvent.ToTable("operational_exception_events", table =>
            table.HasCheckConstraint("ck_operational_exception_events_type", "type IN ('DETECTED','TRIAGE_UPDATED','AUTO_RESOLVED')"));
        exceptionEvent.HasKey(item => item.Id);
        exceptionEvent.Property(item => item.Id).HasColumnName("id");
        exceptionEvent.Property(item => item.OperationId).HasColumnName("operation_id");
        exceptionEvent.Property(item => item.OperationalExceptionCaseId).HasColumnName("operational_exception_case_id");
        exceptionEvent.Property(item => item.Type).HasColumnName("type").HasMaxLength(30)
            .HasConversion(value => ExceptionEventTypeToDatabase(value), value => ExceptionEventTypeFromDatabase(value));
        exceptionEvent.Property(item => item.PreviousStatus).HasColumnName("previous_status").HasMaxLength(20)
            .HasConversion(value => value == null ? null : value == OperationalExceptionStatus.InProgress ? "IN_PROGRESS" : value.ToString()!.ToUpperInvariant(), value => string.IsNullOrWhiteSpace(value) ? null : value == "IN_PROGRESS" ? OperationalExceptionStatus.InProgress : Enum.Parse<OperationalExceptionStatus>(value, true));
        exceptionEvent.Property(item => item.CurrentStatus).HasColumnName("current_status").HasMaxLength(20)
            .HasConversion(value => value == null ? null : value == OperationalExceptionStatus.InProgress ? "IN_PROGRESS" : value.ToString()!.ToUpperInvariant(), value => string.IsNullOrWhiteSpace(value) ? null : value == "IN_PROGRESS" ? OperationalExceptionStatus.InProgress : Enum.Parse<OperationalExceptionStatus>(value, true));
        exceptionEvent.Property(item => item.PreviousAssignedUserId).HasColumnName("previous_assigned_user_id");
        exceptionEvent.Property(item => item.CurrentAssignedUserId).HasColumnName("current_assigned_user_id");
        exceptionEvent.Property(item => item.ActorUserId).HasColumnName("actor_user_id");
        exceptionEvent.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(500);
        exceptionEvent.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        exceptionEvent.HasIndex(item => item.OperationId).IsUnique().HasFilter("operation_id IS NOT NULL");
        exceptionEvent.HasIndex(item => new { item.OperationalExceptionCaseId, item.RecordedAt });
        exceptionEvent.HasOne(item => item.OperationalExceptionCase).WithMany(item => item.Events).HasForeignKey(item => item.OperationalExceptionCaseId).OnDelete(DeleteBehavior.Cascade);
        exceptionEvent.HasOne(item => item.PreviousAssignedUser).WithMany().HasForeignKey(item => item.PreviousAssignedUserId).OnDelete(DeleteBehavior.Restrict);
        exceptionEvent.HasOne(item => item.CurrentAssignedUser).WithMany().HasForeignKey(item => item.CurrentAssignedUserId).OnDelete(DeleteBehavior.Restrict);
        exceptionEvent.HasOne(item => item.ActorUser).WithMany().HasForeignKey(item => item.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInventoryMovementCorrection(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InventoryMovementCorrection>();
        entity.ToTable("inventory_movement_corrections", table =>
            table.HasCheckConstraint("ck_inventory_movement_corrections_type", "type IN ('REVERSAL', 'REPLACEMENT')"));
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id");
        entity.Property(item => item.OperationId).HasColumnName("operation_id");
        entity.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(item => item.Type).HasColumnName("type").HasMaxLength(20).HasConversion(
            value => value == InventoryMovementCorrectionType.Reversal ? "REVERSAL" : "REPLACEMENT",
            value => value == "REVERSAL" ? InventoryMovementCorrectionType.Reversal : InventoryMovementCorrectionType.Replacement);
        entity.Property(item => item.OriginalMovementId).HasColumnName("original_movement_id");
        entity.Property(item => item.ReversalMovementId).HasColumnName("reversal_movement_id");
        entity.Property(item => item.ReplacementMovementId).HasColumnName("replacement_movement_id");
        entity.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        entity.Property(item => item.RequestedByUserId).HasColumnName("requested_by_user_id");
        entity.Property(item => item.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        entity.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        entity.HasIndex(item => item.OperationId).IsUnique();
        entity.HasIndex(item => item.OriginalMovementId).IsUnique();
        entity.HasIndex(item => item.ReversalMovementId).IsUnique();
        entity.HasIndex(item => item.ReplacementMovementId).IsUnique().HasFilter("replacement_movement_id IS NOT NULL");
        entity.HasOne(item => item.OriginalMovement).WithMany().HasForeignKey(item => item.OriginalMovementId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.ReversalMovement).WithMany().HasForeignKey(item => item.ReversalMovementId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.ReplacementMovement).WithMany().HasForeignKey(item => item.ReplacementMovementId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.AuthorizedByUser).WithMany().HasForeignKey(item => item.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWipDisposition(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WipDisposition>();
        entity.ToTable("wip_dispositions", table =>
        {
            table.HasCheckConstraint("ck_wip_dispositions_type", "type IN ('WAREHOUSE_RETURN', 'SUPPLIER_RETURN')");
            table.HasCheckConstraint("ck_wip_dispositions_quantity", "quantity > 0");
            table.HasCheckConstraint("ck_wip_dispositions_shape", "(type = 'WAREHOUSE_RETURN' AND destination_location_id IS NOT NULL AND inventory_movement_id IS NOT NULL) OR (type = 'SUPPLIER_RETURN' AND destination_location_id IS NULL AND inventory_movement_id IS NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id");
        entity.Property(item => item.OperationId).HasColumnName("operation_id");
        entity.Property(item => item.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(item => item.OriginalMovementLineId).HasColumnName("original_movement_line_id");
        entity.Property(item => item.Type).HasColumnName("type").HasMaxLength(30).HasConversion(
            value => value == WipDispositionType.WarehouseReturn ? "WAREHOUSE_RETURN" : "SUPPLIER_RETURN",
            value => value == "WAREHOUSE_RETURN" ? WipDispositionType.WarehouseReturn : WipDispositionType.SupplierReturn);
        entity.Property(item => item.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        entity.Property(item => item.ResponsibleUserId).HasColumnName("responsible_user_id");
        entity.Property(item => item.DestinationLocationId).HasColumnName("destination_location_id");
        entity.Property(item => item.InventoryMovementId).HasColumnName("inventory_movement_id");
        entity.Property(item => item.ReversesDispositionId).HasColumnName("reverses_disposition_id");
        entity.Property(item => item.Reference).HasColumnName("reference").HasMaxLength(120);
        entity.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(500);
        entity.Property(item => item.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");
        entity.Property(item => item.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        entity.HasIndex(item => item.OperationId).IsUnique();
        entity.HasIndex(item => item.OriginalMovementLineId);
        entity.HasIndex(item => item.InventoryMovementId).IsUnique().HasFilter("inventory_movement_id IS NOT NULL");
        entity.HasIndex(item => item.ReversesDispositionId).IsUnique().HasFilter("reverses_disposition_id IS NOT NULL");
        entity.HasIndex(item => item.OccurredAt);
        entity.HasOne(item => item.OriginalMovementLine).WithMany().HasForeignKey(item => item.OriginalMovementLineId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.ResponsibleUser).WithMany().HasForeignKey(item => item.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.DestinationLocation).WithMany().HasForeignKey(item => item.DestinationLocationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.InventoryMovement).WithMany().HasForeignKey(item => item.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.ReversesDisposition).WithMany().HasForeignKey(item => item.ReversesDispositionId).OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureMovementHistoryIsImmutable();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureMovementHistoryIsImmutable();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureMovementHistoryIsImmutable()
    {
        var changedHistory = ChangeTracker.Entries()
            .Where(entry => (entry.Entity is InventoryMovement or InventoryMovementLine or InventoryBalanceChange or InventoryMovementCorrection or WipDisposition or ProductLotDateChange or WarehouseMapRevision or CycleCountAction or LabelTemplateEvent or OperationalExceptionEvent or ReceivingConfirmation or ReceivingConfirmationLine or ReceivingDocumentEvent) &&
                (entry.State is EntityState.Modified or EntityState.Deleted))
            .Select(entry => entry.Metadata.ClrType.Name)
            .Distinct()
            .ToArray();

        if (changedHistory.Length != 0)
            throw new InvalidOperationException($"Los movimientos confirmados y su historial son inmutables: {string.Join(", ", changedHistory)}.");

        var changedPublishedTemplate = ChangeTracker.Entries<LabelTemplateVersion>().Any(entry =>
            (entry.State is EntityState.Modified or EntityState.Deleted) &&
            (entry.OriginalValues.GetValue<LabelTemplateStatus>(nameof(LabelTemplateVersion.Status)) is LabelTemplateStatus.Published or LabelTemplateStatus.Retired) &&
            !(entry.State == EntityState.Modified && entry.Properties.All(property =>
                !property.IsModified || property.Metadata.Name is nameof(LabelTemplateVersion.Status) or nameof(LabelTemplateVersion.RetiredAt) or nameof(LabelTemplateVersion.RetiredByUserId))));
        if (changedPublishedTemplate)
            throw new InvalidOperationException("Las versiones publicadas de etiquetas son inmutables.");
    }

    private static string MovementTypeToDatabase(InventoryMovementType value) => value switch
    {
        InventoryMovementType.Entry => "ENTRY",
        InventoryMovementType.Exit => "EXIT",
        InventoryMovementType.Transfer => "TRANSFER",
        InventoryMovementType.Adjustment => "ADJUSTMENT",
        _ => throw new InvalidOperationException("Tipo de movimiento no soportado.")
    };

    private static string ExceptionCategoryToDatabase(OperationalExceptionCategory value) => value switch
    {
        OperationalExceptionCategory.NegativeInventory => "NEGATIVE_INVENTORY",
        OperationalExceptionCategory.BelowMinimum => "BELOW_MINIMUM",
        OperationalExceptionCategory.UnassignedBalance => "UNASSIGNED_BALANCE",
        OperationalExceptionCategory.RestrictedInventory => "RESTRICTED_INVENTORY",
        OperationalExceptionCategory.StagnantInventory => "STAGNANT_INVENTORY",
        OperationalExceptionCategory.CycleCountStale => "CYCLE_COUNT_STALE",
        OperationalExceptionCategory.CycleCountPending => "CYCLE_COUNT_PENDING",
        OperationalExceptionCategory.AgedWip => "AGED_WIP",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static OperationalExceptionCategory ExceptionCategoryFromDatabase(string value) => value switch
    {
        "NEGATIVE_INVENTORY" => OperationalExceptionCategory.NegativeInventory,
        "BELOW_MINIMUM" => OperationalExceptionCategory.BelowMinimum,
        "UNASSIGNED_BALANCE" => OperationalExceptionCategory.UnassignedBalance,
        "RESTRICTED_INVENTORY" => OperationalExceptionCategory.RestrictedInventory,
        "STAGNANT_INVENTORY" => OperationalExceptionCategory.StagnantInventory,
        "CYCLE_COUNT_STALE" => OperationalExceptionCategory.CycleCountStale,
        "CYCLE_COUNT_PENDING" => OperationalExceptionCategory.CycleCountPending,
        "AGED_WIP" => OperationalExceptionCategory.AgedWip,
        _ => throw new InvalidOperationException("Categoría de excepción no soportada.")
    };

    private static string ExceptionEventTypeToDatabase(OperationalExceptionEventType value) => value switch
    {
        OperationalExceptionEventType.Detected => "DETECTED",
        OperationalExceptionEventType.TriageUpdated => "TRIAGE_UPDATED",
        OperationalExceptionEventType.AutoResolved => "AUTO_RESOLVED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static OperationalExceptionEventType ExceptionEventTypeFromDatabase(string value) => value switch
    {
        "DETECTED" => OperationalExceptionEventType.Detected,
        "TRIAGE_UPDATED" => OperationalExceptionEventType.TriageUpdated,
        "AUTO_RESOLVED" => OperationalExceptionEventType.AutoResolved,
        _ => throw new InvalidOperationException("Tipo de evento de excepción no soportado.")
    };

    private static string LabelSizeToDatabase(LabelSizePreset value) => value switch
    {
        LabelSizePreset.SixByFourLandscape => "6X4_L",
        LabelSizePreset.FourBySixPortrait => "4X6_P",
        LabelSizePreset.ThreeByOneLandscape => "3X1_L",
        LabelSizePreset.FourByFourPointFivePortrait => "4X45_P",
        LabelSizePreset.ElevenByEightPointFiveLandscape => "11X85_L",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static LabelSizePreset LabelSizeFromDatabase(string value) => value switch
    {
        "6X4_L" => LabelSizePreset.SixByFourLandscape,
        "4X6_P" => LabelSizePreset.FourBySixPortrait,
        "3X1_L" => LabelSizePreset.ThreeByOneLandscape,
        "4X45_P" => LabelSizePreset.FourByFourPointFivePortrait,
        "11X85_L" => LabelSizePreset.ElevenByEightPointFiveLandscape,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static InventoryMovementType MovementTypeFromDatabase(string value) => value switch
    {
        "ENTRY" => InventoryMovementType.Entry,
        "EXIT" => InventoryMovementType.Exit,
        "TRANSFER" => InventoryMovementType.Transfer,
        "ADJUSTMENT" => InventoryMovementType.Adjustment,
        _ => throw new InvalidOperationException("Tipo de movimiento almacenado no soportado.")
    };

    private static string MovementPurposeToDatabase(InventoryMovementPurpose value) => value switch
    {
        InventoryMovementPurpose.GeneralExit => "GENERAL_EXIT",
        InventoryMovementPurpose.ProductionIssue => "PRODUCTION_ISSUE",
        InventoryMovementPurpose.WipWarehouseReturn => "WIP_WAREHOUSE_RETURN",
        InventoryMovementPurpose.WipConsumption => "WIP_CONSUMPTION",
        InventoryMovementPurpose.WipSupplierReturn => "WIP_SUPPLIER_RETURN",
        InventoryMovementPurpose.CycleCountAdjustment => "CYCLE_COUNT_ADJUSTMENT",
        InventoryMovementPurpose.DocumentReceipt => "DOCUMENT_RECEIPT",
        _ => "STANDARD"
    };

    private static InventoryMovementPurpose MovementPurposeFromDatabase(string value) => value switch
    {
        "GENERAL_EXIT" => InventoryMovementPurpose.GeneralExit,
        "PRODUCTION_ISSUE" => InventoryMovementPurpose.ProductionIssue,
        "WIP_WAREHOUSE_RETURN" => InventoryMovementPurpose.WipWarehouseReturn,
        "WIP_CONSUMPTION" => InventoryMovementPurpose.WipConsumption,
        "WIP_SUPPLIER_RETURN" => InventoryMovementPurpose.WipSupplierReturn,
        "CYCLE_COUNT_ADJUSTMENT" => InventoryMovementPurpose.CycleCountAdjustment,
        "DOCUMENT_RECEIPT" => InventoryMovementPurpose.DocumentReceipt,
        _ => InventoryMovementPurpose.Standard
    };

    private static string ReceivingDocumentTypeToDatabase(ReceivingDocumentType value) => value switch
    {
        ReceivingDocumentType.PurchaseOrder => "PURCHASE_ORDER",
        ReceivingDocumentType.DeliveryNote => "DELIVERY_NOTE",
        ReceivingDocumentType.PackingList => "PACKING_LIST",
        ReceivingDocumentType.ProductionOrder => "PRODUCTION_ORDER",
        _ => "OTHER"
    };

    private static ReceivingDocumentType ReceivingDocumentTypeFromDatabase(string value) => value switch
    {
        "PURCHASE_ORDER" => ReceivingDocumentType.PurchaseOrder,
        "DELIVERY_NOTE" => ReceivingDocumentType.DeliveryNote,
        "PACKING_LIST" => ReceivingDocumentType.PackingList,
        "PRODUCTION_ORDER" => ReceivingDocumentType.ProductionOrder,
        _ => ReceivingDocumentType.Other
    };

    private static string ReceivingDocumentStatusToDatabase(ReceivingDocumentStatus value) => value switch
    {
        ReceivingDocumentStatus.PartiallyReceived => "PARTIALLY_RECEIVED",
        ReceivingDocumentStatus.ClosedWithDifferences => "CLOSED_WITH_DIFFERENCES",
        _ => value.ToString().ToUpperInvariant()
    };

    private static ReceivingDocumentStatus ReceivingDocumentStatusFromDatabase(string value) => value switch
    {
        "PARTIALLY_RECEIVED" => ReceivingDocumentStatus.PartiallyReceived,
        "CLOSED_WITH_DIFFERENCES" => ReceivingDocumentStatus.ClosedWithDifferences,
        _ => Enum.Parse<ReceivingDocumentStatus>(value, true)
    };

    private static string ReceivingDocumentEventTypeToDatabase(ReceivingDocumentEventType value) => value switch
    {
        ReceivingDocumentEventType.ReceiptConfirmed => "RECEIPT_CONFIRMED",
        ReceivingDocumentEventType.AutomaticallyCompleted => "AUTOMATICALLY_COMPLETED",
        ReceivingDocumentEventType.ClosedWithDifferences => "CLOSED_WITH_DIFFERENCES",
        ReceivingDocumentEventType.ReceiptCorrected => "RECEIPT_CORRECTED",
        ReceivingDocumentEventType.ReopenedAfterCorrection => "REOPENED_AFTER_CORRECTION",
        _ => value.ToString().ToUpperInvariant()
    };

    private static ReceivingDocumentEventType ReceivingDocumentEventTypeFromDatabase(string value) => value switch
    {
        "RECEIPT_CONFIRMED" => ReceivingDocumentEventType.ReceiptConfirmed,
        "AUTOMATICALLY_COMPLETED" => ReceivingDocumentEventType.AutomaticallyCompleted,
        "CLOSED_WITH_DIFFERENCES" => ReceivingDocumentEventType.ClosedWithDifferences,
        "RECEIPT_CORRECTED" => ReceivingDocumentEventType.ReceiptCorrected,
        "REOPENED_AFTER_CORRECTION" => ReceivingDocumentEventType.ReopenedAfterCorrection,
        _ => Enum.Parse<ReceivingDocumentEventType>(value, true)
    };

    private static string LocationRoleToDatabase(LocationOperationalRole value) => value switch
    {
        LocationOperationalRole.Wip => "WIP",
        LocationOperationalRole.Other => "OTHER",
        _ => "STORAGE"
    };

    private static LocationOperationalRole LocationRoleFromDatabase(string value) => value switch
    {
        "WIP" => LocationOperationalRole.Wip,
        "OTHER" => LocationOperationalRole.Other,
        _ => LocationOperationalRole.Storage
    };

    private static string LotAllocationModeToDatabase(InventoryLotAllocationMode value) => value switch
    {
        InventoryLotAllocationMode.DailyLot => "DAILY_LOT",
        InventoryLotAllocationMode.AutomaticFefo => "AUTOMATIC_FEFO",
        _ => "NONE"
    };

    private static InventoryLotAllocationMode LotAllocationModeFromDatabase(string value) => value switch
    {
        "DAILY_LOT" => InventoryLotAllocationMode.DailyLot,
        "AUTOMATIC_FEFO" => InventoryLotAllocationMode.AutomaticFefo,
        _ => InventoryLotAllocationMode.None
    };
}
