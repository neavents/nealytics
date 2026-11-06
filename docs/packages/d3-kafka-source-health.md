# D3 project-scoped Kafka source health

Selected from the remaining source-visibility/operator group, 2026-11-06. Branch:
feat/developer-operator-tools-d3-kafka-source-health. Base includes local E8 merge 3c6555c.
Status: implementation in progress. Main stays untouched; measurements remain stopped.

Outcome: a project-pinned server key can inspect the existing optional Kafka source through
GET /api/v1/telemetry/sources/kafka/default. Return only a fixed source name, observed state,
fixed issue category and UTC change time. Never expose topic/group/broker addresses, credential
material, native error text, source payloads or exceptions. Missing/invalid keys return 401;
public/unpinned/disallowed keys 403; another project's valid pinned key 404. Header precedence
matches existing ingestion/job routes. Disabled Kafka exposes no route or health service.

States: starting before subscription, waiting without an assignment, assigned after a nonempty
assignment or successful manual offset commit, unavailable after observed nonfatal native errors
or assignment loss, failed after a fatal native error/worker failure, stopped after normal shutdown.
An empty assignment/revocation means waiting. TLS/authentication/authorization issues outrank assignment loss, which outranks
transport/generic/rebalance notifications until a successful assignment or commit confirms progress.
Fatal/worker failure and stopped states cannot be overwritten by late callbacks. Repeated identical
observations preserve their timestamp. Snapshots are detached and synchronized; process restart
starts a new observation history. No persisted source state or journal version is introduced.

This is an observed state, not an active network probe, freshness guarantee, lag counter or
independent ingestion-readiness assertion. An idle connection recovering without another native
assignment/commit can retain its last observed issue; show the UTC timestamp rather than invent
current reachability. Existing /health and /ready remain unchanged. Native retry behavior, fatal/
poison host-failure behavior, source offset ownership and at-least-once semantics stay unchanged.

Use a singleton bounded state holder, fixed strings and startup callback delegates. Steady-state
successful commits use a volatile state check; they do not allocate snapshots or read clocks.
Health requests allocate only their small response and perform no provider/database calls.
Callbacks observe assignment/revocation/loss/error only: they must not assign/unassign partitions,
commit offsets or change consumer security/queue/poll settings. Single-file packaging and producer
contracts remain intact. Multi-source declarations and explicit repair/skip actions are separate.

Acceptance: deterministic clock/state transitions, repeated observations, error-priority ordering,
terminal fencing, concurrent coherent snapshots and source-generated serialization; host lifecycle,
worker failure, commit-after-durability and unchanged cancellation; project/key isolation and
route disablement; actual broker assignment/security failures, mapped/managed/custom ingestion,
restart/poison offsets and actual ARM64 published io_uring/custom checks. Cover changed maintained
lines/branches, run Complete changed Kafka mutation with unchanged thresholds, preserve all
existing mutation responsibilities and report whole-engine gaps. Focused tests during edits;
full milestone build/unit/integration/coverage/native gates once stable. Remote CI/actual x64 remain
owner-deferred. Run Archy preflight/verify and report their known unavailability accurately.
