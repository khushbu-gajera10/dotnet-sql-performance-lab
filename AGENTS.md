# DotNetSqlPerformanceLab Project Rules and Accepted Plan

## Purpose and stack

Build a portfolio project demonstrating practical SQL, LINQ, EF Core, and ASP.NET Core API performance work through reproducible comparisons. Use .NET 10, ASP.NET Core Web API, EF Core with PostgreSQL, xUnit, and Docker Compose for local PostgreSQL.

Keep the design simple and maintainable. Do not add messaging systems, microservices, CQRS frameworks, or dependencies without a concrete need. Follow established .NET naming, formatting, async, dependency injection, and configuration conventions. Keep secrets out of source control; use environment variables or user secrets and safe example configuration.

Never claim a performance improvement before measuring it. State the workload, configuration, environment, measurement method, and limitations alongside results. A measured result applies only to the tested environment and workload.

## Accepted repository structure

```text
DotNetSqlPerformanceLab.sln
src/
  DotNetSqlPerformanceLab.Api/             # HTTP endpoints and API composition
  DotNetSqlPerformanceLab.Core/            # Domain entities and focused query/use-case contracts
  DotNetSqlPerformanceLab.Infrastructure/  # EF Core, PostgreSQL, mappings, migrations, seed data
tests/
  DotNetSqlPerformanceLab.UnitTests/       # Fast correctness tests without PostgreSQL
  DotNetSqlPerformanceLab.IntegrationTests/# PostgreSQL-backed correctness and API behavior
benchmarks/
  DotNetSqlPerformanceLab.Benchmarks/      # Explicit benchmark runner and scenarios
docker-compose.yml                         # Local PostgreSQL only
README.md
```

Keep project references one-directional and minimal. Do not introduce a generic repository or mediator layer merely to connect these projects.

## Entities and deterministic data

Initial entities:

- `Customer`: ID, name, email, created timestamp.
- `Product`: ID, SKU, name, category, current price.
- `Order`: ID, customer ID, order timestamp, status, total.
- `OrderLine`: ID, order ID, product ID, quantity, unit price.

Seed generation must be deterministic for a given seed and configuration. Use a documented fixed default seed and allow overriding it. Derive all generated values from a stable pseudo-random generator and deterministic ID assignment; do not use current time or nondeterministic GUIDs. Allow order count and seed to be configured through command-line options or environment variables, document their precedence/defaults, and print the effective configuration before seeding and benchmarking. Seed idempotently or provide an explicit reset/reseed workflow that produces the same logical dataset.

Benchmark datasets:

| Orders | Customers | Products | Order lines |
| ---: | ---: | ---: | ---: |
| 10,000 | 2,000 | 500 | 30,000–50,000 |
| 100,000 | 20,000 | 2,000 | 300,000–500,000 |

Generate 3–5 lines per order (documented deterministic distribution); assign customers and products across the full sets, with realistic order dates over a configurable fixed historical window and a small documented set of statuses/categories. Dataset sizes are configurable, while these two sizes are the standard reported benchmark profiles. The actual generated row counts must be printed and included with results.

## Benchmark rules and measurement protocol

