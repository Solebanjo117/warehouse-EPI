using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Persistence;

internal static class WipDocumentConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var document = model.Entity<WipDocument>();
        document.ToTable("wip_documents");
        document.HasKey(x => x.Id);
        document.HasIndex(x => x.MovementLineId).IsUnique();
        document.HasIndex(x => new { x.WipLocationId, x.ProductId, x.OccurredAt });
        document.HasOne(x => x.MovementLine).WithMany().HasForeignKey(x => x.MovementLineId).OnDelete(DeleteBehavior.Restrict);
        document.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        document.HasOne(x => x.WipLocation).WithMany().HasForeignKey(x => x.WipLocationId).OnDelete(DeleteBehavior.Restrict);
        document.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        var lot = model.Entity<WipDocumentLot>();
        lot.ToTable("wip_document_lots");
        lot.HasKey(x => x.Id);
        lot.HasOne(x => x.Document).WithMany(x => x.Lots).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        lot.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        var assignment = model.Entity<WipDocumentAssignment>();
        assignment.ToTable("wip_document_assignments");
        assignment.HasKey(x => x.Id);
        assignment.HasIndex(x => new { x.DocumentId, x.IssueLinkId }).IsUnique();
        assignment.HasOne(x => x.Document).WithMany(x => x.Assignments).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne(x => x.IssueLink).WithMany().HasForeignKey(x => x.IssueLinkId).OnDelete(DeleteBehavior.Restrict);
        var application = model.Entity<WipDocumentApplication>();
        application.ToTable("wip_document_applications");
        application.HasKey(x => x.Id);
        application.HasIndex(x => x.OperationId);
        application.HasIndex(x => x.ReversesApplicationId).IsUnique();
        application.HasOne(x => x.Document).WithMany(x => x.Applications).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        application.HasOne(x => x.InventoryMovementLine).WithMany().HasForeignKey(x => x.InventoryMovementLineId).OnDelete(DeleteBehavior.Restrict);
        application.HasOne<WipDocumentApplication>().WithMany().HasForeignKey(x => x.ReversesApplicationId).OnDelete(DeleteBehavior.Restrict);
        var cutover = model.Entity<WipDocumentCutover>();
        cutover.ToTable("wip_document_cutovers");
        cutover.HasKey(x => x.Id);
        cutover.HasIndex(x => x.Singleton).IsUnique();
        cutover.HasIndex(x => x.OperationId).IsUnique();
        foreach (var type in new[] { typeof(WipDocument), typeof(WipDocumentLot), typeof(WipDocumentAssignment), typeof(WipDocumentApplication), typeof(WipDocumentCutover) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                if (property.ClrType == typeof(decimal)) { property.SetPrecision(18); property.SetScale(4); }
    }
}
