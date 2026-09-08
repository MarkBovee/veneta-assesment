# Veneta Backend Assessment

## Repository boundaries

- `Veneta.Assessments.Backend/` contains only backend application and backend test projects.
- `Veneta.Assessments.Crawler/` contains only the independent crawler application and crawler tests.
- `Veneta.Assessments.AppHost/` contains only Aspire orchestration/development tooling.
- `Data/` contains local runtime SQLite files only.
- `docs/` contains repository-level assessment documentation.

The top-level `Veneta.Assessments.slnx` covers the complete workflow. Dedicated solutions live beside their bounded applications: `Veneta.Assessments.Backend/Veneta.Assessments.Backend.slnx`, `Veneta.Assessments.Crawler/Veneta.Assessments.Crawler.slnx`, and `Veneta.Assessments.AppHost/Veneta.Assessments.AppHost.slnx`.

## Architecture rules

- KISS is the guiding principle. Keep implementation small, explicit, and traceable to requirements.
- Backend and crawler are separate applications.
- The crawler communicates with the backend only through the HTTP API.
- Do not move crawler code into backend namespaces or projects.
- The crawler must not reference backend persistence or domain implementations.
- `Consumer` is the backend aggregate root. Domain code remains independent from HTTP, EF Core, SQLite, and NEventStore.
- NEventStore is the authoritative event store. One `consumer` stream is used per Consumer.
- Events commit before projection. Projection failure may leave read storage behind but must not rewrite history.
- The EF Core read model is disposable derived state and must be rebuildable from committed events.
- Crawler storage is independently owned and must not be combined logically with backend persistence.
- Runtime SQLite files belong under `Data/`, never source directories, `bin/`, or `obj/`.

## KISS constraints

- Do not introduce MediatR, generic repositories, generic event-sourcing infrastructure, snapshots, brokers, Redis, authentication, or distributed processing.
- Do not introduce abstractions solely to move files or folders.
- Keep NEventStore, EF Core, and crawler HTTP behavior as implemented.
- Keep the crawler one-shot. Do not add workers, schedules, queues, or continuous processing.
- Keep API contracts and concurrency semantics unchanged.

## API and crawler conventions

- Routes remain `POST /consumers`, `PUT /consumers/{id}/name`, `PUT /consumers/{id}/address`, `GET /consumers/{id}`, and bounded `GET /consumers?offset=&limit=`.
- Pagination accepts `offset >= 0` and `1 <= limit <= 100`.
- The crawler follows every page, retries network failures, `429`, and `5xx`, honors `Retry-After`, supports cancellation, and rejects malformed pagination.
- The crawler upserts by Consumer ID and does not overwrite newer local versions with stale source data.

## Documentation and verification

Keep `README.md`, `docs/assessment-spec.md`, `docs/implementation-plan.md`, solution paths, project references, launch settings, and SQLite paths aligned with the actual repository structure.

```bash
dotnet restore
```

Run applications from repository root:

```bash
dotnet run --project Veneta.Assessments.Backend/Veneta.Assessments.Backend.ConsumerService/Veneta.Assessments.Backend.ConsumerService.csproj
```
