# Veneta Assessment Implementation Record

## 1. Objective

The Veneta backend assessment was implemented in small, verifiable increments. This document records the delivered structure, verification evidence, and known test gaps.

The implementation must remain:

* simple;
* explicit;
* event-sourced;
* independently testable;
* easy to explain;
* strictly within assessment scope.

Do not introduce architecture that is not required.

---

## 2. Delivered State

The delivered implementation has:

* the Consumer API implements all required operations;
* Consumer state is event sourced;
* NEventStore persists durable event history;
* optimistic concurrency is enforced during commit;
* the EF Core read model is derived and rebuildable;
* normal projection handles only newly committed events;
* the crawler is a separate executable;
* the crawler communicates only through HTTP;
* crawler persistence is independent;
* crawler execution is idempotent;
* important behavior is covered by tests;
* Aspire supports the intended local workflow;
* README and `AGENTS.md` reflect the final code;
* Aspire exposes the live API scenario and one-shot backend/crawler test resources;
* Development seed data exercises three crawler pages by default;
* OpenTelemetry traces cover HTTP, application, event-store, projection, crawler, and live scenario flows;
* Development traces can include structured request, response, event, and projection payloads;
* a successful build with no compiler errors;
* all current automated tests passing.

Known limitation: projection behavior is currently exercised through API integration, startup replay, and the live Aspire scenario rather than by a dedicated `ConsumerProjector` test class. Payload capture is intentionally Development-only because it may contain request data.

Package verification currently reports no vulnerable direct or transitive packages. Aspire may emit `ASPIRE010` when the local CLI bundle is disabled; this does not affect application compilation or tests.

---

# 3. Implementation Sequence

## Step 1 — Establish the final project structure

### Goal

Make the backend structure understandable before adding functionality.

### Work

Review the existing solution and split oversized files where this improves clarity.

Target logical structure:

```text
ConsumerService/
├── Domain/
├── Application/
├── Infrastructure/
├── ReadModel/
├── Contracts/
├── Endpoints/
└── Program.cs
```

Do not mechanically create folders that add no value.

### Rules

* `Program.cs` should primarily contain application composition/configuration.
* Endpoint behavior should live in focused endpoint classes/functions where that improves readability.
* Domain types belong in Domain.
* Infrastructure types belong in Infrastructure.
* Read-model concerns remain separate from the aggregate.
* One public type per file.

### Acceptance

* No business implementation is unnecessarily concentrated in `Program.cs`.
* Dependencies are easy to identify.
* No unnecessary abstraction layer has been introduced.

---

# Step 2 — Implement the Consumer domain

### Goal

Create the smallest correct event-sourced aggregate.

### Work

Implement:

```text
Consumer
ConsumerCreated
ConsumerNameChanged
ConsumerAddressChanged
```

Implement:

```text
Consumer.Create(...)
Consumer.ChangeName(...)
Consumer.ChangeAddress(...)
Consumer.Rehydrate(...)
```

Maintain:

```text
Current state
Current version
Uncommitted events
```

### Rules

The aggregate:

* validates business rules;
* raises events;
* applies events;
* tracks its version;
* tracks uncommitted events.

`Apply(event)` must only mutate state.

Rehydration must not create new uncommitted events.

Do not add factories.

### Acceptance

* Creation raises exactly one `ConsumerCreated`.
* Version starts at 1 after creation.
* Name/address changes raise the correct events.
* Invalid domain data is rejected.
* Rehydration recreates the exact state represented by history.
* Rehydration does not create duplicate uncommitted events.

### Tests

* create;
* invalid create;
* name change;
* address change;
* versions;
* event contents;
* replay;
* invalid history.

---

# Step 3 — Configure NEventStore persistence

### Goal

Replace scaffold/in-memory persistence with durable SQLite Event Store persistence.

### Work

Configure:

```text
NEventStore
+
SQL persistence
+
MicrosoftDataSqliteDialect
+
Microsoft.Data.Sqlite
+
JSON serialization
```

Use one `consumer` bucket stream per Consumer.

### Rules

Keep NEventStore visible.

Do not create:

```text
IEventStore<T>
GenericEventStore
Repository<T>
EventStoreManager
```

unless an actual requirement emerges.

### Acceptance

* events are persisted to a SQLite file;
* events survive store recreation/restart;
* stream revision matches aggregate version;
* committed events load in order.

### Tests

Use a real temporary SQLite file.

Prove:

```text
create
→ commit
→ reopen
→ load
→ same history
```

---

# Step 4 — Implement aggregate loading and optimistic concurrency

### Goal

Make updates safe under concurrent writes.

### Work

Implement the application flow:

```text
Consumer ID
    ↓
load stream
    ↓
rehydrate Consumer
    ↓
execute command
    ↓
collect uncommitted events
    ↓
append
    ↓
commit
```

Use `expectedVersion` from the API request.

### Critical rule

Do not rely only on an application-level version check.

The final persistence operation must enforce optimistic concurrency.

### Required behavior

Given two writes based on version 3:

```text
A expectedVersion=3
B expectedVersion=3
```