- Keep baseline and optimized variants functionally equivalent for identical inputs. Compare returned values (including ordering and pagination boundaries) in correctness tests before timing. Change one meaningful factor at a time wherever practical; label unavoidable bundled changes.
- Correctness tests and performance benchmarks are separate. Unit and integration tests assert behavior, not fixed timing thresholds.
- Keep database-query benchmarks separate from end-to-end API measurements. Query benchmarks invoke the data/query path without HTTP serialization/network overhead. API benchmarks call the running API over loopback using the same request, dataset, and response shape; report full request latency and response bytes separately.
- Count SQL commands with a scoped EF Core `DbCommandInterceptor` (or equivalent provider-level instrumentation), reporting commands per operation and noting retries/transactions. Do not infer query count from elapsed time.
- Capture elapsed time with a monotonic high-resolution timer around the operation under test. Report sample count, median, p95, and spread (min/max or interquartile range); report allocations for in-process query runs when practical. Exclude setup/seeding and plan capture from timed intervals.
- Standard protocol: one untimed connection/context warmup and at least three untimed operation warmups per case; then ten measured iterations per case, with the order of baseline/optimized cases alternated or randomized deterministically. Record raw iteration values. If runtime/measurement stability is insufficient, report it and repeat rather than hiding the variance.
- Record .NET/runtime and EF Core versions, PostgreSQL version, OS, CPU, memory, Docker resource limits, dataset seed and row counts, configuration, benchmark command, and whether API/database ran in containers. Keep database settings and schema identical between paired variants.
- Capture query plans separately for representative SQL using `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` on PostgreSQL. Save the SQL and plan with the run output or a documented result artifact. Plan capture executes the query and is diagnostic; never include it in benchmark timings. Explain actual versus estimated rows, chosen access path, and buffer activity without treating a plan node alone as proof of faster end-to-end behavior.
- Document warmup effects, PostgreSQL/OS page-cache effects, connection pooling, Docker resource contention, and measurement noise. Do not imply cold-cache results unless cache state was controlled and documented. Compare repeated runs under the same stated conditions.

Provide reproducible documented commands after implementation, using the following interface shape (final options must match the runner):

```powershell
docker compose up -d db
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 10000 --scenario all
dotnet run --project benchmarks/DotNetSqlPerformanceLab.Benchmarks -- --seed 20261019 --orders 100000 --scenario all
```

The runner must make it clear when it initializes or resets data. Avoid silently destroying a user's database. Results should be emitted in a machine-readable format (for example JSON or CSV) plus a concise console summary; use built-in capabilities or a small dependency already justified by the chosen harness.

## Scenarios and intended indexes

Every scenario has baseline and optimized variants, equivalent inputs/results, recorded SQL command count, and query/API timing appropriate to its boundary.

1. **Tracked entities vs. read-only projection.** Load customer order history as tracked entities, then compare a projection using `AsNoTracking` and selecting only response fields. Isolate tracking/projection changes and verify the same ordered output.
2. **Filtering, sorting, and indexing.** Compare the baseline filter/sort query with a SQL-translatable form and a candidate composite index for a representative status/date or customer/date search. Capture SQL and plans. Compare query-shape changes and index changes in separate runs where practical; do not credit the index if the query also changed.
3. **Offset vs. keyset pagination.** Compare pages at shallow and deep positions with identical stable ordering and equivalent records. Candidate ordering is `(OrderDate, Id)`; keyset continuation uses both fields. Offset must use the same ordering.
4. **Per-order queries vs. grouped aggregation.** Compare loading totals with one query per order against one grouped database aggregation for the same order set and rounding rules. Report command counts and result equivalence.
5. **Repeated related-data loading vs. explicit projection.** Compare the repeated/lazy related entity load pattern (including its N+1 command count) with one explicit projection for the same customers, orders, and lines. Keep result fields and ordering equivalent.
6. **API response behavior.** Measure the full HTTP request path, including database access, mapping, serialization, and loopback transport. Compare baseline and optimized query/response behavior on identical endpoints and payload semantics; report API latency, response bytes, and SQL command count separately. Also report query-only timings so HTTP/serialization costs are not attributed to SQL.

Candidate indexes and intended access patterns (validate using measured plans and timings; an index is not presumed beneficial):

- `Order(CustomerId, OrderDate DESC, Id DESC)`: customer order history and customer-scoped date ordering/keyset pages.
- `Order(Status, OrderDate DESC, Id DESC)`: status-filtered date searches and pages.
- `Order(OrderDate DESC, Id DESC)`: global date-ordered listing and keyset pagination when no status filter applies.
- `OrderLine(OrderId)`: order-line joins, per-order loads, and grouped totals by order.
- `OrderLine(ProductId)`: product-to-order-line lookup if that access pattern is used.
- Unique `Customer(Email)` and `Product(SKU)`: lookup and integrity constraints; they are not performance claims for benchmark scenarios.

Avoid redundant indexes. Document index size/write cost and verify whether PostgreSQL uses each candidate. Add or retain only indexes supported by workload needs and the observed trade-offs.

## Milestones

