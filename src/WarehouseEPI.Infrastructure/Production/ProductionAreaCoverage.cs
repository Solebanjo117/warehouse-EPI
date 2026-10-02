namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionAreaCoverage(bool Applies, decimal BeforeAdvance, decimal AdvanceApplied,
    decimal Target, decimal Produced, decimal Balance)
{
    public decimal Covered => AdvanceApplied + Produced;
    public decimal? Ratio => Applies && BeforeAdvance > 0 ? Covered / BeforeAdvance : null;
    public decimal? Percent => Ratio is decimal ratio
        ? decimal.Round(ratio * 100, 1, MidpointRounding.AwayFromZero) : null;
    public bool CoveredByAdvance => Applies && BeforeAdvance == 0 && Balance < 0 && Produced == 0;

    public static ProductionAreaCoverage Create(bool applies, decimal signedBefore, decimal openingToday,
        decimal programmedToday, decimal produced, decimal balance)
    {
        var beforeAdvance = Math.Max(0, signedBefore) + openingToday + programmedToday;
        var credit = Math.Min(Math.Max(0, -signedBefore), beforeAdvance);
        return new(applies, beforeAdvance, credit, Math.Max(0, signedBefore + openingToday + programmedToday),
            produced, balance);
    }
}

public sealed record ProductionAccumulatedCoverage(bool Applies, decimal Target, decimal Produced,
    decimal InitialCredit)
{
    public decimal Covered => Produced + Math.Min(InitialCredit, Target);
    public decimal? Ratio => Applies && Target > 0 ? Covered / Target : null;
    public decimal? Percent => Ratio is decimal ratio
        ? decimal.Round(ratio * 100, 1, MidpointRounding.AwayFromZero) : null;

    public static ProductionAccumulatedCoverage Create(bool applies, decimal target, decimal produced,
        decimal initialCredit) => new(applies, target, produced, initialCredit);
}