result:

```text
A -> revision 4 -> success
B -> concurrency conflict -> 409
```

Exactly one succeeds.

### Acceptance

* stale writes cannot overwrite newer events;
* `ConcurrencyException` becomes HTTP `409`;
* event history remains intact;
* successful updates increment revision correctly.

### Tests

* expected version succeeds;
* stale expected version fails;
* concurrent writers produce one winner;
* loser does not append an incorrect event.

---

# Step 5 — Implement the read model

### Goal

Provide efficient query access without rebuilding aggregates for every GET.

### Work

Create the EF Core read model:

```text
ConsumerReadModel
```

Configure SQLite persistence.

Create projection behavior for:

```text
ConsumerCreated
ConsumerNameChanged
ConsumerAddressChanged
```

Track the projected version.

### Normal projection rule

After a successful Event Store commit:

```text
commit.Events
    ↓
projection
```

Do not do:

```text
commit
    ↓
reload complete stream
    ↓
replay complete stream
    ↓
projection
```

### Rebuild rule

Create a separate rebuild path that can:

```text
delete/reset read model
    ↓
replay all committed events
    ↓
recreate current state
```

### Acceptance

* create is projected;
* name update is projected;
* address update is projected;
* versions remain correct;
* duplicate/replayed events do not corrupt the model;
* full rebuild recreates the same state.

### Tests

* projection for each event;
* idempotent projection;
* stale event protection;
* rebuild from event history.

---

# Step 6 — Implement API contracts and endpoints

### Goal

Expose the assessment requirements through a clean HTTP API.

### Endpoints

```text
POST /consumers

PUT /consumers/{id}/name

PUT /consumers/{id}/address

GET /consumers/{id}

GET /consumers?offset=&limit=
```

### Work

Create explicit request/response contracts.

Keep HTTP contracts separate from:

* domain entities;
* NEventStore event types;
* EF Core persistence types.

### Expected responses

| Operation            | Success          |
| -------------------- | ---------------- |
| Create               | `201 Created`    |
| Change name          | `204 No Content` |
| Change address       | `204 No Content` |
| Get one              | `200 OK`         |
| Get missing          | `404 Not Found`  |
| Concurrency conflict | `409 Conflict`   |

### Validation

Validate:

* malformed HTTP input;
* required values;
* pagination bounds;
* expected version.

Domain validation remains in the domain.

### Acceptance

All required routes behave correctly and use the documented JSON contracts.

### Tests

End-to-end API tests for:

* create;
* get;
* update name;
* update address;
* missing Consumer;
* stale update;
* pagination.

---

# Step 7 — Implement the crawler

### Goal

Build the required independent application.

### Project boundary

The crawler must not reference backend implementation projects.

It may reference:

* its own DTOs;
* HTTP infrastructure;
* its own EF Core models;
* its own database.

### Flow

```text
start
  ↓
GET first page
  ↓
validate page
  ↓
upsert Consumers
  ↓
advance offset
  ↓
repeat
  ↓
finish
  ↓
exit
```

### Pagination

Process the complete result set.

Detect:

* inconsistent totals;
* invalid offsets;
* duplicate IDs;
* page-size mismatch;
* no-progress responses.

### Persistence

Use crawler-owned SQLite.

Use Consumer ID as the logical key.

### Idempotency

Repeated runs must result in:

```text
one row per Consumer
```

Do not duplicate records.

Do not overwrite newer local state with stale API data.

### Resilience

Retry:

* transient network errors;
* HTTP 429;
* HTTP 5xx.

Use:

```text
exponential backoff
+
jitter
```

Respect `Retry-After`.

Do not blindly retry permanent 4xx responses.

### Acceptance

* multiple pages are retrieved;
* all Consumers are stored;
* repeated runs are safe;
* transient failures recover;
* permanent failures terminate appropriately;
* crawler exits after completion.

### Tests

Use a deterministic HTTP handler and temporary SQLite database.

---

# Step 8 — Add Aspire orchestration

### Goal

Improve the local developer workflow without changing application architecture.

### Work

Configure AppHost so:

```text
API -> starts normally
Crawler -> explicit/manual start
```

Use explicit crawler start behavior.

### Rules

Do not add:

* crawler scheduler;
* `BackgroundService`;
* queue;
* periodic execution.

The crawler remains a one-shot executable.

### Acceptance

* API can be started from Aspire;
* crawler can be manually started;
* crawler does not automatically run continuously;
* both applications remain usable without Aspire.

---

# Step 9 — Complete the test suite

### Goal

Finish with a focused automated test suite.

### Required areas

```text
Domain
Event Store
Projection
API
Crawler
```

### Test philosophy

Test behavior and failure modes.

Do not add tests solely to increase a coverage percentage.

### Final test matrix

| Area        | Required confidence                  |
| ----------- | ------------------------------------ |
| Domain      | invariants, commands, events, replay |
| Event Store | persistence, ordering, concurrency   |
| Projection  | correctness, idempotency, rebuild    |
| API         | HTTP contract and error mapping      |
| Crawler     | pagination, retry, idempotency       |

---

