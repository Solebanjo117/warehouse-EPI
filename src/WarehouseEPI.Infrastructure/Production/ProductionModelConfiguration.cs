using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public static partial class ProductionModelConfiguration
{
    public static void ConfigureProduction(this ModelBuilder modelBuilder)
    {
        ConfigureTraceability(modelBuilder);
        ConfigureExecution(modelBuilder);
        var stage = modelBuilder.Entity<ProductionStage>();
        stage.ToTable("production_stages"); stage.HasKey(x => x.Id);
        stage.Property(x => x.Id).HasColumnName("id"); stage.Property(x => x.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        stage.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired(); stage.Property(x => x.IsActive).HasColumnName("is_active");
        stage.Property(x => x.DefaultWipLocationId).HasColumnName("default_wip_location_id");
        stage.Property(x => x.DefaultWipRowCode).HasColumnName("default_wip_row_code").HasMaxLength(20);
        stage.Property(x => x.DefaultWipRackNumber).HasColumnName("default_wip_rack_number");
        stage.Property(x => x.InactivityAlertHours).HasColumnName("inactivity_alert_hours");
        stage.Property(x => x.ReworkAlertHours).HasColumnName("rework_alert_hours");
        stage.ToTable(table => table.HasCheckConstraint("ck_production_stages_alert_hours", "(inactivity_alert_hours IS NULL OR inactivity_alert_hours > 0) AND (rework_alert_hours IS NULL OR rework_alert_hours > 0)"));
        stage.ToTable(table => table.HasCheckConstraint("ck_production_stages_default_wip_shape", "(default_wip_location_id IS NULL AND default_wip_row_code IS NULL AND default_wip_rack_number IS NULL) OR (default_wip_location_id IS NOT NULL AND default_wip_row_code IS NULL AND default_wip_rack_number IS NULL) OR (default_wip_location_id IS NULL AND default_wip_row_code IS NOT NULL AND default_wip_rack_number IS NOT NULL)"));
        stage.HasOne(x => x.DefaultWipLocation).WithMany().HasForeignKey(x => x.DefaultWipLocationId).OnDelete(DeleteBehavior.Restrict);
        stage.HasIndex(x => x.Code).IsUnique();

        var materialDefault = modelBuilder.Entity<ProductionMaterialWipDefault>();
        materialDefault.ToTable("production_material_wip_defaults", table => table.HasCheckConstraint("ck_production_material_wip_defaults_shape", "(location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)"));
        materialDefault.HasKey(x => x.Id);
        materialDefault.Property(x => x.Id).HasColumnName("id"); materialDefault.Property(x => x.ProductId).HasColumnName("product_id");
        materialDefault.Property(x => x.ProductionStageId).HasColumnName("production_stage_id"); materialDefault.Property(x => x.LocationId).HasColumnName("location_id");
        materialDefault.Property(x => x.RowCode).HasColumnName("row_code").HasMaxLength(20); materialDefault.Property(x => x.RackNumber).HasColumnName("rack_number");
        materialDefault.Property(x => x.CreatedAt).HasColumnName("created_at"); materialDefault.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        materialDefault.HasIndex(x => new { x.ProductId, x.ProductionStageId }).IsUnique();
        materialDefault.HasOne(x => x.Product).WithMany(x => x.ProductionWipDefaults).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        materialDefault.HasOne(x => x.ProductionStage).WithMany().HasForeignKey(x => x.ProductionStageId).OnDelete(DeleteBehavior.Restrict);
        materialDefault.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);

        var materialRevision = modelBuilder.Entity<ProductionMaterialWipRevision>();
        materialRevision.ToTable("production_material_wip_revisions"); materialRevision.HasKey(x => x.Id);
        materialRevision.Property(x => x.Id).HasColumnName("id"); materialRevision.Property(x => x.OperationId).HasColumnName("operation_id");
        materialRevision.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength(); materialRevision.Property(x => x.ProductId).HasColumnName("product_id");
        materialRevision.Property(x => x.AuthorizedByUserId).HasColumnName("authorized_by_user_id"); materialRevision.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        materialRevision.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb"); materialRevision.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb"); materialRevision.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        materialRevision.HasIndex(x => x.OperationId).IsUnique(); materialRevision.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        materialRevision.HasOne(x => x.AuthorizedByUser).WithMany().HasForeignKey(x => x.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);

        var target = modelBuilder.Entity<ProductionProcessWipTarget>();
        target.ToTable("production_process_wip_targets", table => table.HasCheckConstraint(
            "ck_production_process_wip_targets_shape",
            "(location_id IS NOT NULL AND row_code IS NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NULL) OR (location_id IS NULL AND row_code IS NOT NULL AND rack_number IS NOT NULL)"));
        target.HasKey(x => x.Id);
        target.Property(x => x.Id).HasColumnName("id");
        target.Property(x => x.ProductionStageId).HasColumnName("production_stage_id");
        target.Property(x => x.LocationId).HasColumnName("location_id");
        target.Property(x => x.RowCode).HasColumnName("row_code").HasMaxLength(20);
        target.Property(x => x.RackNumber).HasColumnName("rack_number");
        target.Property(x => x.CreatedAt).HasColumnName("created_at");
        target.HasIndex(x => new { x.ProductionStageId, x.LocationId }).IsUnique().HasFilter("location_id IS NOT NULL");
        target.HasIndex(x => new { x.ProductionStageId, x.RowCode }).IsUnique().HasFilter("row_code IS NOT NULL AND rack_number IS NULL");
        target.HasIndex(x => new { x.ProductionStageId, x.RowCode, x.RackNumber }).IsUnique().HasFilter("rack_number IS NOT NULL");
        target.HasOne(x => x.ProductionStage).WithMany(x => x.WipTargets).HasForeignKey(x => x.ProductionStageId).OnDelete(DeleteBehavior.Restrict);
        target.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Cascade);

        var configuration = modelBuilder.Entity<ProductionProcessConfiguration>();
        configuration.ToTable("production_process_configuration");
        configuration.HasKey(x => x.Id);
        configuration.Property(x => x.Id).HasColumnName("id");
        configuration.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        configuration.ToTable(table => table.HasCheckConstraint("ck_production_process_configuration_singleton", "id = 1"));
        configuration.HasData(new ProductionProcessConfiguration { Id = 1, Version = 0 });

        var revision = modelBuilder.Entity<ProductionProcessRevision>();
        revision.ToTable("production_process_revisions"); revision.HasKey(x => x.Id);
        revision.Property(x => x.Id).HasColumnName("id"); revision.Property(x => x.OperationId).HasColumnName("operation_id");
        revision.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();
        revision.Property(x => x.ProductionStageId).HasColumnName("production_stage_id");
        revision.Property(x => x.AuthorizedByUserId).HasColumnName("authorized_by_user_id");
        revision.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        revision.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
        revision.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
        revision.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        revision.HasIndex(x => x.OperationId).IsUnique();
        revision.HasOne(x => x.ProductionStage).WithMany().HasForeignKey(x => x.ProductionStageId).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne(x => x.AuthorizedByUser).WithMany().HasForeignKey(x => x.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);

        var issue = modelBuilder.Entity<ProductionMaterialIssueLink>();
        issue.ToTable("production_material_issue_links");
        issue.HasKey(x => x.Id);
        issue.Property(x => x.Id).HasColumnName("id");
        issue.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        issue.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id");
        issue.Property(x => x.InventoryMovementLineId).HasColumnName("inventory_movement_line_id");
        issue.Property(x => x.SupplyRequestLineId).HasColumnName("supply_request_line_id");
        issue.Property(x => x.ProductId).HasColumnName("product_id");
        issue.Property(x => x.WipLocationId).HasColumnName("wip_location_id");
        issue.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        issue.Property(x => x.CancelledQuantity).HasColumnName("cancelled_quantity").HasPrecision(18, 4);
        issue.Property(x => x.Source).HasColumnName("source").HasMaxLength(20).HasConversion<string>();
        issue.Property(x => x.CreatedAt).HasColumnName("created_at");
        issue.HasIndex(x => x.InventoryMovementLineId).IsUnique();
        issue.ToTable("production_material_issue_links", table => table.HasCheckConstraint(
            "ck_production_material_issue_link_cancelled", "cancelled_quantity >= 0 AND cancelled_quantity <= quantity"));
        issue.HasIndex(x => new { x.WorkOrderId, x.WorkOrderStageId });
        issue.HasIndex(x => x.SupplyRequestLineId);
        issue.HasOne(x => x.WorkOrder).WithMany(x => x.MaterialIssues).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        issue.HasOne(x => x.WorkOrderStage).WithMany(x => x.MaterialIssues).HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        issue.HasOne(x => x.InventoryMovementLine).WithOne(x => x.MaterialIssueLink)
            .HasForeignKey<ProductionMaterialIssueLink>(x => x.InventoryMovementLineId).OnDelete(DeleteBehavior.Restrict);
        issue.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        issue.HasOne(x => x.WipLocation).WithMany().HasForeignKey(x => x.WipLocationId).OnDelete(DeleteBehavior.Restrict);
        issue.HasOne(x => x.SupplyRequestLine).WithMany(x => x.IssueLinks)
            .HasForeignKey(x => x.SupplyRequestLineId).OnDelete(DeleteBehavior.Restrict);

        var issueLot = modelBuilder.Entity<ProductionMaterialIssueLot>();
        issueLot.ToTable("production_material_issue_lots", table => table.HasCheckConstraint(
            "ck_production_material_issue_lot_quantity", "quantity > 0"));
        issueLot.HasKey(x => x.Id);
        issueLot.Property(x => x.Id).HasColumnName("id");
        issueLot.Property(x => x.IssueLinkId).HasColumnName("issue_link_id");
        issueLot.Property(x => x.LotId).HasColumnName("lot_id");
        issueLot.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        issueLot.HasIndex(x => new { x.IssueLinkId, x.LotId }).IsUnique();
        issueLot.HasOne(x => x.IssueLink).WithMany(x => x.Lots).HasForeignKey(x => x.IssueLinkId).OnDelete(DeleteBehavior.Cascade);
        issueLot.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);

        var operation = modelBuilder.Entity<ProductionMaterialOperation>();
        operation.ToTable("production_material_operations", table => table.HasCheckConstraint(
            "ck_production_material_operation_reversal",
            "(type = 'REVERSAL' AND reverses_operation_id IS NOT NULL) OR (type <> 'REVERSAL' AND reverses_operation_id IS NULL)"));
        operation.HasKey(x => x.Id);
        operation.Property(x => x.Id).HasColumnName("id");
        operation.Property(x => x.OperationId).HasColumnName("operation_id");
        operation.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();
        operation.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
        operation.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id");
        operation.Property(x => x.ReturnEffect).HasColumnName("return_effect").HasMaxLength(20).HasConversion<string>();
        operation.Property(x => x.Type).HasColumnName("type").HasMaxLength(24).HasConversion(
            x => x == ProductionMaterialOperationType.Consumption ? "CONSUMPTION" :
                 x == ProductionMaterialOperationType.WarehouseReturn ? "WAREHOUSE_RETURN" :
                 x == ProductionMaterialOperationType.SupplierReturn ? "SUPPLIER_RETURN" : x == ProductionMaterialOperationType.Scrap ? "SCRAP" : "REVERSAL",
            x => x == "CONSUMPTION" ? ProductionMaterialOperationType.Consumption :
                 x == "WAREHOUSE_RETURN" ? ProductionMaterialOperationType.WarehouseReturn :
                 x == "SUPPLIER_RETURN" ? ProductionMaterialOperationType.SupplierReturn : x == "SCRAP" ? ProductionMaterialOperationType.Scrap : ProductionMaterialOperationType.Reversal);
        operation.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id");
        operation.Property(x => x.ReversesOperationId).HasColumnName("reverses_operation_id");
        operation.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(120);
        operation.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);
        operation.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        operation.HasIndex(x => x.OperationId).IsUnique();
        operation.HasIndex(x => new { x.WorkOrderId, x.WorkOrderStageId });
        operation.HasIndex(x => x.ReversesOperationId).IsUnique().HasFilter("reverses_operation_id IS NOT NULL");
        operation.HasOne(x => x.WorkOrder).WithMany(x => x.MaterialOperations).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        operation.HasOne(x => x.WorkOrderStage).WithMany().HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict);
        operation.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        operation.HasOne(x => x.ReversesOperation).WithMany().HasForeignKey(x => x.ReversesOperationId).OnDelete(DeleteBehavior.Restrict);

        var operationLine = modelBuilder.Entity<ProductionMaterialOperationLine>();
        operationLine.ToTable("production_material_operation_lines", table => table.HasCheckConstraint(
            "ck_production_material_operation_line_quantity", "quantity > 0"));
        operationLine.HasKey(x => x.Id);
        operationLine.Property(x => x.Id).HasColumnName("id");
        operationLine.Property(x => x.ProductionMaterialOperationId).HasColumnName("production_material_operation_id");
        operationLine.Property(x => x.IssueLinkId).HasColumnName("issue_link_id");
        operationLine.Property(x => x.InventoryMovementLineId).HasColumnName("inventory_movement_line_id");
        operationLine.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18, 4);
        operationLine.HasIndex(x => new { x.ProductionMaterialOperationId, x.IssueLinkId }).IsUnique();
        operationLine.HasIndex(x => x.InventoryMovementLineId).IsUnique();
        operationLine.HasOne(x => x.Operation).WithMany(x => x.Lines).HasForeignKey(x => x.ProductionMaterialOperationId).OnDelete(DeleteBehavior.Cascade);
        operationLine.HasOne(x => x.IssueLink).WithMany(x => x.OperationLines).HasForeignKey(x => x.IssueLinkId).OnDelete(DeleteBehavior.Restrict);
        operationLine.HasOne(x => x.InventoryMovementLine).WithMany().HasForeignKey(x => x.InventoryMovementLineId).OnDelete(DeleteBehavior.Restrict);

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
        order.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id"); order.Property(x => x.CreatedAt).HasColumnName("created_at"); order.Property(x => x.ReleasedAt).HasColumnName("released_at"); order.Property(x => x.ClosedAt).HasColumnName("closed_at"); order.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0u).IsConcurrencyToken(); order.Property(x => x.UsesBatchTraceability).HasColumnName("uses_batch_traceability"); order.Property(x => x.RecipeVersion).HasColumnName("recipe_version");
        order.HasIndex(x => x.CreateOperationId).IsUnique(); order.HasIndex(x => x.Number).IsUnique(); order.HasIndex(x => new { x.Status, x.DueDate });
        order.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict); order.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict); order.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        var snapshot = modelBuilder.Entity<ProductionWorkOrderStage>();
        snapshot.ToTable("production_work_order_stages"); snapshot.HasKey(x => x.Id);
        snapshot.Property(x => x.Id).HasColumnName("id"); snapshot.Property(x => x.WorkOrderId).HasColumnName("work_order_id"); snapshot.Property(x => x.SourceStageId).HasColumnName("source_stage_id"); snapshot.Property(x => x.Sequence).HasColumnName("sequence"); snapshot.Property(x => x.Code).HasColumnName("code").HasMaxLength(40); snapshot.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        snapshot.HasIndex(x => new { x.WorkOrderId, x.Sequence }).IsUnique(); snapshot.HasIndex(x => new { x.WorkOrderId, x.SourceStageId }).IsUnique(); snapshot.HasOne(x => x.WorkOrder).WithMany(x => x.Stages).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict); snapshot.HasOne(x => x.SourceStage).WithMany().HasForeignKey(x => x.SourceStageId).OnDelete(DeleteBehavior.Restrict);

        var evt = modelBuilder.Entity<ProductionEvent>();
        evt.ToTable("production_events"); evt.HasKey(x => x.Id);
        evt.Property(x => x.Id).HasColumnName("id"); evt.Property(x => x.OperationId).HasColumnName("operation_id"); evt.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength(); evt.Property(x => x.WorkOrderId).HasColumnName("work_order_id"); evt.Property(x => x.WorkOrderStageId).HasColumnName("work_order_stage_id"); evt.Property(x => x.RelatedStageId).HasColumnName("related_stage_id"); evt.Property(x => x.BatchId).HasColumnName("batch_id"); evt.Property(x => x.RelatedEventId).HasColumnName("related_event_id"); evt.Property(x => x.Type).HasColumnName("type").HasMaxLength(30).HasConversion<string>(); evt.Property(x => x.ResponsibleUserId).HasColumnName("responsible_user_id"); evt.Property(x => x.ShiftId).HasColumnName("shift_id");
        evt.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(18,4); evt.Property(x => x.GoodQuantity).HasColumnName("good_quantity").HasPrecision(18,4); evt.Property(x => x.ReworkQuantity).HasColumnName("rework_quantity").HasPrecision(18,4); evt.Property(x => x.ScrapQuantity).HasColumnName("scrap_quantity").HasPrecision(18,4); evt.Property(x => x.InventoryMovementId).HasColumnName("inventory_movement_id"); evt.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500); evt.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        evt.HasIndex(x => x.OperationId).IsUnique(); evt.HasIndex(x => new { x.WorkOrderId, x.RecordedAt }); evt.HasIndex(x => x.InventoryMovementId).IsUnique().HasFilter("inventory_movement_id IS NOT NULL");
        evt.HasOne(x => x.WorkOrder).WithMany(x => x.Events).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.WorkOrderStage).WithMany().HasForeignKey(x => x.WorkOrderStageId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.RelatedStage).WithMany().HasForeignKey(x => x.RelatedStageId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.RelatedEvent).WithMany().HasForeignKey(x => x.RelatedEventId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict); evt.HasOne(x => x.InventoryMovement).WithMany().HasForeignKey(x => x.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
    }
}
