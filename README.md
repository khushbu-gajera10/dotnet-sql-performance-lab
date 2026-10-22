# DotNetSqlPerformanceLab

A reproducible portfolio project for exploring SQL, LINQ, EF Core, and ASP.NET Core API query behavior. It compares equivalent baseline and comparison paths, records raw measurements, and captures PostgreSQL plans so each observation can be inspected rather than assumed.

## Technology and architecture

- **.NET 10 / ASP.NET Core Web API** — HTTP endpoints and application composition.
- **Core** — domain model and focused query contracts; independent of EF Core and ASP.NET Core.
- **Infrastructure** — EF Core mappings, PostgreSQL access, migrations, deterministic seed workflow, and query implementations.
- **xUnit** — unit tests and PostgreSQL-backed integration tests.
- **PostgreSQL with Docker Compose** — local database for development and benchmarks.
- **Benchmarks** — a small custom runner in the existing benchmark project; no benchmark framework dependency.

The API composes Core and Infrastructure. The benchmark runner measures query paths in process and the API path separately over loopback HTTP. Tests check correctness; benchmarks record performance without timing assertions.

## What the benchmarks compare

1. Tracked customer order history versus a read-only projection.
2. Client-side materialize/filter/sort versus a SQL-translatable order search.
3. Deep offset pagination versus keyset pagination with stable `(OrderDate, Id)` ordering.
4. Per-order total queries versus grouped database aggregation.
5. Repeated related-data loading versus one explicit projection.
6. Baseline versus comparison API response behavior, including loopback latency, response bytes, and SQL command count.

Each pair uses equivalent inputs and checks result equivalence before timing. SQL command counts are collected independently from elapsed time. API measurements include HTTP/database/serialization work; they are not query-only timings.

## Prerequisites and local PostgreSQL

- .NET 10 SDK
- Docker Desktop or Docker Engine with Docker Compose v2

In PowerShell, load a local password into the current process, choose the port used by the recorded runs, and start PostgreSQL. Do not commit credentials or local environment files.

```powershell
$securePassword = Read-Host 'Local PostgreSQL password' -AsSecureString
$env:POSTGRES_PASSWORD = [System.Net.NetworkCredential]::new('', $securePassword).Password
Remove-Variable securePassword
$env:POSTGRES_PORT = '5433'
docker compose up -d db
docker compose ps
```

Compose defaults are database `dotnet_sql_performance_lab`, user `dotnetlab`, and host port `5432`; the examples above use `5433`. `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PORT` can be overridden. If you change them, use the same database, user, and port in `ConnectionStrings__Postgres`. PostgreSQL data persists in the `postgres-data` volume. `docker compose down -v` removes that volume and its data; use it only when that deletion is intended.

The API, EF tooling, seed CLI, and benchmark runner use the same environment variable. Set it in the same shell used for commands; use the password that initialized the existing volume.

```powershell
$port = if ([string]::IsNullOrWhiteSpace($env:POSTGRES_PORT)) { '5432' } else { $env:POSTGRES_PORT }
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=$port;Database=dotnet_sql_performance_lab;Username=dotnetlab;Password=$($env:POSTGRES_PASSWORD)"
```

## Migrations and deterministic data

Restore the local EF tool and inspect/apply the migration against the database selected by `ConnectionStrings__Postgres`:

```powershell
dotnet tool restore
dotnet ef migrations list --project src/DotNetSqlPerformanceLab.Infrastructure --startup-project src/DotNetSqlPerformanceLab.Api
dotnet ef database update --project src/DotNetSqlPerformanceLab.Infrastructure --startup-project src/DotNetSqlPerformanceLab.Api
```

The current schema migration is `InitialCreate`. Check the target before applying migrations. Migration updates do not reset application rows.

The seed CLI defaults to seed `20261019` and 10,000 orders. Supported profiles are:

| Orders | Customers | Products | Order lines |
| ---: | ---: | ---: | ---: |
| 10,000 | 2,000 | 500 | 30,000–50,000 |
| 100,000 | 20,000 | 2,000 | 300,000–500,000 |

