# Simplified Suite — conventions

What the nine apps in `F:\Claude Code\Simplified Suite` have in common, and
what an app has to do to belong to it. Written while bringing **Simplified Team
Editor** into line, and §8 records what that took — but everything above it
applies to any app in the suite. §9 is the second arrival, the **Driver
Swapper**, which needed a different half of the same list.

The apps: Studio, ERP, Toolkit (`stk`, CLI), Chassis Swapper, Driverwear
Swapper, Swapper, Helmet Swapper, Team Editor, Driver Swapper. Two shared
libraries and a `Shared/` folder of odds and ends sit beside them.

---

## 1. Use the shared libraries. Don't fork them.

This is the biggest one, and the one the Team Editor was on the wrong side of.

**`EGO Engine Library`** owns every game file format: ERP archives, DDS, `.lng`
language files, binary XML, PKG, texconv, and the endian/IO plumbing under
them. Every app calls it with paths and gets results back. No app carries
format code of its own — the Driverwear Core says why, in a comment worth
copying:

> No ERP code of its own: every game file is read and written by the shared
> library, whose operations this app calls with paths. That keeps the format in
> one place across the suite — anything missing gets added there rather than
> reimplemented here.

If the library is missing something, **add it there**. That is how `RemoveAll`
and a `filter` on `StripMips` arrived this week: the Helmet Swapper needed
them, so they went into `Operations/ErpOperations.cs` and every app has them
now.

**`Simplified UI Library`** (`SimplifiedUi`) owns the look: `Theme.xaml`, the
`Palette.Light` / `Palette.Dark` / `Palette.Glass` dictionaries, `ThemeManager`,
`FramelessWindow`, and `DialogChrome`. No app carries its own copy of any of
these.

**`Shared/NativeLibraryFolder.cs`** is linked into each app rather than copied:

```xml
<Compile Include="..\..\..\Shared\NativeLibraryFolder.cs" Link="NativeLibraryFolder.cs" />
```

---

## 2. Project layout

```
Simplified <Name>/
    Directory.Build.props        version, company, copyright — and the import below
    Directory.Packages.props
    Simplified<Name>.slnx
    icon.ico
    src/
        Simplified<Name>/        the WPF app       (net10.0-windows)
        Simplified<Name>.Core/   logic, no WPF     (net10.0)
```

Core holds anything testable without a window. Keeping it WPF-free is what let
the Markdown parser in Studio be exercised from a console probe.

### Directory.Build.props must chain, not replace

MSBuild stops at the first `Directory.Build.props` it finds walking up, so a
per-app one **silently replaces** the suite-wide one. Every app's version
therefore imports the root explicitly, and the quotes matter — unquoted,
`GetPathOfFileAbove` returns empty and MSBuild fails with `MSB4020`:

```xml
<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
```

Declare `Version`, `Company` and `Copyright` **after** the import so they win.
Everything else — `LangVersion`, `Nullable`, `ImplicitUsings`, `Deterministic`,
`DebugType` — comes from the root; repeating it locally is how an app ends up
shipping a stray `.pdb` when the root changes and the copy doesn't.

### App .csproj

```xml
<OutputType>WinExe</OutputType>
<TargetFramework>net10.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
<RootNamespace>Simplified<Name></RootNamespace>
<AssemblyName>Simplified <Name></AssemblyName>   <!-- with the space -->
<ApplicationManifest>app.manifest</ApplicationManifest>
<ApplicationIcon>..\..\icon.ico</ApplicationIcon>
<Platforms>x64</Platforms>
<PlatformTarget>x64</PlatformTarget>
```

and, gated on publishing:

```xml
<PropertyGroup Condition="'$(_IsPublishing)' == 'true'">
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <DebugType>embedded</DebugType>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
```

