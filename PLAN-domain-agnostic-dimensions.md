# Plan: make nealytics domain-agnostic, with declared dimensions

**For the implementing agent.** Read all of §1–§3 before writing anything. Every step tells you
what to do and why; the *why* is what stops you making a locally-sensible change that reintroduces
the problem.

**Scope:** `nealytics` (primary), `neavents-smartmenu-web` (Worker payload + beacon ids),
`neavents-dashboard-website` (consume the new endpoint). Nothing else in the estate is touched.

---

## 1. The problem, stated precisely

nealytics is meant to be a generic, open-source, high-performance analytics engine. Its **query API
already is** — nine routes, zero domain vocabulary:

```
POST /api/v1/telemetry/beacon | track
GET  /api/v1/telemetry/timeline
GET  /api/v1/analytics/active | sessions | timeseries | top
GET  /health  /ready
```

Its **write path is not**. Three columns of one customer's vocabulary are compiled into the engine:

| file | leak |
|---|---|
| `src/Nealytics.Engine/Infrastructure/Serialization/GlobalTelemetryPayload.cs` | `MenuId`, `SectionId`, `TableId` properties |
| `src/Nealytics.Engine/Features/BatchProcessor/TelemetryColumnBuffers.cs` | `MenuIds`/`SectionIds`/`TableIds` arrays + `["menu_id"]` mapping |
| `src/Nealytics.Engine/Features/BatchProcessor/ClickHouseBatchWriter.cs` | column names inside the INSERT string |
| `src/Nealytics.Engine/Infrastructure/Storage/ClickHouseSchemaMigrator.cs` | `ADD COLUMN … menu_id / section_id / table_id` |
| `clickhouse-init.sql` (repo root) | the same three columns in `CREATE TABLE` |

Anyone cloning nealytics for a social app or a storefront inherits `menu_id`. That is the whole
defect.

### Why the leak happened, and why you must not "fix" it back

Those three fields used to live inside `metadata_json`. They were promoted to real columns because
**ClickHouse cannot `GROUP BY` a field inside a JSON string without parsing every row** — so
per-menu analytics reported itself unmeasured while the data was arriving all along. That reasoning
is correct and is recorded in comments in all five files above.

The mistake was not "columns". The mistake was **the engine deciding what the columns are called.**

Do not solve this by moving the fields back into `metadata_json` or into a new JSON/Map column. The
project owner has explicitly rejected schemaless containers, and the evidence supports him: across
the entire dataset, `metadata_json` held exactly **four** distinct keys — `p` (a URL path), `ms` (a
duration), `ref` (a referrer) and `tenant` (a *duplicate* of the `tenant_id` column). Three are
universal analytics fields that should be typed columns, and one is a second source of truth for a
fact already stored. The container was not absorbing variety; it was absorbing three decisions
nobody made.

### The insight that makes this cheap

The transition needs **zero data movement**. `menu_id` stays a column called `menu_id`, holding the
same values. The only thing that changes is *where the name comes from*: today it is compiled into
C#; afterwards it comes from the deployment's config file.

Additionally — **the existing data is disposable.** It is localhost/test data (~108 rows). You are
explicitly permitted to `DROP` and recreate `nealytics_core.global_events`. Do not write backfills,
deprecated aliases, or compatibility shims. Delete the old fields outright.

---

## 2. Ground truth — measured, not assumed

Verified against the running estate before this plan was written. Re-verify if you doubt anything.

**Schema today** (`DESCRIBE nealytics_core.global_events`):

```
event_id UUID | project_id LowCardinality(String) | tenant_id String | session_id String
user_id Nullable(String) | event_type LowCardinality(String) | item_id Nullable(String)
menu_id Nullable(String) | section_id Nullable(String) | table_id Nullable(String)
device_class/os/browser/country LowCardinality(String) | metadata_json String | timestamp DateTime64(3,'UTC')

ENGINE = ReplacingMergeTree()
ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE
```

**Population** (108 rows):

```
menu_id 66 | section_id 3 | table_id 3 | item_id 30
device_class 108 | os 107 | browser 107 | country 108 | tenant_id 108
```

**A trap you must not walk into.** `menu_id` is populated but every value is garbage:

```
menu_id = "0"       36 rows
menu_id = "01MENU"  30 rows
```