Each order receives 3–5 lines from a deterministic distribution. IDs and generated values are stable for a given seed/profile. Customers use fixed historical creation dates; orders fall within `2023-01-01` to `2025-12-31` UTC. Statuses are `Pending`, `Paid`, `Shipped`, `Cancelled`, and `Completed`; product categories are `Books`, `Electronics`, `Home`, `Office`, and `Sports`. Inserts are batched, and the CLI prints actual row counts.

```powershell
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 10000
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 100000
```

Seeding refuses to run when any target table is populated unless `--reset` is explicitly supplied. **Reset deletes every row** from `order_lines`, `orders`, `products`, and `customers` in foreign-key-safe order; it does not drop tables, schema, migration history, or unrelated tables. It is destructive and should only be run against a deliberately selected local database:

```powershell
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 10000 --reset
```

Benchmark mode never seeds or resets. It checks that customer, product, and order counts match the selected profile and records the actual order-line count. For the benchmark runs documented below, the seed value is recorded run metadata; the runner does not prove the persisted database was generated from that seed.

## Build and tests

```powershell
dotnet restore DotNetSqlPerformanceLab.sln
dotnet build .\DotNetSqlPerformanceLab.sln --no-restore -m:1
dotnet test DotNetSqlPerformanceLab.sln
```

Integration tests require a dedicated database whose name ends in `_test`; they refuse other targets and clean only their own rows. Create the database once if needed, then configure the test connection:

```powershell
docker compose exec -T db psql -U dotnetlab -d dotnet_sql_performance_lab -c "CREATE DATABASE dotnet_sql_performance_lab_test;"
$env:ConnectionStrings__PostgresTest = "Host=127.0.0.1;Port=$port;Database=dotnet_sql_performance_lab_test;Username=dotnetlab;Password=$($env:POSTGRES_PASSWORD)"
dotnet test DotNetSqlPerformanceLab.sln
```

Omit the `CREATE DATABASE` command if that database already exists. **Historical results from their original runs:** the 10K validation included restore, a build with zero warnings/errors, and 7 unit plus 10 integration tests passing. A separate run after the 100K benchmark passed 7 unit and 10 integration tests against the dedicated test database with `dotnet test DotNetSqlPerformanceLab.sln --no-build -m:1`. **Latest validation (2026-10-22):** 18 passed, 0 failed, 0 skipped. The historical 7-unit/10-integration results are not validation of the latest Swagger test change.

## Run the API and benchmarks

The API scenario requires a running API at the selected URL. Start it in a separate terminal with the same `ConnectionStrings__Postgres` setting:

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5179'
dotnet run --project src/DotNetSqlPerformanceLab.Api
```

Check `http://127.0.0.1:5179/health`. The health endpoint does not check PostgreSQL connectivity; the runner separately requires an HTTP 200 health response before running the API scenario. If unavailable, the API scenario fails visibly rather than being skipped.

`--scenario` accepts `all`, `customer-history`, `order-search`, `pagination`, `order-totals`, `related-data`, or `api-response`. The runner warms one connection/context, runs three untimed operation warmups per variant, and records ten measured iterations per variant in deterministic alternating order. Results include raw samples, median, p95, min/max, SQL command counts, in-process allocation summaries where applicable, and API response bytes. Plan capture uses `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` after timings and is excluded from measured intervals.

Use a new, non-existing output directory for every run; the runner writes `results.json`, query SQL/plan files, and (when requested) candidate-index artifacts there. These examples use unique timestamped paths to avoid replacing recorded runs:

```powershell
$runDir = "artifacts/benchmark-runs/10000-orders-$((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ'))"
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 10000 --scenario all --api-url http://127.0.0.1:5179 --output $runDir

$runDir = "artifacts/benchmark-runs/100000-orders-$((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ'))"
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 100000 --scenario all --api-url http://127.0.0.1:5179 --output $runDir
```

Run one query scenario by replacing `all` with its name. `--evaluate-indexes` is a separate documented option: it measures three temporary composite order indexes, captures candidate plans and index sizes, then drops those indexes in a `finally` cleanup path. It is not required for the six query/API scenarios. Candidate indexes are not part of migrations.

## Recorded benchmark observations

Public copies of the cited evidence are indexed in [docs/benchmark-results/README.md](docs/benchmark-results/README.md).

