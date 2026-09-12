using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static partial class ProductionModelConfiguration
{
    private static void ConfigureTraceability(ModelBuilder modelBuilder)
    {
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
        recipeLine.HasIndex(x => new { x.RecipeId, x.MaterialProductId, x.StageId }).IsUnique();
        recipeLine.HasOne(x => x.Recipe).WithMany(x => x.Lines).HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        recipeLine.HasOne(x => x.MaterialProduct).WithMany().HasForeignKey(x => x.MaterialProductId).OnDelete(DeleteBehavior.Restrict);
        recipeLine.HasOne(x => x.Stage).WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict);

        var plan = modelBuilder.Entity<ProductionOrderMaterialPlan>();
        plan.ToTable("production_order_material_plans", t => t.HasCheckConstraint("ck_production_order_material_plan_quantities", "planned_quantity > 0 AND original_planned_quantity > 0"));
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
        plan.HasIndex(x => new { x.WorkOrderId, x.WorkOrderStageId, x.MaterialProductId }).IsUnique();
        plan.HasOne(x => x.WorkOrder).WithMany(x => x.MaterialPlan).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.WorkOrderStage).WithMany(x => x.MaterialPlan).HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.MaterialProduct).WithMany().HasForeignKey(x => x.MaterialProductId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        plan.HasOne(x => x.AdjustedByUser).WithMany().HasForeignKey(x => x.AdjustedByUserId).OnDelete(DeleteBehavior.Restrict);

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
}
