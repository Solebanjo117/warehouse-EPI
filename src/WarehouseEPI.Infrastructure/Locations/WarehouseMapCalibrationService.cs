using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Locations;

public sealed record WarehouseMapGeoSample(double Latitude, double Longitude, double Accuracy, long Timestamp);
public sealed record WarehouseMapCalibrationPointInput(Guid Id, string Kind, string Name, decimal MapX, decimal MapY,
    IReadOnlyList<WarehouseMapGeoSample> Samples);
public sealed record WarehouseMapCalibrationCommand(Guid OperationId, Guid RequestedByUserId, int ExpectedRevision,
    string Pin, string Reason, IReadOnlyList<WarehouseMapCalibrationPointInput> Points);
public sealed record WarehouseMapCalibrationTransform(double OriginLatitude, double OriginLongitude,
    double A11, double A12, double A13, double A21, double A22, double A23,
    double FitErrorMeters, double CheckErrorMeters, double MaximumScaleSvgPerMeter);
public sealed record WarehouseMapCalibrationReview(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    WarehouseMapCalibrationTransform? Transform, int ReferenceCount, int CheckCount);
public sealed record WarehouseMapCalibrationState(bool Exists, bool IsCurrent, int Revision, int LayoutVersion,
    DateTimeOffset? PublishedAt, WarehouseMapCalibrationTransform? Transform,
    IReadOnlyList<WarehouseMapCalibrationPointInput> Points);
public enum WarehouseMapCalibrationSaveStatus { Success, InvalidPin, Unauthorized, ValidationFailed, Conflict, IdempotencyConflict, NotInitialized }
public sealed record WarehouseMapCalibrationSaveResult(WarehouseMapCalibrationSaveStatus Status, int Revision = 0,
    IReadOnlyList<string>? Errors = null);

