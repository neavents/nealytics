# A3b: distinguish encoding reservations from committed record bytes

Branch: feat/correctness-and-safety-a3b-encoding-reservations

Outcome: accept a valid record whose framed UTF-8 output fits WalMaxRecordBytes, including exact
limits and long quoted strings. Continue refusing actual oversized output before it can enter
the WAL. Do not change the JSON format, producer API, durability barrier or pending byte budget.

B4's exact group-byte test exposed a pre-existing limitation: Utf8JsonWriter can request a
working span larger than the final encoded value. Treating its size hint as committed output
rejects valid records. Preserve strict output-sized buffers for replay and copying; allow the
serialization scratch owner to lend a separately bounded reservation and enforce the output
limit when bytes are advanced.

The encoding reservation ceiling is min(Int32.MaxValue, 6 * record limit + 4096). It is a lazy
capacity ceiling, not an allocation made at startup. The factor covers the runtime's escape
expansion bound; the constant covers its growth request. Actual output remains capped at the
configured record limit, including the newline. A larger pooled array cannot increase the
exposed reservation. Shared scratch ownership stays behind the existing serialization gate,
and all loans are cleared and returned on failure/disposal. This increases the declared worst
case scratch ceiling and must be reflected in memory/performance qualification; pending records
and persisted bytes do not gain extra capacity.

See the pinned runtime's [writer growth rules](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Writer/Utf8JsonWriter.cs)
and [escaping bounds](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/JsonConstants.cs).
Recheck this assumption when upgrading the runtime or changing serialization. It is an explicit
compatibility bound rather than an unlimited hint exemption.

Acceptance: exact/one-byte-over frames with ASCII, quotes, escaping and multibyte text; large
hints with small actual advances; reservation ceiling/overflow boundaries; invalid hints and
advances; reset, pool growth/return and allocation failures. Repeat shared WAL/HTTP/native
contracts, full unit/ClickHouse suites, changed-code line/branch coverage and complete mutation.
Merge this feature fix into B4 before accepting its byte-bound test. Main remains untouched.
