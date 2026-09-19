namespace ReadMeGenerator.Models;

public sealed class FundingStats
{
    public double CurrentDollars { get; init; }
    public double GoalDollars { get; init; } = 500;
    public double Percent => Math.Min((CurrentDollars / GoalDollars) * 100, 100);
    public int PixelWidth => (int)(400 * (Percent / 100));
}
