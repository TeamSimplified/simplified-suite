# Simplified Toolkit (`stk`) — CLI Integration Guide

A single self-contained Windows executable, **`stk.exe`**, for manipulating
Codemasters Ego Engine game files (F1 series and relatives): `.erp` resource
archives, `.lng` language files, plus general-purpose file utilities.

This document is written so an automation agent (or another program) can
drive `stk` without prior knowledge of the codebase. Every command, argument,
flag, exit code, and output convention is documented below.

---

## 1. Invocation model

```
stk <group> <command> [subcommand] [arguments...] [--flags]
```

- The binary is named **`stk.exe`** (`stk` on PATH).
- It is **self-contained**: no .NET runtime install is required.
- It is built `net10.0-windows` and targets **win-x64**.
- All commands are **non-interactive** — they never prompt. They read
  arguments, do the work, print a summary, and exit. Safe to call from a
  script, build step, or another process.

### Top-level groups

| Group | Purpose |
|-------|---------|
| `stk erp`      | ERP archive operations (list, extract, repack, merge, edit resources/fragments, textures, XML, PKG, config) |
| `stk lng`      | `.lng` language-file operations (list, get, set, add, remove, merge) |
| `stk file`     | Generic file utilities (bulk rename, delete, mkdir, text replace, hex edit) |
| `stk variable` | Persistent variables expanded as `{name}` in any later command |
| `stk run`      | Execute a script file of `stk` commands, one per line |

Run any command with `--help` to print its usage, e.g. `stk lng merge --help`.

### Exit codes & output streams

- **Exit `0`** on success, **non-zero** (usually `1`) on failure. Always
  check the process exit code in automation — do not parse stdout to detect
  failure.
- **stdout** carries the human-readable result/summary (and data for read
  commands like `lng get` / `lng list`).
- **stderr** carries error messages. A command that "can't find the key" or
  "file not found" writes to stderr and returns a non-zero code.

### Conventions shared across commands

- **In-place by default.** Commands that modify an ERP/LNG overwrite the
  input file. Most accept `--output <path>` (LNG) or `--output` (ERP merge)
  to write a copy instead. ERP resource/fragment edits always write back to
  the input file — copy the file first if you need a backup.
- **`--dry-run`** is supported by every mutating `stk file` verb. Use it to
  preview before committing.
- Paths with spaces must be quoted by the calling shell as usual.

---

## 2. `stk lng` — language files

`.lng` files are key→value string tables (UTF-8). Keys look like
`lng_1000_hertz`; values are the displayed strings. A typical F1 language
file holds ~56,000 entries.

> All mutating `lng` commands accept **`--output <path>`** to write the
> result to a new file instead of overwriting the input.

### `stk lng list <file> [--values]`
Print every key, one per line. With `--values`, prints `key<TAB>value`.
The entry count is written to **stderr** (so piping stdout stays clean).

```bash
stk lng list language_eng.lng
stk lng list language_eng.lng --values > all_entries.tsv
```

### `stk lng get <file> <key>`
Print one key's value to stdout. Exit `1` (message on stderr) if the key is
absent.

```bash
stk lng get language_eng.lng lng_1000_hertz      # → 1,000 Hertz
```

### `stk lng set <file> <key> <value> [--output <path>]`
Change the value of an **existing** key. Errors if the key doesn't exist
(use `add` to create one).

```bash
stk lng set language_eng.lng lng_1000_hertz "1000 Hz"
```

### `stk lng add <file> <key> [value] [--force] [--output <path>]`
Add a **new** key. `value` is optional (defaults to empty string). Errors if
the key already exists unless `--force` is given (which then overwrites).

```bash
stk lng add language_eng.lng lng_my_new_string "Hello World"
stk lng add language_eng.lng lng_existing "new" --force
```

### `stk lng remove <file> <key> [--output <path>]`
Delete a key. Exit `1` if the key isn't present.

```bash
stk lng remove language_eng.lng lng_obsolete_string
```