# Step 10 — Review architecture against KISS

Before documentation is finalized, review every abstraction.

For each interface/class ask:

```text
What problem does this solve?
Would the implementation be clearer without it?
Does it remove real coupling?
Is it required for testing?
```

Remove anything whose only justification is:

```text
"We might need this later."
```

Specifically review for accidental introduction of:

* factories;
* managers;
* generic repositories;
* generic event-store abstractions;
* command buses;
* service layers that only forward calls;
* unnecessary interfaces;
* unnecessary dependency injection.

The final code should remain easy to follow.

---

# Step 11 — Update documentation

## `README.md`

Document the final delivered solution:

* assessment objective;
* architecture;
* Event Sourcing;
* Consumer aggregate;
* events;
* Event Store;
* optimistic concurrency;
* read model;
* projection/rebuild;
* crawler;
* Aspire;
* tests;
* run instructions;
* deliberate trade-offs;
* production discussion points.

Include a Mermaid diagram matching the actual implementation.

## `AGENTS.md`

Document:

* repository structure;
* architecture rules;
* KISS principle;
* domain boundaries;
* Event Store rules;
* read-model rules;
* crawler isolation;
* testing expectations;
* coding conventions;
* ASK workflow;
* verification rules.

## `docs/assessment-spec.md`

Must describe what the system is required to do and why the major architectural decisions were made.

## `docs/implementation-plan.md`

Must describe how the implementation is built and how completion is verified.

---

# 12. Verification Sequence

Run verification in this order:

```text
dotnet restore
       ↓
dotnet build
       ↓
dotnet test
       ↓
API integration tests
       ↓
Crawler integration tests
       ↓
Repeat crawler
       ↓
Read-model rebuild/recovery verification
       ↓
Final documentation review
```

### Build gate

Expected:

```text
0 errors
0 warnings introduced by the implementation
```

### Test gate

All tests pass.

### API integration gate

Covered by `ConsumerApiIntegrationTests`:

```text
create
read
change name
change address
list
stale update
```

### Crawler integration gate

Covered by `ConsumerCrawlerTests`:

```text
crawl all pages
persist all Consumers
run crawler again
confirm idempotency
```

---

# 13. Final Review Checklist

## Domain

* [ ] Consumer is the aggregate root.
* [ ] Business rules live in the domain.
* [ ] Events are immutable facts.
* [ ] `Apply` only mutates state.
* [ ] Replay does not create new uncommitted events.
* [ ] No unnecessary Factory Pattern.

## Event Store

* [ ] NEventStore is used explicitly.
* [ ] SQLite persistence is durable.
* [ ] JSON serialization is configured.
* [ ] One Consumer maps to one stream.
* [ ] Event history is authoritative.
* [ ] Commit-time optimistic concurrency is enforced.

## Projection

* [ ] Read model is derived.
* [ ] EF Core is used for query persistence.
* [ ] Normal projection only processes new committed events.
* [x] Projection is idempotent/version-aware in implementation; dedicated projector tests remain a gap.
* [ ] Full rebuild works from event history.

## API

* [ ] All required endpoints exist.
* [ ] DTOs are explicit.
* [ ] HTTP layer remains thin.
* [ ] Domain owns business rules.
* [ ] `404` and `409` semantics are correct.
* [ ] Pagination is bounded.

## Crawler

* [ ] Separate executable.
* [ ] HTTP-only communication.
* [ ] Independent database.
* [ ] Complete pagination.
* [ ] Idempotent upsert.
* [ ] Stale-source protection.
* [ ] Transient retries.
* [ ] `Retry-After`.
* [ ] Permanent 4xx not blindly retried.
* [ ] One-shot execution.

## Aspire

* [ ] API starts normally.
* [ ] Crawler is explicit/manual.
* [ ] No scheduler.
* [ ] No background worker.

## Tests

* [ ] Domain tests.
* [ ] Event Store integration tests.
* [x] Projection behavior covered indirectly through API/startup integration.
* [ ] Dedicated projector tests for direct commit, duplicate commit, gap recovery, and full rebuild.
* [ ] Dedicated cancellation behavior test.
* [ ] API integration tests.
* [ ] Crawler tests.

## Documentation

* [ ] README matches actual implementation.
* [ ] AGENTS matches actual architecture.
* [ ] Specification matches actual requirements.
* [ ] Implementation plan matches actual implementation.
* [ ] Mermaid diagram matches final code.
* [ ] Non-goals are explicit.
* [ ] Production considerations are clearly separated from assessment scope.

---

# 14. Final Principle

The implementation should end up at this level of complexity:

```text
HTTP
  ↓
Thin API
  ↓
Application orchestration
  ↓
Consumer aggregate
  ↓
NEventStore / SQLite
  ↓
Projection
  ↓
EF Core read model
```

with the crawler as a completely separate path:

```text
Crawler
  ↓
HTTP
  ↓
Consumer API
  ↓
Crawler-owned EF Core / SQLite
```

Nothing is added merely because a larger system might need it.

The target is:

> **Simple enough to understand quickly, robust enough to discuss deeply.**
