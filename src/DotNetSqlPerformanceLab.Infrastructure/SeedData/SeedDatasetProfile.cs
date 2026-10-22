namespace DotNetSqlPerformanceLab.Infrastructure.SeedData;

public sealed record SeedDatasetProfile(int OrderCount, int CustomerCount, int ProductCount)
{
    public static SeedDatasetProfile ForOrderCount(int orderCount) => orderCount switch
    {
        10_000 => new SeedDatasetProfile(10_000, 2_000, 500),
        100_000 => new SeedDatasetProfile(100_000, 20_000, 2_000),
        _ => throw new ArgumentOutOfRangeException(nameof(orderCount),
            "Supported order counts are 10000 and 100000.")
    };
}
