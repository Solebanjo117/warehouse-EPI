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
    public DbSet<ProductLocationAssignment> ProductLocationAssignments => Set<ProductLocationAssignment>();
    public DbSet<ProductLot> ProductLots => Set<ProductLot>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryMovementLine> InventoryMovementLines => Set<InventoryMovementLine>();
    public DbSet<InventoryBalanceChange> InventoryBalanceChanges => Set<InventoryBalanceChange>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<InventoryMovementCorrection> InventoryMovementCorrections => Set<InventoryMovementCorrection>();
    public DbSet<ProductLotDateChange> ProductLotDateChanges => Set<ProductLotDateChange>();
    public DbSet<WarehouseMapLayout> WarehouseMapLayouts => Set<WarehouseMapLayout>();
    public DbSet<WarehouseMapElement> WarehouseMapElements => Set<WarehouseMapElement>();
    public DbSet<WarehouseMapRevision> WarehouseMapRevisions => Set<WarehouseMapRevision>();

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
        ConfigureProductLocationAssignment(modelBuilder);
        ConfigureProductLot(modelBuilder);
        ConfigureInventoryMovement(modelBuilder);
        ConfigureInventoryMovementLine(modelBuilder);
        ConfigureInventoryBalanceChange(modelBuilder);
        ConfigureInventoryBalance(modelBuilder);
        ConfigureInventoryMovementCorrection(modelBuilder);
        ConfigureProductLotDateChange(modelBuilder);
        ConfigureWarehouseMap(modelBuilder);
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
        entity.ToTable("business_settings", table =>
            table.HasCheckConstraint("ck_business_settings_singleton", "id = 1"));
        entity.HasKey(settings => settings.Id);
        entity.Property(settings => settings.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(settings => settings.BusinessName).HasColumnName("business_name").HasMaxLength(160).IsRequired();
        entity.Property(settings => settings.WarehouseName).HasColumnName("warehouse_name").HasMaxLength(120).IsRequired();
        entity.Property(settings => settings.WarehouseCode).HasColumnName("warehouse_code").HasMaxLength(30).IsRequired();
        entity.Property(settings => settings.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(100).IsRequired();
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
        entity.Property(location => location.RowCode).HasColumnName("row_code").HasMaxLength(1);
        entity.Property(location => location.RackNumber).HasColumnName("rack_number");
        entity.Property(location => location.PalletNumber).HasColumnName("pallet_number");
        entity.Property(location => location.Description).HasColumnName("description").HasMaxLength(200);
        entity.Property(location => location.IsBlocked).HasColumnName("is_blocked").HasDefaultValue(false);
        entity.Property(location => location.BlockReason).HasColumnName("block_reason").HasMaxLength(200);
        entity.Property(location => location.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(location => location.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(location => location.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.Ignore(location => location.IsOperational);
        entity.Ignore(location => location.LevelNumber);
        entity.Ignore(location => location.HorizontalPosition);
        entity.HasIndex(location => location.Code).IsUnique();
        entity.HasIndex(location => new { location.RowCode, location.RackNumber, location.PalletNumber })
            .IsUnique().HasFilter("kind = 'RACK'");
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
            table.HasCheckConstraint(
                "ck_inventory_movements_type",
                "type IN ('ENTRY', 'EXIT', 'TRANSFER', 'ADJUSTMENT')"));
        entity.HasKey(movement => movement.Id);
        entity.Property(movement => movement.Id).HasColumnName("id");
        entity.Property(movement => movement.OperationId).HasColumnName("operation_id");
        entity.Property(movement => movement.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(movement => movement.Type).HasColumnName("type").HasMaxLength(20)
            .HasConversion(
                value => MovementTypeToDatabase(value),
                value => MovementTypeFromDatabase(value));
        entity.Property(movement => movement.ResponsibleUserId).HasColumnName("responsible_user_id");
        entity.Property(movement => movement.Reference).HasColumnName("reference").HasMaxLength(120);
        entity.Property(movement => movement.Notes).HasColumnName("notes").HasMaxLength(500);
        entity.Property(movement => movement.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()");
        entity.Property(movement => movement.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        entity.HasIndex(movement => movement.OperationId).IsUnique();
        entity.HasIndex(movement => movement.OccurredAt);
        entity.HasIndex(movement => new { movement.ResponsibleUserId, movement.OccurredAt });
        entity.HasOne(movement => movement.ResponsibleUser)
            .WithMany()
            .HasForeignKey(movement => movement.ResponsibleUserId)
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
        layout.ToTable("warehouse_map_layouts", table => table.HasCheckConstraint("ck_warehouse_map_layout_singleton", "id = 1"));
        layout.HasKey(item => item.Id);
        layout.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        layout.Property(item => item.Version).HasColumnName("version");
        layout.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        layout.Property(item => item.UpdatedByUserId).HasColumnName("updated_by_user_id");
        layout.Property(item => item.RowVersion).IsRowVersion().HasColumnName("xmin");
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
            .Any(entry => entry.Entity is InventoryMovement or InventoryMovementLine or InventoryBalanceChange or InventoryMovementCorrection or ProductLotDateChange or WarehouseMapRevision &&
                entry.State is EntityState.Modified or EntityState.Deleted);

        if (changedHistory)
            throw new InvalidOperationException("Los movimientos confirmados y su historial son inmutables.");
    }

    private static string MovementTypeToDatabase(InventoryMovementType value) => value switch
    {
        InventoryMovementType.Entry => "ENTRY",
        InventoryMovementType.Exit => "EXIT",
        InventoryMovementType.Transfer => "TRANSFER",
        InventoryMovementType.Adjustment => "ADJUSTMENT",
        _ => throw new InvalidOperationException("Tipo de movimiento no soportado.")
    };

    private static InventoryMovementType MovementTypeFromDatabase(string value) => value switch
    {
        "ENTRY" => InventoryMovementType.Entry,
        "EXIT" => InventoryMovementType.Exit,
        "TRANSFER" => InventoryMovementType.Transfer,
        "ADJUSTMENT" => InventoryMovementType.Adjustment,
        _ => throw new InvalidOperationException("Tipo de movimiento almacenado no soportado.")
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
