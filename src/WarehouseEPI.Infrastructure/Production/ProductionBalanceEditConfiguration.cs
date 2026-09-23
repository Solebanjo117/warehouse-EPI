using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static partial class ProductionModelConfiguration
{
    private static void ConfigureBalanceEdits(ModelBuilder modelBuilder)
    {
        var edit = modelBuilder.Entity<ProductionBalanceEdit>();
        edit.ToTable("production_balance_edits");
        edit.HasKey(x => x.Id);
        edit.HasIndex(x => x.OperationId).IsUnique();
        edit.Property(x => x.RequestFingerprint).HasMaxLength(64).IsFixedLength();
        edit.Property(x => x.Reason).HasMaxLength(500);
        edit.HasOne<ProductionScheduleWeek>().WithMany().HasForeignKey(x => x.WeekId).OnDelete(DeleteBehavior.Restrict);
        edit.HasOne<User>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        edit.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.EditId).OnDelete(DeleteBehavior.Restrict);
        var item = modelBuilder.Entity<ProductionBalanceEditItem>();
        item.ToTable("production_balance_edit_items");
        item.HasKey(x => x.Id);
        item.Property(x => x.Area).HasConversion<string>().HasMaxLength(24);
        item.Property(x => x.PreviousTotal).HasPrecision(18, 4);
        item.Property(x => x.RequestedTotal).HasPrecision(18, 4);
        item.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<ProductionShift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<ProductionDailyCapture>().WithMany().HasForeignKey(x => x.ReversedCaptureId).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<ProductionDailyCapture>().WithMany().HasForeignKey(x => x.CreatedCaptureId).OnDelete(DeleteBehavior.Restrict);
    }
}
