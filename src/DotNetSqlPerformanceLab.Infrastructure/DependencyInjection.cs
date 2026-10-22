using DotNetSqlPerformanceLab.Infrastructure.Persistence;
using DotNetSqlPerformanceLab.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSqlPerformanceLab.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<SqlCommandCounterInterceptor>();
        services.AddDbContext<PerformanceLabDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string 'Postgres' is required to use the database. " +
                    "Set ConnectionStrings__Postgres in the environment or configure it in application configuration.");
            }

            options.UseNpgsql(connectionString)
                .AddInterceptors(serviceProvider.GetRequiredService<SqlCommandCounterInterceptor>());
        });
        services.AddScoped<OrderQueryService>();

        return services;
    }
}
