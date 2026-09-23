using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static partial class ProductionModelConfiguration
{
    private static void ConfigureExecution(ModelBuilder builder)
    {
        var reason = builder.Entity<ProductionReason>();
        reason.ToTable("production_reasons");
        reason.HasKey(x => x.Id);
        reason.HasIndex(x => new { x.Category, x.Code }).IsUnique();
        reason.Property(x => x.Code).HasMaxLength(40);
        reason.Property(x => x.Description).HasMaxLength(160);
        reason.Property(x => x.Version).IsConcurrencyToken();
        foreach (var category in Enum.GetValues<ProductionReasonCategory>())
            reason.HasData(new ProductionReason { Id = ReasonId(category), Category = category,
                Code = "OTRO", Description = "Otro", RequiresComment = true });
        var audit = builder.Entity<ProductionExecutionAudit>();
        audit.ToTable("production_execution_audits");
        audit.HasKey(x => x.Id);
        audit.HasIndex(x => x.OperationId).IsUnique();
        audit.HasIndex(x => new { x.WorkOrderId, x.RecordedAt });
        audit.Property(x => x.Fingerprint).HasMaxLength(64);
        audit.Property(x => x.Action).HasMaxLength(40);
        audit.Property(x => x.Reason).HasMaxLength(500);
        audit.Property(x => x.BeforeJson).HasColumnType("jsonb");
        audit.Property(x => x.AfterJson).HasColumnType("jsonb");
        audit.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<User>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<User>().WithMany().HasForeignKey(x => x.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
        var rework = builder.Entity<ProductionReworkCase>();
        rework.ToTable("production_rework_cases"); rework.HasKey(x => x.Id);
        rework.HasIndex(x => x.OriginResultId).IsUnique();
        rework.Property(x => x.InitialQuantity).HasPrecision(18, 4);
        rework.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        rework.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        rework.HasOne(x => x.WorkOrderStage).WithMany().HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        rework.HasOne(x => x.OriginResult).WithMany().HasForeignKey(x => x.OriginResultId).OnDelete(DeleteBehavior.Restrict);
        var attempt = builder.Entity<ProductionReworkAttempt>();
        attempt.ToTable("production_rework_attempts"); attempt.HasKey(x => x.Id);
        attempt.HasIndex(x => x.ResultId).IsUnique();
        attempt.HasOne(x => x.ReworkCase).WithMany(x => x.Attempts).HasForeignKey(x => x.ReworkCaseId).OnDelete(DeleteBehavior.Restrict);
        attempt.HasOne(x => x.Result).WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
        var retention = builder.Entity<ProductionReworkRetention>();
        retention.ToTable("production_rework_retentions", t => t.HasCheckConstraint("ck_rework_retention_source", "(\"IssueLinkId\" IS NULL) <> (\"WarehouseReservationId\" IS NULL) AND \"Quantity\" > 0"));
        retention.HasKey(x => x.Id);
        retention.Property(x => x.Quantity).HasPrecision(18, 4);
        retention.HasIndex(x => x.IssueLinkId).IsUnique();
        retention.HasIndex(x => x.WarehouseReservationId).IsUnique();
        retention.HasOne(x => x.ReworkCase).WithMany().HasForeignKey(x => x.ReworkCaseId).OnDelete(DeleteBehavior.Restrict);
        retention.HasOne(x => x.IssueLink).WithMany().HasForeignKey(x => x.IssueLinkId).OnDelete(DeleteBehavior.Restrict);
        retention.HasOne(x => x.WarehouseReservation).WithMany().HasForeignKey(x => x.WarehouseReservationId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ProductionWorkOrder>().Property(x => x.OriginalTargetQuantity).HasColumnName("original_target_quantity").HasPrecision(18, 4);
        builder.Entity<ProductionWorkOrder>().Property(x => x.PrincipalClosedAt).HasColumnName("principal_closed_at");
        builder.Entity<ProductionSupplyRequestLine>().Property(x => x.ReworkCaseId).HasColumnName("rework_case_id");
        builder.Entity<ProductionSupplyRequestLine>().HasOne(x => x.ReworkCase).WithMany().HasForeignKey(x => x.ReworkCaseId).OnDelete(DeleteBehavior.Restrict);
    }

    public static Guid ReasonId(ProductionReasonCategory category) => new($"55555555-0000-0000-0000-{(int)category + 1:000000000000}");
}
