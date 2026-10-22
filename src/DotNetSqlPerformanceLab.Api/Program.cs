using DotNetSqlPerformanceLab.Infrastructure;
using DotNetSqlPerformanceLab.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Use(async (context, next) =>
{
    var counter = context.RequestServices.GetService<DotNetSqlPerformanceLab.Infrastructure.Persistence.SqlCommandCounterInterceptor>();
    counter?.Reset();
    context.Response.OnStarting(() =>
    {
        if (counter is not null)
            context.Response.Headers["X-Sql-Command-Count"] = counter.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    });
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("Health");
app.MapPerformanceEndpoints();

app.Run();

public partial class Program { }
