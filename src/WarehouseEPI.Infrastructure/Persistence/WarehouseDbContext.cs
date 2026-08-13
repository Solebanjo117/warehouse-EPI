using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Persistence;

public sealed class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options)
    : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<Location> Locations => Set<Location>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureRole(modelBuilder);
        ConfigureUser(modelBuilder);
        ConfigureUnit(modelBuilder);
        ConfigureProduct(modelBuilder);
        ConfigureProductBarcode(modelBuilder);
        ConfigureLocation(modelBuilder);
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

    private static void ConfigureUnit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Unit>();

        entity.ToTable("units");
        entity.HasKey(unit => unit.Id);
        entity.Property(unit => unit.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(unit => unit.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        entity.Property(unit => unit.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        entity.Property(unit => unit.AllowsDecimals).HasColumnName("allows_decimals").HasDefaultValue(true);
        entity.Property(unit => unit.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.HasIndex(unit => unit.Code).IsUnique();

        entity.HasData(new Unit
        {
            Id = 1,
            Code = "EA",
            Name = "Pieza",
            AllowsDecimals = false,
            IsActive = true
        });
    }

    private static void ConfigureProduct(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Product>();

        entity.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_minimum_stock", "minimum_stock >= 0");
            table.HasCheckConstraint(
                "ck_products_expiration_requires_lots",
                "NOT tracks_expiration OR tracks_lots");
        });
        entity.HasKey(product => product.Id);
        entity.Property(product => product.Id).HasColumnName("id");
        entity.Property(product => product.Sku).HasColumnName("sku").HasMaxLength(60).IsRequired();
        entity.Property(product => product.Name).HasColumnName("name").HasMaxLength(180).IsRequired();
        entity.Property(product => product.Description).HasColumnName("description");
        entity.Property(product => product.TypeCode).HasColumnName("type_code").HasMaxLength(40);
        entity.Property(product => product.ClassCode).HasColumnName("class_code").HasMaxLength(60);
        entity.Property(product => product.BaseUnitId).HasColumnName("base_unit_id");
        entity.Property(product => product.MinimumStock).HasColumnName("minimum_stock").HasPrecision(18, 4);
        entity.Property(product => product.TracksLots).HasColumnName("tracks_lots").HasDefaultValue(false);
        entity.Property(product => product.TracksExpiration).HasColumnName("tracks_expiration").HasDefaultValue(false);
        entity.Property(product => product.AllowsNegativeStock).HasColumnName("allows_negative_stock").HasDefaultValue(true);
        entity.Property(product => product.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(product => product.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(product => product.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.HasIndex(product => product.Sku).IsUnique();
        entity.HasOne(product => product.BaseUnit)
            .WithMany(unit => unit.Products)
            .HasForeignKey(product => product.BaseUnitId)
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
            table.HasCheckConstraint("ck_locations_shelf", "shelf IS NULL OR shelf > 0");
            table.HasCheckConstraint("ck_locations_level", "level_number IS NULL OR level_number > 0");
        });
        entity.HasKey(location => location.Id);
        entity.Property(location => location.Id).HasColumnName("id");
        entity.Property(location => location.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        entity.Property(location => location.Aisle).HasColumnName("aisle").HasMaxLength(10);
        entity.Property(location => location.Shelf).HasColumnName("shelf");
        entity.Property(location => location.LevelNumber).HasColumnName("level_number");
        entity.Property(location => location.PalletPosition).HasColumnName("pallet_position").HasMaxLength(10);
        entity.Property(location => location.Description).HasColumnName("description").HasMaxLength(200);
        entity.Property(location => location.IsBlocked).HasColumnName("is_blocked").HasDefaultValue(false);
        entity.Property(location => location.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        entity.Property(location => location.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        entity.Property(location => location.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        entity.HasIndex(location => location.Code).IsUnique();
    }
}
