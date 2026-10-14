using System.Globalization;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// Decodes a game texture at the best resolution the install has: the
    /// streamed sidecars' top mip first, then the low-resolution copy in the
    /// ERP itself. BC5 textures are decoded raw rather than through the
    /// preview cache's grayscale-R path, which flattens surface (_s) and
    /// mask (_m) values. The FBX writer's PNGs and the DX showroom's normal
    /// and surface maps both come from here.
    /// </summary>
    internal static class TextureDecodeLadder
    {
        /// <summary>
        /// A colour, surface or mask texture as BGRA, or null. <paramref name="sourceLabel"/>
        /// says which source won, for the export's diagnostics.
        /// </summary>
        public static DecodedTexture Best(string textureEaid, TextureCache cache, Action<string> warn, out string sourceLabel)
        {
            DecodedTexture decoded = null;
            sourceLabel = null;

            // Step 1: streamed driver-atlas / team-textures sidecar.
            try
            {
                var hi = MipmapsSidecarLoader.TryLoadTopMip(
                    textureEaid, cache.PackageRoot, _ => { });
                if (hi != null && hi.Bgra != null && hi.Bgra.Length > 0 &&
                    hi.Width > 0 && hi.Height > 0)
                {
                    decoded = hi;
                    sourceLabel = "MipmapsSidecarLoader (" +
                                  (hi.FormatName ?? "BC?") + ", " +
                                  hi.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                  hi.Height.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }
            catch (Exception ex)
            {
                warn(
                    "MipmapsSidecarLoader threw for '" + textureEaid +
                    "': " + ex.Message + " -- trying next source");
            }

            // Step 2: shared/generics flavour of the sidecar.
            if (decoded == null)
            {
                try
                {
                    var hi = GenericMipmapsSidecarLoader.TryLoadTopMip(
                        textureEaid, cache.PackageRoot, _ => { });
                    if (hi != null && hi.Bgra != null && hi.Bgra.Length > 0 &&
                        hi.Width > 0 && hi.Height > 0)
                    {
                        decoded = hi;
                        sourceLabel = "GenericMipmapsSidecarLoader (" +
                                      (hi.FormatName ?? "BC?") + ", " +
                                      hi.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                      hi.Height.ToString(CultureInfo.InvariantCulture) + ")";
                    }
                }
                catch (Exception ex)
                {
                    warn(
                        "GenericMipmapsSidecarLoader threw for '" + textureEaid +
                        "': " + ex.Message + " -- falling back to ERP tail");
                }
            }

            // Step 2.5: direct BC5 raw decode from the temp001.image
            // payload. The general cache path (cache.GetPixelsForTexture,
            // step 3) routes BC5 through DecodeBC5AsGrayscaleR -- a
            // decoder tuned for tyre-tread previews that clamps the R
            // channel into a narrow 14..27 band to look like rubber.
            // That's catastrophic for character surface (_s) and mask
            // (_m) textures where the actual roughness / opacity
            // values matter (the helmet_s on Hulkenberg came out as
            // flat dark grey). Decode raw here ourselves before
            // falling through to the cache.
            if (decoded == null)
            {
                try
                {
                    var (_, metaPath, payloadPath) =
                        EaidPathResolver.ResolveTexturePaths(textureEaid, cache.PackageRoot);
                    if (metaPath != null && File.Exists(metaPath) &&
                        payloadPath != null && File.Exists(payloadPath))
                    {
                        var desc = ImageDescriptor.FromFile(metaPath);
                        if (desc.IsBC5 && desc.Width > 0 && desc.Height > 0)
                        {
                            byte[] payload = File.ReadAllBytes(payloadPath);
                            if (payload.Length > 0)
                            {
                                byte[] bgra = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC5AsRawR(
                                    payload, desc.Width, desc.Height);
                                if (bgra != null && bgra.Length > 0)
                                {
                                    decoded = new DecodedTexture
                                    {
                                        Bgra = bgra,
                                        Width = desc.Width,
                                        Height = desc.Height,
                                        FormatName = "BC5/raw-R",
                                    };
                                    sourceLabel = $"ERP tail (BC5/raw-R, " +
                                                  $"{desc.Width}x{desc.Height})";
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    warn(
                        $"BC5 raw decode threw for '{textureEaid}': {ex.Message} " +
                        "-- falling back to cache");
                }
            }

            // Step 3: ERP-embedded tail mip (legacy behaviour, used
            // for non-BC5 textures).
            if (decoded == null)
            {
                var lo = cache.GetPixelsForTexture(textureEaid);
                if (lo != null && lo.Bgra != null && lo.Bgra.Length > 0 &&
                    lo.Width > 0 && lo.Height > 0)
                {
                    decoded = lo;
                    sourceLabel = "ERP tail (" +
                                  (lo.FormatName ?? "BC?") + ", " +
                                  lo.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                  lo.Height.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }

            return decoded;
        }

        /// <summary>
        /// A tangent-space normal map as BGRA (R = X, G = Y, B = Z), or null
        /// when the texture isn't BC5 or can't be found.
        /// </summary>
        public static DecodedTexture Normal(string textureEaid, TextureCache cache, Action<string> warn, out string sourceLabel)
        {
            DecodedTexture decoded = null;
            sourceLabel = null;

            try
            {
                var hi = GenericMipmapsSidecarLoader.TryLoadTopMip(
                    textureEaid, cache.PackageRoot, _ => { },
                    GenericMipmapsSidecarLoader.BC5DecodeMode.NormalMap);
                if (hi != null && hi.Bgra != null && hi.Bgra.Length > 0 &&
                    hi.Width > 0 && hi.Height > 0)
                {
                    decoded = hi;
                    sourceLabel = $"hi-res sidecar ({hi.Width}x{hi.Height})";
                }
            }
            catch (Exception ex)
            {
                warn(
                    $"Normal-map sidecar lookup threw for '{textureEaid}': {ex.Message} " +
                    "-- falling back to ERP tail");
            }

            if (decoded == null)
            {
                // ERP-tail fallback: re-decode the temp001.image payload
                // ourselves to avoid the cache's grayscale-R preview
                // decoder.
                var (_, metaPath, payloadPath) =
                    EaidPathResolver.ResolveTexturePaths(textureEaid, cache.PackageRoot);
                if (metaPath == null || !File.Exists(metaPath) ||
                    payloadPath == null || !File.Exists(payloadPath))
                    return null;

                ImageDescriptor desc;
                try { desc = ImageDescriptor.FromFile(metaPath); }
                catch { return null; }
                if (!desc.IsBC5) return null;
                if (desc.Width <= 0 || desc.Height <= 0) return null;

                byte[] payload = File.ReadAllBytes(payloadPath);
                if (payload.Length == 0) return null;

                byte[] bgraTail = EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC5AsNormalMap(
                    payload, desc.Width, desc.Height);
                if (bgraTail == null || bgraTail.Length == 0) return null;

                decoded = new DecodedTexture
                {
                    Bgra = bgraTail,
                    Width = desc.Width,
                    Height = desc.Height,
                    FormatName = "BC5",
                };
                sourceLabel = $"ERP tail ({desc.Width}x{desc.Height})";
            }

            return decoded;
        }
    }
}
