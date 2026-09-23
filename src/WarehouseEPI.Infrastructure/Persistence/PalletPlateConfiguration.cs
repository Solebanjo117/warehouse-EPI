using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Persistence;

internal static class PalletPlateConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var plate = model.Entity<PalletPlate>();
        plate.ToTable("pallet_plates");
        plate.HasKey(x => x.Id);
        plate.Ignore(x => x.Identifier); plate.Ignore(x => x.Status);
        plate.Property(x => x.Quantity).HasPrecision(18, 4);
        plate.Property(x => x.Version).IsConcurrencyToken();
        plate.HasIndex(x => new { x.ProductId, x.LocationId });
        plate.HasIndex(x => x.OriginMovementId);
        plate.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        plate.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        plate.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.OriginMovementId).OnDelete(DeleteBehavior.Restrict);
        var lot = model.Entity<PalletPlateLot>();
        lot.ToTable("pallet_plate_lots"); lot.HasKey(x => new { x.PlateId, x.LotId });
        lot.Property(x => x.Quantity).HasPrecision(18, 4);
        lot.HasOne(x => x.Plate).WithMany(x => x.Lots).HasForeignKey(x => x.PlateId).OnDelete(DeleteBehavior.Restrict);
        lot.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        var audit = model.Entity<PalletPlateEvent>();
        audit.ToTable("pallet_plate_events"); audit.HasKey(x => x.Id);
        audit.Property(x => x.Kind).HasMaxLength(40);
        audit.Property(x => x.Fingerprint).HasMaxLength(64);
        audit.HasIndex(x => new { x.PlateId, x.PlateVersion }).IsUnique();
        audit.HasIndex(x => x.OperationId); audit.HasIndex(x => x.MovementId);
        audit.HasIndex(x => x.ReversesEventId).IsUnique();
        audit.HasOne(x => x.Plate).WithMany().HasForeignKey(x => x.PlateId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<InventoryMovementLine>().WithMany().HasForeignKey(x => x.MovementLineId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<User>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<PalletPlateEvent>().WithMany().HasForeignKey(x => x.ReversesEventId).OnDelete(DeleteBehavior.Restrict);
    }
}
