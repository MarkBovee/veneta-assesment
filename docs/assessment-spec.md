# Veneta Assessment Specification

## 1. Purpose

This document is the technical source of truth for the Veneta backend assessment.

The objective is to deliver a small, correct and explainable event-sourced Consumer API together with a separate Consumer API crawler.

The implementation deliberately follows **KISS**:

> Build the smallest solution that correctly solves the assessment and is easy to explain, test and maintain.

Architectural complexity must be justified by an actual requirement or a concrete technical problem. Patterns, abstractions or infrastructure must not be added merely to demonstrate knowledge.

---

## 2. Assessment Requirements

| ID   | Requirement            | Acceptance Criteria                                                                                              |
| ---- | ---------------------- | ---------------------------------------------------------------------------------------------------------------- |
| R-01 | Create Consumer        | `POST /consumers` creates a Consumer, persists a `ConsumerCreated` event and returns `201 Created`.              |
| R-02 | Update first/last name | `PUT /consumers/{id}/name` validates the request, raises `ConsumerNameChanged` and updates the read model.       |
| R-03 | Update address         | `PUT /consumers/{id}/address` validates the request, raises `ConsumerAddressChanged` and updates the read model. |
| R-04 | Retrieve one Consumer  | `GET /consumers/{id}` returns the current projected Consumer or `404 Not Found`.                                 |
| R-05 | Retrieve all Consumers | `GET /consumers?offset=&limit=` returns all Consumers through bounded pagination.                                |
| R-06 | Event Sourcing         | Consumer state is reconstructed from its committed event stream. The Event Store is authoritative.               |
| R-07 | Test coverage          | Important domain, persistence, projection, API and crawler behavior is covered by automated tests.               |
| R-08 | Separate crawler       | A separate application retrieves Consumers from the API over HTTP and stores them independently.                 |

---

## 3. Architecture Principles

### 3.1 KISS

Prefer explicit code over frameworks and generic abstractions.

Do not introduce a component unless there is a clear reason for it.

The following are deliberately not part of the design:

* MediatR
* generic repositories
* generic event-store abstractions
* generic aggregate frameworks
* generic event buses
* message brokers
* Redis
* snapshots
* distributed transactions
* microservices
* background crawler workers
* unnecessary factories
* unnecessary interfaces

These technologies can be discussed as possible production evolution, but they are not requirements for the assessment.

### 3.2 Modular Monolith

The backend remains one deployable application.

Logical boundaries are maintained inside the application:

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

The exact physical file layout may evolve during implementation, but dependency direction must remain clear.

### 3.3 Domain Isolation

The domain must not know about:

* HTTP
* ASP.NET Core
* EF Core
* SQLite
* NEventStore
* DTOs
* configuration
* logging infrastructure

The domain contains business behavior, not infrastructure behavior.

---

## 4. Domain Model

`Consumer` is the aggregate root and the consistency boundary.

The aggregate exposes business operations:

```text
Consumer.Create(...)
Consumer.ChangeName(...)
Consumer.ChangeAddress(...)
```

These operations validate business rules and raise immutable events:

```text
ConsumerCreated
ConsumerNameChanged
ConsumerAddressChanged
```

The aggregate maintains:

* current state;
* current version;
* uncommitted events.

### 4.1 Creation

Consumer creation is performed through:

```text
Consumer.Create(...)
```

There is intentionally no Factory Pattern.

There is one aggregate and one construction strategy. A factory would add another abstraction without solving a real problem.

### 4.2 Event Application

`Apply(event)` is responsible only for mutating aggregate state.

It must not:

* validate commands;
* make business decisions;
* raise additional events;
* perform persistence.

### 4.3 Rehydration

When loading an existing Consumer:

```text
Event history
    ↓
Apply events in order
    ↓
Current aggregate state
```

Historical events must not become new uncommitted events during rehydration.

### 4.4 Version

The aggregate version represents the number of events applied to the aggregate.

For example:

```text
ConsumerCreated          -> version 1
ConsumerNameChanged      -> version 2
ConsumerAddressChanged   -> version 3
```

---

## 5. Event Sourcing

The Event Store is the authoritative source of truth.

A Consumer maps to one NEventStore stream in the `consumer` bucket.

Conceptually:

```text
Consumer
   |
   v
Event Store
   |
   v
Historical events
```

Events are immutable historical facts.

Normal updates never rewrite or delete previous events.