`"0"` is qrmenu-edge's **dense payload id** — the packer numbers the single menu in a payload as 0 —
and `"01MENU"` is a placeholder. Neither joins back to a real menu. The beacon field is typed
`mid?: number`, so it can only ever carry the dense id. A leaderboard built on this column today
renders two bars labelled "0" and "01MENU" and looks like it is working. **§6 fixes this. Nothing
may depend on `menu_id` until it does.**

**Config style.** `TelemetryEngineOptions` binds from `appsettings.json` under `TelemetryEngine`,
and `docker-compose.yml` overrides it with double-underscore env vars
(`TelemetryEngine__AllowedProjectKeys: neavents:projkey123`). Follow that. **Do not introduce YAML
or a second configuration system.**

**ClickHouse is 26.7.1.** `ADD COLUMN` is a metadata-only operation — instant, no data rewrite,
regardless of table size. Adding a dimension is not a migration event. (`DROP`/`MODIFY COLUMN` are
not free; only `ADD` is.)

---

## 3. Target design

**The engine holds dimensions it was told about. It never knows their names at compile time.**

- A deployment declares its dimensions in config: a name and a type.
- On boot the engine reconciles config against `system.columns` and creates any missing column.
- Ingestion accepts a generic `dimensions` map; each key must be declared, or it is rejected —
  **loudly, and counted**, never silently dropped.
- The query API can group and filter by any declared dimension, validated against the registry.

A stranger clones nealytics, declares `author_id` and `post_id`, and gets a social-media analytics
engine. Neavents declares `menu_id`, `section_id`, `table_id` and gets what it has now.

### What stays generic and must NOT be touched

Do not "clean up" these — they are not leaks:

- **`event_type` values** (`menu_view`, `item_dwell`, `tab`). Product-defined event names are *data*.
  Every analytics tool has them.
- **`tenant_id`, `project_id`, `session_id`, `user_id`.** Multi-tenancy is a generic concern.
- **`device_class`, `os`, `browser`, `country`.** Universal web analytics, and correctly derived
  server-side at the edge so a client cannot forge them.

### One rename

`item_id` → `object_id`. "The thing this event is about" is a generic concept most engines have;
`item` reads as commerce/menu vocabulary. This is a straight rename, no semantic change.

---

## 4. Step 1 — engine stops knowing the words

**Goal:** `grep -riE "\b(menu|section|table)" src/` returns only ordinary English and `DataTable`
noise. No behaviour change beyond the payload shape.

### 4.1 Config

Add to `TelemetryEngineOptions`:

```csharp
public List<DimensionOptions> Dimensions { get; set; } = [];

public sealed class DimensionOptions
{
    public string Name { get; set; } = "";        // snake_case, [a-z][a-z0-9_]{0,62}
    public string Type { get; set; } = "String";  // String | LowCardinality | UInt64 | Int64 | DateTime
    public bool Retired { get; set; }             // see §5.3
}
```

**Ship the engine with `Dimensions` empty** in `appsettings.json`, plus a commented example. The
declaration for Neavents lives in `docker-compose.yml`, *not* in this repo. That file location is
the boundary — it is what makes the repo domain-free.

Add to `docker-compose.yml` under the `nealytics-api` service:

```yaml
TelemetryEngine__Dimensions__0__Name: menu_id
TelemetryEngine__Dimensions__0__Type: String
TelemetryEngine__Dimensions__1__Name: section_id
TelemetryEngine__Dimensions__1__Type: String
TelemetryEngine__Dimensions__2__Name: table_id
TelemetryEngine__Dimensions__2__Type: String
```

> Note: `docker-compose.yml` at the estate root is **not a git repo** — changes there are on disk
> only. Mention this in your final report so the owner knows to persist it.

**Validate at bind time** and refuse to start on: a name failing the regex; a duplicate name; an
unknown type; a name colliding with a reserved column (`event_id`, `project_id`, `tenant_id`,
`session_id`, `user_id`, `event_type`, `object_id`, `device_class`, `os`, `browser`, `country`,
`timestamp`). *Why:* a dimension named `timestamp` would generate DDL that either fails obscurely or
shadows a core column.

Add a ceiling — `MaxDimensions` (default 64). *Why:* the registry issues DDL; a config bug must not
be able to add columns without limit.

### 4.2 Registry

`Infrastructure/Configuration/DimensionRegistry.cs` — a singleton built once from
`IOptions<TelemetryEngineOptions>`:

- `IReadOnlyList<Dimension> Active` — declared and not retired.
- `bool IsActive(string name)`
- `string? ClickHouseType(string name)`

