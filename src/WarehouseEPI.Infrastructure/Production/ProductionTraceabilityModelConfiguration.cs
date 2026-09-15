using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static partial class ProductionModelConfiguration
{
    private static void ConfigureTraceability(ModelBuilder modelBuilder)
    {
        ConfigureSupplyRequests(modelBuilder);
        var recipe = modelBuilder.Entity<ProductionRecipe>();
        recipe.ToTable("production_recipes", t => t.HasCheckConstraint("ck_production_recipe_base_quantity", "base_quantity > 0"));
        recipe.HasKey(x => x.Id);
        recipe.Property(x => x.Id).HasColumnName("id");
        recipe.Property(x => x.ProductId).HasColumnName("product_id");
        recipe.Property(x => x.Version).HasColumnName("version");
        recipe.Property(x => x.BaseQuantity).HasColumnName("base_quantity").HasPrecision(18, 4);
        recipe.Property(x => x.IsActive).HasColumnName("is_active");
        recipe.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        recipe.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        recipe.Property(x => x.CreatedAt).HasColumnName("created_at");
        recipe.HasIndex(x => new { x.ProductId, x.Version }).IsUnique();
        recipe.HasIndex(x => x.ProductId).IsUnique().HasFilter("is_active");
        recipe.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        recipe.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var recipeLine = modelBuilder.Entity<ProductionRecipeLine>();
        recipeLine.ToTable("production_recipe_lines", t => t.HasCheckConstraint("ck_production_recipe_line_quantity", "quantity > 0"));
        recipeLine.HasKey(x => x.Id);
        recipeLine.Property(x => x.Id).HasColumnName("id");
        recipeLine.Property(x => x.RecipeId).HasColumnName("recipe_id");
        recipeLine.Property(x => x.MaterialProductId).HasColumnName("material_product_id");
        recipeLine.Property(x => x.StageId).HasColumnName("stage_id");
        recipeLine.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        recipeLine.HasIndex(x => new { x.RecipeId, x.MaterialProductId, x.StageId }).IsUnique()
            .HasFilter("stage_id IS NOT NULL");
        recipeLine.HasIndex(x => new { x.RecipeId, x.MaterialProductId }).IsUnique()
            .HasFilter("stage_id IS NULL");
        recipeLine.HasOne(x => x.Recipe).WithMany(x => x.Lines).HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        recipeLine.HasOne(x => x.MaterialProduct).WithMany().HasForeignKey(x => x.MaterialProductId).OnDelete(DeleteBehavior.Restrict);
        recipeLine.HasOne(x => x.Stage).WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        var plan = modelBuilder.Entity<ProductionOrderMaterialPlan>();
        plan.ToTable("production_order_material_plans", t =>
        {
            t.HasCheckConstraint("ck_production_order_material_plan_quantities", "planned_quantity > 0 AND original_planned_quantity > 0");
            t.HasCheckConstraint("ck_production_order_material_plan_wip_shape", "(wip_target_kind IS NULL AND wip_location_id IS NULL AND wip_row_code IS NULL AND wip_rack_number IS NULL AND wip_target_code IS NULL AND wip_resolution_source IS NULL) OR (wip_target_kind IN ('Area', 'Position') AND wip_location_id IS NOT NULL AND wip_row_code IS NULL AND wip_rack_number IS NULL AND wip_target_code IS NOT NULL AND wip_resolution_source IS NOT NULL) OR (wip_target_kind = 'Rack' AND wip_location_id IS NULL AND wip_row_code IS NOT NULL AND wip_rack_number IS NOT NULL AND wip_target_code IS NOT NULL AND wip_resolution_source IS NOT NULL)");
        });
        plan.HasKey(x => x.Id);
        plan.Property(x => x.Id).HasColumnName("id");
        plan.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        plan.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id");
        plan.Property(x => x.MaterialProductId).HasColumnName("material_product_id");
        plan.Property(x => x.UnitId).HasColumnName("unit_id");
        plan.Property(x => x.PlannedQuantity).HasColumnName("planned_quantity").HasPrecision(18, 4);
        plan.Property(x => x.OriginalPlannedQuantity).HasColumnName("original_planned_quantity").HasPrecision(18, 4);
        plan.Property(x => x.AdjustmentReason).HasColumnName("adjustment_reason").HasMaxLength(500);
        plan.Property(x => x.AdjustedByUserId).HasColumnName("adjusted_by_user_id");
        plan.Property(x => x.AdjustedAt).HasColumnName("adjusted_at");
        plan.Property(x => x.WipTargetKind).HasColumnName("wip_target_kind").HasMaxLength(20).HasConversion<string>();
        plan.Property(x => x.WipLocationId).HasColumnName("wip_location_id");
        plan.Property(x => x.WipRowCode).HasColumnName("wip_row_code").HasMaxLength(20);
        plan.Property(x => x.WipRackNumber).HasColumnName("wip_rack_number");
        plan.Property(x => x.WipTargetCode).HasColumnName("wip_target_code").HasMaxLength(80);
        plan.Property(x => x.WipResolutionSource).HasColumnName("wip_resolution_source").HasMaxLength(30).HasConversion<string>();
        plan.HasIndex(x => new { x.WorkOrderId, x.WorkOrderStageId, x.MaterialProductId }).IsUnique();
        plan.HasOne(x => x.WorkOrder).WithMany(x => x.MaterialPlan).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.WorkOrderStage).WithMany(x => x.MaterialPlan).HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.MaterialProduct).WithMany().HasForeignKey(x => x.MaterialProductId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.AdjustedByUser).WithMany().HasForeignKey(x => x.AdjustedByUserId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.WipLocation).WithMany().HasForeignKey(x => x.WipLocationId).OnDelete(DeleteBehavior.Restrict);
        plan.HasIndex(x => x.WipLocationId);

        var planningRevision = modelBuilder.Entity<ProductionOrderPlanningRevision>();
        planningRevision.ToTable("production_order_planning_revisions");
        planningRevision.HasKey(x => x.Id);
        planningRevision.Property(x => x.Id).HasColumnName("id");
        planningRevision.Property(x => x.OperationId).HasColumnName("operation_id");
        planningRevision.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();
        planningRevision.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        planningRevision.Property(x => x.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        planningRevision.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        planningRevision.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("text");
        planningRevision.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("text");
        planningRevision.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        planningRevision.HasIndex(x => x.OperationId).IsUnique();
        planningRevision.HasIndex(x => new { x.WorkOrderId, x.RecordedAt });
        planningRevision.HasOne(x => x.WorkOrder).WithMany(x => x.PlanningRevisions).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        planningRevision.HasOne(x => x.AuthorizedByUser).WithMany().HasForeignKey(x => x.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);

        var batch = modelBuilder.Entity<ProductionBatch>();
        batch.ToTable("production_batches", t => t.HasCheckConstraint("ck_production_batch_quantity", "assigned_quantity > 0"));
        batch.HasKey(x => x.Id);
        batch.Property(x => x.Id).HasColumnName("id");
        batch.Property(x => x.CreateOperationId).HasColumnName("create_operation_id");
        batch.Property(x => x.CreateFingerprint).HasColumnName("create_fingerprint").HasMaxLength(64).IsFixedLength();
        batch.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        batch.Property(x => x.Number).HasColumnName("number").HasMaxLength(60);
        batch.Property(x => x.AssignedQuantity).HasColumnName("assigned_quantity").HasPrecision(18, 4);
        batch.Property(x => x.FinishedProductLotId).HasColumnName("finished_product_lot_id");
        batch.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        batch.Property(x => x.CreatedAt).HasColumnName("created_at");
        batch.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        batch.HasIndex(x => x.CreateOperationId).IsUnique();
        batch.HasIndex(x => x.Number).IsUnique();
        batch.HasIndex(x => new { x.WorkOrderId, x.FinishedProductLotId }).IsUnique();
        batch.HasOne(x => x.WorkOrder).WithMany(x => x.Batches).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        batch.HasOne(x => x.FinishedProductLot).WithMany().HasForeignKey(x => x.FinishedProductLotId).OnDelete(DeleteBehavior.Restrict);
        batch.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var result = modelBuilder.Entity<ProductionBatchResult>();
        result.ToTable("production_batch_results", t => t.HasCheckConstraint("ck_production_batch_result_quantities", "input_quantity > 0 AND good_quantity >= 0 AND rework_quantity >= 0 AND scrap_quantity >= 0 AND good_quantity + rework_quantity + scrap_quantity = input_quantity"));
        result.HasKey(x => x.Id);
        result.Property(x => x.Id).HasColumnName("id");
        result.Property(x => x.OperationId).HasColumnName("operation_id");
        result.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();
        result.Property(x => x.BatchId).HasColumnName("batch_id");
        result.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id");
        result.Property(x => x.ShiftId).HasColumnName("shift_id");
        result.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id");
        result.Property(x => x.IsRework).HasColumnName("is_rework");
        result.Property(x => x.InputQuantity).HasColumnName("input_quantity").HasPrecision(18, 4);
        result.Property(x => x.GoodQuantity).HasColumnName("good_quantity").HasPrecision(18, 4);
        result.Property(x => x.ReworkQuantity).HasColumnName("rework_quantity").HasPrecision(18, 4);
        result.Property(x => x.ScrapQuantity).HasColumnName("scrap_quantity").HasPrecision(18, 4);
        result.Property(x => x.DifferenceReason).HasColumnName("difference_reason").HasMaxLength(500);
        result.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        result.HasIndex(x => x.OperationId).IsUnique();
        result.HasIndex(x => new { x.BatchId, x.WorkOrderStageId, x.RecordedAt });
        result.HasOne(x => x.Batch).WithMany(x => x.Results).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        result.HasOne(x => x.WorkOrderStage).WithMany().HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        result.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        result.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);

        var consumption = modelBuilder.Entity<ProductionBatchMaterialConsumption>();
        consumption.ToTable("production_batch_material_consumptions", t => t.HasCheckConstraint("ck_production_batch_material_consumption_quantity", "quantity > 0"));
        consumption.HasKey(x => x.Id);
        consumption.Property(x => x.Id).HasColumnName("id");
        consumption.Property(x => x.BatchResultId).HasColumnName("batch_result_id");
        consumption.Property(x => x.IssueLinkId).HasColumnName("issue_link_id");
        consumption.Property(x => x.MaterialLotId).HasColumnName("material_lot_id");
        consumption.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        consumption.HasIndex(x => new { x.BatchResultId, x.IssueLinkId, x.MaterialLotId }).IsUnique();
        consumption.HasOne(x => x.BatchResult).WithMany(x => x.Materials).HasForeignKey(x => x.BatchResultId).OnDelete(DeleteBehavior.Cascade);
        consumption.HasOne(x => x.IssueLink).WithMany().HasForeignKey(x => x.IssueLinkId).OnDelete(DeleteBehavior.Restrict);
        consumption.HasOne(x => x.MaterialLot).WithMany().HasForeignKey(x => x.MaterialLotId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSupplyRequests(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<ProductionWorkOrder>();
        order.Property(x => x.UsesSupplyRequests).HasColumnName("uses_supply_requests").HasDefaultValue(false);
        order.Property(x => x.SupplyPriority).HasColumnName("supply_priority").HasMaxLength(12).HasConversion<string>().HasDefaultValue(ProductionSupplyPriority.Normal);

        var request = modelBuilder.Entity<ProductionSupplyRequest>();
        request.ToTable("production_supply_requests");
        request.HasKey(x => x.Id);
        request.Property(x => x.Id).HasColumnName("id"); request.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        request.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id"); request.Property(x => x.DestinationCode).HasColumnName("destination_code").HasMaxLength(80);
        request.Property(x => x.DestinationLocationId).HasColumnName("destination_location_id"); request.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasConversion<string>();
        request.Property(x => x.CreatedAt).HasColumnName("created_at"); request.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        request.HasIndex(x => new { x.WorkOrderId, x.WorkOrderStageId, x.DestinationCode }).IsUnique();
        request.HasOne(x => x.WorkOrder).WithMany(x => x.SupplyRequests).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne(x => x.WorkOrderStage).WithMany().HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne(x => x.DestinationLocation).WithMany().HasForeignKey(x => x.DestinationLocationId).OnDelete(DeleteBehavior.Restrict);

        var line = modelBuilder.Entity<ProductionSupplyRequestLine>();
        line.ToTable("production_supply_request_lines", t => t.HasCheckConstraint("ck_production_supply_request_line_quantities", "required_quantity > 0 AND cancelled_quantity >= 0 AND cancelled_quantity <= required_quantity"));
        line.HasKey(x => x.Id);
        line.Property(x => x.Id).HasColumnName("id"); line.Property(x => x.SupplyRequestId).HasColumnName("supply_request_id"); line.Property(x => x.MaterialPlanId).HasColumnName("material_plan_id");
        line.Property(x => x.ProductId).HasColumnName("product_id"); line.Property(x => x.UnitId).HasColumnName("unit_id"); line.Property(x => x.RequiredQuantity).HasColumnName("required_quantity").HasPrecision(18, 4); line.Property(x => x.CancelledQuantity).HasColumnName("cancelled_quantity").HasPrecision(18, 4);
        line.HasIndex(x => x.MaterialPlanId).IsUnique();
        line.HasOne(x => x.SupplyRequest).WithMany(x => x.Lines).HasForeignKey(x => x.SupplyRequestId).OnDelete(DeleteBehavior.Cascade);
        line.HasOne(x => x.MaterialPlan).WithMany().HasForeignKey(x => x.MaterialPlanId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);

        var reservation = modelBuilder.Entity<ProductionWarehouseReservation>();
        reservation.ToTable("production_warehouse_reservations", t => t.HasCheckConstraint("ck_production_warehouse_reservation_quantities", "quantity > 0 AND released_quantity >= 0 AND released_quantity <= quantity"));
        reservation.HasKey(x => x.Id);
        reservation.Property(x => x.Id).HasColumnName("id"); reservation.Property(x => x.SupplyRequestLineId).HasColumnName("supply_request_line_id"); reservation.Property(x => x.LocationId).HasColumnName("location_id"); reservation.Property(x => x.LotId).HasColumnName("lot_id");
        reservation.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4); reservation.Property(x => x.ReleasedQuantity).HasColumnName("released_quantity").HasPrecision(18, 4); reservation.Property(x => x.CreatedAt).HasColumnName("created_at");
        reservation.HasIndex(x => new { x.SupplyRequestLineId, x.LocationId, x.LotId }).IsUnique(); reservation.HasIndex(x => new { x.LocationId, x.LotId });
        reservation.HasOne(x => x.SupplyRequestLine).WithMany(x => x.Reservations).HasForeignKey(x => x.SupplyRequestLineId).OnDelete(DeleteBehavior.Restrict);
        reservation.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict); reservation.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);

        var supplyEvent = modelBuilder.Entity<ProductionSupplyEvent>();
        supplyEvent.ToTable("production_supply_events"); supplyEvent.HasKey(x => x.Id);
        supplyEvent.Property(x => x.Id).HasColumnName("id"); supplyEvent.Property(x => x.OperationId).HasColumnName("operation_id"); supplyEvent.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();
        supplyEvent.Property(x => x.SupplyRequestId).HasColumnName("supply_request_id"); supplyEvent.Property(x => x.SupplyRequestLineId).HasColumnName("supply_request_line_id"); supplyEvent.Property(x => x.Type).HasColumnName("type").HasMaxLength(24).HasConversion<string>();
        supplyEvent.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id"); supplyEvent.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4); supplyEvent.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500); supplyEvent.Property(x => x.InventoryMovementId).HasColumnName("inventory_movement_id"); supplyEvent.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        supplyEvent.HasIndex(x => x.OperationId).IsUnique(); supplyEvent.HasIndex(x => new { x.SupplyRequestId, x.RecordedAt });
        supplyEvent.HasOne(x => x.SupplyRequest).WithMany(x => x.Events).HasForeignKey(x => x.SupplyRequestId).OnDelete(DeleteBehavior.Restrict); supplyEvent.HasOne(x => x.SupplyRequestLine).WithMany().HasForeignKey(x => x.SupplyRequestLineId).OnDelete(DeleteBehavior.Restrict);
        supplyEvent.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict); supplyEvent.HasOne(x => x.InventoryMovement).WithMany().HasForeignKey(x => x.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
    }
}