The current Consumer state is derived by replaying its event history.

---

## 6. NEventStore

NEventStore is retained because it is already part of the supplied assessment scaffold and directly supports the required behavior:

* event streams;
* stream revisions;
* SQL persistence;
* JSON serialization;
* optimistic concurrency;
* polling/projecting committed events.

Replacing NEventStore with a custom Event Store would mean implementing persistence, transactions and concurrency ourselves without a requirement that justifies that additional code.

Therefore:

> **Keep NEventStore and use it explicitly.**

There is no generic `IEventStore<T>` abstraction around it.

### 6.1 Persistence

The assessment uses:

```text
NEventStore
    ↓
SQL persistence
    ↓
Microsoft.Data.Sqlite
    ↓
SQLite
```

SQLite is intentionally chosen because the assessment needs a self-contained persistence solution that is easy to run locally.

This is an assessment choice, not a claim that SQLite is the preferred production event-store deployment model.

### 6.2 Event Serialization

Events are serialized using the existing NEventStore JSON serialization support.

Event contracts must remain explicit and stable.

---

## 7. Optimistic Concurrency

Updates require an `expectedVersion`.

The version supplied by the client represents the version the client believes it is modifying.

The update flow is:

```text
Client has version 3
       |
       v
PUT expectedVersion=3
       |
       v
Load stream
       |
       v
Rehydrate aggregate
       |
       v
Execute domain command
       |
       v
Raise event
       |
       v
Commit against revision 3
       |
       +------------------+
       |                  |
       v                  v
    success             stale
       |                  |
   revision 4          409 Conflict
```

The important point is that concurrency is ultimately enforced at the persistence boundary.

An application-level comparison alone is insufficient because two requests can pass the comparison concurrently.

The final Event Store commit must reject a stale writer atomically.

### Required behavior

Given two concurrent writes against version 3:

```text
Request A -> expectedVersion=3
Request B -> expectedVersion=3
```

exactly one may successfully commit revision 4.

The other must receive:

```text
409 Conflict
```

and must not overwrite the newer event.

---

## 8. Read Model

The Event Store is optimized for historical truth.

API queries are optimized for current state.

Therefore the API uses a derived read model:

```text
Event Store
     |
     v
Projection
     |
     v
ConsumerReadModel
     |
     v
API queries
```

The read model is stored using:

```text
EF Core
   ↓
SQLite
```

The read model is not authoritative.

It is derived data and may be deleted and rebuilt from the Event Store.

### 8.1 Normal Projection

After a successful event commit, the projector processes the committed events from that commit.

The normal update path must **not** reload and replay the complete Consumer stream.

Correct:

```text
new commit
   ↓
new events
   ↓
projection
```

Not:

```text
new commit
   ↓
reload entire Consumer history
   ↓
replay everything again
   ↓
projection
```

The latter adds unnecessary work and obscures the projection model.

### 8.2 Rebuild

Full event-history replay is reserved for:

* startup recovery;
* explicit read-model rebuild;
* controlled recovery operations.

Conceptually:

```text
All committed events
        ↓
Projection
        ↓
Fresh read model
```

### 8.3 Idempotency

Projection must be safe to replay.

If the same event/commit is processed again, the read model must not end up in an invalid state or receive duplicate logical data.

The read model should therefore maintain enough version information to reject stale or already-applied events.

---

## 9. Consistency and Failure Semantics

The Event Store commit happens before the projection.

Therefore:

```text
Command
   ↓
Event Store commit
   ↓
Projection
```

There is intentionally no distributed transaction between these two stores.

### Projection failure

If the Event Store commit succeeds but projection fails:

1. the event remains committed;
2. the command must not be treated as if it never happened;
3. the read model may temporarily lag;
4. the read model can be rebuilt or caught up from event history.

This is an intentional trade-off.

If request cancellation occurs after the event commit, cancellation is propagated to the caller. Event history remains authoritative and a later replay repairs any incomplete read-side work.

The source of truth remains correct even if the derived query model temporarily falls behind.

---

## 10. API

The API should remain thin.

The API is responsible for:

* HTTP transport;
* request/response contracts;
* HTTP validation;
* invoking application/domain behavior;
* mapping infrastructure/domain outcomes to HTTP responses.

Business rules belong in the domain.

### 10.1 Create Consumer

```http
POST /consumers
```

The API generates the Consumer ID.

Expected result:

```text
201 Created
```

