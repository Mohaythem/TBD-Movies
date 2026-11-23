# TBD Movies (To Be Determined Movies)

A modern, fast, and local-first desktop media library for Windows, built with **.NET 10** and **WinUI 3 (Windows App SDK)**.

> **TBD** stands for **To Be Determined Movies** — your personal movie and TV show catalog with complete local privacy, Trakt synchronization, and safety-first file management.

---

## ✨ Features

- **🎬 Movies & TV Shows Management**: Organize your local video collection with rich metadata, high-resolution backdrops, genres, community ratings, and multi-season TV episode tracking.
- **🔍 Smart TMDB Matching**: Automatic filename parsing and matching against The Movie Database (TMDB) with interactive candidate selection.
- **🔄 Trakt.tv Integration**: Sync watch history, movie favorites, unified `[TBD]` custom lists, watchlist updates, and playback scrobble events with Trakt.tv via Device Code OAuth and automatic rate-limit backoff.
- **🛡️ Safety-First Architecture**: Read-only by default. Physical video files are never renamed, moved, or deleted without explicit user preview and confirmation.
- **🌍 Native Arabic & English Support (Full RTL/LTR)**: Flawless bidirectional layout switching with Fluent typography and custom localization.
- **⚡ Local-First & Private**: All your library data lives locally in SQLite (WAL mode). TMDB API and Trakt OAuth tokens are securely stored in Windows Credential Manager (`CredWriteW` / `CredReadW`).
- **🎨 Modern Windows 11 UI**: Crafted with WinUI 3, Mica Alt materials, responsive poster grids, and sleek themes (Dark, Light).

---

## 🛠️ Tech Stack

- **UI Framework**: .NET 10 (C# 14) + WinUI 3 / Windows App SDK
- **Database**: SQLite (`Microsoft.Data.Sqlite`) with ACID transactions & WAL mode (`Schema v24`)
- **Integrations**: TMDB API, Trakt.tv REST API, & SubDL Subtitles API
- **Security**: Windows Credential Manager (`CredWriteW` / `CredReadW`) for TMDB & Trakt tokens with one-way legacy migration
- **Packaging**: Inno Setup Installer & Portable Self-Contained Win-x64

---

## 🚀 Download & Installation

Download the latest official release directly from **[GitHub Releases](https://github.com/Mohaythem/TBD-Movies/releases)**:

- **Installer (`TBDMoviesSetup.exe`)**: Automatic per-user setup with Desktop and Start Menu shortcuts.
- **Portable Edition (`TBD-Movies-v0.12.6-portable.zip`)**: Ready to run anywhere without installation. Just extract and launch `TBD-Movies.exe`.

---

## 📄 License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)** — keeping it free and open-source for everyone. Any derivative work or modifications must also remain open-source under the same terms.
