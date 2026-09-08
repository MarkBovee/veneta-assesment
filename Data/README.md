# Runtime data

This directory contains local SQLite runtime state and is not application source code.

- `backend-eventstore.sqlite`: backend NEventStore event history. This is the authoritative backend source.
- `backend-readmodel.sqlite`: backend EF Core read model. It is derived state and can be rebuilt from events.
- `crawler.sqlite`: database independently owned by the crawler application.

The backend event store, backend projection/read model, and crawler storage remain separate logical boundaries even though their local files share this directory. Delete these files when appropriate; applications recreate them on startup.
