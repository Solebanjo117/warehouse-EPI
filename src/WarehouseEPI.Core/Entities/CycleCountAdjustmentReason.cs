namespace WarehouseEPI.Core.Entities;

public enum CycleCountAdjustmentReason
{
    UnrecordedEntry,
    UnrecordedExit,
    WrongLocation,
    UnrecordedDamageOrScrap,
    CaptureOrUnitError,
    Unknown,
    Other
}