The response identifies the created resource.

### 10.2 Change Name

```http
PUT /consumers/{id}/name
```

The request contains:

* first name;
* last name;
* expected version.

Expected successful response:

```text
204 No Content
```

### 10.3 Change Address

```http
PUT /consumers/{id}/address
```

The request contains:

* address;
* expected version.

Expected successful response:

```text
204 No Content
```

### 10.4 Retrieve One

```http
GET /consumers/{id}
```

Expected responses:

```text
200 OK
404 Not Found
```

### 10.5 Retrieve All

```http
GET /consumers?offset={offset}&limit={limit}
```

The query uses the read model.

Pagination is explicitly bounded to prevent uncontrolled result sizes.

### 10.6 Error Mapping

Expected mapping:

| Situation                                   |                   HTTP |
| ------------------------------------------- | ---------------------: |
| Invalid request                             |                    400 |
| Valid request but domain validation failure | 400/422 as appropriate |
| Consumer not found                          |                    404 |
| Optimistic concurrency failure              |                    409 |
| Unexpected infrastructure failure           |                    5xx |

The API must never return a successful response when the requested state was not actually persisted.

### 10.7 JSON Contract

API contracts use ASP.NET Core's default web JSON naming policy:

```text
camelCase
```

No unnecessary serialization attributes should be introduced.

---

## 11. Crawler

The crawler is deliberately a separate application.

Architecture:

```text
Crawler
   |
   | HTTP only
   v
Consumer API
   |
   v
Paged Consumer data
   |
   v
Crawler-owned EF Core SQLite
```

The crawler must not:

* reference the backend domain project;
* reference the backend persistence project;
* access the backend Event Store;
* access the backend database;
* reuse backend persistence entities.

The only coupling is the public HTTP API contract.

### 11.1 Pagination

The crawler retrieves all pages until the complete Consumer collection has been processed.

Example:

```text
GET offset=0   limit=100
GET offset=100 limit=100
GET offset=200 limit=100
...
```

The crawler must validate the returned pagination metadata and detect inconsistent or non-progressing responses.

### 11.2 Persistence

The crawler owns its own database.

Consumer ID is the logical key.

Repeated runs must not create duplicate records.

### 11.3 Idempotency

The crawler should be safe to run more than once.

For the same API data:

```text
run 1 -> Consumer stored
run 2 -> same Consumer updated/upserted
run 3 -> same Consumer remains one record
```

The crawler must also avoid replacing newer local data with an older source version.

### 11.4 Retry Policy

Only transient failures are retried.

Retry candidates:

* network failures;
* HTTP `429`;
* HTTP `5xx`.

Retries use:

```text
bounded exponential backoff
+
jitter
```

When the server supplies `Retry-After`, the crawler should respect it, subject to a sensible maximum delay.

Permanent client errors are not blindly retried.

Examples:

```text
400 -> no retry
401 -> no retry
403 -> no retry
404 -> no retry
```

### 11.5 One-Shot Execution

The crawler runs once and exits.

It is intentionally not:

* a `BackgroundService`;
* a scheduler;
* a queue consumer;
* a continuously running worker.

---

## 12. Aspire

Aspire is a local orchestration convenience.

It is not part of the business architecture.

Expected local workflow:

```text
Aspire AppHost
      |
      +--> API starts normally
      |
      +--> Crawler is available as explicit/manual start
```

The crawler must not automatically become a continuously running service.

The API and crawler must remain usable outside Aspire.

---

## 13. Testing Strategy

The goal is meaningful confidence, not an arbitrary coverage percentage.

### 13.1 Domain Tests

Cover:

* valid Consumer creation;
* required-field validation;
* name validation;
* address validation;
* event creation;
* aggregate version;
* uncommitted events;
* rehydration;
* invalid/inconsistent event history.

### 13.2 Event Store Integration Tests

Use real temporary SQLite persistence.

Verify:

* events are persisted;
* events survive reopening the store;
* stream history is ordered;
* stream versions are correct;
* stale writers are rejected.

### 13.3 Projection Tests

The desired projection coverage is:

* create event creates the read model;
* name change updates the read model;
* address change updates the read model;
* already-applied events do not corrupt state;
* stale event versions do not overwrite newer state;
* full rebuild produces the same result as normal projection.

Current status: these behaviors are covered indirectly by API integration and startup replay. Dedicated projector tests for direct commit projection, duplicate/already-applied commits, gap recovery, and full rebuild remain explicit test gaps.