### `stk lng merge <target> <sources...> [--overwrite] [--output <path>]`
Merge one or more source `.lng` files into the target.

- **Default (preserve):** new keys from sources are added; keys already in
  the target keep the **target's** value.
- **`--overwrite`:** keys present in both are replaced with the **source's**
  value. New keys still get added.

Sources are applied left-to-right. The per-source summary line reports
`N added, N replaced, N preserved`.

```bash
# Add any new keys from the update, but never touch existing translations:
stk lng merge base.lng update.lng

# Pull in new keys AND overwrite changed ones from the update:
stk lng merge base.lng update.lng --overwrite --output merged.lng
```

---

## 3. `stk erp` — resource archives

ERP files are Codemasters' resource package format. Each ERP has a version
and a flat list of **resources**; each resource has an **identifier** (an
`eaid://…` URI), a **ResourceType** (≤16-char tag, e.g. `GfxSRVResource`),
and one or more **fragments** (the actual compressed payloads).

### Resource identifiers — how `<id>` arguments resolve
Commands that take an `<id>` accept any of:
- a **full identifier** — `eaid://.../foo.tga`
- a **filename** — `foo.tga` (the basename)
- a **glob** — `ferrari_*` (`*` and `?` wildcards, matched against
  identifier or filename)
- a **numeric index** — `#42` (0-based position in the resource list)

A lookup that matches **zero** or **more than one** resource is an error
(the command aborts and lists the ambiguous matches). Use `stk erp list`
first to find the exact identifier/index.

### `stk erp list <file> [--type <ResourceType>] [--filter <regex>]`
List resources. `--type` filters by exact ResourceType;
`--filter` is a case-insensitive regex matched against the identifier.
Header shows file path, ERP version, and counts. Each row:
`#<index> [<ResourceType>] <fragments>f <packedBytes>b <identifier>`.

```bash
stk erp list car.erp
stk erp list car.erp --type GfxSRVResource
stk erp list car.erp --filter "ferrari.*livery"
```

### `stk erp extract <file> <outDir> [--filter <regex>]`
Extract every fragment of every resource to `outDir`, preserving the ERP's
internal folder structure. `--filter` regex-matches the resource filename.

### `stk erp repack <file> <inDir>`
Re-import previously-extracted fragments from `inDir` and write the ERP
back **in place**. The inverse of `extract`.

```bash
stk erp extract car.erp ./car_dump
# …edit files under ./car_dump…
stk erp repack car.erp ./car_dump
```

### `stk erp resource …` — edit resource metadata
All write back to the input file.

| Command | Effect |
|---------|--------|
| `stk erp resource rename <file> <id> <newName>` | Change a resource's filename (keeps folder + `eaid://` prefix) |
| `stk erp resource rename-all <file> <find> <replace>` | Substring find/replace across **every** identifier |
| `stk erp resource set-uri <file> <id> <newUri>` | Replace the full identifier |
| `stk erp resource set-type <file> <id> <newType>` | Change ResourceType (≤16 chars) |
| `stk erp resource duplicate <file> <id>` | Deep-copy a resource (clones every fragment) |
| `stk erp resource remove <file> <id>` | Delete a resource |

```bash
stk erp resource rename car.erp ferrari_decal.tga my_decal.tga
stk erp resource rename-all car.erp ferrari mercedes
stk erp resource remove car.erp "#42"
```

### `stk erp fragment …` — add/remove fragments
| Command | Effect |
|---------|--------|
| `stk erp fragment add <file> <id>` | Append a fragment cloned from the resource's last fragment |
| `stk erp fragment remove-last <file> <id>` | Delete the last fragment (refuses if only 1 remains) |

### `stk erp textures …` — bulk texture export/import
Operates on `GfxSRVResource` entries. Default format is **DDS**; add
`--png` to go through `texconv` (requires `texconv.exe`, see §7; BC6H/HDR is
refused for PNG).