Everything downstream (ingestion validation, the reconciler, the query allowlist) reads from this
one object. *Why:* three copies of "which dimensions exist" is how they drift — the same failure
that put the source-language and localized tag queries out of step in menu-service and cost 48 of 50
tagged items their allergen chips.

### 4.3 Payload

In `GlobalTelemetryPayload.cs`:

- **Delete** `MenuId`, `SectionId`, `TableId`.
- **Rename** `ItemId` → `ObjectId`.
- **Add** `public Dictionary<string, string>? Dimensions { get; init; }`.

Keep `MetadataJson` for now; §7 removes it.

### 4.4 Buffers and writer

`TelemetryColumnBuffers.cs` currently declares one strongly-typed array per column, including
`MenuIds`, `SectionIds`, `TableIds`. Replace those three with a dictionary of pooled arrays keyed by
dimension name, sized from `DimensionRegistry.Active`.

`ClickHouseBatchWriter.cs` builds the INSERT column list as a string with the names inline. Build it
from the fixed core columns plus `DimensionRegistry.Active`, in a **stable order** (registry order),
and build the column-block dictionary the same way.

*Why stable order matters:* the writer pairs a name list against a value-array list positionally. If
the two are built by different iterations of an unordered collection you get a column misalignment —
tenant ids landing in the browser column — which will not throw and will not be noticed.

The existing class doc already warns that hand-maintained parallel lists are how a buffer goes
unreturned or a column ends up misaligned by one. Keep that property: **one list, built once.**

Preserve the existing `ArrayPool` rent/return discipline and the clearing of reference-typed arrays
on return. *Why:* a pooled `string[]` handed to a later batch without clearing can leak one tenant's
ids into another tenant's rows.

### 4.5 Ingestion validation

Where the payload is accepted (`Features/IngestTelemetry/`):

- For each key in `Dimensions`: if `!registry.IsActive(key)` → **drop that field only**, increment a
  counter tagged with the dimension name, and log at warning with the project id and key.
- Never reject the whole batch or the whole event for one unknown key.

*Why per-field and not per-batch:* the client is `navigator.sendBeacon`, which cannot retry and whose
caller cannot react. Losing a whole session's events because one key was misspelled is worse than
losing one field. *Why it must be counted and logged:* an unregistered dimension silently vanishing
is precisely the failure this whole plan exists to end. In this same estate, a Cloudflare Worker
returned `204` for three months while discarding every analytics beacon, and the only evidence was a
table that stopped growing.

### 4.6 Verify step 1

```bash
# no domain vocabulary left in the engine
grep -rniE "\b(menu|section|table)" --include="*.cs" src/ | grep -viE "DataTable|TableName|ToTable"
# expect: nothing (or only unrelated English prose)

dotnet build && dotnet test
```

---

## 5. Step 2 — the reconciler and the data-loss guards

Rewrite `ClickHouseSchemaMigrator` to reconcile declared dimensions against the live schema. It
already runs as an `IHostedService` before the batch writer takes traffic — keep that ordering.
*Why:* a missing column rejects **entire batches**, not one row, and telemetry has no retry.

Read the current shape first:

```sql
SELECT name, type FROM system.columns
WHERE database = 'nealytics_core' AND table = 'global_events'
```

Then, for each declared dimension:

### 5.1 Declared, column missing
`ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS <name> <type>`
Metadata-only, instant. Log at information with the name and type.

### 5.2 Declared, column present, **type differs**
**Refuse to start.** Log the dimension, the declared type and the actual type.

*Why fail rather than coerce:* silently changing `menu_id` from `String` to `UInt64` either drops
every existing value or starts writing values the old rows cannot be compared against. A refused
boot is recoverable in a minute; a coerced column is not recoverable at all.

### 5.3 Column present with data, **not declared**
**Refuse to start**, naming the dimension and its non-null row count.

*Why this is the most important rule in the plan:* the failure mode it prevents is someone
accidentally deleting one line from config and redeploying. Without this guard the column and its
data survive, but new writes for that dimension start being rejected and the query API stops
offering it — so a hole begins accumulating *and the widget that would have shown you goes blank at
the same instant*. The symptom and the evidence disappear together. Someone notices three months
later.

**Deletion must not be expressible by absence**, because absence is what a mistake looks like.
Retiring is an explicit act:

```yaml
TelemetryEngine__Dimensions__2__Retired: "true"
```

A retired dimension: **keeps its column and every row**, is refused on ingestion like any
unregistered name, and is not offered by the query API. Never auto-`DROP` a column — that is data,
and `DROP COLUMN` in ClickHouse is not free either.

