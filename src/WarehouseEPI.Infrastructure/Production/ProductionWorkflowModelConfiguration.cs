using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static partial class ProductionModelConfiguration
{
    private static void ConfigureDailyWorkflow(ModelBuilder modelBuilder)
    {
        var submission = modelBuilder.Entity<ProductionCaptureSubmission>();
        submission.ToTable("production_capture_submissions");
        submission.HasKey(x => x.Id);
        submission.HasIndex(x => x.OperationId).IsUnique();
        submission.Property(x => x.RequestFingerprint).HasMaxLength(64).IsFixedLength();
        submission.HasOne<User>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        var item = modelBuilder.Entity<ProductionCaptureSubmissionItem>();
        item.ToTable("production_capture_submission_items");
        item.HasKey(x => new { x.SubmissionId, x.CaptureId });
        item.HasIndex(x => x.CaptureId).IsUnique();
        item.HasOne(x => x.Submission).WithMany(x => x.Items).HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
        item.HasOne(x => x.Capture).WithMany().HasForeignKey(x => x.CaptureId).OnDelete(DeleteBehavior.Restrict);
        var plan = modelBuilder.Entity<ProductionCarryoverPlan>();
        plan.ToTable("production_carryover_plans", table => table.HasCheckConstraint("ck_carryover_plan_quantity", "quantity >= 0"));
        plan.HasKey(x => x.Id);
        plan.HasIndex(x => new { x.WeekId, x.PlannedDate, x.ProductId, x.Area }).IsUnique();
        plan.Property(x => x.Quantity).HasPrecision(18, 4);
        plan.Property(x => x.Area).HasConversion<string>().HasMaxLength(20);
        plan.Property(x => x.Version).IsConcurrencyToken();
        plan.HasOne<ProductionScheduleWeek>().WithMany().HasForeignKey(x => x.WeekId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        foreach (var entity in new[] { submission.Metadata, item.Metadata, plan.Metadata })
            foreach (var property in entity.GetProperties())
                property.SetColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
    }
}