**Export:**
```
stk erp textures export <file> <outDir>
    [--png] [--with-mips] [--game-dir <path>] [--flat] [--filter <regex>] [--slice <N>]
```
- `--with-mips` pulls in the external high-res `.mipmaps` (needs a game
  directory; see §6).
- `--flat` dumps everything into `outDir` with no folder structure
  (colliding basenames get a parent-folder prefix).
- `--filter` regex-matches the resource identifier.
- `--slice <N>` exports only **one layer** of a texture array as a plain 2D
  image (0-based). The output file is tagged `<name>.sliceN.<ext>`.
  Resources without that layer are skipped. Omit it (default `-1`) to export
  the whole array as one DX10 array DDS.

**Import:**
```
stk erp textures import <file> <inDir>
    [--png] [--game-dir <path>] [--mips-out <path>] [--slice <N>]
```
- `--png` reads PNGs and re-encodes to each texture's existing ERP format.
- `--game-dir` writes regenerated `.mipmaps` straight into the game install;
  otherwise they go to `--mips-out` (default `<inDir>_mipmaps/`).
- `--slice <N>` imports a single 2D image into **one layer** of a texture
  array (0-based), leaving the other layers intact. It looks for the
  `<name>.sliceN.<ext>` file the matching export produced. Omit it to import
  the whole array.
- Writes the ERP back in place.

```bash
stk erp textures export car.erp ./tex --png --filter ferrari
# …edit PNGs…
stk erp textures import car.erp ./tex --png

# Single-slice round-trip (e.g. layer 2 of a tyre/wheel array):
stk erp textures export car.erp ./tex --slice 2 --filter wheel
# …edit ./tex/.../<name>.slice2.dds…
stk erp textures import car.erp ./tex --slice 2
```

> **Note:** `import` has no `--filter` option — it processes every
> texture and reports `missing` for any without a matching input file.
> Scope a single-slice import by exporting into a dedicated folder that
> only holds the slice you want to re-import.

### `stk erp xml …` — binary-XML fragments ↔ text
```
stk erp xml export <file> <outDir>     # decode binary-XML fragments to text
stk erp xml import <file> <inDir>      # re-encode text back into fragments
```
Non-XML fragments are skipped automatically.

### `stk erp pkg …` — PKG fragments ↔ JSON
```
stk erp pkg export <file> <outDir>     # decode PKG fragments to .json
stk erp pkg import <file> <inDir>      # re-encode .json back into fragments
```
Non-PKG fragments are skipped automatically.

### `stk erp merge <target> <sources...> [--overwrite] [--output <path>]`
Merge resources from one or more source ERPs into the target. By default the
**target wins** on identifier conflict (source resource skipped);
`--overwrite` makes the **source win**. New resources are always added.
`--output` writes a copy instead of overwriting the target.

```bash
stk erp merge base.erp livery.erp numbers.erp
stk erp merge base.erp patch.erp --overwrite --output out.erp
```

### `stk erp config …` — game directory
Stores user config under `%APPDATA%`. Currently the only key is `game-dir`,
used to locate external `.mipmaps` files for texture commands.
```
stk erp config set game-dir "C:\…\F1 25"
stk erp config get game-dir
stk erp config unset game-dir
```
Game-dir resolution order for texture commands: `--game-dir` flag →
`ERP_GAME_DIR` env var → stored config.

---

## 4. `stk file` — file utilities

General-purpose helpers. Every mutating verb supports `--dry-run`.

### `stk file folder <paths...> [--no-parents] [--fail-if-exists]`
Create one or more folders. Creates parent directories by default;
silently succeeds if the folder already exists (unless `--fail-if-exists`).

### `stk file rename <find> <replace> <path> [flags]`
Bulk-rename files/folders under `path` by find/replace on their names.
Flags: `--recursive`/`-r`, `--regex`, `--files-only`, `--folders-only`,
`--ignore-case`/`-i`, `--dry-run`.

