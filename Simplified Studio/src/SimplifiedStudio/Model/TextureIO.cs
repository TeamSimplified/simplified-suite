using System.IO;
using EgoEngineLibrary.Archive.Erp.Data;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using SimplifiedStudio.Core.Texconv;

namespace SimplifiedStudio.Model;

/// <summary>
/// Texture export / import via texconv. All external-mipmap I/O is routed
/// through an optional <see cref="PendingMips"/> store so the actual .mipmaps
/// file isn't touched until the user saves the ERP.
///
/// This file is kept byte-identical in Simplified Studio and Simplified ERP, so
/// anything added here reaches both whether or not both have a use for it.
/// Stripping mipmaps is ERP-only and lives in MipmapStripper there for that
/// reason.
/// </summary>
public static class TextureIO
{
    public sealed record ExportResult(uint Width, uint Height, bool UsedFullRes);
    /// <param name="ResizedFrom">
    /// The size the file arrived at, when it had to be resized to fit a tex
    /// array slice. Null when nothing was resized. Reported rather than done
    /// quietly: the image that lands is not the one the user saved.
    /// </param>
    public sealed record ImportResult(uint Width, uint Height, string? MipMapsWrittenTo, bool MipMapsPending,
        (uint Width, uint Height)? ResizedFrom = null);

    /// <summary>
    /// Decode the texture to PNG. Uses external mipmaps if reachable
    /// (in <see cref="PendingMips"/> first, then on disk).
    /// </summary>
    public static ExportResult ExportPng(TextureVm vm, string pngPath, TexconvRunner runner,
        string? gameDir, uint texArrayIndex = 0, PendingMips? pendingMips = null)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "simplified-studio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mipPath = vm.ResolveExternalMipPath(gameDir);

            Stream? mipStream = null;
            if (vm.HasExternalMips && vm.HasValidExternalMips && !string.IsNullOrEmpty(mipPath))
            {
                if (pendingMips is not null && pendingMips.HasPending(mipPath))
                    mipStream = pendingMips.OpenForRead(mipPath);
                else if (File.Exists(mipPath))
                    mipStream = File.OpenRead(mipPath);
            }
            var useFullRes = mipStream is not null;

            DdsFile dds;
            using (mipStream)
            {
                dds = vm.Srv.ToDdsFile(mipStream, exportTexArray: false, texArrayIndex: texArrayIndex);
            }

            var safeBase = vm.FileName.Replace("?", "%3F");
            var tmpDds = Path.Combine(tmpDir, safeBase + ".dds");
            using (var fs = File.Open(tmpDds, FileMode.Create, FileAccess.Write, FileShare.Read))
                dds.Write(fs, -1);

