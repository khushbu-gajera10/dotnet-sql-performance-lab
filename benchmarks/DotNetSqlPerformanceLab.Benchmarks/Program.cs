using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotNetSqlPerformanceLab.Infrastructure.Persistence;
using DotNetSqlPerformanceLab.Infrastructure.Queries;
using DotNetSqlPerformanceLab.Infrastructure.SeedData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

var options = Options.Parse(args);
if (options.Help) { Usage(); return 0; }

try
{
    var runDir = options.Scenario is null
        ? null
        : Path.GetFullPath(options.Output ?? Path.Combine("artifacts", "benchmark-runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ")));
    if (runDir is not null)
        EnsureOutputDirectoryIsAvailable(runDir);

    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Set ConnectionStrings__Postgres. The runner never seeds or resets data.");
    if (options.Scenario is null)
    {
        var profile = SeedDatasetProfile.ForOrderCount(options.Orders);
        Console.WriteLine($"Seed: {options.Seed}; orders: {profile.OrderCount}; customers: {profile.CustomerCount}; products: {profile.ProductCount}; reset: {options.Reset}");
        var seedOptions = new DbContextOptionsBuilder<PerformanceLabDbContext>().UseNpgsql(connectionString).Options;
        await using var seedContext = new PerformanceLabDbContext(seedOptions);
        var seedCounts = await new SeedDataService(seedContext).SeedAsync(options.Seed, profile, options.Reset);
        Console.WriteLine($"Seed complete. Actual rows — customers: {seedCounts.Customers}; products: {seedCounts.Products}; orders: {seedCounts.Orders}; order_lines: {seedCounts.OrderLines}.");
        return 0;
    }
    if (options.Reset) throw new ArgumentException("--reset is only valid for the explicit seed workflow (omit --scenario). Benchmark runs never reset data.");
    return await RunBenchmarks(options, connectionString, runDir!);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Run failed: {ex.Message}");
    return 1;
}

static void EnsureOutputDirectoryIsAvailable(string runDir)
{
    if (Directory.Exists(runDir) && Directory.EnumerateFileSystemEntries(runDir).Any())
        throw new InvalidOperationException(
            $"Benchmark output directory '{runDir}' already exists and is not empty. Choose a fresh directory with --output <new-path>; existing files were left untouched.");
}

