# Simplified Suite

Modding tools for F1® 25 by Team Simplified: swappers, editors, an ERP
workbench, a track installer, a launcher and a model exporter, built on one
shared EGO engine library and one shared UI library.

## Licence

GPL-3.0 with an attribution requirement - see [LICENSE](LICENSE) and
[NOTICE.md](NOTICE.md). In short: if your program uses code from this suite,
your program must be open source under the GPL-3.0 too, and it must credit
**Simplified Suite by Team Simplified**.

## Building

Each app is its own solution (`*.slnx`) beside its folder, targeting .NET 10.
Packages are pinned centrally in `Directory.Packages.props`. How the apps are
laid out is described in [SUITE-CONVENTIONS.md](SUITE-CONVENTIONS.md);
`tools/release.ps1` packages them.

## Game files are not included

This repository distributes no files from the game. Most of what the apps
need from it they take out of your install themselves, on first run and again
after each game update, into `runtime\original` beside the app:

- the language files, out of the game's own executable;
- the team database, `game_package/gamedatabases/gamedatabases.2025.bin`;
- the team-branding texture arrays, out of the game's UI archives, rebuilt
  into `ui_texarrays_team_branding.erp`.

Set the game folder in the app's Settings and it does the rest.

One set is still not taken automatically - the Sponsor Editor's sponsor
archives. It expects `Simplified Sponsor Editor/original/sponsors.erp` and
`sponsors_ui.erp`; that path is ignored by git, so a copy you place there
stays local. Without them the app still builds, but its sponsor features
will not work.

F1® 25 is a trademark of its owners. This is an unofficial, fan-made project,
not affiliated with or endorsed by Electronic Arts, Codemasters, Formula 1 or
the FIA.
