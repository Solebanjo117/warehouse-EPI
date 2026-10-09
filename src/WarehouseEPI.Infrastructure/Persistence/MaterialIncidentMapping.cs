using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Persistence;

internal static class MaterialIncidentMapping
{
    public static void Configure(ModelBuilder model)
    {
        var incident = model.Entity<MaterialIncident>();
        incident.ToTable("material_incidents"); incident.HasKey(x => x.Id); incident.Ignore(x => x.Folio);
        incident.Property(x => x.Version).IsConcurrencyToken();
        incident.Property(x => x.Description).HasMaxLength(2000);
        incident.Property(x => x.Quantity).HasPrecision(18, 4);
        incident.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne(x => x.DetectionLocation).WithMany().HasForeignKey(x => x.DetectionLocationId).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne(x => x.ReportedBy).WithMany().HasForeignKey(x => x.ReportedById).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<PalletPlate>().WithMany().HasForeignKey(x => x.PlateId).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<InventoryMovementLine>().WithMany().HasForeignKey(x => x.ArrivalLineId).OnDelete(DeleteBehavior.Restrict);
        incident.HasIndex(x => new { x.Status, x.ReportedAt, x.Id });
        incident.HasIndex(x => new { x.ProductId, x.DetectionLocationId, x.Status });
        var entry = model.Entity<MaterialIncidentEvent>();
        entry.ToTable("material_incident_events"); entry.HasKey(x => x.Id);
        entry.Property(x => x.Fingerprint).HasMaxLength(64); entry.Property(x => x.Action).HasMaxLength(24);
        entry.Property(x => x.Comment).HasMaxLength(2000);
        entry.HasIndex(x => x.OperationId).IsUnique(); entry.HasIndex(x => new { x.IncidentId, x.Version }).IsUnique();
        entry.HasOne<MaterialIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne(x => x.Responsible).WithMany().HasForeignKey(x => x.ResponsibleId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<InventoryMovementCorrection>().WithMany().HasForeignKey(x => x.CorrectionId).OnDelete(DeleteBehavior.Restrict);
        var photo = model.Entity<MaterialIncidentPhoto>();
        photo.ToTable("material_incident_photos"); photo.HasKey(x => x.Id);
        photo.Property(x => x.Name).HasMaxLength(120); photo.Property(x => x.ContentType).HasMaxLength(24); photo.Property(x => x.Sha256).HasMaxLength(64);
        photo.HasOne<MaterialIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        photo.HasOne<MaterialIncidentEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        photo.HasIndex(x => new { x.IncidentId, x.Sha256 }).IsUnique();
    }
}