static async Task<int> RunBenchmarks(Options options, string connectionString, string runDir)
{
    using var http = new HttpClient { BaseAddress = new Uri(options.ApiUrl), Timeout = TimeSpan.FromSeconds(30) };
    var requiresApi = options.Scenario is "all" or "api-response";
    var httpAvailable = !requiresApi || await ApiAvailable(http);
    if (requiresApi && !httpAvailable)
        throw new HttpRequestException($"API unavailable at {options.ApiUrl} (health probe did not return HTTP 200). Start the API as documented; API scenario was not skipped.");
    var profile = SeedDatasetProfile.ForOrderCount(options.Orders);
    var services = new ServiceCollection();
    services.AddScoped<SqlCommandCounterInterceptor>();
    services.AddDbContext<PerformanceLabDbContext>((sp, builder) => builder.UseNpgsql(connectionString)
        .AddInterceptors(sp.GetRequiredService<SqlCommandCounterInterceptor>()));
    services.AddScoped<OrderQueryService>();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PerformanceLabDbContext>();
    var counter = scope.ServiceProvider.GetRequiredService<SqlCommandCounterInterceptor>();
    await db.Database.OpenConnectionAsync(); // single untimed connection/context warmup
    await db.Database.CloseConnectionAsync();
    var counts = new { Customers = await db.Customers.CountAsync(), Products = await db.Products.CountAsync(), Orders = await db.Orders.CountAsync(), OrderLines = await db.OrderLines.CountAsync() };
    if (counts.Orders != profile.OrderCount || counts.Customers != profile.CustomerCount || counts.Products != profile.ProductCount)
        throw new InvalidOperationException($"Dataset does not match requested profile. Actual rows: customers={counts.Customers}, products={counts.Products}, orders={counts.Orders}, lines={counts.OrderLines}; expected customers={profile.CustomerCount}, products={profile.ProductCount}, orders={profile.OrderCount}. Seed explicitly first.");
    var pgVersion = await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").SingleAsync();
    var endpoint = new NpgsqlConnectionStringBuilder(connectionString);
    var dockerLimits = await GetDockerLimits();
    var service = scope.ServiceProvider.GetRequiredService<OrderQueryService>();
    var cutoff = DateTimeOffset.Parse("2024-06-01T00:00:00Z");
    var search = new OrderSearchRequest("Paid", DateTimeOffset.Parse("2024-01-01T00:00:00Z"), DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
    var orderIds = await db.Orders.OrderBy(o => o.Id).Select(o => o.Id).Take(25).ToArrayAsync();
    var customerIds = await db.Customers.OrderBy(c => c.Id).Select(c => c.Id).Take(8).ToArrayAsync();
    var deepSkip = Math.Max(0, options.Orders * 3 / 4);
    var cursor = await db.Orders.AsNoTracking().OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id).Skip(Math.Max(0, deepSkip - 1)).Select(o => new { o.OrderDate, o.Id }).FirstAsync();
    var latestCustomer = await db.Orders.AsNoTracking().OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id).Select(o => o.CustomerId).FirstAsync();
    var env = new { Seed = options.Seed, RequestedOrders = options.Orders, ActualCounts = counts, Runtime = Environment.Version.ToString(), EfCore = typeof(DbContext).Assembly.GetName().Version?.ToString(), PostgreSql = pgVersion, DatabaseHost = endpoint.Host, DatabasePort = endpoint.Port, DatabaseName = endpoint.Database, DatabaseExecution = "Database connection uses the configured PostgreSQL endpoint; local run was Docker Compose.", DockerDatabaseContainerLimits = dockerLimits, OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), ProcessorCount = Environment.ProcessorCount, GcMemoryInfo = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, ApiUrl = options.ApiUrl, ApiExecution = "Loopback HTTP API process on host", ApiAvailable = httpAvailable, WarmupIterations = 3, MeasuredIterations = 10, Command = Environment.CommandLine, Limitations = new[] { "Warm PostgreSQL/OS page cache and pooled connections; no cache clearing.", "Docker Desktop VM-level resource limits and concurrent workload may affect results.", "Short-run timing noise; compare repeated runs on the same host.", "EF command counts count executions/retries separately; transactions are not commands." } };
    Console.WriteLine(JsonSerializer.Serialize(env, JsonOptions()));

    Directory.CreateDirectory(runDir);
    var results = new List<CaseResult>();
    async Task<T> Query<T>(Func<CancellationToken, Task<T>> op) { db.ChangeTracker.Clear(); counter.Reset(); var value = await op(CancellationToken.None); return value; }
    async Task Pair<T>(string scenario, string baselineName, string optimizedName, string inputs, Func<CancellationToken, Task<T>> baseline, Func<CancellationToken, Task<T>> optimized)
    {
        var left = await Query(baseline); var leftCommands = counter.Count;
        var right = await Query(optimized); var rightCommands = counter.Count;
        if (JsonSerializer.Serialize(left, JsonOptions()) != JsonSerializer.Serialize(right, JsonOptions()))
            throw new InvalidOperationException($"{scenario} variants returned different results for identical inputs.");
        var variants = new[] { (baselineName, baseline), (optimizedName, optimized) };
        // Three untimed operation warmups each; then ten alternating samples (deterministic order).
        foreach (var variant in variants) for (var i = 0; i < 3; i++) await Query(variant.Item2);
        var samples = new Dictionary<string, List<Sample>> { [baselineName] = [], [optimizedName] = [] };
        for (var i = 0; i < 10; i++)
        {
            var order = i % 2 == 0 ? variants : variants.Reverse().ToArray();
            foreach (var variant in order)
            {
                counter.Reset(); var allocatedBefore = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
                await variant.Item2(CancellationToken.None);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
                samples[variant.Item1].Add(new Sample(elapsed, counter.Count, allocated, null));
            }
        }
        results.Add(Summarize(scenario, baselineName, inputs, samples[baselineName])); results.Add(Summarize(scenario, optimizedName, inputs, samples[optimizedName]));
    }

    var selected = options.Scenario == "all" ? new[] { "customer-history", "order-search", "pagination", "order-totals", "related-data", "api-response" } : [options.Scenario!];
    foreach (var scenario in selected)
    {
        switch (scenario)
        {
            case "customer-history":
                await Pair(scenario, "tracked", "projection", $"customerId={latestCustomer}", async ct => await service.GetCustomerHistoryTrackedAsync(latestCustomer, ct) ?? throw new InvalidOperationException("Customer history missing."), async ct => await service.GetCustomerHistoryProjectionAsync(latestCustomer, ct) ?? throw new InvalidOperationException("Customer history missing."));
                break;
            case "order-search":
                await Pair(scenario, "materialize-filter-sort", "sql-translated", $"status={search.Status};from={search.From:O};to={search.To:O}", ct => service.SearchOrdersBaselineAsync(search, ct), ct => service.SearchOrdersTranslatedAsync(search, ct));
                break;
            case "pagination":
                var offsetReq = new OrderPageRequest(deepSkip, 50);
                var keysetReq = new OrderKeysetRequest(cursor.OrderDate, cursor.Id, 50);
                await Pair(scenario, "deep-offset", "keyset", $"skip={deepSkip};take=50;cursorDate={cursor.OrderDate:O};cursorId={cursor.Id};ordering=OrderDateDesc,IdDesc", ct => service.GetOffsetPageAsync(offsetReq, ct), ct => service.GetKeysetPageAsync(keysetReq, ct));
                break;
            case "order-totals":
                await Pair(scenario, "per-order-queries", "grouped-aggregation", $"orderIds={string.Join(',', orderIds)}", ct => service.GetOrderTotalsPerOrderAsync(orderIds, ct), ct => service.GetOrderTotalsGroupedAsync(orderIds, ct));
                break;
            case "related-data":
                await Pair(scenario, "repeated-related-queries", "explicit-projection", $"customerIds={string.Join(',', customerIds)}", ct => service.GetRelatedDataRepeatedAsync(customerIds, ct), ct => service.GetRelatedDataProjectionAsync(customerIds, ct));
                break;
            case "api-response":
                if (!httpAvailable) throw new HttpRequestException($"API unavailable at {options.ApiUrl}. Start it as documented; API scenario was not skipped.");
                await BenchmarkHttp(http, latestCustomer, results);
                break;
            default: throw new ArgumentException($"Unknown scenario '{scenario}'. Use --scenario all or one of: customer-history, order-search, pagination, order-totals, related-data, api-response.");
        }
    }

    var plans = await CapturePlans(db, runDir, latestCustomer, deepSkip);
    var indexes = options.EvaluateIndexes ? await EvaluateIndexes(db, runDir, latestCustomer, search, deepSkip, cursor.OrderDate, cursor.Id) : Array.Empty<object>();
    var report = new { SchemaVersion = 1, GeneratedAtUtc = DateTimeOffset.UtcNow, Configuration = env, Results = results, QueryPlans = plans, CandidateIndexEvaluation = indexes };
    await File.WriteAllTextAsync(Path.Combine(runDir, "results.json"), JsonSerializer.Serialize(report, JsonOptions()));
    Console.WriteLine($"Wrote {Path.Combine(runDir, "results.json")}");
    foreach (var r in results) Console.WriteLine($"{r.Scenario}/{r.Variant}: median={r.MedianMs:F3}ms p95={r.P95Ms:F3}ms min={r.MinMs:F3} max={r.MaxMs:F3}ms SQL={r.CommandCount} allocMedian={r.MedianAllocatedBytes}");
    return 0;
}

