# Security and privacy

## Experimental status

BR-EpicImport is experimental, unofficial software. It uses Epic launcher
services that are not offered as a stable public personal-library API. Epic can
change, restrict, or disable this flow without notice.

## What the importer handles

The user signs in on Epic's own website. BR-EpicImport receives only the
authorization code the user copies from Epic's response. It exchanges that code
directly with Epic for an OAuth access token and refresh token.

The access token is sent over HTTPS only to Epic-operated account, launcher,
and catalog services when the importer authenticates, reads the user's library,
and retrieves metadata. This direct transmission to Epic is necessary for the
import to work.

The refresh token is saved as `EPIC/token.json` beside the importer. The `EPIC`
folder is excluded by `.gitignore` and is never included in release archives.
The file is not uploaded to the author, GitHub, BOXROOM, Boxroom Studio, or a
BOXROOM metadata service. It is plain JSON and should be protected like any
other local login token. Deleting the file removes the saved sign-in from the
importer.

## What the importer does not handle

BR-EpicImport does not:

- receive, read, or store the user's Epic password;
- read or copy browser cookies;
- collect analytics or telemetry;
- upload the user's library or tokens to BOXROOM or the author;
- include any user's token or personal credential in its source or releases.

Imported BOXROOM metadata and artwork remain in the user's local BOXROOM cache.

## The launcher client credential

The source contains `LauncherClientId` and `LauncherClientSecret`. Despite the
OAuth field name, this is not a user's private key or account credential. It is
the publicly known Epic Games Launcher client credential used by the open-source
Legendary project. It identifies the launcher client during OAuth but cannot by
itself authenticate a player. A player still has to sign in with Epic and supply
a short-lived authorization code.

Because BR-EpicImport is open source, this public launcher credential is visible
in the repository. No personal secret is substituted into the source before a
build.

## Reporting a security issue

Do not post tokens, authorization codes, passwords, or `EPIC/token.json` in a
public issue. Report the behavior without sensitive values, or contact the
repository owner privately through their GitHub profile.