`IncludeNativeLibrariesForSelfExtract` is **off on purpose**: on, the host
extracts natives to `%TEMP%\.net` at startup — outside the app folder, once per
build, never cleaned up. They publish as loose files and a
`MoveNativesIntoRuntimeFolder` target tidies them into `runtime\`, which is
where `NativeLibraryFolder` then points the resolver.

Packages are managed centrally in the root `Directory.Packages.props` —
`PackageReference` without a `Version`.

---

## 3. The look

`App.xaml` merges the palette **first**, then the theme. Order matters on the
way in because `Theme.xaml` resolves colours against what is already in the
dictionary:

```xml
<ResourceDictionary Source="pack://application:,,,/SimplifiedUi;component/Palette.Light.xaml"/>
<ResourceDictionary Source="pack://application:,,,/SimplifiedUi;component/Theme.xaml"/>
```

That entry is the fallback, not the decision. `App.OnStartup` applies the saved
one before the first window is built, so the app never flashes the wrong theme:

```csharp
SimplifiedUi.ThemeManager.Apply(UserConfig.Load().Theme);
```

### Never name a colour

Everything comes from palette keys via `DynamicResource`:

> `Surface` · `SurfaceAlt` · `SurfaceSunken` · `TextPrimary` · `TextMuted` ·
> `TextDisabled` · `BorderSoft` · `BorderStrong` · `Accent` · `AccentSoft` ·
> `Danger` · `Badge` · `BrandInk` · `Hover` · `Pressed` · `Selected` ·
> `Syntax*`

Named styles: `CaptionButton`, `CloseButton`, `IconButton`, `Panel`,
`FilterChip`, and the `IconNew` / `IconOpen` / `IconSave` / `IconSettings`
geometries.

For the few brushes set in code, look the key up with a fallback so a control
built before the dictionary is ready still draws:

```csharp
private static Brush Themed(string key, byte r, byte g, byte b) =>
    Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));
```

In XAML, a **keyed** style must say `BasedOn="{StaticResource {x:Type Button}}"`
— without it the keyed style replaces the themed template outright and drops
that control back to the stock Windows look.

### Windows

Main windows are frameless but still real windows — `WindowStyle="None"` plus
`WindowChrome` keeps native resize borders, snapping, Aero Snap and
double-click-to-maximise, all of which `AllowsTransparency` would throw away:

```xml
<shell:WindowChrome CaptionHeight="56" ResizeBorderThickness="6"
                    GlassFrameThickness="0" CornerRadius="0"
                    UseAeroCaptionButtons="False"/>
