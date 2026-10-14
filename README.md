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

This repository distributes no files from the game. A few apps need some at
runtime, and look for them where a full build of the suite keeps them. Take
them from your own copy of F1® 25:

| App | Expects | Taken from the game |
|---|---|---|
| Team Editor, Grid Editor | `Simplified Team Editor/original/gamedatabases.2025.bin` | the game's `gamedatabases.2025.bin` |
| Team Editor, Grid Editor | `Simplified Team Editor/original/ui/ui_texarrays_team_branding.erp` | the game's UI texture arrays |
| Sponsor Editor | `Simplified Sponsor Editor/original/sponsors.erp`, `sponsors_ui.erp` | the game's sponsor archives |
| Grid, Sponsor, Team Editors, Studio, Track Installer | `Shared/serps.erp` | the shader-package surface descriptor and vehicle shadow material every mod's shader package carries |

Language files are not needed in the repository: the apps take them out of
the game's own executable on first run.

These paths are ignored by git, so a copy you place there stays local.
Without them those apps still build (the surface descriptor is embedded only
when present), but the features that read them will not work.

F1® 25 is a trademark of its owners. This is an unofficial, fan-made project,
not affiliated with or endorsed by Electronic Arts, Codemasters, Formula 1 or
the FIA.
