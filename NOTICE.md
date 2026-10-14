# Notices

Simplified Suite
Copyright (C) 2026 Team Simplified

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation, version 3 of the License. It is distributed WITHOUT ANY WARRANTY;
see [LICENSE](LICENSE) for the full terms.

## Additional terms (GPL-3.0, section 7(b))

Under section 7(b) of the GNU General Public License version 3, the following
attribution requirement applies to this work and to every work based on it:

> Any program or library that contains, copies, ports or is based on code from
> Simplified Suite must credit it, by preserving this notice in its source and
> by stating, in its credits, About screen or documentation - wherever the
> program shows its own Appropriate Legal Notices:
>
> **Based on Simplified Suite by Team Simplified - https://github.com/TeamSimplified/simplified-suite**

Taken together with the GPL itself, this means: a program that uses code from
this suite must be released under the GPL-3.0 as well (with its source
available to everyone it is distributed to) and must carry the credit above.

## Game files are not included

This repository contains no files from the game. The F1® 25 files some apps
read at runtime - the game database, language files, UI and sponsor texture
archives and the shader-package surface descriptor - are the property of
their owners and are not distributed here. See [README.md](README.md) for
where each app expects them.

F1® 25 is a trademark of its owners. Simplified Suite is an unofficial,
fan-made project and is not affiliated with or endorsed by Electronic Arts,
Codemasters, Formula 1 or the FIA.

## Third-party components

These are used under their own licences, which this suite's licence does not
change:

| Component | Where | Licence |
|---|---|---|
| EgoEngineLibrary, (c) ryder25 (Petar Tasev) | ported into `EGO Engine Library/` | MIT |
| NeFS Library, (c) 2020 victorbush | `NeFS Library/` | MIT - see `NeFS Library/LICENSE` |
| DirectXTex texconv, (c) Microsoft Corporation | `tools/texconv.exe` in several apps | MIT |
| Outfit font, (c) 2021 The Outfit Project Authors | `Shared/Fonts/` | SIL Open Font License 1.1 - see `Shared/Fonts/OFL.txt` |
| HelixToolkit, AssimpNet, ZstdSharp, K4os.Compression.LZ4, CommunityToolkit and the other NuGet packages | restored at build time | each package's own licence |

### MIT licence text (EgoEngineLibrary, DirectXTex texconv)

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Credits

- **RM Designs** - the mesh, node-table, material and bounds formats in
  `EGO Engine Library/Archive/Erp/Model/`, worked out for their Steering Wheel
  and Helmet Swappers and used here with their permission.
- **Jefry** - the showroom lighting in the Universal Exporter, from Jefry's
  Model Handler.
- **Petar Tasev (ryder25)** - EgoEngineLibrary, the foundation of the ERP code.