static async Task<bool> ApiAvailable(HttpClient http)
{
    try { using var response = await http.GetAsync("/health"); return response.StatusCode == HttpStatusCode.OK; }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return false; }
}

static async Task<string> GetDockerLimits()
{
    try
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "docker", UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        } };
        process.StartInfo.ArgumentList.Add("inspect");
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("{{.HostConfig.Memory}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.CpusetCpus}}");
        process.StartInfo.ArgumentList.Add("dotnet-sql-performance-lab-db-1");
        if (!process.Start()) return "unavailable";
        var outputTask = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        return process.ExitCode == 0 ? output.Trim() + " (memory-bytes|nano-cpus|cpu-set; zero means unset)" : "unavailable";
    }
    catch { return "unavailable"; }
}

static async Task BenchmarkHttp(HttpClient http, int customerId, List<CaseResult> results)
{
    var routes = new[] { ("baseline", $"/api/queries/customers/{customerId}/response/baseline"), ("optimized", $"/api/queries/customers/{customerId}/response/optimized") };
    async Task<(byte[] Body, int Commands)> Request(string path)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode(); var body = await response.Content.ReadAsByteArrayAsync();
        var commands = int.TryParse(response.Headers.GetValues("X-Sql-Command-Count").FirstOrDefault(), out var count) ? count : -1;
        return (body, commands);
    }
    var baseline = await Request(routes[0].Item2); var optimized = await Request(routes[1].Item2);
    using (var a = JsonDocument.Parse(baseline.Body)) using (var b = JsonDocument.Parse(optimized.Body))
        if (JsonSerializer.Serialize(a.RootElement, JsonOptions()) != JsonSerializer.Serialize(b.RootElement, JsonOptions())) throw new InvalidOperationException("API variants returned different JSON results.");
    var samples = routes.ToDictionary(x => x.Item1, _ => new List<Sample>());
    foreach (var route in routes) for (var i = 0; i < 3; i++) await Request(route.Item2);
    for (var i = 0; i < 10; i++) foreach (var route in i % 2 == 0 ? routes : routes.Reverse())
    {
        var start = Stopwatch.GetTimestamp(); var result = await Request(route.Item2); var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        samples[route.Item1].Add(new Sample(ms, result.Commands, null, result.Body.Length));
    }
    foreach (var route in routes) results.Add(Summarize("api-response", route.Item1, $"customerId={customerId};path={route.Item2};method=GET", samples[route.Item1]));
}

