# TBD Movies — Unified Future Product Roadmap (`ROADMAP.md`)

> [!NOTE]
> **Scope & Authority of this Document (Single Source of Truth for Future Ideas):**  
> This document aggregates all vetted, future product proposals and strategic directions consolidated from legacy planning notes (`ALL_IDEAS_ROADMAP.md`, `CONSOLIDATED_ROADMAP_AND_IDEAS.md`, and `RELEASE_2_V2_ROADMAP.md`).
> - This document is strictly for forward-looking architectural planning; it does not authorize any immediate implementation without an independent execution plan and explicit user approval.
> - Active bugs, stability issues, and ongoing engineering batches (`[U9]`, `[U10]`, `[L6]`, `[L8]`, etc.) are tracked exclusively in **`/BUGS.md`**, not in this document.

---

## Core Product Principles

- **Local-First & Offline Resilience**: The media library, searching, playback tracking, watch history, and lists remain 100% functional without an active internet connection or mandatory third-party accounts.
- **Zero Unapproved Physical Mutation**: Strict logical separation between media catalog identity and physical disk files. Physical video files are never deleted, renamed, or moved without an explicit dry-run preview, user confirmation, and journaled recording in the operation history (`FileOperationCoordinator`).
- **Speed, Smoothness & Authentic Fluent WinUI 3**: Guarantee sub-5ms UI responsiveness on libraries with 10,000+ items. Full bidirectional Arabic (`RTL`) and English (`LTR`) support, Dark/Light theme parity, and native Mica Alt backdrop materials.

---

## Pillar 1: File & Storage Intelligence (`V2 Core — Storage Architecture`)

### 1.1 Multiple Versions, Editions & Redundant Copies
- Expand the entity relationship `Movie / Episode -> Multiple MediaFiles` to cleanly distinguish:
  - Resolution and encoding tiers (`4K UHD`, `1080p`, `Remux`, `Blu-ray`, `WEB-DL`).
  - Cut variations (`Theatrical`, `Director's Cut`, `Extended Edition`).
  - Redundant intentional backups distributed across multiple external drives.
- Surface a single cohesive catalog card displaying a user-configured "Preferred Default Copy", with an interactive flyout to select specific physical files for playback or folder navigation, adapting gracefully when an external drive is offline.

### 1.2 Duplicate Detection & Non-Destructive Catalog Merging
- Background audit engine detecting potential duplicates across volumes based on title tokens, file sizes, TMDB metadata matching, and optional file hashes.
- Absolute separation between **merging logical catalog records** (unifying watch history, ratings, and custom list membership) and **deleting physical files** (physical files are never deleted automatically).

### 1.3 Smart Relink for Externally Moved Media Files
- Detect files moved or renamed externally via Windows Explorer.
- Interactive relink dialog presenting previous vs. detected paths with confidence signals (name similarity, file size, partial hash match) before applying updates, preserving movie/episode identity, watch status, and list memberships.

### 1.4 Library Health & Storage Intelligence Dashboard
- Upgrade `CheckLibraryPage` into a comprehensive storage health hub:
  - Real-time disk drive health and volume root availability (Connected / Temporarily Disconnected / Low Space), preventing false "Missing" alerts for offline drives.
  - Granular metrics distinguishing logical title counts from physical file instances and total disk storage in bytes.
  - Multi-season TV episode coverage alongside movies, highlighting series with missing intermediate episodes once expected episode counts are resolved.

---

## Pillar 2: Safe Operations, Advanced Search & Metadata Governance

### 2.1 Safe Organization & Opt-in Rename Templates
- Configurable naming templates for movies and series (defaulting to safe in-place renaming).
- Mandatory dry-run preview, collision detection, and companion subtitle file grouping before executing atomic journaled moves through `SafeTransferEngine`.

### 2.2 Operation History & Verified Undo UI
- Expose the underlying operation journal in a dedicated UI page, providing a verified `Undo` capability that checks target file availability and checksum integrity before reversing any file movement.

### 2.3 Advanced Local Filters & Dynamic Smart Lists
- Sub-millisecond composite in-memory filtering (Genre, Release Year, User Rating, Watch Status, Metadata Health, Storage Volume, Resolution, Subtitle Availability).
- Ability to persist any filter query as a dynamic **Smart List** (e.g., *"Unwatched 4K Movies"*, *"Added in Last 30 Days"*, *"External Drive Movies"*).

### 2.4 Local Metadata Editing & Locking
- Enable inline editing of title, release year, overview, and poster references for home videos or unindexed media.
- Introduce an explicit **Metadata Lock** flag preventing future TMDB sync passes from overwriting user-customized fields.

### 2.5 User-Facing Backup & Validated Restore
- Export and import self-contained archive bundles containing the SQLite database, custom lists, watch history, settings, and approved media roots (**strictly excluding physical video files**).
- Validate schema versions, foreign key integrity, and process lock coordination, with automated rollback on restore failure.

---

## Pillar 3: Candidates & UI Polish

- **Optional Passive Folder Watcher**: Non-intrusive filesystem change monitoring for registered media roots. Events act strictly as hints (waiting for write completion, ignoring transient disconnections) that feed the standard discovery confirmation workflow.
- **Subtitle Companion Intelligence**: Filter media items missing target language subtitles (Arabic / English) and automatically link external `.srt` / `.ass` files during library organization passes.
- **Artwork Lightbox Viewer**: Fullscreen high-resolution poster and backdrop inspector supporting interactive zoom, pan, and 1:1 pixel scaling.
- **Fluent Layout & Vector Empty States**:
  - Align `MyListsPage` card layout to match `LibraryPosterLayout` responsive behavior.
  - Standardize horizontal margins to `32 epx` across all detail views to eliminate layout jumps, replacing generic placeholders with clean vector empty-state graphics.
- **Post-V2 Explorations**:
  - Optional Letterboxd CSV import/export preserving local database primacy.
  - Optional discover/trending catalog browse strictly separated from local library views.

---

## Explicitly Avoided Directions

- Social networking features, comments, follower counts, or public feed syndication.
- Mandatory cloud account registration or forced online telemetry to use local features.
- Silent, unconfirmed background file mutations or physical deletions.
- Complex or unnecessary third-party plugin systems where built-in architecture suffices.
