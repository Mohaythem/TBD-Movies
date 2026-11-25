# 🎬 TBD Movies v0.12.7

A modern, fast, and local-first desktop media library for Windows built with **WinUI 3 & .NET 10**.

### ✨ What's New & Highlights
• **🔄 Trakt Scrobbler Lifecycle & 80% Parity (`[F4]`)**: Deterministic `/scrobble/start`, `/scrobble/pause`, and `/scrobble/stop` on player exit/app shutdown, unified 80% watched completion parity, and account-isolated offline sync queues (`HasLinkedTraktAccount`).
• **🗄️ SQLite WAL Schema v25 & Active Viewings**: Simplified continue-watching pipeline backed by `active_viewings`, show-level `deduped CTE` to prevent duplicate episode cards per series, and immediate tracking upon confirmed player launch.
• **⚡ Zero-Jitter List Removal & Live Reordering (`[F7]` / `[F8]`)**: Automatic library reordering when returning from detail pages and in-place list diffing to eliminate flicker and double-entrance animations on item removal.
• **🎨 Modern UI Polish (Wave 10)**:
  - **Enlarged Tab Pills (`[F9]`)**: `36px` height, `16,6` padding, `14px SemiBold` typography, and `> 6.4:1` WCAG AA contrast.
  - **Unified `TraktSyncCapsule` (`[F10]`)**: `36px` borderless sync capsule shared across `LibraryPage` and `MyListsPage`, with `AllowFocusOnInteraction="False"` to prevent search-box focus stealing.
  - **Adaptive 4-State Home Layout**: Smart `EmptyState`, `MoviesOnlyState`, `ShowsOnlyState`, and `MixedState` layouts with full-width KPI cards.
  - **Episode Stills & Season Art (`[F1]`)**: Clean separation of episode stills from show backdrops, end-to-end season art propagation, and race-safe hero poster switching.
  - **Smart List Deletion Selection**: `MyListsSelectionPolicy` selects the predecessor list (`N - 1`) on custom list deletion.
• **🧪 Clean Build**: Zero compiler warnings (`0 Warning(s), 0 Error(s)`) and **1,516 / 1,516** passing automated tests (+78 new tests since `v0.12.6`).

### 📦 Download & Install
- **Installer (`TBDMoviesSetup.exe`)**: Per-user Windows installer (no admin privileges required).
- **Portable Edition (`TBD-Movies-v0.12.7-portable.zip`)**: Self-contained `win-x64` archive — extract and run `TBD-Movies.exe`.
