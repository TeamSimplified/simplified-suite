using System.IO;
using System.Windows.Media.Imaging;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Dds;
using EgoEngineLibrary.Texconv;

namespace SimplifiedStudio.Model;

/// <summary>
/// Decode a texture for the preview pane. Uses external mipmaps when a game
/// directory is configured and the sidecar is available, otherwise falls
/// back to the base image stored in the ERP. The external sidecar lookup
/// goes through <see cref="PendingMips"/> first so freshly-imported
/// textures preview correctly even before the user has saved the ERP.
/// </summary>
public static class TexturePreview
{
    public static (BitmapSource? Image, string? Error, bool UsedFullRes) DecodeBaseImage(
        TextureVm vm, TexconvRunner runner, string? gameDir, uint texArrayIndex = 0,
        PendingMips? pendingMips = null)
    {
        if (!TexconvRunner.IsSupportedForPngConversion(vm.ImageType))
            return (null, $"Preview not supported for {vm.ImageType} (BC6H/HDR).", false);

        var tmpDir = Path.Combine(Path.GetTempPath(), "simplified-studio-preview", Guid.NewGuid().ToString("N"));
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

            var ddsPath = Path.Combine(tmpDir, "preview.dds");
            using (var fs = File.Open(ddsPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                dds.Write(fs, -1);

            runner.DdsToPng(ddsPath, tmpDir, isSrgb: TexconvRunner.IsSrgbFormat(vm.ImageType));
            var pngPath = Path.Combine(tmpDir, "preview.png");
            if (!File.Exists(pngPath))
                return (null, "texconv didn't produce a preview PNG.", useFullRes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(pngPath);
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, null, useFullRes);
        }
        catch (Exception ex)
        {
            return (null, ex.Message, false);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }
}
