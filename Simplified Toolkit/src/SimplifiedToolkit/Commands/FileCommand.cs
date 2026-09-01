using System.CommandLine;
using System.Text;
using System.Text.RegularExpressions;

namespace SimplifiedToolkit.Cli.Commands;

/// <summary>
/// File-system / file-content utility commands grouped under <c>stk file ...</c>:
///   rename    — bulk find/replace in file/folder names (PowerRename-style)
///   delete    — delete files/folders by path or glob
///   replace   — text find/replace inside file contents (Notepad++ style)
///   hex-edit  — byte-level find/replace inside file contents
/// All mutating verbs support --dry-run.
/// </summary>
internal static class FileCommand
{
    public static Command Build()
    {
        return new Command("file", "File utilities: bulk rename, delete, make folder, text replace, hex edit")
        {
            BuildFolder(),
            BuildRename(),
            BuildDelete(),
            BuildReplace(),
            BuildHexEdit(),
        };
    }

    // ───────────────────────────────────────────────────── folder ──────────

    private static Command BuildFolder()
    {
        var pathArg     = new Argument<string[]>("paths") { Description = "One or more folders to create" };
        var noParents   = new Option<bool>("--no-parents") { Description = "Fail if parent directories don't exist (default: create them)" };
        var failExists  = new Option<bool>("--fail-if-exists") { Description = "Fail if the folder already exists (default: silently succeed)" };

        var cmd = new Command("folder", "Create one or more folders (creates parent directories by default)")
        {
            pathArg, noParents, failExists
        };
        cmd.SetAction(p =>
        {
            var paths = p.GetValue(pathArg) ?? [];
            var requireParents = !p.GetValue(noParents);
            var failExists_ = p.GetValue(failExists);
            int created = 0, skip = 0, fail = 0;

            foreach (var path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        if (failExists_)
                        {
                            Console.Error.WriteLine($"  exists: {path}");
                            fail++;
                            continue;
                        }
                        Console.WriteLine($"  exists: {path}");
                        skip++;
                        continue;
                    }
                    if (!requireParents)
                    {
                        var parent = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        {
                            Console.Error.WriteLine($"  FAIL: parent missing for {path}");
                            fail++;
                            continue;
                        }
                    }
                    Directory.CreateDirectory(path);
                    Console.WriteLine($"  created: {path}");
                    created++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  FAIL: {path} — {ex.Message}");
                    fail++;
                }
            }

            Console.WriteLine($"\n{created} created, {skip} already existed, {fail} failed");
            return fail == 0 ? 0 : 1;
        });
        return cmd;
    }

    // ───────────────────────────────────────────────────── rename ──────────

    private static Command BuildRename()
    {
        var findArg    = new Argument<string>("find") { Description = "Substring or regex to find" };
        var replArg    = new Argument<string>("replace") { Description = "Replacement string" };
        var pathArg    = new Argument<string>("path") { Description = "Directory to scan" };
        var recursive  = new Option<bool>("--recursive", "-r") { Description = "Recurse into subdirectories" };
        var regexOpt   = new Option<bool>("--regex") { Description = "Treat <find> as a regex (default: literal substring)" };
        var filesOnly  = new Option<bool>("--files-only") { Description = "Only rename files" };
        var dirsOnly   = new Option<bool>("--folders-only") { Description = "Only rename folders" };
        var dryRun     = new Option<bool>("--dry-run") { Description = "Show planned renames without applying" };
        var caseInsens = new Option<bool>("--ignore-case", "-i") { Description = "Case-insensitive match" };

        var cmd = new Command("rename", "Bulk rename files/folders by find/replace")
        {
            findArg, replArg, pathArg, recursive, regexOpt, filesOnly, dirsOnly, dryRun, caseInsens
        };
        cmd.SetAction(p =>
        {
            var find = p.GetValue(findArg)!;
            var repl = p.GetValue(replArg)!;
            var root = p.GetValue(pathArg)!;
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"Directory not found: {root}");
                return 1;
            }
            var rec = p.GetValue(recursive);
            var useRegex = p.GetValue(regexOpt);
            var onlyFiles = p.GetValue(filesOnly);
            var onlyDirs = p.GetValue(dirsOnly);
            var dry = p.GetValue(dryRun);
            var ci = p.GetValue(caseInsens);

            if (onlyFiles && onlyDirs)
            {
                Console.Error.WriteLine("--files-only and --folders-only are mutually exclusive.");
                return 1;
            }

            Regex? rx = null;
            if (useRegex)
                rx = new Regex(find, ci ? RegexOptions.IgnoreCase : RegexOptions.None);
            var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            int renamed = 0, skipped = 0, fail = 0;
            var opts = new EnumerationOptions { RecurseSubdirectories = rec };

            if (!onlyDirs)
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", opts))
                {
                    if (TryRenameOne(file, isDir: false, find, repl, rx, cmp, dry, out var msg))
                        renamed++;
                    else if (msg is null) skipped++;
                    else { fail++; Console.Error.WriteLine(msg); }
                }
            }
            if (!onlyFiles)
            {
                var dirs = Directory.EnumerateDirectories(root, "*", opts)
                    .OrderByDescending(d => d.Length)
                    .ToList();
                foreach (var dir in dirs)
                {
                    if (TryRenameOne(dir, isDir: true, find, repl, rx, cmp, dry, out var msg))
                        renamed++;
                    else if (msg is null) skipped++;
                    else { fail++; Console.Error.WriteLine(msg); }
                }
            }

            Console.WriteLine($"\n{(dry ? "DRY-RUN: " : "")}{renamed} renamed, {skipped} unchanged, {fail} failed");
            return fail == 0 ? 0 : 1;
        });
        return cmd;
    }

    private static bool TryRenameOne(string fullPath, bool isDir,
        string find, string repl, Regex? rx, StringComparison cmp, bool dry,
        out string? errorMessage)
    {
        errorMessage = null;
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        var newName = rx is not null
            ? rx.Replace(name, repl)
            : ReplaceCase(name, find, repl, cmp);

        if (string.Equals(name, newName, StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(newName))
        {
            errorMessage = $"  skip (empty result): {fullPath}";
            return false;
        }

        var parent = Path.GetDirectoryName(fullPath)!;
        var dest = Path.Combine(parent, newName);

        if (File.Exists(dest) || Directory.Exists(dest))
        {
            errorMessage = $"  collision: {fullPath}  →  {dest}";
            return false;
        }

        Console.WriteLine($"  {(isDir ? "[D]" : "[F]")} {name}  →  {newName}    ({parent})");
        if (!dry)
        {
            try
            {
                if (isDir) Directory.Move(fullPath, dest);
                else File.Move(fullPath, dest);
            }
            catch (Exception ex)
            {
                errorMessage = $"  FAIL: {fullPath} — {ex.Message}";
                return false;
            }
        }
        return true;
    }

    private static string ReplaceCase(string s, string find, string repl, StringComparison cmp)
    {
        if (find.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            var idx = s.IndexOf(find, i, cmp);
            if (idx < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }
            sb.Append(s, i, idx - i);
            sb.Append(repl);
            i = idx + find.Length;
        }
        return sb.ToString();
    }

    // ───────────────────────────────────────────────────── delete ──────────

    private static Command BuildDelete()
    {
        var pathArg    = new Argument<string>("path") { Description = "File, directory, or directory containing files to match against --pattern" };
        var patternOpt = new Option<string?>("--pattern") { Description = "Glob applied when <path> is a directory (e.g. '*.tmp')" };
        var recursive  = new Option<bool>("--recursive", "-r") { Description = "Recurse into subdirectories when pattern-matching" };
        var dryRun     = new Option<bool>("--dry-run") { Description = "List what would be deleted without deleting" };
        var force      = new Option<bool>("--force", "-f") { Description = "Clear read-only attribute before deletion" };
        var emptyDirs  = new Option<bool>("--empty-dirs") { Description = "Also remove empty directories after file deletion" };

        var cmd = new Command("delete", "Delete files (and optionally empty directories) by path or glob")
        {
            pathArg, patternOpt, recursive, dryRun, force, emptyDirs
        };
        cmd.SetAction(p =>
        {
            var path = p.GetValue(pathArg)!;
            var pattern = p.GetValue(patternOpt);
            var rec = p.GetValue(recursive);
            var dry = p.GetValue(dryRun);
            var force_ = p.GetValue(force);
            var pruneEmpty = p.GetValue(emptyDirs);

            int deleted = 0, fail = 0;

            IEnumerable<string> targets;
            if (File.Exists(path)) targets = [path];
            else if (Directory.Exists(path))
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = rec };
                targets = Directory.EnumerateFiles(path, pattern ?? "*", opts);
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Console.Error.WriteLine($"Path not found: {path}");
                    return 1;
                }
                var opts = new EnumerationOptions { RecurseSubdirectories = rec };
                targets = Directory.EnumerateFiles(dir, name, opts);
            }

            foreach (var file in targets)
            {
                Console.WriteLine($"  delete: {file}");
                if (dry) { deleted++; continue; }
                try
                {
                    if (force_)
                        File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.Error.WriteLine($"  FAIL: {file} — {ex.Message}");
                }
            }

            if (pruneEmpty && Directory.Exists(path))
            {
                var dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length)
                    .ToList();
                foreach (var d in dirs)
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(d).Any())
                        {
                            Console.WriteLine($"  rmdir : {d}");
                            if (!dry) Directory.Delete(d);
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Console.Error.WriteLine($"  FAIL rmdir: {d} — {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"\n{(dry ? "DRY-RUN: " : "")}{deleted} deleted, {fail} failed");
            return fail == 0 ? 0 : 1;
        });
        return cmd;
    }

    // ─────────────────────────────────────────────────── replace (text) ────

    private static Command BuildReplace()
    {
        var findArg    = new Argument<string>("find") { Description = "Text to find (literal by default; --regex for pattern)" };
        var replArg    = new Argument<string>("replace") { Description = "Replacement text. With --regex, $1/$2/${name} backrefs work." };
        var pathArg    = new Argument<string>("path") { Description = "File, directory, or glob" };
        var recursive  = new Option<bool>("--recursive", "-r") { Description = "Recurse into subdirectories" };
        var patternOpt = new Option<string?>("--pattern") { Description = "Glob filter when <path> is a directory (e.g. '*.txt')" };
        var regexOpt   = new Option<bool>("--regex") { Description = "Treat <find> as a regular expression" };
        var ci         = new Option<bool>("--ignore-case", "-i") { Description = "Case-insensitive match" };
        var wholeWord  = new Option<bool>("--whole-word", "-w") { Description = "Match whole word only" };
        var dryRun     = new Option<bool>("--dry-run") { Description = "Report matches without writing files" };
        var encOpt     = new Option<string>("--encoding") { Description = "Force a specific encoding: utf8 (default), utf8-bom, utf16le, utf16be, ascii, latin1. Auto-detects BOM otherwise.", DefaultValueFactory = _ => "auto" };

        var cmd = new Command("replace", "Find/replace text inside file contents (Notepad++ style)")
        {
            findArg, replArg, pathArg, recursive, patternOpt, regexOpt, ci, wholeWord, dryRun, encOpt
        };
        cmd.SetAction(p =>
        {
            var find = p.GetValue(findArg)!;
            var repl = p.GetValue(replArg)!;
            var path = p.GetValue(pathArg)!;
            var rec = p.GetValue(recursive);
            var pattern = p.GetValue(patternOpt);
            var useRegex = p.GetValue(regexOpt);
            var caseInsens = p.GetValue(ci);
            var ww = p.GetValue(wholeWord);
            var dry = p.GetValue(dryRun);
            var encName = (p.GetValue(encOpt) ?? "auto").ToLowerInvariant();

            if (find.Length == 0)
            {
                Console.Error.WriteLine("<find> is empty.");
                return 1;
            }

            string pat = useRegex ? find : Regex.Escape(find);
            if (ww) pat = $@"\b(?:{pat})\b";

            var rxOpts = RegexOptions.Multiline;
            if (caseInsens) rxOpts |= RegexOptions.IgnoreCase;
            var rx = new Regex(pat, rxOpts);

            IEnumerable<string> files;
            try { files = ResolveFileSet(path, pattern, rec); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

            int touched = 0, totalHits = 0, fail = 0;
            foreach (var file in files)
            {
                try
                {
                    var enc = ResolveEncoding(file, encName);
                    var text = File.ReadAllText(file, enc);
                    int hits = 0;
                    var newText = rx.Replace(text, m => { hits++; return useRegex ? m.Result(repl) : repl; });
                    if (hits == 0) continue;
                    touched++;
                    totalHits += hits;
                    Console.WriteLine($"  {hits,5} hit(s)  {file}");
                    if (!dry) File.WriteAllText(file, newText, enc);
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.Error.WriteLine($"  FAIL: {file} — {ex.Message}");
                }
            }

            Console.WriteLine($"\n{(dry ? "DRY-RUN: " : "")}{totalHits} replacements across {touched} file(s), {fail} failed");
            return fail == 0 ? 0 : 1;
        });
        return cmd;
    }

    private static Encoding ResolveEncoding(string file, string encName)
    {
        if (encName != "auto")
        {
            return encName switch
            {
                "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                "utf16" or "utf16le" => Encoding.Unicode,
                "utf16be" => Encoding.BigEndianUnicode,
                "ascii" => Encoding.ASCII,
                "latin1" => Encoding.GetEncoding("ISO-8859-1"),
                _ => throw new ArgumentException($"Unknown encoding: {encName}"),
            };
        }

        // BOM sniff
        Span<byte> head = stackalloc byte[4];
        using var fs = File.OpenRead(file);
        var n = fs.Read(head);
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
        if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    // ─────────────────────────────────────────────────── hex-edit ──────────

    private static Command BuildHexEdit()
    {
        var findArg    = new Argument<string>("find") { Description = "Bytes/string to find" };
        var replArg    = new Argument<string>("replace") { Description = "Replacement bytes/string (must be SAME LENGTH as find unless --resize)" };
        var pathArg    = new Argument<string>("path") { Description = "File, directory, or glob" };
        var hexOpt     = new Option<bool>("--hex") { Description = "Treat <find>/<replace> as hex pairs (e.g. 'DEADBEEF')" };
        var encOpt     = new Option<string>("--encoding") { Description = "Text encoding when not --hex: utf8 (default), utf16le, ascii", DefaultValueFactory = _ => "utf8" };
        var recursive  = new Option<bool>("--recursive", "-r") { Description = "Recurse into subdirectories" };
        var patternOpt = new Option<string?>("--pattern") { Description = "Glob filter when <path> is a directory" };
        var dryRun     = new Option<bool>("--dry-run") { Description = "Report matches but don't rewrite files" };
        var resize     = new Option<bool>("--resize") { Description = "Allow replacement of different length (shifts subsequent bytes)" };

        var cmd = new Command("hex-edit", "Find/replace bytes (or a literal string) inside file contents")
        {
            findArg, replArg, pathArg, hexOpt, encOpt, recursive, patternOpt, dryRun, resize
        };
        cmd.SetAction(p =>
        {
            var hex = p.GetValue(hexOpt);
            var encName = p.GetValue(encOpt) ?? "utf8";
            var enc = encName.ToLowerInvariant() switch
            {
                "utf8" => Encoding.UTF8,
                "utf16" or "utf16le" => Encoding.Unicode,
                "ascii" => Encoding.ASCII,
                _ => throw new ArgumentException($"Unknown encoding: {encName}"),
            };

            byte[] find = hex ? ParseHex(p.GetValue(findArg)!) : enc.GetBytes(p.GetValue(findArg)!);
            byte[] repl = hex ? ParseHex(p.GetValue(replArg)!) : enc.GetBytes(p.GetValue(replArg)!);

            if (find.Length == 0)
            {
                Console.Error.WriteLine("<find> is empty.");
                return 1;
            }

            var allowResize = p.GetValue(resize);
            if (!allowResize && find.Length != repl.Length)
            {
                Console.Error.WriteLine(
                    $"find and replace lengths differ ({find.Length} vs {repl.Length}). " +
                    $"Pass --resize to allow length-changing edits.");
                return 1;
            }

            var path = p.GetValue(pathArg)!;
            var rec = p.GetValue(recursive);
            var dry = p.GetValue(dryRun);
            var pattern = p.GetValue(patternOpt);

            IEnumerable<string> files;
            try { files = ResolveFileSet(path, pattern, rec); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

            int touched = 0, totalHits = 0, fail = 0;
            foreach (var file in files)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    var (newData, hits) = ReplaceAll(data, find, repl);
                    if (hits == 0) continue;
                    touched++;
                    totalHits += hits;
                    Console.WriteLine($"  {hits,5} hit(s)  {file}");
                    if (!dry) File.WriteAllBytes(file, newData);
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.Error.WriteLine($"  FAIL: {file} — {ex.Message}");
                }
            }

            Console.WriteLine($"\n{(dry ? "DRY-RUN: " : "")}{totalHits} replacements across {touched} file(s), {fail} failed");
            return fail == 0 ? 0 : 1;
        });
        return cmd;
    }

    // ───────────────────────────────────────────────────── helpers ─────────

    private static IEnumerable<string> ResolveFileSet(string path, string? pattern, bool recursive)
    {
        if (File.Exists(path)) return [path];
        if (Directory.Exists(path))
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = recursive };
            return Directory.EnumerateFiles(path, pattern ?? "*", opts);
        }
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new FileNotFoundException($"Path not found: {path}");
        var opts2 = new EnumerationOptions { RecurseSubdirectories = recursive };
        return Directory.EnumerateFiles(dir, name, opts2);
    }

    private static byte[] ParseHex(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) clean.Append(c);
        var t = clean.ToString();
        if (t.Length % 2 != 0)
            throw new ArgumentException($"Hex string has odd length: '{s}'");
        var bytes = new byte[t.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(t.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static (byte[] data, int hits) ReplaceAll(byte[] src, byte[] find, byte[] repl)
    {
        if (find.Length == repl.Length)
        {
            int hits = 0;
            byte[]? mutable = null;
            int i = 0;
            while (i <= src.Length - find.Length)
            {
                if (MatchesAt(src, i, find))
                {
                    mutable ??= (byte[])src.Clone();
                    Buffer.BlockCopy(repl, 0, mutable, i, repl.Length);
                    hits++;
                    i += find.Length;
                }
                else i++;
            }
            return (mutable ?? src, hits);
        }

        var output = new List<byte>(src.Length);
        int hitsR = 0;
        int j = 0;
        while (j < src.Length)
        {
            if (j <= src.Length - find.Length && MatchesAt(src, j, find))
            {
                output.AddRange(repl);
                hitsR++;
                j += find.Length;
            }
            else output.Add(src[j++]);
        }
        return (output.ToArray(), hitsR);
    }

    private static bool MatchesAt(byte[] src, int offset, byte[] needle)
    {
        for (int k = 0; k < needle.Length; k++)
            if (src[offset + k] != needle[k]) return false;
        return true;
    }
}
