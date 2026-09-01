using System.Diagnostics;
using System.Reflection;
using EgoEngineLibrary.Archive.Erp.Data;

namespace EgoEngineLibrary.Texconv;

/// <summary>
/// Thin wrapper around Microsoft DirectXTex texconv.exe for DDS &lt;-&gt; PNG conversion.
/// Resolution order for the binary:
///   1. <see cref="TexconvPath"/> set explicitly
///   2. <c>TEXCONV</c> environment variable
///   3. <c>runtime/texconv.exe</c> or <c>tools/texconv.exe</c> beside the app
///   4. <c>texconv</c> on PATH
/// </summary>
public sealed class TexconvRunner
{
    public string? TexconvPath { get; init; }

    private static readonly Dictionary<ErpGfxSurfaceFormat, string> SurfaceToDxgi = new()
    {
        [ErpGfxSurfaceFormat.ABGR8]     = "R8G8B8A8_UNORM",
        [ErpGfxSurfaceFormat.DXT1]      = "BC1_UNORM",
        [ErpGfxSurfaceFormat.DXT1_SRGB] = "BC1_UNORM_SRGB",
        [ErpGfxSurfaceFormat.DXT5]      = "BC3_UNORM",
        [ErpGfxSurfaceFormat.DXT5_SRGB] = "BC3_UNORM_SRGB",
        [ErpGfxSurfaceFormat.BC2_SRGB]  = "BC2_UNORM_SRGB",
        [ErpGfxSurfaceFormat.ATI1]      = "BC4_UNORM",
        [ErpGfxSurfaceFormat.ATI2]      = "BC5_UNORM",
        [ErpGfxSurfaceFormat.BC7]       = "BC7_UNORM",
        [ErpGfxSurfaceFormat.BC7_SRGB]  = "BC7_UNORM_SRGB",
        // BC6 (HDR) intentionally omitted — refused in v1.
    };

    public static bool IsSupportedForPngConversion(ErpGfxSurfaceFormat fmt)
        => SurfaceToDxgi.ContainsKey(fmt);

    public static string GetDxgiFormat(ErpGfxSurfaceFormat fmt)
        => SurfaceToDxgi.TryGetValue(fmt, out var d)
            ? d
            : throw new NotSupportedException($"Surface format {fmt} cannot be converted to PNG (BC6H/HDR is unsupported in v1).");