This mirrors an existing pattern in the estate: tag definitions have `/retire` and `/restore` rather
than a delete.

### 5.4 On concurrency — read this before adding a lock

You may be tempted to wrap this in an advisory lock. **ClickHouse has no advisory locks**, and the
Postgres lesson does not transfer here:

- `ADD COLUMN IF NOT EXISTS` is idempotent; two replicas racing converge on the same result.
- Replicas of one deployment read the *same* config, so they cannot disagree about a type.
- A genuine disagreement means two different configs deployed simultaneously — a deploy error, which
  §5.2 already turns into a refused boot.

So: **no lock. Idempotent DDL plus the refuse-to-start checks.** Do not copy
`MigrateWithAdvisoryLockAsync` from the .NET services — that helper is for Postgres and already
exists as five diverging copies across the estate. Do not create a sixth.

### 5.5 `clickhouse-init.sql`

Strip `menu_id`, `section_id`, `table_id` from `CREATE TABLE`. Rename `item_id` → `object_id`.
Update the comment block, which currently explains why the three domain columns are first-class —
that reasoning is superseded by this plan.

The file only runs via `docker-entrypoint-initdb.d`, i.e. once on an empty volume, which is why the
reconciler exists at all. Keep the two in step and say so in the comment, as it already does.

### 5.6 Recreate the table

The data is disposable. Do this rather than writing a migration:

```bash
docker exec neavents-nealytics-clickhouse clickhouse-client -q \
  "DROP TABLE IF EXISTS nealytics_core.global_events"
docker compose restart nealytics-api   # init sql + reconciler rebuild it
```

### 5.7 Verify step 2

```bash
# columns exist and are declared
docker exec neavents-nealytics-clickhouse clickhouse-client -q \
  "SELECT name, type FROM system.columns WHERE database='nealytics_core' AND table='global_events' ORDER BY position"
```

Then prove each guard actually fires — a guard you have not seen fail is a guard you have not
written:

1. **Add** a fourth dimension to compose, restart → column appears, service healthy.
2. **Change** its declared type, restart → service refuses to start, log names the dimension and
   both types.
3. **Send** an event carrying that dimension, then **delete** the line from compose and restart →
   service refuses to start, log names the dimension and its row count.
4. **Mark it `Retired: true`** and restart → starts cleanly, column and rows still present.

---

## 6. Step 3 — fix the beacon ids (smartmenu-web)

**Do this before any dashboard widget is switched on.** See §2: `menu_id` currently carries the
dense payload id.

In `neavents-smartmenu-web/worker/index.ts`:

- `interface ClientBeacon` — change `mid?: number` to `mid?: string`, and make `sec` `string` only.
  *Why:* the numeric type is what forces the dense id; a ULID cannot be a number.
- `toTelemetryPayload` — emit `dimensions: { menu_id, section_id, table_id }` instead of the three
  named fields, omitting absent ones. Rename `itemId` → `objectId`.

In the client (`src/core/track.ts` and its callers) — send the **real menu ULID**, which is already
available in the injected payload (`window.__M.t` is the tenant; the menu ULID is in the URL path
`/menu/{tenant}/{menuId}`) rather than the dense id used inside the packed payload. Section ids are
likewise dense in the payload; send the source ULID. `PublicMenuParser` builds a `SourceIds` map for
exactly this purpose — dense int back to ULID — use it rather than recomputing.

Run both suites (`pnpm test:client`, `pnpm test:worker`), then `npx wrangler deploy`.

**Verify with real values, not shapes:**

```bash
docker exec neavents-nealytics-clickhouse clickhouse-client -q \
  "SELECT menu_id, count(*) FROM nealytics_core.global_events WHERE menu_id != '' GROUP BY 1"
```

Every key must be a 26-character ULID. If you see `0`, stop — the fix has not landed.

---

## 7. Step 4 — the generic breakdown endpoint

This is the feature that makes nealytics genuinely reusable **and** the feature that lights up the
dashboard. They are the same build.

`GET /api/v1/analytics/breakdown`

| param | meaning |
|---|---|
| `metric` | `events` \| `sessions` \| `users` — count, or count-distinct of session/user |
| `eventType` | optional, filter to one event type |
| `groupBy` | one dimension or core column name |
| `filter` | repeatable `name:value` |
| `from` / `to` | time range |
| `limit`, `orderBy` | ranking |

Response: `{ total, truncated, rows: [{ key, value, share }] }`.