### 13.4 API Integration Tests

Verify important end-to-end behavior:

* create returns `201`;
* update returns `204`;
* retrieve returns `200`;
* missing Consumer returns `404`;
* stale update returns `409`;
* pagination works;
* API JSON contracts deserialize into concrete DTOs.

Integration tests should use strongly typed request/response models rather than arbitrary JSON structures.

### 13.5 Crawler Tests

Verify:

* multiple pages;
* transient retry;
* `Retry-After`;
* permanent error behavior;
* idempotent upsert;
* stale-source protection;
* crawler-owned persistence.

Cancellation behavior is supported by the crawler API but does not currently have a dedicated test.

---

## 14. Technology Decisions

| Concern        | Decision                 | Rationale                                                                               |
| -------------- | ------------------------ | --------------------------------------------------------------------------------------- |
| Runtime        | .NET 10                  | Assessment target.                                                                      |
| API            | ASP.NET Core Minimal API | Small HTTP surface with little ceremony.                                                |
| Domain         | Plain C#                 | Framework-independent business logic.                                                   |
| Event Store    | NEventStore              | Already supplied and directly supports streams, revisions, persistence and concurrency. |
| Event-store DB | SQLite                   | Self-contained and sufficient for the assessment.                                       |
| Read model     | EF Core + SQLite         | Simple and familiar query persistence.                                                  |
| Crawler DB     | EF Core + SQLite         | Independent crawler-owned persistence.                                                  |
| Orchestration  | Aspire AppHost           | Local developer convenience only.                                                       |

---

## 15. Explicit Non-Goals

The following are intentionally outside the assessment implementation:

* MediatR;
* generic repositories;
* generic Event Store abstractions;
* generic aggregate frameworks;
* generic event buses;
* message brokers;
* queues;
* Redis;
* snapshots;
* microservices;
* distributed transactions;
* continuous crawler workers;
* schedulers;
* production deployment infrastructure;
* authentication/authorization infrastructure.

These can be discussed as production concerns, but should not be implemented unless a concrete assessment requirement appears.

---

## 16. Production Discussion Points

The assessment implementation is intentionally smaller than a production platform.

Potential production evolution includes:

### Event Store

A production system with higher availability and concurrency requirements could use a dedicated event-store platform such as KurrentDB/EventStoreDB.

### Database

A server-grade shared database could replace local SQLite where multiple application instances and operational requirements demand it.

### Projection

The projection could become asynchronous when scale or isolation makes that worthwhile.

### Event Schema Evolution

Historical events should remain readable forever.

Potential approaches include:

* event versioning;
* upcasting;
* explicit compatibility handling.

Historical events should not be rewritten merely because the current domain model changed.

### Observability

Production could add:

* structured logging;
* metrics;
* OpenTelemetry traces;
* health/readiness checks.

Useful dimensions include:

```text
ConsumerId
EventType
Version
CorrelationId
```

PII should not unnecessarily be included in logs.

### Snapshots

Snapshots are not implemented initially.

They are only justified if event-history replay becomes a measured performance bottleneck.

A snapshot is an optimization, never the authoritative source of truth.

### Security

Production would also require:

* authentication;
* authorization;
* HTTPS;
* rate limiting;
* secret management;
* PII protection.

These are outside the assessment scope.

---

## 17. Acceptance Gate

The solution is complete when all of the following are true:

1. all required Consumer operations work through HTTP;
2. Consumer state is represented by event history rather than CRUD persistence;
3. the Event Store is authoritative;
4. stale updates cannot overwrite newer events;
5. stale updates return `409 Conflict`;
6. the read model is derived from committed events;
7. normal projection processes the new committed events rather than replaying the full aggregate stream;
8. full read-model rebuild is possible from event history;
9. the crawler communicates with the API through HTTP only;
10. the crawler owns its own persistence;
11. all pages are retrieved;
12. repeated crawler execution is idempotent;
13. meaningful automated tests pass;
14. README and `AGENTS.md` describe the actual implementation;
15. the complete solution builds and tests cleanly.

---

## 18. Guiding Rule

When choosing between two implementations, prefer:

> **The simplest implementation that preserves correctness, testability and clear boundaries.**

The assessment should be understandable in a few minutes, while still providing enough depth to discuss Event Sourcing, optimistic concurrency, projections, idempotency, failure handling and production trade-offs during the interview.