All values below are milliseconds from the raw `results.json` summaries, displayed to three decimal places using the runner's rounding. The JSON retains full precision. Each 10K value is one run’s ten-sample median; runs are shown separately and are not pooled. The 100K values are from one full-suite run. SQL counts are commands per operation (baseline / comparison). These are observations for this workload and machine, not performance guarantees.

### 10,000 orders — three separate runs

The repeatability set used seed metadata `20261019`, 2,000 customers, 500 products, and 39,879 order lines. Run labels are the output directory suffixes.

| Scenario (baseline / comparison) | `124443161Z` median | `125232955Z` median | `125249076Z` median | SQL commands |
| --- | ---: | ---: | ---: | ---: |
| Customer history: tracked / projection | 1.978 / 1.981 | 2.034 / 1.981 | 1.793 / 1.903 | 1 / 1 |
| Order search: materialize / SQL-translated | 27.395 / 5.573 | 31.267 / 5.785 | 29.254 / 5.657 | 1 / 1 |
| Deep pagination: offset / keyset | 3.815 / 2.653 | 3.727 / 2.565 | 3.820 / 3.087 | 1 / 1 |
| Order totals: per-order / grouped | 36.613 / 1.645 | 42.477 / 2.081 | 36.532 / 1.628 | 25 / 1 |
| Related data: repeated / projection | 93.552 / 4.826 | 104.951 / 4.705 | 91.166 / 4.318 | 49 / 1 |
| API response: baseline / optimized | 4.140 / 4.764 | 4.304 / 4.088 | 4.515 / 4.083 | 1 / 1 |

Across these three runs, SQL-translated search, keyset pagination, grouped aggregation, and related-data projection had lower comparison medians in each run. Customer-history direction changed and the values were close. API direction also changed; both variants returned 567 bytes and issued one SQL command, so these runs do not establish a consistent API timing improvement. Search and grouped-aggregation p95s varied notably: SQL-translated search was 5.993, 12.183, and 14.792 ms; grouped aggregation was 2.095, 6.975, and 1.762 ms. This spread is one reason to retain individual runs instead of combining them.

### 100,000 orders — one run

The single run used seed metadata `20261019`, 20,000 customers, 2,000 products, and 399,830 order lines. Values are median (p95); this has not been repeated, and the values should not be compared directly with the 10K runs as though they were a controlled cross-size experiment.

| Scenario (baseline / comparison) | Baseline median (p95) | Comparison median (p95) | SQL commands |
| --- | ---: | ---: | ---: |
| Customer history: tracked / projection | 1.776 (2.143) | 1.759 (2.277) | 1 / 1 |
| Order search: materialize / SQL-translated | 95.700 (115.845) | 13.330 (22.455) | 1 / 1 |
| Deep pagination: offset / keyset | 29.397 (31.186) | 6.672 (8.761) | 1 / 1 |
| Order totals: per-order / grouped | 33.433 (45.666) | 1.622 (13.190) | 25 / 1 |
| Related data: repeated / projection | 77.845 (80.197) | 3.312 (3.665) | 49 / 1 |
| API response: baseline / optimized | 2.838 (3.260) | 2.912 (3.503) | 1 / 1 |

This one run showed lower medians for SQL-translated search, keyset pagination, grouped aggregation, and related-data projection. Customer-history medians were close. The API baseline median was lower in this run; both responses were 579 bytes. These are single-run observations, not proof of a general improvement. The grouped-aggregation p95 (13.190 ms) also shows that its median alone does not describe the sample spread.

### Index evaluation and query plans

The optional index evaluation compares representative SQL before and after temporarily creating each candidate index, runs `ANALYZE`, captures an `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` plan, records index size and raw timings, and removes candidates in `finally`. Existing indexes include the primary/unique indexes and `IX_orders_CustomerId`, `IX_order_lines_OrderId`, and `IX_order_lines_ProductId`.

Candidate median timings (baseline / candidate, ms) varied by run:

| Candidate index | 10K runs 1 / 2 / 3 | 100K single run | 100K size |
| --- | ---: | ---: | ---: |
| `orders(CustomerId, OrderDate DESC, Id DESC)` | 1.235 / 1.218; 1.677 / 1.584; 1.112 / 1.006 | 1.154 / 1.038 | 4,079,616 bytes |
| `orders(Status, OrderDate DESC, Id DESC)` | 1.322 / 1.401; 2.001 / 1.282; 1.527 / 1.251 | 1.273 / 1.208 | 4,440,064 bytes |
| `orders(OrderDate DESC, Id DESC)` | 1.772 / 1.746; 1.678 / 2.365; 1.918 / 1.807 | 5.837 / 5.782 | 3,178,496 bytes |

