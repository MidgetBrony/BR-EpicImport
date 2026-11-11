# BR-EpicImport 0.1.0 — Experimental

Initial experimental build.

> This release relies on unsupported Epic launcher services and is published as
> a GitHub pre-release. See `SECURITY.md` before signing in.

- Signs in through Epic's browser authorization-code flow and refreshes the saved token.
- Imports owned Windows game assets with Epic catalog titles, descriptions, artwork, screenshots, developers, publishers, and tags.
- Writes BOXROOM-compatible custom entries and updates `owned_games.json` safely.
- Detects previously imported Epic app names so reruns add only new games.
- Launches through Epic Games Launcher on Windows and Heroic on Linux.
- Skips DLC, mods, and Unreal Marketplace entries.
- Keeps the user's saved Epic refresh token local and excluded from source and
  release packages; no passwords, cookies, library data, analytics, or
  telemetry are collected.

