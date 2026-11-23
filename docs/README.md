# TBD Movies — Official Documentation Index (`docs/README.md`)

Welcome to the official developer and architecture documentation index for **TBD Movies** (`v0.12.6`).

---

## 1. Documentation Structure & Tree

The `docs/` directory contains the clean, authoritative project documentation tracked in the public repository:

```
docs/
├── README.md              # Documentation index (this file)
├── ROADMAP.md             # Multi-pillar product roadmap & future feature specifications
├── adr/                   # Architecture Decision Records (e.g. ADR-0016 Trakt Read-Only Cache)
├── releases/              # Official release notes and manifests (v0.10.0 through v0.12.6)
└── source/                # Foundational product specifications (The Idea-v3.md)
```

> [!NOTE]
> **Internal Workspace Isolation:**  
> Internal developer handoffs, agent constitutions, and active task worklogs reside strictly in `.internal_docs/` and are excluded from the public Git repository to keep the codebase clean, lean, and production-ready.

---

## 2. Core Architectural & Codebase Contracts

All contributions and development on TBD Movies must adhere to the following binding architectural standards:

1. **GPL-3.0 Clean-Room Standard**:
   - The project is licensed under **GPL-3.0**.
   - Reference implementations (e.g. Seanime, Habari) may be studied strictly for official API protocols, schemas, and WinRT conventions, but code must be authored cleanly and natively to match TBD Movies contracts without copying third-party code.

2. **Local-First & Safety-First Persistence**:
   - The primary database engine is SQLite in WAL mode (`Microsoft.Data.Sqlite`) governed by **`Schema v24`** (`DatabaseMigrator.LatestVersion = 24`, `PRAGMA foreign_keys = ON;`).
   - Physical video files are read-only by default. Catalog metadata mutations never delete or modify physical media files without explicit user dry-run confirmation.
   - All database writes pass exclusively through `DatabaseWriteGate`.

3. **Secure Credential Storage**:
   - Plaintext credentials and API keys in SQLite or configuration files are strictly forbidden.
   - TMDB API keys/tokens and Trakt OAuth tokens reside exclusively in **Windows Credential Manager** (`CredWriteW` / `CredReadW`).

4. **Playback Scrobbler & Watched Parity**:
   - Local watched completion and Trakt scrobble completion are strictly unified at **`80%`**:
     - Below 80%: Local database stores resume checkpoints in `playback_progress`.
     - At or above 80%: Marked watched in local `watch_events` and Trakt history; resume checkpoint is purged.
   - Scrobbler lifecycle executes `/scrobble/start` on launch, `/scrobble/pause` on pause, and `/scrobble/stop` on player exit, early close, or app shutdown.
   - **Local-First Durability**: Local database updates and UI events execute first in $< 1\text{ ms}$; cloud network calls dispatch second with bounded cancellation timeouts.

5. **Test Data Isolation Guard**:
   - The exact environment variable used in code (`AppServices.cs:77`) to isolate application data during automated testing is **`TBDMOVIES_TEST_DATA_ROOT`** (uppercase, strictly zero spaces).

---

## 3. Releases & Manifests

Official version changelogs and release notes:
- [`RELEASE_NOTES_0.12.6.md`](releases/RELEASE_NOTES_0.12.6.md): Trakt full scrobbler lifecycle, unified 80% parity, instant library loading, and batch sync pushes.
- [`RELEASE_NOTES_0.12.5.md`](releases/RELEASE_NOTES_0.12.5.md): Unified Trakt custom lists, resume UI, and GSMTC dynamic position tracking.
- Earlier release manifests and patch notes are available under [`docs/releases/`](releases/).
