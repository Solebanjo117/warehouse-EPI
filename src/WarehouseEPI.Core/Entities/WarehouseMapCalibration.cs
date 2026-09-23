namespace WarehouseEPI.Core.Entities;

public enum WarehouseMapCalibrationStatus
{
    Active,
    Disabled
}

public enum WarehouseMapCalibrationPointKind
{
    Reference,
    Check
}

public sealed class WarehouseMapCalibration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public short LayoutId { get; set; } = 1;
    public int LayoutVersion { get; set; }
    public int Revision { get; set; } = 1;
    public WarehouseMapCalibrationStatus Status { get; set; } = WarehouseMapCalibrationStatus.Active;
    public double OriginLatitude { get; set; }
    public double OriginLongitude { get; set; }
    public double A11 { get; set; }
    public double A12 { get; set; }
    public double A13 { get; set; }
    public double A21 { get; set; }
    public double A22 { get; set; }
    public double A23 { get; set; }
    public double FitErrorMeters { get; set; }
    public double CheckErrorMeters { get; set; }
    public double MaximumScaleSvgPerMeter { get; set; }
    public string AlgorithmVersion { get; set; } = "AFFINE_TANGENT_V1";
    public Guid PublishedByUserId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public Guid? DisabledByUserId { get; set; }
    public WarehouseMapLayout Layout { get; set; } = null!;
    public User PublishedByUser { get; set; } = null!;
    public User? DisabledByUser { get; set; }
    public ICollection<WarehouseMapCalibrationPoint> Points { get; set; } = [];
}

public sealed class WarehouseMapCalibrationPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CalibrationId { get; set; }
    public WarehouseMapCalibrationPointKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal MapX { get; set; }
    public decimal MapY { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AccuracyMeters { get; set; }
    public double DispersionMeters { get; set; }
    public int SampleCount { get; set; }
    public string SamplesJson { get; set; } = "[]";
    public WarehouseMapCalibration Calibration { get; set; } = null!;
}

public sealed class WarehouseMapCalibrationRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid? CalibrationId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = "{}";
    public Guid RequestedByUserId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public WarehouseMapCalibration? Calibration { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
}
