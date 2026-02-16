namespace Semantico.Core.Models.ControlTower;

public record AnomalySparklinePoint
{
    public DateTime Date { get; init; }
    public int AnomalyCount { get; init; }
}