1. Planning and acceptance criteria (this document); no application scaffold in the planning phase.
2. Scaffold solution/projects, Docker Compose PostgreSQL, safe configuration, and documented local startup.
3. Add entities, EF Core schema/migrations, deterministic configurable seed workflow, and row-count verification.
4. Implement baseline/optimized query paths and correctness tests; verify equivalent outputs and command-count expectations.
5. Add benchmark runner/protocol, candidate indexes, plan capture, and machine-readable results.
6. Run both standard dataset profiles, document environment/commands/results/limitations, and provide a concise interpretation without unsupported claims.

## Acceptance criteria

- A clean checkout can follow documented commands to start PostgreSQL and run unit tests, integration tests, API, and benchmark scenarios.
- Repeating a seed/configuration produces the same logical rows and printed row counts; 10,000- and 100,000-order profiles are supported and documented.
- Each baseline/optimized pair accepts identical inputs and returns equivalent results; correctness tests cover relevant ordering, filters, aggregation, and pagination edges.
- SQL commands are counted independently from timing; query-only and full API timings are labeled and measured at distinct boundaries.
- Warmup, measured iteration counts, raw samples, environment, dataset, query plans, and known limitations are captured for reported runs.
- Indexes have documented intended patterns and are retained or recommended based on evidence, including trade-offs.
- No unit test contains fixed elapsed-time assertions; no performance claims are made until benchmark results exist.
- Secrets remain out of source control and no unnecessary architectural or package complexity is introduced.

## Current task boundary

- The planning-only restriction has been superseded by the user's explicit implementation requests. Continue from the approved plan and implement only the milestone or portion the user asks for.
- The current authorized scope includes the Core entities, EF Core/PostgreSQL model, initial migration, and deterministic seed workflow. Seed defaults are seed `20261019` and 10,000 orders. Supported profiles are 10,000 orders (2,000 customers, 500 products) and 100,000 orders (20,000 customers, 2,000 products); generation uses fixed SplitMix64 arithmetic and explicit sequential IDs, with 3–5 order lines per order.
- Seed inserts are batched at 1,000 rows. The CLI refuses to seed when any target table is populated unless `--reset` is explicitly supplied. `--reset` deletes all rows from `order_lines`, `orders`, `products`, and `customers`, and no other table or schema object. Both the API, EF design-time factory, and seed CLI use `ConnectionStrings__Postgres`; never commit credentials.
- Milestone 4 query variants now live in Infrastructure and are exposed through paired API routes. Keep baseline/optimized inputs and ordered output shapes equivalent; query correctness and command counts belong in PostgreSQL integration tests. The test fixture must require a dedicated database name ending in `_test`, create isolated test-owned rows, and clean only those rows.
- Milestone 5 is implemented in the existing benchmark project. The benchmark mode is selected with `--scenario`; runs never seed or reset. The no-scenario seed workflow remains unchanged and still requires explicit `--reset` before deleting target rows.
- The runner checks the selected profile's real row counts, verifies paired query outputs before timing, warms the connection/context once and each operation three times, then captures ten deterministically alternating samples per variant. It records raw samples, summary timings, command counts, allocations where practical, environment information, and API response bytes. The API scenario requires an HTTP 200 health probe and reports unavailable API as a failure rather than skipping.
- Query-path and loopback HTTP timings are separate. EF command counts use the scoped command interceptor; direct index-evaluation SQL reports one command per operation. Retries count as executions and transactions are not commands.
- Plans are captured outside timed intervals. `--evaluate-indexes` inspects existing FK indexes and temporarily evaluates the three documented composite order indexes, recording before/after raw SQL timings, index size, and plans; candidate indexes are dropped in `finally` and are not persisted in migrations. OrderLine FK indexes are inspected, not duplicated. No performance claim follows from the harness alone.
- Default JSON output is under `artifacts/benchmark-runs/<UTC timestamp>/`, with per-plan SQL and JSON artifacts. The runner best-effort records Docker host-config memory/CPU limits for the default Compose container; Docker Desktop VM resource limits may not be visible. Warmed-cache effects, connection pooling, host contention, short-run noise, and index evaluation order remain documented limitations.
