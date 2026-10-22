namespace DotNetSqlPerformanceLab.Infrastructure.SeedData;

public static class SeedDataGuard
{
    public static void EnsureCanSeed(
        long customerRows, long productRows, long orderRows, long orderLineRows, bool reset)
    {
        if (reset || customerRows + productRows + orderRows + orderLineRows == 0)
            return;

        throw new InvalidOperationException(
            "Seed tables already contain data. No rows were changed. Pass --reset to delete all rows " +
            "from order_lines, orders, products, and customers before reseeding.");
    }
}