```

Then `SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton)` in the
constructor, and three handlers: minimise, `FramelessWindow.ToggleMaximize`,
close.

The title bar is 56px, `SurfaceAlt`, with the wordmark left, a version pill
beside it read from the assembly (so it can't drift from the build), and
minimise/maximise/close right.

Dialogs are built in code and wrapped with `SimplifiedUi.DialogChrome.Apply(win)`
**after** setting `Content` and before `ShowDialog` — it wraps the current
content, so anything assigned later replaces the title bar along with it.

---

## 4. Behaviour rules

- **A save icon means Save As, in every app.** `Ctrl+S` is the one that
  overwrites in place; `Ctrl+Shift+S` is Save As. This is a standing rule
  across the suite.
- **Portable.** Settings live in a `config.json` **beside the exe**, not under
  `%APPDATA%`. The whole folder can be moved to another machine or deleted with
  nothing left behind. (Studio and the older swappers still use `%APPDATA%` and
  are the exception, not the model.) Temp and scratch files are exempt — those
  belong in `%TEMP%`, which makes the app more robust, not less portable.
- **Dark by default**, with a Light toggle in Settings.
- A failed config write must never interrupt real work — catch and carry on.

---

## 5. Releasing

`tools\release.ps1` publishes, packages and tags. Register the app in the list
near the top:

```powershell
[pscustomobject]@{
    Short = 'Helmet'; Folder = 'Simplified Helmet Swapper'
    Project = 'src\SimplifiedHelmetSwapper\SimplifiedHelmetSwapper.csproj'
    Exe = 'Simplified Helmet Swapper'; Package = 'Simplified Helmet Swapper'
    Tag = 'helmet-v'; Kind = 'window'; Ships = @('*.exe', 'runtime\*')
}
```

and add its `Short` name to the `[ValidateSet(...)]` on the `$App` parameter.
`Ships` is checked against what actually lands in the zip, so a stray file
stops the release instead of shipping.

The guards exist because the manual version got each of them wrong at least
once: a dirty tree is refused, the release folder is mirrored rather than
copied over, staging reads only from the publish output (so a `config.json`
written on first run can't end up in a package), and the packaged exe's stamp
is checked against `HEAD`.

---

## 6. How the code reads

Comments explain **why**, not what — and very often name the thing that went
wrong, so the next person doesn't undo the fix. From real files:

```csharp
// Cutting at the first space is the obvious way and the wrong one: PureRef
// registers itself unquoted as C:\Program Files\PureRef\PureRef.exe "%1",
// which that reads as C:\Program.
```

```csharp
// The suite's TextBox style centres its content — right for a one-line
// field, wrong for a page of writing, which has to start at the top.
```

Public types and members carry `<summary>` docs that say what the thing is for
in plain prose. Prefer measuring over assuming: this codebase's filters and
formats were derived by diffing real game files, and it shows.

---

## 7. Gotchas that have cost real time

- **`DockPanel` allocates in declaration order, not by side.** Right-docked
  children declared last lose their space first, so they're listed in *reverse*
  visual order. The gap between two buttons is the right-hand one's **left**
  margin — so the leftmost button carries no margin, and adding a new leftmost
  button means moving the margin onto its neighbour.
- **The shared `TextBox` style sets `VerticalContentAlignment="Center"`.**
  Correct for one-line fields, wrong for any multi-line editor — override it to
  `Top`.
- **Answering `WM_GETMINMAXINFO` takes min-size enforcement away from WPF.**
  If you handle it, supply `MinTrackSize` yourself or the window will resize
  below its `MinWidth`/`MinHeight`.
- **An owned window with `ShowInTaskbar = false` drops its owner on close.**
  Windows picks the next window itself and picks one behind the app, which
  looks exactly like the app minimising. Call `Owner?.Activate()` as it closes.
- **Gate shortcuts on the right thing.** Studio's `Ctrl+S` was gated on a
  legacy hidden button's `IsEnabled` and did nothing in project mode for
  months.
- **A `Style` without `BasedOn` replaces rather than extends.** See §3.

---

## 8. What the Team Editor needed, in order

It already gets a lot right — `src/App` + `src/App.Core`, single-file
self-contained publish, `IncludeNativeLibrariesForSelfExtract` off with a
`MoveNativesIntoRuntimeFolder` target, `runtime\` for natives, assets bundled
rather than read from the install. The gaps are all about sharing.

1. **Replace the forked engine library.** `SimplifiedTeamEditor.Core` currently
   contains its own copy of `Archive/Erp/**`, `Conversion/*`, `Graphics/Dds/*`,
   `Graphics/GraphicsExtensions.cs`, `IO/*`, `Texconv/TexconvRunner.cs` and
   `Util/ErpFileIO.cs` — all of which exist in `EGO Engine Library` under the
   same names. Delete them, add
   `<ProjectReference Include="..\..\..\EGO Engine Library\EgoEngineLibrary.csproj" />`,
   and fix up namespaces. Diff before deleting — if the fork gained anything,
   that improvement belongs in the library.

   `Lng/LngFile.cs` looks like a duplicate of the library's
   `Language/LngFile.cs` and is not — the two have entirely different APIs. The
   library's is upstream's, built around `Hshs`/`Hsht`/`SidA`/`SidB` and
   `DataTable`; the Team Editor's is a small purpose-built reader with an
   `Entries` dictionary and a `BuildSaveData(edits)` that writes back only what
   changed. Keep the Team Editor's. Merging them is a real piece of work and
   not a prerequisite for belonging to the suite.

   Genuinely the Team Editor's own, and staying: `Lng/`, `GameDatabase/`,
   `Logos/`, `Packaging/`, `Teams/`, `Config/`, `AppPaths.cs` and
   `GameFiles.cs`. (`Texconv/ProcessRunner.cs` goes — it exists only to serve
   the forked runner, and the library's uses `Process` directly.)

2. **Replace the forked theme.** Delete `Palette.Dark.xaml` and `Theme.xaml`
   from the app, add
   `<ProjectReference Include="..\..\..\Simplified UI Library\SimplifiedUi.csproj" />`,
   and merge the shared dictionaries by pack URI (§3). The current comment
   says the app is dark-only because there's no Settings to hold the choice —
   fair when written, but the suite now has a Light palette and a toggle, and
   using the shared library gets both for the cost of a Settings window.

3. **Link `Shared/NativeLibraryFolder.cs`** instead of keeping the copy in
   `src/SimplifiedTeamEditor/`.

4. **Move it into the suite** at `F:\Claude Code\Simplified Suite\Simplified
   Team Editor`. The `ProjectReference` paths above assume that; the shared
   libraries are what make it worth doing.

5. **Fix `Directory.Build.props`** — add the chaining import (§2) and drop
   `LangVersion`, `Nullable`, `ImplicitUsings` and `Deterministic`, which the
   root already sets. Keep `Version` and `Company`, add `Copyright`.

6. **Add the missing publish properties** — `Platforms`, `PlatformTarget` and
   `RuntimeIdentifier` are all absent from the app `.csproj`.

7. **Register it in `tools\release.ps1`** (§5). There is already a
   `Release\Simplified Team Editor` folder, so it has been packaged by hand at
   least once; the script's guards are the point of doing it properly.

8. **Adopt the behaviour rules** (§4): portable `config.json` beside the exe,
   Save-icon-means-Save-As, dark by default with a Light toggle.

Steps 1–3 are the ones that make it part of the suite rather than an app that
looks like one. The rest is tidying.

---

## 9. What the Driver Swapper needed, in order

The opposite shape to the Team Editor. Nothing was forked — it reads the game's
**database**, not its archives, so it carries no format code at all and had no
theme to replace. What it had instead was no theme at all: `App.xaml` was empty,
the window wore stock Windows chrome, and every colour in it was a literal.

1. **Move it in**, from `Simplified Apps\Database\Simplified Driver Swapper`.
   The original stays there as a backup; the copy in the suite is the one that
   gets edited. `.git`, `bin`, `obj` and the app's own `.gitignore` stay behind,
   and its `Directory.Packages.props` goes — the root's is the one that should
   be reached.

2. **Chain `Directory.Build.props`** (§2), and drop `LangVersion`, `Nullable`,
   `ImplicitUsings` and `Deterministic`, which the root sets. `Copyright` is
   overridden here rather than inherited: the root's credits EgoEngineLibrary,
   which this app carries none of.

3. **Reference `Simplified UI Library`**, merge the palette and theme in
   `App.xaml` (§3), and apply the saved one in `App.OnStartup` before the first
   window is built.

4. **Frameless window** (§3): `WindowStyle="None"` plus `WindowChrome`, the
   56px title bar with the wordmark and version pill,
   `FramelessWindow.Attach` and the three caption handlers.

5. **Every literal colour onto a palette key.** The four brushes the status line
   used were `static readonly` fields, which is the wrong shape twice over —
   resolved once at type init, and frozen through a theme change. They became
   properties looking the key up each time, with the old literal as the
   fallback (§3).

6. **The table.** This is the first app in the suite with a `DataGrid`, and a
   near-stock one cannot be themed from the outside: it builds its chrome from
   `SystemColors`, so it stays white in the dark palette. `DataGrid`,
   `DataGridColumnHeader`, `DataGridRow` and `DataGridCell` were retemplated in
   the shared `Theme.xaml`, where the next app to want a table will find them.

7. **Portable config** (§4): `AppPaths.cs` in Core, `config.json` beside the
   exe, and a `Theme` on `UserConfig`. It was writing to
   `%APPDATA%\simplified-driver-swapper\`.

8. **Settings on the shared shell** — `SettingsWindow.xaml` deleted, replaced
   by a `DriverSettings.Show` built from `SimplifiedUi.SettingsDialog`.

9. **Drop the forked `GameDirDetector`** in favour of the linked
   `Shared/GameDirDetector.cs`, and with it the app's own first-run detection —
   `App.OnStartup` does that the same way everywhere now.

10. **Register it in `tools\release.ps1`** (§5) as `Driver`, tag `driver-v`.

One thing found on the way: `app.manifest` still identified the assembly as
`SimplifiedChassisSwapper`, from whichever app it was copied out of.