    public string Resolve()
    {
        if (!string.IsNullOrEmpty(TexconvPath) && File.Exists(TexconvPath))
            return TexconvPath;

        var env = Environment.GetEnvironmentVariable("TEXCONV");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        // AppContext.BaseDirectory rather than Assembly.Location: the latter is
        // "" in a single-file build (IL3000), which is every packaged app here.
        //
        // Two folders because the packagings differ and both are right: the
        // windowed apps keep everything the user should not touch in runtime\,
        // the Toolkit ships a tools\ folder beside the exe.
        foreach (var folder in new[] { "runtime", "tools" })
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, folder, "texconv.exe");
            if (File.Exists(bundled))
                return bundled;
        }

        return "texconv";
    }

    /// <summary>True when the surface format carries the sRGB gamma curve.</summary>
    public static bool IsSrgbFormat(ErpGfxSurfaceFormat fmt) => fmt switch
    {
        ErpGfxSurfaceFormat.DXT1_SRGB or
        ErpGfxSurfaceFormat.DXT5_SRGB or
        ErpGfxSurfaceFormat.BC2_SRGB  or
        ErpGfxSurfaceFormat.BC7_SRGB => true,
        _ => false,
    };

    /// <summary>
    /// Convert a DDS file to PNG, written into <paramref name="outDir"/>.
    /// Forces an R8G8B8A8(_SRGB) intermediate so 1-/2-channel BCn formats
    /// (BC4/BC5 normal maps) can be written — PNG cannot store 2-channel
    /// pixels directly. When <paramref name="isSrgb"/> is true the output uses
    /// <c>R8G8B8A8_UNORM_SRGB</c> so PNG viewers don't re-apply the gamma
    /// curve and render the texture too dark.
    /// </summary>
    public void DdsToPng(string ddsPath, string outDir, bool overwrite = true, bool isSrgb = false)
    {
        Directory.CreateDirectory(outDir);
        var fmt = isSrgb ? "R8G8B8A8_UNORM_SRGB" : "R8G8B8A8_UNORM";
        var args = new List<string> { "-ft", "png", "-f", fmt, "-o", outDir };
        if (overwrite) args.Add("-y");
        args.Add(ddsPath);
        Run(args);
    }

    /// <summary>
    /// Convert a PNG to DDS targeting <paramref name="targetFormat"/>, optionally regenerating
    /// a mip chain of <paramref name="mipCount"/> levels (0 = no mip generation).
    /// The DDS lands in <paramref name="outDir"/> as <c>&lt;basename&gt;.dds</c>.
    ///
    /// <paramref name="width"/> and <paramref name="height"/> force the output
    /// size. Only callers that have to hit an exact byte count pass them — a
    /// tex array slice sits in a fixed stretch of its resource, so a PNG at any
    /// other size cannot go in, and resizing it beats refusing it. An ordinary
    /// texture is free to change size and passes nothing.
    /// </summary>
    public string PngToDds(string pngPath, string outDir, ErpGfxSurfaceFormat targetFormat, int mipCount = 0, bool overwrite = true,
        uint width = 0, uint height = 0)
    {
        Directory.CreateDirectory(outDir);
        var dxgi = GetDxgiFormat(targetFormat);
        var args = new List<string> { "-ft", "dds", "-f", dxgi, "-o", outDir };

        // The PNGs we round-trip are sRGB-encoded. When the target DDS format
        // is also sRGB, tell texconv the input is already sRGB (-srgbi) so it
        // doesn't treat the bytes as linear and re-apply the gamma curve —
        // which makes the imported texture visibly lighter than the source.
        if (IsSrgbFormat(targetFormat)) args.Add("-srgbi");

        if (width > 0 && height > 0) args.AddRange(["-w", width.ToString(), "-h", height.ToString()]);
        if (mipCount > 0) args.AddRange(["-m", mipCount.ToString()]);
        if (overwrite) args.Add("-y");
        args.Add(pngPath);

        try
        {
            Run(args);
        }
        catch (InvalidOperationException ex) when (mipCount > 0 && LooksLikeMipError(ex.Message))
        {
            // The caller asked for a mip count the PNG cannot produce — the
            // image was cropped or resized, and a smaller one has fewer levels
            // in it. Retry once without the explicit -m and let texconv use the
            // most this image can carry, which is what a person resizing their
            // texture expects to happen.
            //
            // A tex array slice is the exception, and needs no special case
            // here: fewer levels means fewer bytes than the slice holds, and
            // the import refuses it by name rather than writing something
            // short.
            var retryArgs = args.ToList();
            var mIdx = retryArgs.IndexOf("-m");
            if (mIdx >= 0 && mIdx + 1 < retryArgs.Count) retryArgs.RemoveRange(mIdx, 2);
            Run(retryArgs);
        }

        var outName = Path.GetFileNameWithoutExtension(pngPath) + ".dds";
        return Path.Combine(outDir, outName);
    }

    private static bool LooksLikeMipError(string msg)
        => msg.Contains("mip", StringComparison.OrdinalIgnoreCase);

    private void Run(IList<string> arguments)
    {
        var exe = Resolve();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"texconv exited with code {proc.ExitCode}." +
                (stderr.Length > 0 ? $"\nstderr: {stderr.Trim()}" : "") +
                (stdout.Length > 0 ? $"\nstdout: {stdout.Trim()}" : ""));
        }
    }
}