static async Task<object[]> CapturePlans(PerformanceLabDbContext db, string dir, int customerId, int deepSkip)
{
    var sql = new Dictionary<string, string>
    {
        ["customer-date"] = $"SELECT \"Id\", \"OrderDate\" FROM orders WHERE \"CustomerId\" = {customerId} ORDER BY \"OrderDate\" DESC, \"Id\" DESC LIMIT 51",
        ["status-date"] = "SELECT \"Id\", \"OrderDate\" FROM orders WHERE \"Status\" = 'Paid' ORDER BY \"OrderDate\" DESC, \"Id\" DESC LIMIT 100",
        ["global-date"] = $"SELECT \"Id\", \"OrderDate\" FROM orders ORDER BY \"OrderDate\" DESC, \"Id\" DESC OFFSET {deepSkip} LIMIT 50",
        ["lines-by-order"] = "SELECT \"OrderId\", sum(\"Quantity\" * \"UnitPrice\") FROM order_lines WHERE \"OrderId\" = 1 GROUP BY \"OrderId\"",
        ["lines-by-product"] = "SELECT \"OrderId\", \"Quantity\" FROM order_lines WHERE \"ProductId\" = 1",
        ["grouped-order-totals"] = "SELECT \"OrderId\", sum(\"Quantity\" * \"UnitPrice\") FROM order_lines WHERE \"OrderId\" IN (1,2,3,4,5,6,7,8,9,10) GROUP BY \"OrderId\"",
        ["related-data-join"] = "SELECT c.\"Id\", o.\"Id\", l.\"Id\", p.\"Name\" FROM customers c LEFT JOIN orders o ON c.\"Id\"=o.\"CustomerId\" LEFT JOIN order_lines l ON o.\"Id\"=l.\"OrderId\" LEFT JOIN products p ON l.\"ProductId\"=p.\"Id\" WHERE c.\"Id\" IN (1,2,3,4,5,6,7,8) ORDER BY c.\"Id\",o.\"OrderDate\" DESC,o.\"Id\" DESC,l.\"Id\""
    };
    var result = new List<object>();
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    var closeWhenDone = connection.State == System.Data.ConnectionState.Closed;
    if (closeWhenDone) await connection.OpenAsync();
    try
    {
        foreach (var (name, query) in sql)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"plan-{name}.sql"), query + ";\n");
            await using var command = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {query}", connection);
            var plan = (string)(await command.ExecuteScalarAsync() ?? "[]");
            await File.WriteAllTextAsync(Path.Combine(dir, $"plan-{name}.json"), plan);
            result.Add(new { Name = name, SqlFile = $"plan-{name}.sql", PlanFile = $"plan-{name}.json", Plan = JsonDocument.Parse(plan).RootElement.Clone() });
        }
    }
    finally { if (closeWhenDone) await connection.CloseAsync(); }
    return result.ToArray();
}

