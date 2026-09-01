using System.Text.RegularExpressions;

namespace SimplifiedDriverwearSwapper.Core.Toolkit;

/// <summary>
/// The Toolkit's <c>erp</c> commands, as methods.
///
/// Every ERP operation this app performs goes through here, so the argument
/// shapes are written once and the rest of the app never builds a command line.
/// Commands that don't exist yet get added as the app needs them.
/// </summary>
public static class Erp
{
    /// <summary>One resource as reported by <c>stk erp list</c>.</summary>
    /// <param name="Index">Position in the archive, as printed.</param>
    /// <param name="ResourceType">e.g. <c>GfxSRVResource</c>, <c>VTF</c>.</param>
    /// <param name="FragmentCount">Fragments the resource holds.</param>
    /// <param name="Size">Total bytes, as reported.</param>
    /// <param name="Identifier">Full <c>eaid://</c> identifier.</param>
    public sealed record ErpResource(
        int Index, string ResourceType, int FragmentCount, long Size, string Identifier)
    {
        /// <summary>File name at the end of the identifier, query string and all.</summary>
        public string Name => Identifier[(Identifier.LastIndexOf('/') + 1)..];
    }

    // "#0    [VTF                 ] 2f     18,519b  eaid://.../lotus.vtf"
    private static readonly Regex ListLine = new(
        @"^#(?<index>\d+)\s+\[(?<type>[^\]]+)\]\s+(?<frags>\d+)f\s+(?<size>[\d,]+)b\s+(?<id>\S.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// List an ERP's resources, optionally narrowed by resource type or an
    /// identifier regex — both filters are applied by the Toolkit itself, so
    /// large archives don't have to come back in full.
    /// </summary>
    public static async Task<List<ErpResource>> ListAsync(
        string erpPath, string? type = null, string? filter = null, CancellationToken cancel = default)
    {
        var args = new List<string> { "erp", "list", erpPath };
        if (!string.IsNullOrWhiteSpace(type)) { args.Add("--type"); args.Add(type); }
        if (!string.IsNullOrWhiteSpace(filter)) { args.Add("--filter"); args.Add(filter); }

        var result = await ToolkitRunner.RunAsync(args, cancel: cancel);
        if (!result.Succeeded) throw new ToolkitException(result);

        var resources = new List<ErpResource>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var match = ListLine.Match(line.TrimEnd('\r'));
            if (!match.Success) continue;   // header lines

            resources.Add(new ErpResource(
                int.Parse(match.Groups["index"].Value),
                match.Groups["type"].Value.Trim(),
                int.Parse(match.Groups["frags"].Value),
                long.Parse(match.Groups["size"].Value.Replace(",", "")),
                match.Groups["id"].Value.Trim()));
        }
        return resources;
    }

    /// <summary>Extract every fragment of every resource to disk.</summary>
    public static Task<ToolkitRunner.ToolkitResult> ExtractAsync(
        string erpPath, string outDir, Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "extract", erpPath, outDir }, onOutput, cancel);

    /// <summary>Re-import fragments from disk and write the ERP back.</summary>
    public static Task<ToolkitRunner.ToolkitResult> RepackAsync(
        string erpPath, string inDir, Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "repack", erpPath, inDir }, onOutput, cancel);

    /// <summary>Merge resources from one or more source ERPs into the target.</summary>
    public static Task<ToolkitRunner.ToolkitResult> MergeAsync(
        string targetErp, IEnumerable<string> sourceErps,
        Action<string>? onOutput = null, CancellationToken cancel = default)
    {
        var args = new List<string> { "erp", "merge", targetErp };
        args.AddRange(sourceErps);
        return ToolkitRunner.RunAsync(args, onOutput, cancel);
    }

    /// <summary>Substring find/replace across every resource identifier.</summary>
    public static Task<ToolkitRunner.ToolkitResult> RenameAllAsync(
        string erpPath, string find, string replace,
        Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "resource", "rename-all", erpPath, find, replace }, onOutput, cancel);

    /// <summary>Delete a resource.</summary>
    public static Task<ToolkitRunner.ToolkitResult> RemoveResourceAsync(
        string erpPath, string identifier, Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "resource", "remove", erpPath, identifier }, onOutput, cancel);

    /// <summary>Deep-copy a resource, cloning every fragment.</summary>
    public static Task<ToolkitRunner.ToolkitResult> DuplicateResourceAsync(
        string erpPath, string identifier, Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "resource", "duplicate", erpPath, identifier }, onOutput, cancel);

    /// <summary>Export textures as DDS, or PNG when <paramref name="png"/> is set.</summary>
    public static Task<ToolkitRunner.ToolkitResult> ExportTexturesAsync(
        string erpPath, string outDir, bool png = false,
        Action<string>? onOutput = null, CancellationToken cancel = default)
    {
        var args = new List<string> { "erp", "textures", "export", erpPath, outDir };
        if (png) args.Add("--png");
        return ToolkitRunner.RunAsync(args, onOutput, cancel);
    }

    /// <summary>Re-import textures, rebuilding fragments and external mipmaps.</summary>
    public static Task<ToolkitRunner.ToolkitResult> ImportTexturesAsync(
        string erpPath, string inDir, bool png = false,
        Action<string>? onOutput = null, CancellationToken cancel = default)
    {
        var args = new List<string> { "erp", "textures", "import", erpPath, inDir };
        if (png) args.Add("--png");
        return ToolkitRunner.RunAsync(args, onOutput, cancel);
    }

    /// <summary>Decode PKG fragments to JSON files.</summary>
    public static Task<ToolkitRunner.ToolkitResult> ExportPkgAsync(
        string erpPath, string outDir, Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "pkg", "export", erpPath, outDir }, onOutput, cancel);

    /// <summary>Re-encode JSON files into matching PKG fragments.</summary>
    public static Task<ToolkitRunner.ToolkitResult> ImportPkgAsync(
        string erpPath, string inDir, Action<string>? onOutput = null, CancellationToken cancel = default) =>
        ToolkitRunner.RunAsync(new[] { "erp", "pkg", "import", erpPath, inDir }, onOutput, cancel);
}

/// <summary>A Toolkit command that failed, carrying what it printed.</summary>
public sealed class ToolkitException : Exception
{
    public ToolkitException(ToolkitRunner.ToolkitResult result)
        : base(result.ErrorMessage)
    {
        Result = result;
    }

    public ToolkitRunner.ToolkitResult Result { get; }
}
