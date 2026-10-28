# A2: ingestion compatibility and durability fixtures

Branch: feat/correctness-and-safety-a2-compatibility-and-durability

Outcome: encode the transport and persistence contract before replacing WAL I/O or dispatch.
Run the existing requests through real Kestrel, inspect durable newline-framed WAL records and
broker publication, and reopen the files to verify recovery. Keep tests independent of a live
database where the database is not the subject of the assertion.

Cover successful track responses, credentials and their precedence, beacon element rejection,
streamed accepted prefixes, malformed tails, and client disconnects. A response acknowledges
durable WAL persistence before publication. An interrupted response can leave accepted events;
it does not establish that the server rejected them. No exactly-once transport promise is added.

Failed storage writes, flushes and admission cancellation need deterministic I/O gates in B1/A3;
those checks remain open until that boundary exists. Add them alongside the boundary and run
the same compatibility suite for every backend and dispatcher in B6.

Resolve the inherited test-only SSH.NET advisories with the patched dependency rather than
suppressing NuGet audit. Enable verification on feature branches without publishing images.
