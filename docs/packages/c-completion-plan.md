# Category C completion contract

Selected 2026-11-03 by the owner's request to finish category C. Prerequisite: qualified
C1–C3 category snapshot `ad2d943`. Keep each step on its own feature branch and integrate only
into `feat/analytics-customization`. Preserve main and the original unfinished rebase.

| Step | Outcome | Acceptance boundary |
| --- | --- | --- |
| C4 typed formulas | Versioned bounded expression trees over named metrics and constants, with immutable compiled dependencies | Reject cycles, unresolved references, unsupported operations, excessive depth/size and incompatible units before activation; exact integer/decimal inputs; explicit null/division/rounding semantics; existing scalar/report endpoints |
| C5 report shapes and parameters | Declared typed filter parameters, grouped reports and time series with bounded rows | Required/default/invalid parameters; fixed authorization/segments; deterministic ordering and limits; formulas finalize after aggregation; existing scalar response remains compatible |
| C6 computed dimensions | Versioned deterministic mapping, normalization, bounded string operations and numeric bucketing on reads | Validate source/type/limits and dependencies; safe generated expressions; discovery/filter/group parity; no arbitrary SQL/scripts or ingestion-path cost |
| C7 project contexts | Immutable authenticated logical project catalogs over one bounded compatible physical schema union | Distinct discovery/query/ingestion validation; unknown-project policy; pinned credentials; incompatible types rejected before DDL; no cross-project fallback/leakage |
| C8 planning and accuracy | Raw/rollup/hybrid plans, explicit accuracy contracts and administrative explanations | Disjoint raw edges and summarized interiors; persisted readiness and complete coverage; count/sum/average state correctness; typed filters, grouping, null, replay and membership parity; unsupported plans fall back with a reason |

These are sequential packages, not separate services. Validate each changed contract with focused
checks before building on it. Consolidate full suites, changed-code coverage, Complete-level
mutation and published executable checks at implementation milestones. Investigate all failing
gates without suppressions. Single executable, low bounded memory, source-generated JSON,
domain neutrality, source style and unchanged ingestion callers remain required.

Benchmarks, profiling, latency/throughput measurements and automatic backend-default changes
remain stopped. Query execution statistics and workload recommendation experiments are deferred
by that instruction. No speed claims may be inferred from query shape or functional results.

Arbitrary scripts, user/session historical cohorts, automatic rollup creation, authorization-aware
result caching, external enrichment/reference-source connectors, report deployment/CRUD services,
and hosted operations remain the roadmap's explicit later work. Read-time transforms do not
retroactively rewrite stored values or enable stored ingest transforms. Physical migrations retain
their ordinary migration/rollback requirements.

Completion means each selected row is implemented and functionally qualified, operator contracts
and limitations are documented, branch/evidence identities are recorded, and remaining deferred
measurement/hosting work is reported accurately. Do not equate selecting this plan with completion.

Completion recorded 2026-11-03: all selected C1–C8 rows are implemented and functionally
qualified on the feature category/step branches. See the [qualification ledger](c-verification.md)
and [operator contracts](../analytics-customization.md). The explicit deferred boundaries above
remain unchanged; no merge into main or measurement/default-enablement decision is included.
