# BR-EpicImport — Experimental

> [!CAUTION]
> **EXPERIMENTAL SOFTWARE.** Epic does not provide a supported personal-library
> API for third-party launchers. Authentication, library import, metadata, and
> launching may stop working if Epic changes its launcher services.

Experimental Epic Games library importer for BOXROOM.

It signs in through Epic's own website, reads the Windows games available to the account, retrieves metadata and artwork from Epic's catalog, and writes custom game entries into BOXROOM's `steam_cache_v2` folder.

This tool uses the same launcher-service flow as the open-source Legendary
launcher. It is not affiliated with or supported by Epic Games.

## Sign-in

The importer opens Epic's sign-in page in the default browser. After signing in, Epic displays JSON containing an `authorizationCode`. Copy either that value or the complete JSON and paste it into BR-EpicImport.

The resulting refresh token is stored locally in the importer's ignored `EPIC`
folder. Passwords and browser cookies are never read or stored.

## Privacy and credentials

- Your Epic password is entered only on Epic's website and never passes through
  BR-EpicImport.
- BR-EpicImport does not read browser cookies, collect analytics, include
  telemetry, or upload your library to BOXROOM, the author, or any BOXROOM
  service.
- OAuth authorization codes and tokens are sent only to Epic's HTTPS services,
  because Epic must receive them to authenticate the account and return its
  library. The saved refresh token is not posted to GitHub or bundled in a
  release.
- The `EPIC/token.json` file stays on the computer running the importer. It is
  plain JSON rather than an operating-system credential-vault entry, so treat
  that local file as sensitive. Delete it to remove the saved sign-in.
- The string named `LauncherClientSecret` in the source is **not your private
  key**. It is the publicly known Epic Games Launcher OAuth client credential
  used by Legendary and other compatible launchers. It is visible in this
  public source by design and cannot by itself sign in to a player's account.

See [SECURITY.md](SECURITY.md) for the exact data flow and reporting guidance.

## BOXROOM cache

On Windows, BR-EpicImport uses BOXROOM's standard cache under `AppData/LocalLow/NestedLoop/BOXROOM/steam_cache_v2`.

On Linux, it searches native Unity and Steam/Proton locations. Set `BOXROOM_CACHE_PATH` to a specific `steam_cache_v2` folder if automatic discovery cannot find it.

## Launching

- Windows uses Epic Games Launcher's `com.epicgames.launcher://apps/...` protocol. Epic Games Launcher must be installed and signed in.
- Linux uses Heroic's `heroic://launch?appName=...&runner=legendary` protocol. Heroic must be installed, signed in to Epic, and have the game installed.

Running the importer again preserves existing entries, updates their launch addresses, and imports newly acquired games.

## Current limitations

- Authentication and library endpoints are unofficial and may stop working.
- The importer currently reads Epic's Windows asset library and skips DLC, mods, and Unreal Marketplace content.
- Catalog metadata varies by title, so some entries may have limited artwork, descriptions, or genre data.
- Live Epic sign-in, a complete account import, and Steam Deck launching require hands-on acceptance testing.

## Technical references

- [Legendary](https://github.com/legendary-gl/legendary) for the current Epic launcher authentication and library-service flow.
- [Epic Games protocol activation](https://assets-unreal2-epic-prod-us2.s3.dualstack.us-east-1.amazonaws.com/original/4X/f/c/4/fc4342c50e1a0abf34943e019ee90bacccc62950.pdf) for the Windows launch URI shape.
- [Heroic Games Launcher](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher) for the current Heroic launch protocol.

