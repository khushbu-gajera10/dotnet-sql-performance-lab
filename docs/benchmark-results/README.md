# Curated benchmark evidence

This directory contains sanitized public copies of the benchmark runs cited by the project README. Original run artifacts remain under the ignored local `artifacts/` directory and were not modified.

## Included runs

- [10K run 1](10k-run-124443161Z/): one of three separate 10,000-order repeatability runs.
- [10K run 2](10k-run-125232955Z/): one of three separate 10,000-order repeatability runs.
- [10K run 3](10k-run-125249076Z/): one of three separate 10,000-order repeatability runs.
- [10K before/after verification](10k-repeatability/): row/content fingerprints for the repeatability validation.
- [100K run](100k-run-seed-20261019-20261021/): a single 100,000-order run; it is not a repeated result.

Each run directory includes `results.json` with configuration, raw iteration samples, summaries, command counts, and dataset counts, plus representative SQL and query-plan artifacts. Candidate-index evaluation JSON and existing-index inventory are retained where present. The 100K run also includes its database row-count verification. No benchmark values or raw samples were changed; the copied `configuration.command` was made portable by replacing the captured local executable path with the equivalent repository-relative `dotnet run` command.

The three 10K runs are presented separately, not pooled. The 100K run is a single environment-specific observation. All measurements used warmed connections/cache; Docker Desktop VM limits may not be visible, and short runs remain subject to host contention and measurement noise. Seed values in results are run metadata and do not independently prove persisted seed identity. Query plans are diagnostic evidence, not proof of end-to-end improvement.
