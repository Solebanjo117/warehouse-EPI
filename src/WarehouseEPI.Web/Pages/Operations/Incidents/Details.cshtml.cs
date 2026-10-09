using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.Incidents;

[RequestSizeLimit(30 * 1024 * 1024)]
public sealed class DetailsModel(WarehouseDbContext db, MaterialIncidentService service, WarehouseClock clock) : IncidentCaptureModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public Guid OperationId { get; set; } = Guid.NewGuid();
    [BindProperty] public long ExpectedVersion { get; set; }
    [BindProperty] public string Action { get; set; } = "Comment";
    [BindProperty] public string? Comment { get; set; } = "";
    [BindProperty] public Guid? CorrectionId { get; set; }
    public MaterialIncident Incident { get; private set; } = null!;
    public IncidentContext Snapshot { get; private set; } = null!;
    public PalletPlate? Plate { get; private set; }
    public string CurrentPlateState { get; private set; } = "";
    public IReadOnlyList<MaterialIncidentEvent> Events { get; private set; } = [];
    public IReadOnlyList<PhotoInfo> Images { get; private set; } = [];
    public IReadOnlyList<Guid> Descendants { get; private set; } = [];
    public Guid? ArrivalMovementId { get; private set; }
    public bool CanOpenStaging { get; private set; }
    public IReadOnlyList<InventoryMovementCorrection> Corrections { get; private set; } = [];
    public IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> LocalTimes { get; private set; } = new Dictionary<DateTimeOffset, DateTimeOffset>();
    public sealed record PhotoInfo(Guid Id, Guid EventId, string Name);
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    { if (!await LoadAsync(ct)) return NotFound(); ExpectedVersion = Incident.Version; return Page(); }
    public async Task<IActionResult> OnGetPhotoAsync(Guid photoId, CancellationToken ct)
    {
        var photo = await db.MaterialIncidentPhotos.AsNoTracking().SingleOrDefaultAsync(p => p.Id == photoId && p.IncidentId == Id, ct);
        if (photo is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(photo.Content, photo.ContentType);
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var photos = await ReadPhotosAsync(ct);
        if (ModelState.IsValid)
        {
            var result = await service.FollowUpAsync(new(OperationId, Id, ExpectedVersion, Action, Comment ?? "", CorrectionId, Pin, photos), ct);
            if (result.Id.HasValue && result.Error is null) return RedirectToPage(new { id = Id });
            ModelState.AddModelError("", result.Error ?? MaterialIncidentService.InvalidContext);
        }
        ClearCredentials(); if (!await LoadAsync(ct)) return NotFound();
        ExpectedVersion = Incident.Version; ModelState.Remove(nameof(ExpectedVersion)); return Page();
    }
    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var incident = await db.MaterialIncidents.AsNoTracking().Include(i => i.Product).Include(i => i.Unit).Include(i => i.DetectionLocation).Include(i => i.ReportedBy).SingleOrDefaultAsync(i => i.Id == Id, ct);
        if (incident is null) return false; Incident = incident;
        Snapshot = System.Text.Json.JsonSerializer.Deserialize<IncidentContext>(incident.Snapshot)!;
        ArrivalMovementId = incident.ArrivalLineId.HasValue ? await db.InventoryMovementLines.Where(l => l.Id == incident.ArrivalLineId).Select(l => (Guid?)l.MovementId).SingleOrDefaultAsync(ct) : null;
        CanOpenStaging = incident.ArrivalLineId.HasValue && await new StagingArrivalQuery(db).GetAsync(incident.ArrivalLineId.Value, ct) is not null;
        Corrections = await (await service.CorrectionsForAsync(incident, ct)).OrderByDescending(c => c.Id).ToListAsync(ct);
        if (incident.PlateId.HasValue) Plate = await db.PalletPlates.AsNoTracking().Include(p => p.Location).SingleOrDefaultAsync(p => p.Id == incident.PlateId, ct);
        Events = await db.MaterialIncidentEvents.AsNoTracking().Include(e => e.Responsible).Where(e => e.IncidentId == Id).OrderBy(e => e.Version).ToListAsync(ct);
        Images = await db.MaterialIncidentPhotos.AsNoTracking().Where(p => p.IncidentId == Id).Select(p => new PhotoInfo(p.Id, p.EventId, p.Name)).ToListAsync(ct);
        LocalTimes = await clock.ConvertManyAsync(Events.Select(e => e.RecordedAt).Append(incident.ReportedAt), ct);
        if (Plate is not null)
        {
            CurrentPlateState = await service.PlateStateAsync(Plate, ct);
            var edges = await MaterialIncidentLineage.LoadAsync(db, [Plate.Id], true, ct);
            var found = MaterialIncidentLineage.Expand([Plate.Id], edges, true);
            Descendants = found.Where(i => i != Plate.Id).ToArray();
        }
        return true;
    }
}