### Rules

1. **`groupBy` and every `filter` name are validated against an allowlist** built from
   `DimensionRegistry.Active` plus the core columns. Unknown name → `400` naming it. **Never
   interpolate a caller-supplied string into SQL.** This is the injection boundary and the single
   most security-relevant line in the plan.
2. **Cardinality guard.** `groupBy=session_id` would try to return millions of rows. Cap at
   `MaxQueryLimit` (already in options, default 10 000) and set `truncated: true`.
3. **Truncation is reported, never silent.** The dashboard already has a `ROW_CAP = 5000` that
   quietly flips coverage flags off; do not add a second silent cap.
4. **No domain vocabulary.** `menu_id` appears in this code only as a string arriving from config.

Cover with tests: a valid group-by; an unknown `groupBy` → 400 naming it; an injection attempt
(`groupBy=menu_id; DROP TABLE`) → 400, not executed; truncation sets the flag.

---

## 8. Step 5 — dashboard

In `neavents-dashboard-website/src/services/qr-smart-menu/analyticsService.ts`, point the
menu/section/table/device aggregations at `/analytics/breakdown` and set the corresponding
`coverage` flags.

**Flip a coverage flag only when its query returns numbers you would defend.** The whole value of the
"Henüz ölçülmüyor" state is that it is *true*. A widget showing a confidently wrong number is worse
than one admitting it does not know — and with §6 unfixed, a menu leaderboard would have rendered two
bars labelled `"0"` and `"01MENU"` and looked entirely healthy.

Ready immediately (data verified real): **device / os / browser**, **country**.
Ready after §6: **menus**, **sections**.
Real but thin (3 rows): **tables** — show it with a low-sample note; thin is not absent.
Still out of scope: `languages`, `completionRate`, `visitors`, `sectionReach`, `languageQuality`,
`contentQuality`, `intentValue`, `basketAffinity`. The first two need a new dimension and a new
client event respectively; the rest need a product definition from the owner before any code.

---

## 9. Optional cleanup, only if the steps above are green

Promote the three real fields out of `metadata_json` into typed core columns — `page_path String`,
`duration_ms UInt32`, `referrer LowCardinality(String)` — drop the `tenant` key (a duplicate of
`tenant_id`), then delete `metadata_json` entirely.

Then close the source of the smell, which is not in the database:

```ts
// worker/index.ts, interface ClientBeacon
[k: string]: unknown;   // everything else becomes metadata
```

An open bag in the contract guarantees an open bag in storage. Leave that line and a JSON column
grows back regardless of what you do to the schema.

---

## 10. Do not

- **Do not** reintroduce a JSON, `Map`, or EAV container for dimensions. Explicitly rejected by the
  owner, and §1 gives the evidence.
- **Do not** write backfills, migrations, or deprecated aliases. The data is disposable; drop and
  recreate.
- **Do not** add a Postgres-style advisory lock. See §5.4.
- **Do not** auto-`DROP` a column for any reason.
- **Do not** let an unknown dimension be silently ignored on ingest.
- **Do not** flip a dashboard coverage flag before its query is verified against real values.
- **Do not** put `menu_id` anywhere in the nealytics repo except as example config in a comment.
- **Do not** introduce YAML or a second config system; use `TelemetryEngine:*` binding.

---

## 11. Definition of done

1. `grep -riE "\b(menu|section|table)" --include="*.cs" src/` in nealytics returns nothing
   domain-related.
2. `nealytics_core.global_events` has `menu_id`, `section_id`, `table_id` — created from config,
   named nowhere in the source.
3. All four guard scenarios in §5.7 demonstrated, with the log output quoted in your report.
4. `menu_id` values are real 26-character ULIDs.
5. `/api/v1/analytics/breakdown` returns a correct ranking for
   `metric=sessions&eventType=menu_view&groupBy=menu_id`, and rejects an unknown `groupBy` with a
   `400` naming it.
6. The device/browser widget renders real numbers end-to-end in the dashboard.
7. Every coverage flag you set is backed by a query you have run and read.
8. All suites green: `dotnet test` (nealytics), `pnpm test` (smartmenu-web), `npx vitest run` +
   `npx next build` (dashboard).

## 12. Report back

State plainly: which guards you saw fire and their log lines; which coverage flags you turned on and
the numbers each returned; anything you could not verify. If a step could not be completed, say which
and why rather than working around it — in this estate, three separate defects survived for months
because something reported success while discarding the thing it was asked to do.
