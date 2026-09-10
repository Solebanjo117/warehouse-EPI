using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static class ProductionModelConfiguration
{
    public static void ConfigureProduction(this ModelBuilder modelBuilder)
    {
        var stage = modelBuilder.Entity<ProductionStage>();
        stage.ToTable("production_stages"); stage.HasKey(x => x.Id);
        stage.Property(x => x.Id).HasColumnName("id"); stage.Property(x => x.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        stage.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired(); stage.Property(x => x.IsActive).HasColumnName("is_active");
        stage.HasIndex(x => x.Code).IsUnique();

        var shift = modelBuilder.Entity<ProductionShift>();
        shift.ToTable("production_shifts"); shift.HasKey(x => x.Id);
        shift.Property(x => x.Id).HasColumnName("id"); shift.Property(x => x.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        shift.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired(); shift.Property(x => x.IsActive).HasColumnName("is_active");
        shift.HasIndex(x => x.Code).IsUnique();

        var route = modelBuilder.Entity<ProductionRoute>();
        route.ToTable("production_routes"); route.HasKey(x => x.Id);
        route.Property(x => x.Id).HasColumnName("id"); route.Property(x => x.ProductId).HasColumnName("product_id");
        route.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired(); route.Property(x => x.IsActive).HasColumnName("is_active"); route.Property(x => x.CreatedAt).HasColumnName("created_at");
        route.HasIndex(x => x.ProductId).IsUnique().HasFilter("is_active"); route.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);

        var routeStage = modelBuilder.Entity<ProductionRouteStage>();
        routeStage.ToTable("production_route_stages"); routeStage.HasKey(x => x.Id);
        routeStage.Property(x => x.Id).HasColumnName("id"); routeStage.Property(x => x.RouteId).HasColumnName("route_id"); routeStage.Property(x => x.StageId).HasColumnName("stage_id"); routeStage.Property(x => x.Sequence).HasColumnName("sequence");
        routeStage.HasIndex(x => new { x.RouteId, x.Sequence }).IsUnique(); routeStage.HasIndex(x => new { x.RouteId, x.StageId }).IsUnique();
        routeStage.HasOne(x => x.Route).WithMany(x => x.Stages).HasForeignKey(x => x.RouteId).OnDelete(DeleteBehavior.Restrict); routeStage.HasOne(x => x.Stage).WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict);

        var order = modelBuilder.Entity<ProductionWorkOrder>();
        order.ToTable("production_work_orders"); order.HasKey(x => x.Id);
        order.Property(x => x.Id).HasColumnName("id"); order.Property(x => x.CreateOperationId).HasColumnName("create_operation_id"); order.Property(x => x.CreateFingerprint).HasColumnName("create_fingerprint").HasMaxLength(64).IsFixedLength();
        order.Property(x => x.Number).HasColumnName("number").HasMaxLength(40).IsRequired(); order.Property(x => x.ExternalReference).HasColumnName("external_reference").HasMaxLength(120);
        order.Property(x => x.ProductId).HasColumnName("product_id"); order.Property(x => x.UnitId).HasColumnName("unit_id"); order.Property(x => x.TargetQuantity).HasColumnName("target_quantity").HasPrecision(18,4); order.Property(x => x.AuthorizedQuantity).HasColumnName("authorized_quantity").HasPrecision(18,4);
        order.Property(x => x.DueDate).HasColumnName("due_date"); order.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500); order.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasConversion<string>();
        order.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id"); order.Property(x => x.CreatedAt).HasColumnName("created_at"); order.Property(x => x.ReleasedAt).HasColumnName("released_at"); order.Property(x => x.ClosedAt).HasColumnName("closed_at"); order.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0u).IsConcurrencyToken();
        order.HasIndex(x => x.CreateOperationId).IsUnique(); order.HasIndex(x => x.Number).IsUnique(); order.HasIndex(x => new { x.Status, x.DueDate });
        order.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict); order.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict); order.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var snapshot = modelBuilder.Entity<ProductionWorkOrderStage>();
        snapshot.ToTable("production_work_order_stages"); snapshot.HasKey(x => x.Id);
        snapshot.Property(x => x.Id).HasColumnName("id"); snapshot.Property(x => x.WorkOrderId).HasColumnName("work_order_id"); snapshot.Property(x => x.SourceStageId).HasColumnName("source_stage_id"); snapshot.Property(x => x.Sequence).HasColumnName("sequence"); snapshot.Property(x => x.Code).HasColumnName("code").HasMaxLength(40); snapshot.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        snapshot.HasIndex(x => new { x.WorkOrderId, x.Sequence }).IsUnique(); snapshot.HasIndex(x => new { x.WorkOrderId, x.SourceStageId }).IsUnique(); snapshot.HasOne(x => x.WorkOrder).WithMany(x => x.Stages).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict); snapshot.HasOne(x => x.SourceStage).WithMany().HasForeignKey(x => x.SourceStageId).OnDelete(DeleteBehavior.Restrict);

        var evt = modelBuilder.Entity<ProductionEvent>();
        evt.ToTable("production_events"); evt.HasKey(x => x.Id);
        evt.Property(x => x.Id).HasColumnName("id"); evt.Property(x => x.OperationId).HasColumnName("operation_id"); evt.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength(); evt.Property(x => x.WorkOrderId).HasColumnName("work_order_id"); evt.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id"); evt.Property(x => x.RelatedStageId).HasColumnName("related_stage_id"); evt.Property(x => x.Type).HasColumnName("type").HasMaxLength(30).HasConversion<string>(); evt.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id"); evt.Property(x => x.ShiftId).HasColumnName("shift_id");
        evt.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18,4); evt.Property(x => x.GoodQuantity).HasColumnName("good_quantity").HasPrecision(18,4); evt.Property(x => x.ReworkQuantity).HasColumnName("rework_quantity").HasPrecision(18,4); evt.Property(x => x.ScrapQuantity).HasColumnName("scrap_quantity").HasPrecision(18,4); evt.Property(x => x.InventoryMovementId).HasColumnName("inventory_movement_id"); evt.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500); evt.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        evt.HasIndex(x => x.OperationId).IsUnique(); evt.HasIndex(x => new { x.WorkOrderId, x.RecordedAt }); evt.HasIndex(x => x.InventoryMovementId).IsUnique().HasFilter("inventory_movement_id IS NOT NULL");
        evt.HasOne(x => x.WorkOrder).WithMany(x => x.Events).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.WorkOrderStage).WithMany().HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.RelatedStage).WithMany().HasForeignKey(x => x.RelatedStageId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.InventoryMovement).WithMany().HasForeignKey(x => x.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
    }
}