            runner.DdsToPng(tmpDds, tmpDir, isSrgb: TexconvRunner.IsSrgbFormat(vm.ImageType));
            var producedPng = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(tmpDds) + ".png");
            if (!File.Exists(producedPng))
                throw new FileNotFoundException("texconv didn't produce the expected PNG", producedPng);

            var dstDir = Path.GetDirectoryName(pngPath);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
            File.Copy(producedPng, pngPath, overwrite: true);

            return new ExportResult(dds.header.width, dds.header.height, useFullRes);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Decode the texture to DDS and write it straight to <paramref name="ddsPath"/>.
    /// Same external-mipmap resolution as <see cref="ExportPng"/>; skips the
    /// texconv DDS→PNG step entirely.
    /// </summary>
    public static ExportResult ExportDds(TextureVm vm, string ddsPath,
        string? gameDir, uint texArrayIndex = 0, PendingMips? pendingMips = null)
    {
        var mipPath = vm.ResolveExternalMipPath(gameDir);

        Stream? mipStream = null;
        if (vm.HasExternalMips && vm.HasValidExternalMips && !string.IsNullOrEmpty(mipPath))
        {
            if (pendingMips is not null && pendingMips.HasPending(mipPath))
                mipStream = pendingMips.OpenForRead(mipPath);
            else if (File.Exists(mipPath))
                mipStream = File.OpenRead(mipPath);
        }
        var useFullRes = mipStream is not null;

        DdsFile dds;
        using (mipStream)
        {
            dds = vm.Srv.ToDdsFile(mipStream, exportTexArray: false, texArrayIndex: texArrayIndex);
        }

        var dstDir = Path.GetDirectoryName(ddsPath);
        if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
        using (var fs = File.Open(ddsPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            dds.Write(fs, -1);

        return new ExportResult(dds.header.width, dds.header.height, useFullRes);
    }

    /// <summary>
    /// Import a DDS file directly into the texture, bypassing texconv entirely.
    /// Caller is responsible for picking a DDS whose pixel format matches the
    /// texture's existing format — there's no re-encode step.
    ///
    /// A slice of a tex array is stricter still: with no re-encode there is
    /// nothing to resize with, so the file has to match the array's size,
    /// format and mip count outright. It is told exactly what does not match.
    /// </summary>
    public static ImportResult ImportDds(TextureVm vm, string ddsPath,
        string? gameDir, uint texArrayIndex = 0, PendingMips? pendingMips = null)
    {
        using var fs = File.Open(ddsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var dds = new DdsFile(fs);
        uint w = dds.header.width;
        uint h = dds.header.height;

        string? mipsTargetPath = null;
        bool pending = false;
        Stream? mipStream = null;

        if (vm.HasExternalMips)
        {
            var inGame = vm.ResolveExternalMipPath(gameDir);
            mipsTargetPath = !string.IsNullOrEmpty(inGame)
                ? inGame
                : Path.Combine(
                    Path.GetDirectoryName(ddsPath) ?? Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(ddsPath) + ".mipmaps");

            if (pendingMips is not null)
            {
                mipStream = new MemoryStream();
                pending = true;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(mipsTargetPath)!);
                mipStream = File.Open(mipsTargetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
        }

        using (mipStream)
        {
            dds.ToErpGfxSRVResource(vm.Srv, mipStream!, importTexArray: false, texArrayIndex: texArrayIndex);
            if (pending && mipStream is MemoryStream ms)
                pendingMips!.Set(mipsTargetPath!, ms.ToArray());
        }

        vm.Srv.ToResource(vm.Resource);
        return new ImportResult(w, h, mipsTargetPath, pending);
    }

    /// <summary>
    /// Import a PNG, encoded to DDS using the texture's existing DXGI format,
    /// and write it back into the ERP resource.
    ///
    /// When <paramref name="pendingMips"/> is provided, the new external
    /// <c>.mipmaps</c> bytes are stored in memory keyed by destination path
    /// instead of being written immediately. The destination on disk is left
    /// untouched until the caller flushes the store (typically on ERP save).
    /// </summary>
    public static ImportResult ImportPng(TextureVm vm, string pngPath, TexconvRunner runner,
        string? gameDir, uint texArrayIndex = 0, PendingMips? pendingMips = null)
    {
        if (!TexconvRunner.IsSupportedForPngConversion(vm.ImageType))
            throw new NotSupportedException(
                $"Importing into {vm.ImageType} via PNG is not supported in this version (BC6H/HDR).");

        var tmpDir = Path.Combine(Path.GetTempPath(), "simplified-studio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var mipCount = vm.HasExternalMips
                ? (int)(vm.MipMapCount + vm.Srv.SurfaceRes.Frag2.Mips.Count)
                : (int)vm.MipMapCount;
            mipCount = Math.Max(mipCount, 1);

            // A tex array slice occupies a fixed stretch of the resource, so
            // the encoded DDS has to come out at the array's exact size. Since
            // this route re-encodes anyway, it is told to land there rather
            // than the import being refused for a canvas that grew. An
            // ordinary texture may change size freely — its whole payload is
            // replaced and the header follows — so it is left alone.
            var slice = vm.ArraySize > 1;
            var ddsPath = slice
                ? runner.PngToDds(pngPath, tmpDir, vm.ImageType, mipCount, width: vm.Width, height: vm.Height)
                : runner.PngToDds(pngPath, tmpDir, vm.ImageType, mipCount);

            (uint, uint)? resizedFrom = null;
            if (slice)
            {
                var arrived = ReadPngSize(pngPath);
                if (arrived is { } size && (size.Width != vm.Width || size.Height != vm.Height))
                    resizedFrom = size;
            }

            using var fs = File.Open(ddsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dds = new DdsFile(fs);
            uint w = dds.header.width;
            uint h = dds.header.height;

            // Decide where the new .mipmaps would live, and whether to write
            // it now or stash it for the next ERP save.
            string? mipsTargetPath = null;
            bool pending = false;
            Stream? mipStream = null;

            if (vm.HasExternalMips)
            {
                var inGame = vm.ResolveExternalMipPath(gameDir);
                mipsTargetPath = !string.IsNullOrEmpty(inGame)
                    ? inGame
                    : Path.Combine(
                        Path.GetDirectoryName(pngPath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(pngPath) + ".mipmaps");

                if (pendingMips is not null)
                {
                    mipStream = new MemoryStream();
                    pending = true;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(mipsTargetPath)!);
                    mipStream = File.Open(mipsTargetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                }
            }

            using (mipStream)
            {
                dds.ToErpGfxSRVResource(vm.Srv, mipStream!, importTexArray: false, texArrayIndex: texArrayIndex);
                if (pending && mipStream is MemoryStream ms)
                    pendingMips!.Set(mipsTargetPath!, ms.ToArray());
            }

            vm.Srv.ToResource(vm.Resource);
            return new ImportResult(w, h, mipsTargetPath, pending, resizedFrom);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The pixel size in a PNG's IHDR, read from the first 24 bytes so the
    /// image never has to be decoded just to say whether it was resized.
    /// Null if the file is not a PNG or is unreadable — in which case nothing
    /// is claimed about it.
    /// </summary>
    private static (uint Width, uint Height)? ReadPngSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;

            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            if (!head[..8].SequenceEqual(signature)) return null;

            return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[16..20]),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head[20..24]));
        }
        catch
        {
            return null;
        }
    }
}
