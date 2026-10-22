using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DotNetSqlPerformanceLab.Infrastructure.Persistence;

public sealed class PerformanceLabDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<PerformanceLabDbContext>
{
    public PerformanceLabDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set ConnectionStrings__Postgres before running EF Core commands.");
        }

        var options = new DbContextOptionsBuilder<PerformanceLabDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PerformanceLabDbContext(options);
    }
}