```bash
stk file rename "2024" "2025" ./mod -r --dry-run
```

### `stk file delete <path> [--pattern <glob>] [flags]`
Delete files (and optionally empty dirs) by path or glob.
Flags: `--pattern <glob>` (when `path` is a directory), `--recursive`/`-r`,
`--force`/`-f` (clear read-only), `--empty-dirs` (prune empties),
`--dry-run`.

```bash
stk file delete ./mod --pattern "*.tmp" -r --dry-run
```

### `stk file replace <find> <replace> <path> [flags]`
Find/replace **text inside file contents**. Flags: `--recursive`/`-r`,
`--pattern <glob>`, `--regex` (enables `$1`/`${name}` backrefs),
`--ignore-case`/`-i`, `--whole-word`/`-w`, `--dry-run`,
`--encoding <enc>` (`auto` default; `utf8`, `utf8-bom`, `utf16le`,
`utf16be`, `ascii`, `latin1`).

### `stk file hex-edit <find> <replace> <path> [flags]`
Find/replace **bytes** inside file contents. Flags: `--hex` (treat
find/replace as hex pairs like `DEADBEEF`), `--encoding` (`utf8`/`utf16le`/
`ascii` when not `--hex`), `--recursive`/`-r`, `--pattern <glob>`,
`--resize` (allow different-length replacement — shifts following bytes),
`--dry-run`. Without `--resize`, find and replace must be the same length.

```bash
stk file hex-edit "DE AD BE EF" "00 00 00 00" file.bin --hex --dry-run
```

---

## 5. `stk variable` & `stk run` — scripting

### Variables
```
stk variable define <name> <value>     # name matches [A-Za-z_][A-Za-z0-9_]*
stk variable get <name>
stk variable list
stk variable delete <name>
```
Once defined, **`{name}`** in any later command's arguments is expanded
before parsing. (Expansion is skipped for the `variable` command itself so
literal `{x}` values can be stored.) Stored under `%APPDATA%/erp-handler/config.json`.

```bash
stk variable define MOD "C:\mods\myteam"
stk erp list "{MOD}\car.erp"
```

### Scripts
```
stk run <script.txt> [--stop-on-error]
```
Runs one `stk` command per line. Blank lines and `#` comments are ignored.
Each line runs to completion before the next. By default a failing line is
reported and the run continues; `--stop-on-error` aborts on the first
failure. Variable `{name}` references are expanded per line.

```
# update.txt
variable define ENG language_eng.lng
lng merge {ENG} update_eng.lng --overwrite
erp list car.erp --type GfxSRVResource
```
```bash
stk run update.txt --stop-on-error
```

---

## 6. Integration notes for automation

- **Detect success via the exit code, not stdout text.** Summary wording may
  change; the exit code contract (`0` = ok) is stable.
- **Read commands write data to stdout, diagnostics to stderr.** `lng get`
  and `lng list` are designed to be captured/piped. Redirect stderr
  separately if you only want the data.
- **Always work on a copy for risky edits**, or use `--output` (lng / erp
  merge) so the original stays intact. Resource/fragment/texture edits write
  back in place.
- **Preview with `--dry-run`** for `stk file` verbs before applying.
- **Batch with `stk run`** when you have a fixed sequence; it keeps one
  process invocation and shares variable state across lines.
- **Quoting:** identifiers and values frequently contain `/`, `?`, spaces, or
  `eaid://`. Quote them at the calling-shell level. `?` in a resource
  filename is written as `%3F` in extracted file names.

---

## 7. Dependency: `texconv.exe` (textures only)

The `stk erp textures … --png` path shells out to Microsoft's
**`texconv.exe`** (DirectXTex, MIT). It is resolved in this order:
`tools/texconv.exe` next to `stk.exe` → `TEXCONV` env var → `texconv` on
PATH. DDS export/import needs nothing extra. If none resolve, PNG conversion
commands fail with a clear message. All other commands have no external
dependencies.