public sealed class WarehouseMapCalibrationService(
    WarehouseDbContext dbContext,
    UserPinService? pins = null,
    TimeProvider? timeProvider = null)
{
    private const double EarthRadiusMeters = 6378137d;

    public async Task<WarehouseMapCalibrationState> GetStateAsync(bool includePoints, CancellationToken token = default)
    {
        var layout = await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        var query = dbContext.WarehouseMapCalibrations.AsNoTracking();
        if (includePoints) query = query.Include(item => item.Points);
        var calibration = await query
            .Where(item => item.LayoutId == 1 && item.Status == WarehouseMapCalibrationStatus.Active)
            .SingleOrDefaultAsync(token);
        if (calibration is null)
            return new(false, false, 0, layout?.Version ?? 0, null, null, []);
        return new(true, layout is not null && calibration.LayoutVersion == layout.Version, calibration.Revision,
            calibration.LayoutVersion, calibration.PublishedAt, ToTransform(calibration),
            includePoints ? calibration.Points.OrderBy(item => item.Kind).ThenBy(item => item.Name).Select(ToInput).ToArray() : []);
    }

    public async Task<WarehouseMapCalibrationReview> ReviewAsync(
        IReadOnlyList<WarehouseMapCalibrationPointInput> points, CancellationToken token = default)
    {
        var layout = await dbContext.WarehouseMapLayouts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == 1, token);
        if (layout is null) return new(["Guarda el croquis antes de calibrar la ubicación."], [], null, 0, 0);
        return Calculate(points, layout.CanvasWidth, layout.CanvasHeight);
    }

    public async Task<WarehouseMapCalibrationSaveResult> PublishAsync(
        WarehouseMapCalibrationCommand command, CancellationToken token = default)
    {
        if (command.OperationId == Guid.Empty || command.ExpectedRevision < 0)
            return new(WarehouseMapCalibrationSaveStatus.ValidationFailed, Errors: ["La operación de calibración no es válida."]);
        var auth = await AuthorizeAsync(command.RequestedByUserId, command.Pin, token);
        if (auth.Status is not WarehouseMapCalibrationSaveStatus.Success)
            return new(auth.Status);
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 5 or > 500)
            return new(WarehouseMapCalibrationSaveStatus.ValidationFailed, Errors: ["Escribe un motivo de 5 a 500 caracteres."]);
        var payload = JsonSerializer.Serialize(command.Points.OrderBy(item => item.Id));
        var fingerprint = Hash($"PUBLISH|{command.RequestedByUserId:N}|{auth.User!.Id:N}|{command.ExpectedRevision}|{reason}|{payload}");
        var repeated = await dbContext.WarehouseMapCalibrationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (repeated is not null)
            return new(repeated.RequestFingerprint == fingerprint ? WarehouseMapCalibrationSaveStatus.Success
                : WarehouseMapCalibrationSaveStatus.IdempotencyConflict);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(token) : null;
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await dbContext.Database.ExecuteSqlRawAsync("SELECT id FROM warehouse_map_layouts WHERE id = 1 FOR UPDATE", token);
        var layout = await dbContext.WarehouseMapLayouts.SingleOrDefaultAsync(item => item.Id == 1, token);
        if (layout is null) return new(WarehouseMapCalibrationSaveStatus.NotInitialized);
        var active = await dbContext.WarehouseMapCalibrations
            .SingleOrDefaultAsync(item => item.LayoutId == 1 && item.Status == WarehouseMapCalibrationStatus.Active, token);
        var activeRevision = active?.Revision ?? 0;
        if (activeRevision != command.ExpectedRevision)
            return new(WarehouseMapCalibrationSaveStatus.Conflict, activeRevision);
        var nextRevision = (await dbContext.WarehouseMapCalibrations
            .Where(item => item.LayoutId == 1).MaxAsync(item => (int?)item.Revision, token) ?? 0) + 1;
        var review = Calculate(command.Points, layout.CanvasWidth, layout.CanvasHeight);
        if (review.Errors.Count != 0 || review.Transform is null)
            return new(WarehouseMapCalibrationSaveStatus.ValidationFailed, activeRevision, review.Errors);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (active is not null)
        {
            active.Status = WarehouseMapCalibrationStatus.Disabled;
            active.DisabledAt = now;
            active.DisabledByUserId = auth.User.Id;
        }
        var transform = review.Transform;
        var calibration = new WarehouseMapCalibration
        {
            LayoutId = 1,
            LayoutVersion = layout.Version,
            Revision = nextRevision,
            OriginLatitude = transform.OriginLatitude,
            OriginLongitude = transform.OriginLongitude,
            A11 = transform.A11, A12 = transform.A12, A13 = transform.A13,
            A21 = transform.A21, A22 = transform.A22, A23 = transform.A23,
            FitErrorMeters = transform.FitErrorMeters,
            CheckErrorMeters = transform.CheckErrorMeters,
            MaximumScaleSvgPerMeter = transform.MaximumScaleSvgPerMeter,
            PublishedByUserId = auth.User.Id,
            PublishedAt = now
        };
        foreach (var input in command.Points)
        {
            var summary = Summarize(input.Samples);
            calibration.Points.Add(new WarehouseMapCalibrationPoint
            {
                Id = Guid.NewGuid(),
                Kind = ParseKind(input.Kind),
                Name = input.Name.Trim(), MapX = input.MapX, MapY = input.MapY,
                Latitude = summary.Latitude, Longitude = summary.Longitude,
                AccuracyMeters = summary.Accuracy, DispersionMeters = summary.Dispersion,
                SampleCount = input.Samples.Count,
                SamplesJson = JsonSerializer.Serialize(input.Samples)
            });
        }
        dbContext.WarehouseMapCalibrations.Add(calibration);
        dbContext.WarehouseMapCalibrationRevisions.Add(new WarehouseMapCalibrationRevision
        {
            OperationId = command.OperationId, RequestFingerprint = fingerprint,
            Calibration = calibration, Action = "PUBLISH", Reason = reason,
            ChangesJson = JsonSerializer.Serialize(new { calibration.Revision, calibration.LayoutVersion, review.ReferenceCount, review.CheckCount, review.Warnings, Transform = transform }),
            RequestedByUserId = command.RequestedByUserId, AuthorizedByUserId = auth.User.Id, RecordedAt = now
        });
        await dbContext.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return new(WarehouseMapCalibrationSaveStatus.Success, calibration.Revision);
    }

    public async Task<WarehouseMapCalibrationSaveResult> DisableAsync(Guid operationId, Guid requestedByUserId,
        int expectedRevision, string pin, string reason, CancellationToken token = default)
    {
        if (operationId == Guid.Empty || expectedRevision <= 0)
            return new(WarehouseMapCalibrationSaveStatus.ValidationFailed, Errors: ["La operación de calibración no es válida."]);
        var auth = await AuthorizeAsync(requestedByUserId, pin, token);
        if (auth.Status is not WarehouseMapCalibrationSaveStatus.Success) return new(auth.Status);
        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length is < 5 or > 500)
            return new(WarehouseMapCalibrationSaveStatus.ValidationFailed, Errors: ["Escribe un motivo de 5 a 500 caracteres."]);
        var fingerprint = Hash($"DISABLE|{requestedByUserId:N}|{auth.User!.Id:N}|{expectedRevision}|{reason}");
        var repeated = await dbContext.WarehouseMapCalibrationRevisions.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operationId, token);
        if (repeated is not null)
            return new(repeated.RequestFingerprint == fingerprint ? WarehouseMapCalibrationSaveStatus.Success : WarehouseMapCalibrationSaveStatus.IdempotencyConflict);
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(token) : null;
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await dbContext.Database.ExecuteSqlRawAsync("SELECT id FROM warehouse_map_layouts WHERE id = 1 FOR UPDATE", token);
        var active = await dbContext.WarehouseMapCalibrations.SingleOrDefaultAsync(
            item => item.LayoutId == 1 && item.Status == WarehouseMapCalibrationStatus.Active, token);
        if (active is null || active.Revision != expectedRevision)
            return new(WarehouseMapCalibrationSaveStatus.Conflict, active?.Revision ?? 0);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        active.Status = WarehouseMapCalibrationStatus.Disabled;
        active.DisabledAt = now;
        active.DisabledByUserId = auth.User.Id;
        dbContext.WarehouseMapCalibrationRevisions.Add(new WarehouseMapCalibrationRevision
        {
            OperationId = operationId, RequestFingerprint = fingerprint, CalibrationId = active.Id,
            Action = "DISABLE", Reason = reason, ChangesJson = JsonSerializer.Serialize(new { active.Revision }),
            RequestedByUserId = requestedByUserId, AuthorizedByUserId = auth.User.Id, RecordedAt = now
        });
        await dbContext.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return new(WarehouseMapCalibrationSaveStatus.Success, active.Revision);
    }

    private async Task<(WarehouseMapCalibrationSaveStatus Status, User? User)> AuthorizeAsync(Guid requesterId,
        string pin, CancellationToken token)
    {
        var requester = await dbContext.Users.AsNoTracking().Include(item => item.Role)
            .SingleOrDefaultAsync(item => item.Id == requesterId, token);
        if (requester is null || !requester.IsActive || requester.Role.Code != "ADMIN" || pins is null)
            return (WarehouseMapCalibrationSaveStatus.Unauthorized, null);
        var authorized = await pins.AuthenticateAsync(pin, token);
        return authorized is { Role.Code: "ADMIN" }
            ? (WarehouseMapCalibrationSaveStatus.Success, authorized)
            : (WarehouseMapCalibrationSaveStatus.InvalidPin, null);
    }

    internal static WarehouseMapCalibrationReview Calculate(IReadOnlyList<WarehouseMapCalibrationPointInput> points,
        decimal canvasWidth, decimal canvasHeight)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (points.Count > 100) errors.Add("La calibración admite hasta 100 puntos.");
        if (points.Any(item => item.Samples.Count > 500)) errors.Add("Cada punto admite hasta 500 lecturas.");
        if (points.Select(item => item.Id).Distinct().Count() != points.Count) errors.Add("Los puntos de calibración contienen identificadores repetidos.");
        if (points.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 100)) errors.Add("Cada punto necesita un nombre de hasta 100 caracteres.");
        if (points.GroupBy(item => $"{item.Kind.Trim().ToUpperInvariant()}|{item.Name.Trim().ToUpperInvariant()}").Any(group => group.Count() > 1)) errors.Add("Los nombres deben ser únicos dentro de cada tipo de punto.");
        if (points.Any(item => item.MapX < 0 || item.MapY < 0 || item.MapX > canvasWidth || item.MapY > canvasHeight)) errors.Add("Todos los puntos deben quedar dentro del croquis.");
        if (points.Any(item => item.Samples.Count == 0 || item.Samples.Any(sample => !ValidSample(sample)))) errors.Add("Cada punto necesita lecturas geográficas válidas.");
        var references = points.Where(item => item.Kind.Equals("REFERENCE", StringComparison.OrdinalIgnoreCase)).ToArray();
        var checks = points.Where(item => item.Kind.Equals("CHECK", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (references.Length < 4) errors.Add("Marca y captura al menos cuatro referencias.");
        if (checks.Length < 1) errors.Add("Captura al menos un punto de comprobación.");
        if (points.Any(item => !item.Kind.Equals("REFERENCE", StringComparison.OrdinalIgnoreCase) && !item.Kind.Equals("CHECK", StringComparison.OrdinalIgnoreCase))) errors.Add("El tipo de punto no es válido.");
        if (errors.Count != 0) return new(errors, warnings, null, references.Length, checks.Length);

        var summaries = points.ToDictionary(item => item.Id, item => Summarize(item.Samples));
        foreach (var point in points)
        {
            if (point.Samples.Count < 3) warnings.Add($"{point.Name}: se capturaron pocas lecturas.");
            if (summaries[point.Id].Accuracy > 30) warnings.Add($"{point.Name}: la precisión reportada es de {summaries[point.Id].Accuracy:0.#} m.");
        }
        var originLat = references.Average(item => summaries[item.Id].Latitude);
        var originLon = references.Average(item => summaries[item.Id].Longitude);
        var projected = references.Select(item =>
        {
            var local = Project(summaries[item.Id].Latitude, summaries[item.Id].Longitude, originLat, originLon);
            return (X: local.X, Y: local.Y, MapX: (double)item.MapX, MapY: (double)item.MapY);
        }).ToArray();
        if (!TryFit(projected, out var xCoefficients, out var yCoefficients))
            return new(["Las referencias no permiten calcular la calibración. Sepáralas y evita colocarlas en una sola línea."], warnings, null, references.Length, checks.Length);
        var determinant = xCoefficients[0] * yCoefficients[1] - xCoefficients[1] * yCoefficients[0];
        var maxScale = MaximumScale(xCoefficients[0], xCoefficients[1], yCoefficients[0], yCoefficients[1]);
        var minScale = Math.Abs(determinant) / maxScale;
        if (!double.IsFinite(maxScale) || maxScale <= 1e-9)
            return new(["La escala calculada no es válida. Repite las capturas en puntos más separados."], warnings, null, references.Length, checks.Length);
        if (!double.IsFinite(minScale) || minScale / maxScale < 1e-5)
            return new(["La transformación calculada es inestable. Distribuye las referencias por todo el almacén."], warnings, null, references.Length, checks.Length);
        var fitError = RmsMeters(references, summaries, originLat, originLon, xCoefficients, yCoefficients, determinant);
        var checkError = RmsMeters(checks, summaries, originLat, originLon, xCoefficients, yCoefficients, determinant);
        var transform = new WarehouseMapCalibrationTransform(originLat, originLon,
            xCoefficients[0], xCoefficients[1], xCoefficients[2], yCoefficients[0], yCoefficients[1], yCoefficients[2],
            fitError, checkError, maxScale);
        return new(errors, warnings, transform, references.Length, checks.Length);
    }

    private static double RmsMeters(IEnumerable<WarehouseMapCalibrationPointInput> points,
        IReadOnlyDictionary<Guid, SampleSummary> summaries, double originLat, double originLon,
        double[] xCoefficients, double[] yCoefficients, double determinant)
    {
        var squared = points.Select(point =>
        {
            var sample = summaries[point.Id];
            var local = Project(sample.Latitude, sample.Longitude, originLat, originLon);
            var mapX = xCoefficients[0] * local.X + xCoefficients[1] * local.Y + xCoefficients[2];
            var mapY = yCoefficients[0] * local.X + yCoefficients[1] * local.Y + yCoefficients[2];
            var dx = mapX - (double)point.MapX;
            var dy = mapY - (double)point.MapY;
            var localErrorX = (yCoefficients[1] * dx - xCoefficients[1] * dy) / determinant;
            var localErrorY = (-yCoefficients[0] * dx + xCoefficients[0] * dy) / determinant;
            return localErrorX * localErrorX + localErrorY * localErrorY;
        }).ToArray();
        return squared.Length == 0 ? 0 : Math.Sqrt(squared.Average());
    }

    private static bool TryFit((double X, double Y, double MapX, double MapY)[] points,
        out double[] xCoefficients, out double[] yCoefficients)
    {
        var normal = new double[3, 3];
        var targetX = new double[3];
        var targetY = new double[3];
        foreach (var point in points)
        {
            var row = new[] { point.X, point.Y, 1d };
            for (var i = 0; i < 3; i++)
            {
                targetX[i] += row[i] * point.MapX;
                targetY[i] += row[i] * point.MapY;
                for (var j = 0; j < 3; j++) normal[i, j] += row[i] * row[j];
            }
        }
        if (!TrySolve(normal, targetX, out xCoefficients))
        {
            yCoefficients = [];
            return false;
        }
        return TrySolve(normal, targetY, out yCoefficients);
    }

    private static bool TrySolve(double[,] source, double[] target, out double[] result)
    {
        var matrix = new double[3, 4];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++) matrix[row, column] = source[row, column];
            matrix[row, 3] = target[row];
        }
        for (var pivot = 0; pivot < 3; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 3; row++) if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-8) { result = []; return false; }
            if (best != pivot) for (var column = pivot; column < 4; column++) (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
            var divisor = matrix[pivot, pivot];
            for (var column = pivot; column < 4; column++) matrix[pivot, column] /= divisor;
            for (var row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                var factor = matrix[row, pivot];
                for (var column = pivot; column < 4; column++) matrix[row, column] -= factor * matrix[pivot, column];
            }
        }
        result = [matrix[0, 3], matrix[1, 3], matrix[2, 3]];
        return result.All(double.IsFinite);
    }

    private static (double X, double Y) Project(double latitude, double longitude, double originLatitude, double originLongitude)
    {
        var radians = Math.PI / 180d;
        return (EarthRadiusMeters * (longitude - originLongitude) * radians * Math.Cos(originLatitude * radians),
            EarthRadiusMeters * (latitude - originLatitude) * radians);
    }

    private static double MaximumScale(double a11, double a12, double a21, double a22)
    {
        var trace = a11 * a11 + a12 * a12 + a21 * a21 + a22 * a22;
        var determinant = Math.Pow(a11 * a22 - a12 * a21, 2);
        return Math.Sqrt(Math.Max(0, (trace + Math.Sqrt(Math.Max(0, trace * trace - 4 * determinant))) / 2));
    }

    private static SampleSummary Summarize(IReadOnlyList<WarehouseMapGeoSample> samples)
    {
        var latitude = Median(samples.Select(item => item.Latitude));
        var longitude = Median(samples.Select(item => item.Longitude));
        var accuracy = Median(samples.Select(item => item.Accuracy));
        var distances = samples.Select(item =>
        {
            var local = Project(item.Latitude, item.Longitude, latitude, longitude);
            return Math.Sqrt(local.X * local.X + local.Y * local.Y);
        });
        return new(latitude, longitude, accuracy, Median(distances));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(item => item).ToArray();
        if (sorted.Length == 0) return double.NaN;
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static bool ValidSample(WarehouseMapGeoSample sample) =>
        double.IsFinite(sample.Latitude) && sample.Latitude is >= -90 and <= 90
        && double.IsFinite(sample.Longitude) && sample.Longitude is >= -180 and <= 180
        && double.IsFinite(sample.Accuracy) && sample.Accuracy >= 0;

    private static WarehouseMapCalibrationPointKind ParseKind(string value) =>
        value.Equals("REFERENCE", StringComparison.OrdinalIgnoreCase)
            ? WarehouseMapCalibrationPointKind.Reference : WarehouseMapCalibrationPointKind.Check;

    private static WarehouseMapCalibrationPointInput ToInput(WarehouseMapCalibrationPoint point) => new(
        point.Id, point.Kind == WarehouseMapCalibrationPointKind.Reference ? "REFERENCE" : "CHECK", point.Name,
        point.MapX, point.MapY, JsonSerializer.Deserialize<WarehouseMapGeoSample[]>(point.SamplesJson) ?? []);

    private static WarehouseMapCalibrationTransform ToTransform(WarehouseMapCalibration value) => new(
        value.OriginLatitude, value.OriginLongitude, value.A11, value.A12, value.A13,
        value.A21, value.A22, value.A23, value.FitErrorMeters, value.CheckErrorMeters,
        value.MaximumScaleSvgPerMeter);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record SampleSummary(double Latitude, double Longitude, double Accuracy, double Dispersion);
}
