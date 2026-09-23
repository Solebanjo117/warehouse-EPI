using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionOpeningImportTests
{
    internal static WarehouseDbContext Context(string? name = null) => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);
    internal static ProductionImportDraftService Service(WarehouseDbContext db) => new(db, new(db, TimeProvider.System), TimeProvider.System);
    internal static async Task<Guid> Seed(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var admin = new User { FullName = "Opening admin", RoleId = 1, PinHash = "test", PinLookup = Guid.NewGuid().ToString() };
        db.Add(admin); await db.SaveChangesAsync(); return admin.Id;
    }
    internal static byte[] Bytes(Action<XLWorkbook>? edit = null)
    {
        using var workbook = ProductionDailyModuleTests.BuildWorkbook("FG-100");
        edit?.Invoke(workbook);
        using var stream = new MemoryStream(); workbook.SaveAs(stream); return stream.ToArray();
    }
    internal static IXLWorksheet Prior(XLWorkbook workbook) => workbook.Worksheet("09-14 to 09-20");

    internal static void InactiveZeroClosing(XLWorkbook workbook)
    {
        var sheet = Prior(workbook);
        sheet.Table("Plan3").DataRange.Clear(XLClearOptions.Contents);
        sheet.Table("Execution3").DataRange.Clear(XLClearOptions.Contents);
        for (var c = 21; c <= 23; c++)
        {
            sheet.Cell(169, c).Value = 0;
            sheet.Cell(7, c).Clear(XLClearOptions.Contents);
        }
    }

    [Fact]
    public async Task Inactive_product_with_explicit_zero_closing_needs_no_opening_or_packages()
    {
        await using var db = Context(); await Seed(db);
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(new MemoryStream(Bytes(InactiveZeroClosing)), "file.xlsx");
        Assert.True(preview.CanConfirm);
        var review = Assert.Single(preview.OpeningReview);
        Assert.True(review.Resolved); Assert.Empty(review.Problems); Assert.Empty(review.Packages);
        Assert.Empty(preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers);
        Assert.Equal(12, preview.Weeks.Single(x => x.IsDraft).FinalLines.Count);
    }

    // A carryover line already written in the destination week; the closing of the prior week replaces it.
    internal static void DestinationCarryover(XLWorkbook workbook)
    {
        var destination = workbook.Worksheet("09-21 to 09-27");
        destination.Table("Plan4").Resize(destination.Range(21, 3, 33, 10));
        destination.Cell(21, 10).Value = "Tipo"; destination.Cell(22, 10).Value = "Arrastre";
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("capture")]
    [InlineData("opening")]
    [InlineData("text")]
    [InlineData("note")]
    [InlineData("route")]
    [InlineData("absent")]
    [InlineData("negative")]
    [InlineData("destination")]
    [InlineData("realistic")]
    public async Task Zero_or_absent_closing_opens_at_zero_without_reconciliation(string kind)
    {
        await using var db = Context(); await Seed(db);
        if (kind is "route" or "realistic") { (await db.ProductionRoutes.SingleAsync()).IsActive = false; await db.SaveChangesAsync(); }
        var bytes = Bytes(w =>
        {
            var s = Prior(w);
            // The default prior week keeps its plan line and capture; only its closing row disappears.
            if (kind == "absent") { s.Cell(169, 20).Clear(XLClearOptions.Contents); return; }
            InactiveZeroClosing(w);
            switch (kind)
            {
                case "plan": s.Cell(22, 3).Value = "Monday"; s.Cell(22, 4).Value = "FG-100"; s.Cell(22, 5).Value = 10; break;
                case "capture":
                    s.Cell(24, 12).Value = new DateTime(2026, 9, 14); s.Cell(24, 13).Value = "Cutting";
                    s.Cell(24, 14).Value = "Shift 1"; s.Cell(24, 15).Value = "FG-100"; s.Cell(24, 16).Value = 5; break;
                case "opening": s.Cell(7, 21).Value = 10; break;
                case "text": s.Cell(7, 21).Value = "unknown"; break;
                case "note":
                    s.Table("ProduccionLunes").Resize(s.Range(6, 20, 7, 24));
                    s.Cell(6, 24).Value = "Column1"; s.Cell(7, 24).Value = "YA CORTADO"; break;
                case "negative": s.Cell(169, 21).Value = -1; break;
                case "destination": DestinationCarryover(w); break;
                case "realistic":
                    // Mirrors M6-E-50UP-8M-NOHDL-US: planned and captured work, a note, no Tipo and a 0/0/0 closing.
                    s.Table("Plan3").Resize(s.Range(21, 3, 22, 11));
                    s.Cell(21, 10).Value = "Column1"; s.Cell(21, 11).Value = "Tipo";
                    s.Cell(22, 3).Value = "Tuesday"; s.Cell(22, 4).Value = "FG-100"; s.Cell(22, 5).Value = 300;
                    s.Cell(22, 10).Value = "YA CORTADO";
                    s.Cell(24, 12).Value = new DateTime(2026, 9, 15); s.Cell(24, 13).Value = "Ready to Pack";
                    s.Cell(24, 14).Value = "Shift 1"; s.Cell(24, 15).Value = "FG-100"; s.Cell(24, 16).Value = 195;
                    s.Cell(7, 21).FormulaA1 = "SUMIF(Plan3[Tipo],\"Arrastre semana anterior\",Plan3[Qty])"; break;
            }
        });
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        Assert.DoesNotContain(preview.Issues, x => x.Kind == ProductionScheduleImportIssueKind.OpeningReconciliation);
        var review = Assert.Single(preview.OpeningReview);
        Assert.True(review.Resolved); Assert.Empty(review.Problems); Assert.Empty(review.Packages);
        var draft = preview.Weeks.Single(x => x.IsDraft);
        Assert.Empty(draft.OpeningCarryovers);
        Assert.DoesNotContain(draft.FinalLines, x => x.IsCarryover);
    }

    // The closing is imported as the operators left it. Ready to Pack pending is the unfinished total, so an earlier
    // area never starts more pieces than the next one; negative pending counts as zero. The route plays no part.
    [Theory]
    [InlineData(6, 11, 11, 6, 5, 0)]
    [InlineData(700, 700, 700, 700, 0, 0)]
    [InlineData(20, 20, 0, 0, 0, 0)]
    [InlineData(15, 0, 15, 0, 0, 15)]
    [InlineData(800, 600, 411, 411, 0, 0)]
    [InlineData(354, 284, 488, 284, 0, 204)]
    [InlineData(53, 272, 226, 53, 173, 0)]
    [InlineData(91, 91, 79, 79, 0, 0)]
    [InlineData(-5, 3, 4, 0, 3, 1)]
    public async Task Closing_is_taken_as_captured_and_ready_to_pack_bounds_earlier_areas(
        int cutting, int sewing, int ready, int startCutting, int startSewing, int startReady)
    {
        await using var db = Context(); await Seed(db);
        (await db.ProductionRoutes.SingleAsync()).IsActive = false; await db.SaveChangesAsync();
        var bytes = Bytes(w =>
        {
            var s = Prior(w);
            s.Cell(169, 21).Value = cutting; s.Cell(169, 22).Value = sewing; s.Cell(169, 23).Value = ready;
        });
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        Assert.Empty(Assert.Single(preview.OpeningReview).Problems);
        var packages = preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers;
        Assert.All(packages, x => Assert.True(x.Quantity > 0));
        Assert.Equal(startCutting, packages.Where(x => x.Area == ProductionDailyArea.Cutting).Sum(x => x.Quantity));
        Assert.Equal(startSewing, packages.Where(x => x.Area == ProductionDailyArea.Sewing).Sum(x => x.Quantity));
        Assert.Equal(startReady, packages.Where(x => x.Area == ProductionDailyArea.ReadyToPack).Sum(x => x.Quantity));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("formula")]
    [InlineData("text")]
    [InlineData("duplicate")]
    public async Task Unreadable_or_duplicated_closing_still_blocks(string kind)
    {
        await using var db = Context(); await Seed(db);
        var bytes = Bytes(w =>
        {
            InactiveZeroClosing(w); var s = Prior(w);
            switch (kind)
            {
                case "blank": s.Cell(169, 21).Clear(XLClearOptions.Contents); break;
                case "formula": s.Cell(169, 21).FormulaA1 = "1-1"; break;
                case "text": s.Cell(169, 21).Value = "n/a"; break;
                case "duplicate":
                    s.Table("PendingNextWeek").Resize(s.Range(168, 20, 170, 23));
                    s.Range(169, 20, 169, 23).CopyTo(s.Cell(170, 20)); break;
            }
        });
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.False(preview.CanConfirm);
        Assert.Empty(preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers);
        Assert.Contains(kind == "duplicate" ? "El producto aparece varias veces en el cierre." : "El cierre contiene pendientes vacíos o no numéricos.",
            Assert.Single(preview.OpeningReview).Problems);
    }

    [Fact]
    public async Task Spanish_closing_with_suffix_ignores_daily_tables_and_does_not_triple_700_pieces()
    {
        await using var db = Context(); await Seed(db);
        var bytes = Bytes(w =>
        {
            var s = Prior(w);
            s.Table("PendingNextWeek").Name = "PendienteProximaSemana6778536423";
            s.Cell(168, 21).Value = "Corte pendiente final";
            s.Cell(168, 22).Value = "Costura pendiente final";
            s.Cell(168, 23).Value = "Ready to Pack pendiente final";
            s.Cell(22, 5).Value = 700;
            s.Table("Execution3").DataRange.Clear(XLClearOptions.Contents);
            for (var c = 21; c <= 23; c++) s.Cell(169, c).Value = 700;
            // A daily table has the same closing headings but is not the weekly closing source.
            s.Range(168, 20, 169, 23).CopyTo(s.Cell(40, 20));
            s.Range(40, 20, 41, 23).CreateTable("ProduccionMartes");
            s.Cell(41, 21).Value = 999;
        });
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System).PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.OpeningReview.SelectMany(x => x.Problems)));
        var package = Assert.Single(preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers);
        Assert.Equal(700m, package.Quantity); Assert.Equal(ProductionDailyArea.Cutting, package.Area);
        Assert.Equal(16, preview.LineCount); Assert.Equal(17, preview.FinalLineCount);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("formula")]
    [InlineData("missing")]
    public async Task Missing_or_invalid_closing_requires_explicit_resolution(string kind)
    {
        await using var db = Context(); await Seed(db);
        var bytes = Bytes(w =>
        {
            var s = Prior(w);
            if (kind == "missing") s.Tables.Remove("PendingNextWeek");
            else if (kind == "formula") s.Cell(169, 21).FormulaA1 = "1+1";
            else s.Cell(169, 21).Clear(XLClearOptions.Contents);
        });
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        var preview = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.False(preview.CanConfirm);
        Assert.Empty(preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers);
        var id = (await db.Products.SingleAsync()).Id;
        var resolutions = ProductionScheduleImportResolutions.None with { Opening = [new(id, 0, 4, 2, "Physical opening checked")] };
        var resolved = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx", resolutions);
        Assert.True(resolved.CanConfirm, string.Join(" | ", resolved.Issues.Select(x => x.Message)));
        Assert.Equal(6, resolved.Weeks.Single(x => x.IsDraft).OpeningCarryovers.Sum(x => x.Quantity));
    }

    [Fact]
    public async Task Ambiguous_closing_requires_valid_selection_and_excludes_totals()
    {
        await using var db = Context(); await Seed(db);
        var bytes = Bytes(w =>
        {
            var s = Prior(w); var table = s.Table("PendingNextWeek");
            table.ShowTotalsRow = true; table.Field(0).TotalsRowLabel = "TOTAL";
            s.Range(168, 20, 169, 23).CopyTo(s.Cell(180, 20));
            s.Range(180, 20, 181, 23).CreateTable("PendienteProximaSemana2");
        });
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        var preview = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.False(preview.CanConfirm); Assert.Equal(2, preview.ClosingCandidates.Count);
        var selected = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx",
            ProductionScheduleImportResolutions.None with { ClosingTable = "PendingNextWeek" });
        Assert.True(selected.CanConfirm, string.Join(" | ", selected.Issues.Select(x => x.Message)));
        Assert.Single(selected.OpeningReview); Assert.DoesNotContain(selected.Evidence, x => x.Sku == "TOTAL");
    }

    [Fact]
    public async Task Notes_missing_sku_and_negative_daily_balances_are_evidence_not_blockers()
    {
        await using var db = Context(); await Seed(db);
        var bytes = Bytes(w =>
        {
            var s = Prior(w);
            s.Table("Plan3").Resize(s.Range(21, 3, 22, 11));
            s.Cell(21, 10).Value = "Column1"; s.Cell(22, 10).Value = "YA CORTADO";
            s.Cell(21, 11).Value = "Tipo";
            s.Cell(8, 20).Value = "FG-100"; s.Cell(8, 21).FormulaA1 = "SUMIF(Plan3[Tipo],\"Programación nueva\",Plan3[Qty])";
            s.Cell(8, 22).Value = -5;
            s.Cell(6, 24).Value = "Corte pendiente final";
            s.Table("ProduccionLunes").Resize(s.Range(6, 20, 8, 24)); s.Cell(8, 24).Value = -5;
        });
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System).PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        Assert.Contains(preview.Evidence, x => x.Text == "YA CORTADO" && x.Header == "Column1" && x.Cell == "J22");
        Assert.Empty(preview.OpeningReview.Single().Problems);
        Assert.Equal(4, preview.CaptureCount);
    }

    [Fact]
    public async Task Distinct_source_sku_texts_for_one_product_are_kept_as_evidence()
    {
        await using var db = Context(); await Seed(db);
        var bytes = Bytes(w => Prior(w).Cell(169, 20).Value = "fg-100");
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(new MemoryStream(bytes), "file.xlsx");
        var review = Assert.Single(preview.OpeningReview);
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Issues.Select(x => x.Message)));
        Assert.Contains("FG-100", review.SourceSkus);
        Assert.Contains("fg-100", review.SourceSkus);
        Assert.Empty(review.Problems);
    }

    [Fact]
    public async Task Saved_draft_survives_new_context_is_owned_and_confirmed_exactly_once()
    {
        var database = Guid.NewGuid().ToString(); Guid actor, id; ProductionImportDraftView view;
        await using (var db = Context(database))
        {
            actor = await Seed(db); id = await Service(db).CreateAsync("file.xlsx", Bytes(), actor);
        }
        await using (var db = Context(database))
        {
            var service = Service(db); view = (await service.GetAsync(id, actor))!;
            Assert.True(view.IsCurrent); Assert.True(view.Preview.CanConfirm);
            var other = new User { FullName = "Other admin", RoleId = 1, PinHash = "test", PinLookup = "other" };
            db.Add(other); await db.SaveChangesAsync();
            Assert.Null(await service.GetAsync(id, other.Id));
            Assert.False((await service.ReviseAsync(id, view.Version, other.Id, view.Resolutions)).Success);
        }
        var operation = Guid.NewGuid();
        await using (var db = Context(database))
        {
            var service = Service(db); var command = new ProductionImportConfirmCommand(id, view.Version, view.ReviewedFingerprint, operation, actor);
            var result = await service.ConfirmAsync(command);
            Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
            Assert.True((await service.ConfirmAsync(command)).Success);
            Assert.Equal(18, await db.ProductionScheduleLines.CountAsync());
            Assert.Equal(view.Preview.FinalLineCount, await db.ProductionScheduleLines.CountAsync());
            var savedWeeks = await db.ProductionScheduleWeeks.Include(x => x.Lines).Include(x => x.Captures).ToListAsync();
            foreach (var expectedWeek in view.Preview.Weeks)
            {
                var savedWeek = Assert.Single(savedWeeks, x => x.WeekStart == expectedWeek.WeekStart);
                var savedLines = savedWeek.Lines.OrderBy(x => x.Sequence).ToArray();
                Assert.Equal(expectedWeek.FinalLines.Count, savedLines.Length);
                for (var index = 0; index < savedLines.Length; index++)
                {
                    var expected = expectedWeek.FinalLines[index];
                    var actual = savedLines[index];
                    Assert.Equal(expected.Date, actual.PlannedDate);
                    Assert.Equal(expected.ProductId, actual.ProductId);
                    Assert.Equal(expected.Quantity, actual.Quantity);
                    Assert.Equal(expected.Reference1, actual.OrderReference1);
                    Assert.Equal(expected.Reference2, actual.OrderReference2);
                    Assert.Equal(expected.Reference3, actual.OrderReference3);
                    Assert.Equal(expected.Notes, actual.Notes);
                    Assert.Equal(expected.IsCarryover, actual.IsCarryover);
                    Assert.Equal(expected.StartArea, actual.StartArea);
                    Assert.Equal(expected.SourceSheet, actual.SourceSheet);
                    Assert.Equal(expected.SourceRow, actual.SourceRow);
                }
                var expectedCaptures = expectedWeek.Captures.OrderBy(x => x.SourceRow).ToArray();
                var savedCaptures = savedWeek.Captures.OrderBy(x => x.SourceRow).ToArray();
                Assert.Equal(expectedCaptures.Length, savedCaptures.Length);
                for (var index = 0; index < savedCaptures.Length; index++)
                {
                    Assert.Equal(expectedCaptures[index].Date, savedCaptures[index].EffectiveDate);
                    Assert.Equal(expectedCaptures[index].ProductId, savedCaptures[index].ProductId);
                    Assert.Equal(expectedCaptures[index].Area, savedCaptures[index].Area);
                    Assert.Equal(expectedCaptures[index].Quantity, savedCaptures[index].Quantity);
                    Assert.Equal(expectedCaptures[index].Notes, savedCaptures[index].Notes);
                    Assert.Equal(expectedCaptures[index].Reporter, savedCaptures[index].ImportedReporter);
                }
            }
            Assert.Empty(await db.ProductionWorkOrders.ToListAsync());
            Assert.Empty(await db.InventoryMovements.ToListAsync());
            Assert.Equal(16, (await db.ProductionScheduleImportBatches.SingleAsync()).LineCount);
            Assert.Equal(ProductionImportDraftStatus.Confirmed, (await service.GetAsync(id, actor))!.Status);
            Assert.False((await service.ConfirmAsync(command with { OperationId = Guid.NewGuid() })).Success);
            var another = await service.CreateAsync("again.xlsx", Bytes(), actor);
            Assert.False((await service.GetAsync(another, actor))!.Preview.CanConfirm);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_revision_is_revalidated_only_while_editable_and_keeps_resolutions(bool confirmed)
    {
        await using var db = Context(); var actor = await Seed(db);
        var bytes = Bytes(InactiveZeroClosing);
        var resolutions = ProductionScheduleImportResolutions.None with
        {
            Products = new Dictionary<string, Guid> { ["FG-100"] = (await db.Products.SingleAsync()).Id }
        };
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System)
            .PreviewAsync(new MemoryStream(bytes), "legacy.xlsx", resolutions);
        var legacy = preview with
        {
            Fingerprint = new string('A', 64),
            Issues = [new("09-14 to 09-20", 0, "Apertura pendiente de conciliación: FG-100.",
                ProductionScheduleImportIssueKind.OpeningReconciliation, ProductionScheduleImportTable.Carryover, "FG-100")]
        };
        var draft = new ProductionImportDraft
        {
            OwnerId = actor,
            FileName = "legacy.xlsx",
            FileBytes = bytes,
            FileHash = preview.FileHash,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1,
            Status = confirmed ? ProductionImportDraftStatus.Confirmed : ProductionImportDraftStatus.Reviewing
        };
        draft.Revisions.Add(new ProductionImportRevision
        {
            DraftId = draft.Id,
            Number = 1,
            ActorId = actor,
            CreatedAt = draft.CreatedAt,
            Action = "Uploaded",
            ResolutionsJson = System.Text.Json.JsonSerializer.Serialize(resolutions),
            PreviewJson = System.Text.Json.JsonSerializer.Serialize(legacy),
            Fingerprint = legacy.Fingerprint
        });
        db.Add(draft); await db.SaveChangesAsync();
        var service = Service(db); var view = (await service.GetAsync(draft.Id, actor))!;
        Assert.Equal(resolutions.Products["FG-100"], view.Resolutions.Products["FG-100"]);
        Assert.Equal(1, view.Version); Assert.Single(await db.ProductionImportRevisions.ToListAsync());
        if (confirmed)
        {
            Assert.True(view.IsCurrent); Assert.Equal(0, Assert.Single(view.Preview.Issues).Row);
            Assert.Equal(legacy.Fingerprint, view.Preview.Fingerprint);
        }
        else
        {
            Assert.False(view.IsCurrent); Assert.True(view.Preview.CanConfirm);
            Assert.True((await service.ReviseAsync(draft.Id, view.Version, actor, view.Resolutions)).Success);
            var revised = (await service.GetAsync(draft.Id, actor))!;
            Assert.True(revised.IsCurrent); Assert.Equal(2, revised.Version);
            Assert.Equal(resolutions.Products["FG-100"], revised.Resolutions.Products["FG-100"]);
            Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
        }
    }

    [Fact]
    public async Task Version_conflicts_and_changed_dependencies_require_new_review_without_writes_to_schedule()
    {
        await using var db = Context(); var actor = await Seed(db); var service = Service(db);
        var id = await service.CreateAsync("file.xlsx", Bytes(), actor);
        var old = (await service.GetAsync(id, actor))!;
        Assert.True((await service.ReviseAsync(id, old.Version, actor, old.Resolutions)).Success);
        Assert.False((await service.ReviseAsync(id, old.Version, actor, old.Resolutions)).Success);
        var reviewed = (await service.GetAsync(id, actor))!;
        (await db.ProductionShifts.FirstAsync()).IsActive = false; await db.SaveChangesAsync();
        var fresh = (await service.GetAsync(id, actor))!;
        Assert.False(fresh.IsCurrent);
        Assert.Equal(reviewed.Version, fresh.Version); // GET did not persist a revision.
        var result = await service.ConfirmAsync(new(id, reviewed.Version, reviewed.ReviewedFingerprint, Guid.NewGuid(), actor));
        Assert.False(result.Success);
        Assert.Equal(reviewed.Version + 1, (await service.GetAsync(id, actor))!.Version);
        Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
        var revision = await db.ProductionImportRevisions.FirstAsync(); revision.Action = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Owner_deletes_unconfirmed_drafts_with_their_revisions_and_confirmed_imports_remain()
    {
        var database = Guid.NewGuid().ToString(); Guid actor, reviewing, discarded, confirmed;
        await using (var db = Context(database))
        {
            actor = await Seed(db); var service = Service(db);
            reviewing = await service.CreateAsync("reviewing.xlsx", Bytes(), actor);
            var view = (await service.GetAsync(reviewing, actor))!;
            Assert.True((await service.ReviseAsync(reviewing, view.Version, actor, view.Resolutions)).Success);
            discarded = await service.CreateAsync("discarded.xlsx", Bytes(), actor);
            view = (await service.GetAsync(discarded, actor))!;
            Assert.True((await service.ReviseAsync(discarded, view.Version, actor, view.Resolutions, discard: true)).Success);
            confirmed = await service.CreateAsync("confirmed.xlsx", Bytes(), actor);
            (await db.ProductionImportDrafts.SingleAsync(x => x.Id == confirmed)).Status = ProductionImportDraftStatus.Confirmed;
            await db.SaveChangesAsync();
        }
        await using (var db = Context(database))
        {
            var service = Service(db);
            var other = new User { FullName = "Other admin", RoleId = 1, PinHash = "test", PinLookup = "other" };
            db.Add(other); await db.SaveChangesAsync();
            Assert.Equal(ProductionDailyCommandStatus.NotFound, (await service.DeleteAsync(reviewing, other.Id)).Status);
            Assert.True((await service.DeleteAsync(reviewing, actor)).Success);
            Assert.True((await service.DeleteAsync(discarded, actor)).Success);
            Assert.Equal(ProductionDailyCommandStatus.NotFound, (await service.DeleteAsync(reviewing, actor)).Status);
            Assert.Equal(ProductionDailyCommandStatus.ValidationFailed, (await service.DeleteAsync(confirmed, actor)).Status);
            Assert.Equal(confirmed, Assert.Single(await service.ListAsync(actor)).Id);
            Assert.All(await db.ProductionImportRevisions.AsNoTracking().ToListAsync(), x => Assert.Equal(confirmed, x.DraftId));
        }
        await using (var db = Context(database))
        {
            db.Remove(await db.ProductionImportDrafts.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            db.Remove(await db.ProductionImportRevisions.SingleAsync());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains(nameof(ProductionImportRevision), error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Manual_zero_is_explicit_and_invalid_quantities_or_forged_product_are_rejected()
    {
        await using var db = Context(); await Seed(db);
        var id = (await db.Products.SingleAsync()).Id;
        var bytes = Bytes(w => Prior(w).Tables.Remove("PendingNextWeek"));
        var importer = new ProductionScheduleImportService(db, TimeProvider.System);
        foreach (var resolution in new[] { new ProductionOpeningResolution(id, -1, 0, 0, "Checked"), new(id, 0.00001m, 0, 0, "Checked"),
            new(id, 0, 0, 0, ""), new(Guid.NewGuid(), 0, 0, 0, "Checked") })
            Assert.False((await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx", ProductionScheduleImportResolutions.None with { Opening = [resolution] })).CanConfirm);
        var preview = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx", ProductionScheduleImportResolutions.None with { Opening = [new(id, 0, 0, 0, "No physical pending work")] });
        Assert.True(preview.CanConfirm); Assert.Empty(preview.Weeks.Single(x => x.IsDraft).OpeningCarryovers);
    }
}