static async Task<object[]> EvaluateIndexes(PerformanceLabDbContext db, string dir, int customerId, OrderSearchRequest search, int deepSkip, DateTimeOffset cursorDate, int cursorId)
{
    // Existing FK single-column indexes are inspected first. Candidate order indexes are session-owned for this run and always removed.
    const string inspect = "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = current_schema() AND tablename IN ('customers','products','orders','order_lines') ORDER BY tablename,indexname";
    var found = new List<object>();
    var conn = (NpgsqlConnection)db.Database.GetDbConnection();
    var closeWhenDone = conn.State == System.Data.ConnectionState.Closed;
    if (closeWhenDone) await conn.OpenAsync();
    await using (var command = new NpgsqlCommand(inspect, conn)) await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync()) found.Add(new { Table = reader.GetString(0), Name = reader.GetString(1), Definition = reader.GetString(2) });
    var suffix = $"{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
    var candidates = new[]
    {
        ($"ix_orders_customer_date_{suffix}", "CREATE INDEX ix_orders_customer_date_" + suffix + " ON orders (\"CustomerId\", \"OrderDate\" DESC, \"Id\" DESC)"),
        ($"ix_orders_status_date_{suffix}", "CREATE INDEX ix_orders_status_date_" + suffix + " ON orders (\"Status\", \"OrderDate\" DESC, \"Id\" DESC)"),
        ($"ix_orders_global_date_{suffix}", "CREATE INDEX ix_orders_global_date_" + suffix + " ON orders (\"OrderDate\" DESC, \"Id\" DESC)")
    };
    var runs = new List<object>();
    async Task<double[]> MeasureSql(string sql)
    {
        async Task Execute()
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
        }
        for (var i = 0; i < 3; i++) await Execute();
        var values = new double[10];
        for (var i = 0; i < values.Length; i++) { var start = Stopwatch.GetTimestamp(); await Execute(); values[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        return values;
    }
    try
    {
        foreach (var (name, ddl) in candidates)
        {
            await using (var create = new NpgsqlCommand(ddl, conn)) await create.ExecuteNonQueryAsync();
            await using (var analyze = new NpgsqlCommand("ANALYZE orders", conn)) await analyze.ExecuteNonQueryAsync();
            var sql = name.Contains("status", StringComparison.Ordinal)
                ? "SELECT \"Id\", \"OrderDate\" FROM orders WHERE \"Status\"='Paid' ORDER BY \"OrderDate\" DESC, \"Id\" DESC LIMIT 100"
                : name.Contains("customer", StringComparison.Ordinal)
                    ? $"SELECT \"Id\", \"OrderDate\" FROM orders WHERE \"CustomerId\"={customerId} ORDER BY \"OrderDate\" DESC, \"Id\" DESC LIMIT 51"
                    : $"SELECT \"Id\", \"OrderDate\" FROM orders ORDER BY \"OrderDate\" DESC, \"Id\" DESC OFFSET {deepSkip} LIMIT 50";
            var baselineSamples = await MeasureSql(sql);
            await using (var explain = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {sql}", conn))
            {
                var plan = (string)(await explain.ExecuteScalarAsync() ?? "[]"); await File.WriteAllTextAsync(Path.Combine(dir, $"candidate-{name}.json"), plan);
            }
            var indexedSamples = await MeasureSql(sql);
            await using (var size = new NpgsqlCommand($"SELECT pg_relation_size('{name}')", conn))
                runs.Add(new { Index = name, IntendedPattern = sql, IndexBytes = await size.ExecuteScalarAsync(), CommandsPerOperation = 1, PlanFile = $"candidate-{name}.json", BaselineSamplesMs = baselineSamples, CandidateSamplesMs = indexedSamples, BaselineMedianMs = Median(baselineSamples), CandidateMedianMs = Median(indexedSamples), Note = "Temporary index timings are diagnostic and order/cache sensitive; repeat full suite before any retention decision." });
            await using (var drop = new NpgsqlCommand($"DROP INDEX IF EXISTS \"{name}\"", conn)) await drop.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        foreach (var (name, _) in candidates)
            await using (var drop = new NpgsqlCommand($"DROP INDEX IF EXISTS \"{name}\"", conn)) await drop.ExecuteNonQueryAsync();
        if (closeWhenDone) await conn.CloseAsync();
    }
    _ = search; _ = cursorDate; _ = cursorId;
    await File.WriteAllTextAsync(Path.Combine(dir, "existing-indexes.json"), JsonSerializer.Serialize(found, JsonOptions()));
    return runs.Prepend(new { ExistingIndexesFile = "existing-indexes.json", Note = "EF defaults include Order.CustomerId, OrderLine.OrderId and OrderLine.ProductId FK indexes; no duplicate candidates were created for these access paths." }).Cast<object>().ToArray();
}

static CaseResult Summarize(string scenario, string variant, string inputs, List<Sample> samples)
{
    var sorted = samples.Select(x => x.ElapsedMs).Order().ToArray();
    return new CaseResult(scenario, variant, inputs, samples, sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[0], sorted[^1], samples[0].Commands, samples.Where(x => x.AllocatedBytes.HasValue).Select(x => x.AllocatedBytes!.Value).Order().ElementAtOrDefault(samples.Count / 2), samples.Where(x => x.ResponseBytes.HasValue).Select(x => x.ResponseBytes!.Value).DefaultIfEmpty().Max());
}

static double Median(double[] values) => values.Order().ElementAt(values.Length / 2);

static JsonSerializerOptions JsonOptions() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
static void Usage() => Console.WriteLine("Seed: [--seed INT] [--orders 10000|100000] [--reset]. Benchmark: [--seed INT] [--orders 10000|100000] --scenario all|customer-history|order-search|pagination|order-totals|related-data|api-response [--api-url http://127.0.0.1:5179] [--output PATH] [--evaluate-indexes]. Benchmark mode never seeds/resets.");

internal sealed record Sample(double ElapsedMs, int Commands, long? AllocatedBytes, long? ResponseBytes);
internal sealed record CaseResult(string Scenario, string Variant, string Inputs, IReadOnlyList<Sample> Samples, double MedianMs, double P95Ms, double MinMs, double MaxMs, int CommandCount, long? MedianAllocatedBytes, long ResponseBytes);
internal sealed record Options(int Seed, int Orders, bool Reset, string? Scenario, string ApiUrl, string? Output, bool EvaluateIndexes, bool Help)
{
    public static Options Parse(string[] args)
    {
        var seed = 20261019; var orders = 10000; var reset = false; string? scenario = null; var url = "http://127.0.0.1:5179"; string? output = null; var indexes = false; var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException($"{args[i - 1]} requires a value.");
            switch (args[i])
            {
                case "--seed": seed = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--orders": orders = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--scenario": scenario = Value(); break;
                case "--api-url": url = Value(); break;
                case "--output": output = Value(); break;
                case "--evaluate-indexes": indexes = true; break;
                case "--reset": reset = true; break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }
        _ = SeedDatasetProfile.ForOrderCount(orders);
        return new Options(seed, orders, reset, scenario, url, output, indexes, help);
    }
}