Candidate plans selected `Index Only Scan` nodes, but that access path alone does not establish a useful end-to-end improvement. For example, the global-date candidate still visited about 75,050 rows to satisfy the deep offset at 100K. The 10K results vary by run, and the 100K evidence is one run with modest timing differences. Composite indexes also consume storage and add write/update work. No candidate index was retained or added to migrations.

The standard plans show the existing customer FK index feeding a bitmap/heap scan followed by a sort for customer/date. Status/date and global-date queries used sequential scans plus sorts; at 100K the status plan scanned 19,884 actual rows (20,227 estimated), while the global-date plan scanned 100,000 orders and sorted 75,050 rows for its deep offset. The product-to-line lookup used `IX_order_lines_ProductId` (80 actual/80 estimated rows at 10K; 200/199 at 100K). Order-line totals and related loading used the existing order-line FK indexes. The 100K related join used nested loops and product primary-key lookups; its estimated 160 rows were close to 161 actual rows. Plans showed shared-buffer hits and zero shared reads during these warmed captures. Plans are diagnostic executions, separate from timed samples, and do not establish cold-cache behavior.

### Artifacts, integrity checks, and limitations

Each run directory contains raw samples and environment/configuration in `results.json`, seven representative `plan-*.sql` / `plan-*.json` pairs, candidate plans, and `existing-indexes.json`. The recorded repeatability runs are:

- 10K run 1: [docs/benchmark-results/10k-run-124443161Z/](docs/benchmark-results/10k-run-124443161Z/)
- 10K run 2: [docs/benchmark-results/10k-run-125232955Z/](docs/benchmark-results/10k-run-125232955Z/)
- 10K run 3: [docs/benchmark-results/10k-run-125249076Z/](docs/benchmark-results/10k-run-125249076Z/)
- 100K run: [docs/benchmark-results/100k-run-seed-20261019-20261021/](docs/benchmark-results/100k-run-seed-20261019-20261021/)
- 10K before/after row/content fingerprints: [database-before.txt](docs/benchmark-results/10k-repeatability/database-before.txt) and [database-after.txt](docs/benchmark-results/10k-repeatability/database-after.txt)
- 100K before/after row-count and candidate-index check: [database-row-count-verification.txt](docs/benchmark-results/100k-run-seed-20261019-20261021/database-row-count-verification.txt)

Seed-time validation was performed separately from benchmark verification. For the 100K seed, post-commit checks measured 399,830 order lines, confirmed 3–5 lines per order, zero missing customer/order/product references, zero order-total mismatches, and order dates within the documented historical window. These checks were not rerun by the benchmark and their output was not saved in the benchmark artifact directory. The 10K benchmark fingerprints matched before and after: 2,000 customers, 500 products, 10,000 orders, and 39,879 order lines; candidate-index counts were zero. The 100K benchmark verification records unchanged counts of 20,000 customers, 2,000 products, 100,000 orders, and 399,830 order lines, with zero candidate indexes remaining; it does not include a content fingerprint or repeat the FK, monetary-total, or date-range checks. The benchmark's `seed` field is requested CLI metadata, not proof of persisted seed identity.

An earlier 10K run exists in the local ignored artifacts but is intentionally excluded from the curated evidence and three-run repeatability comparison.

The recorded benchmark environment was .NET 10.0.13, EF Core 10.0.4, PostgreSQL 17.11 in Docker Compose, Windows build `10.0.26200`, x64, 16 processors, a host-run API, and database host endpoint `127.0.0.1:5433`. The runner reported no per-container CPU or memory limit; Docker Desktop VM limits may not be visible. These are warmed runs with JIT, connection pooling, PostgreSQL/OS page caches, possible host contention, and measurement noise. Ten samples and repeated 10K runs do not remove those limitations. The 100K profile has only one recorded run.

For the accepted project rules and detailed design constraints, see [AGENTS.md](AGENTS.md).
