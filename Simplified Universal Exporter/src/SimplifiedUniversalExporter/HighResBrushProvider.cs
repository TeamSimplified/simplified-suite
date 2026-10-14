using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// Wraps texture-brush resolution with a mipmap-sidecar lookup so the
    /// WPF preview shows the high-res mip chain from
    /// "&lt;package&gt;\teams\&lt;team&gt;\textures\&lt;texture&gt;_tif.mipmaps"
    /// (the streaming sidecar Codemasters ships next to the ERP-embedded
    /// tail mip) when one is present, falling back to TextureCache's
    /// existing tail-mip decode otherwise.
    ///
    /// Why a separate provider rather than a TextureCache patch: the
    /// TextureCache implementation lives in a referenced assembly without
    /// available source in this codebase. We can't edit it. Instead we
    /// patch at the LOADER call sites (F1ModelLoader / F1ModelLoaderClassic)
    /// by routing brush resolution through this provider, which:
    ///
    ///   1. Tries MipmapsSidecarLoader.TryLoadTopMip first.
    ///   2. On success, decodes the returned BGRA32 buffer into a
    ///      WPF ImageBrush.
    ///   3. On null, hands back the brush the caller would have used
    ///      (the existing TextureCache decode of the tail mip).
    ///
    /// All heavy lifting (sidecar location, LZ4 decode, BC3 unpack,
    /// foreign-year package exclusion, file-name caching) lives in
    /// MipmapsSidecarLoader; this class just wraps the result in an
    /// ImageBrush, caches the brush by EAID, and decides which to return.
    ///
    /// Frozen objects: the BitmapSource and ImageBrush are both Freeze()d
    /// so the brush can be safely shared across the WPF render thread and
    /// any background loader threads, matching the behaviour of brushes
    /// produced by TextureCache itself.
    ///
    /// v12.09 -- TEMPLATE-MIRRORED COORDINATE SPACE
    /// --------------------------------------------
    /// The ImageBrush built here previously used three hardcoded property
    /// settings (Stretch=Fill, TileMode=None, ViewportUnits=Relative-
    /// ToBoundingBox) and left every OTHER coordinate-space property at
    /// its WPF default. TextureCache, in contrast, ships brushes whose
    /// Viewbox / Viewport / Alignment / Stretch / TileMode are tuned per
    /// texture-and-shader so UVs in [0, 1] sample the intended region of
    /// each atlas -- e.g. some atlases need a non-default Viewbox or
    /// AlignmentY to compensate for the way the EGO shader samples them.
    ///
    /// As long as F1PackageLocator.Last was null on a given load (no
    /// install layout matched) MipmapsSidecarLoader silently returned
    /// null, this provider fell through to the TextureCache brush
    /// untouched, and the coordinate-space mismatch was invisible.
    /// v12.08 added F1 2020's bare "f1_classic_vehicle_package"
    /// packaging to the locator's KnownPackages list, so on F1 2020
    /// classic content the sidecar now resolves and this provider
    /// starts handing out its own ImageBrush instead of TextureCache's.
    /// That swapped the brush out from underneath the same UVs the
    /// pipeline already decoded, shifting where decals land on the body
    /// (the user-reported "v12.08 changed the UVs / decal UV maps
    /// break" symptom).
    ///
    /// Fix: when a template brush from TextureCache is available, mirror
    /// the full set of properties WPF's ImageBrush uses to map between
    /// the UV space [0, 1] and the texture's pixel space:
    ///
    ///   Stretch        Viewbox      Viewport
    ///   TileMode       ViewboxUnits ViewportUnits
    ///                  AlignmentX   AlignmentY
    ///
    /// onto the new hi-res brush BEFORE freezing it. When no template
    /// is available (e.g. TextureCache returned a non-ImageBrush, or
    /// the fallback fetch threw), fall back to the original v12.08-and-
    /// earlier hardcoded defaults so the no-template case is byte-
    /// identical to prior behaviour.
    ///
    /// The bitmap dimensions are NOT touched -- those are intrinsic to
    /// the decoded mipmap and have nothing to do with where UVs sample.
    /// </summary>
    internal static class HighResBrushProvider
    {
        // -------------------------------------------------------------
        // CACHE
        // -------------------------------------------------------------
        // Keyed by texture EAID. Holds either a real ImageBrush (hi-res
        // sidecar succeeded) or a tombstone (ImageBrush representing
        // "we tried the sidecar and it had no high-res for this EAID --
        // use the fallback brush from then on, don't keep retrying").
        //
        // The tombstone pattern matters: TryLoadTopMip is cheap on a
        // cache hit but can scan a directory tree on a cold miss for
        // every unique filename. Without the tombstone we'd re-scan for
        // every submesh that references the same texture.
        //
        // v12.09 note: the cached hi-res brush is built with whatever
        // template came in on the FIRST request for this EAID. That's
        // safe because the template is derived from
        // TextureCache.GetBrushForTexture(eaid), which is deterministic
        // for a given (TextureCache, EAID) pair -- every subsequent
        // caller will pass an equivalent template, so reusing the
        // cached brush across calls preserves UV semantics.
        private static readonly Dictionary<string, ImageBrush> _hiResCache =
            new Dictionary<string, ImageBrush>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static readonly ImageBrush _tombstone = new ImageBrush();

        /// <summary>
        /// Drop-in replacement for <c>textureCache.GetBrushForTexture(eaid)</c>
        /// when the caller wants high-res preview if available.
        ///
        /// <para>Returns the high-res brush from the .mipmaps sidecar when
        /// one is found, otherwise the tail-mip brush from TextureCache,
        /// otherwise null.</para>
        ///
        /// <para>v12.09: the tail-mip brush is fetched eagerly (cheap --
        /// TextureCache caches internally) so it can serve both as the
        /// fallback when the sidecar misses AND as the coordinate-space
        /// template the hi-res brush mirrors when it hits. Without the
        /// template the hi-res brush would use WPF's default Viewbox /
        /// Viewport / Alignment / Stretch / TileMode settings, which may
        /// not match what TextureCache's brush uses for this EAID --
        /// causing UVs in [0, 1] to sample different texture regions
        /// across the two paths (the v12.08 decal-UV regression).</para>
        /// </summary>
        public static ImageBrush GetBrushForTexture(
            string textureEaid,
            TextureCache fallbackCache,
            Action<string> log)
        {
            // Fetch the tail-mip brush once -- both fallback and template.
            ImageBrush tail = null;
            try { tail = fallbackCache?.GetBrushForTexture(textureEaid); }
            catch (Exception ex)
            {
                log?.Invoke($"  HighResBrushProvider: fallback GetBrushForTexture threw " +
                            $"({ex.Message}) for {textureEaid}");
                tail = null;
            }

            ImageBrush hi = TryGetHiResBrush(textureEaid, fallbackCache, log, tail);
            return hi ?? tail;
        }

        /// <summary>
        /// Substitute helper for the
        /// <c>res.Brush</c> / <c>res.OverlayBrush</c> fields of a
        /// TextureCache.PreviewResolution. Pass the EAID and the existing
        /// brush you'd otherwise use; the provider returns the high-res
        /// ImageBrush when a sidecar is available, or the original brush
        /// untouched when none.
        ///
        /// <para>Use this at call sites that go through
        /// <c>ResolveDiffuseForMaterial</c> -- those return a struct we
        /// can't construct ourselves, so we just swap the brush at the
        /// point of use rather than mutating the resolution.</para>
        ///
        /// <para>v12.09: the original brush is also used as the
        /// coordinate-space template for the hi-res brush so its UV
        /// semantics carry over. See <see cref="GetBrushForTexture"/>'s
        /// remarks for why.</para>
        /// </summary>
        public static Brush WrapBrush(
            string textureEaid,
            Brush originalBrush,
            TextureCache fallbackCache,
            Action<string> log)
        {
            ImageBrush hi = TryGetHiResBrush(
                textureEaid, fallbackCache, log, originalBrush as ImageBrush);
            return hi ?? originalBrush;
        }

        /// <summary>
        /// Manual cache invalidation. Called by the loaders' per-load
        /// reset blocks so a fresh ERP load doesn't keep stale brushes
        /// from a previous one. Cheap; safe to call always.
        /// </summary>
        public static void Clear()
        {
            lock (_cacheLock) { _hiResCache.Clear(); }
        }

        // -------------------------------------------------------------
        // INTERNALS
        // -------------------------------------------------------------

        private static ImageBrush TryGetHiResBrush(
            string textureEaid,
            TextureCache fallbackCache,
            Action<string> log,
            ImageBrush template)
        {
            if (string.IsNullOrEmpty(textureEaid)) return null;
            if (fallbackCache == null) return null;
            string packageRoot = fallbackCache.PackageRoot;
            if (string.IsNullOrEmpty(packageRoot)) return null;

            // Cache hit?
            lock (_cacheLock)
            {
                if (_hiResCache.TryGetValue(textureEaid, out var cached))
                    return ReferenceEquals(cached, _tombstone) ? null : cached;
            }

            // Cold miss: try sidecars in priority order. MipmapsSidecarLoader
            // handles the BC3-format driver-atlas / team-textures pyramid;
            // GenericMipmapsSidecarLoader picks up everything else (BC1, BC5,
            // BC7, ...). Helmet livery textures land in the GENERIC bucket
            // (format 54 is rejected by the BC3-only path), so without this
            // second attempt the viewport falls back to the small embedded
            // mip even when the .mipmaps file exists on disk and the FBX
            // exporter -- which already tries both loaders -- substitutes
            // it successfully.
            DecodedTexture dec = null;
            try
            {
                dec = MipmapsSidecarLoader.TryLoadTopMip(textureEaid, packageRoot, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  HighResBrushProvider: TryLoadTopMip threw ({ex.Message}) " +
                            $"for {textureEaid} -- trying generic loader");
            }
            if (dec == null)
            {
                try
                {
                    dec = GenericMipmapsSidecarLoader.TryLoadTopMip(textureEaid, packageRoot, log);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  HighResBrushProvider: GenericMipmaps threw ({ex.Message}) " +
                                $"for {textureEaid} -- falling back");
                }
            }

            ImageBrush hi = null;
            if (dec != null && dec.Bgra != null && dec.Width > 0 && dec.Height > 0)
            {
                try { hi = ConvertBgraToBrush(dec.Bgra, dec.Width, dec.Height, template); }
                catch (Exception ex)
                {
                    log?.Invoke($"  HighResBrushProvider: BGRA->ImageBrush threw " +
                                $"({ex.Message}) for {textureEaid} -- falling back");
                    hi = null;
                }
            }

            lock (_cacheLock)
            {
                _hiResCache[textureEaid] = hi ?? _tombstone;
            }
            if (hi != null)
            {
                string tplNote = (template != null)
                    ? " (mirrored TextureCache brush coordinate space)"
                    : " (no template, default coordinate space)";
                log?.Invoke($"  HighResBrushProvider: HIT {textureEaid} -> " +
                            $"{dec.Width}x{dec.Height} ({dec.FormatName ?? "BC3"})" + tplNote);
            }
            return hi;
        }

        /// <summary>
        /// Build a frozen WPF ImageBrush from a tightly-packed BGRA32
        /// pixel buffer. The buffer's stride is assumed to be
        /// <c>width * 4</c> bytes (no row padding), matching the format
        /// that <see cref="MipmapsSidecarLoader"/> emits.
        ///
        /// <para>v12.09: when <paramref name="template"/> is non-null,
        /// the coordinate-space properties WPF uses to map UV [0, 1]
        /// onto the bitmap's pixel space are mirrored from the template
        /// brush. This is the fix for the decal-UV regression introduced
        /// when v12.08 made F1PackageLocator resolve for F1 2020 classic
        /// ERPs (which in turn made this provider start handing back its
        /// own brushes instead of falling through to TextureCache, and
        /// surfaced the latent property-set mismatch). When the template
        /// is null (TextureCache returned a non-ImageBrush, or the
        /// fallback fetch threw, or the caller didn't have one) the
        /// brush is built with the same hardcoded defaults the
        /// v12.08-and-earlier code used, preserving prior behaviour
        /// byte-for-byte in the no-template case.</para>
        ///
        /// <para>The bitmap dimensions are intrinsic to the decoded mip
        /// and are NOT taken from the template -- only the UV-to-pixel
        /// MAPPING properties are mirrored.</para>
        /// </summary>
        private static ImageBrush ConvertBgraToBrush(
            byte[] bgra, int width, int height, ImageBrush template)
        {
            int stride = width * 4;
            var bitmap = BitmapSource.Create(
                width, height,
                96, 96,
                PixelFormats.Bgra32,
                null,
                bgra,
                stride);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap);

            if (template != null)
            {
                // Mirror every coordinate-space property WPF uses to
                // map UV [0, 1] onto the bitmap's pixel space. Reading
                // these on a frozen ImageBrush is safe; only mutation
                // is blocked, and we mutate `brush` (which is still
                // unfrozen at this point).
                brush.Stretch = template.Stretch;
                brush.TileMode = template.TileMode;
                brush.Viewbox = template.Viewbox;
                brush.ViewboxUnits = template.ViewboxUnits;
                brush.Viewport = template.Viewport;
                brush.ViewportUnits = template.ViewportUnits;
                brush.AlignmentX = template.AlignmentX;
                brush.AlignmentY = template.AlignmentY;
            }
            else
            {
                // No template: the v12.08-and-earlier defaults. Kept
                // explicit (rather than relying on WPF's ImageBrush
                // ctor defaults) so this fallback stays byte-identical
                // to what shipped before v12.09 even if a future WPF
                // release changes a default.
                brush.Stretch = Stretch.Fill;
                brush.TileMode = TileMode.None;
                brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            }

            // WPF defaults its bitmap scaling mode to Linear for sub-pixel-
            // sampled brushes used in 3D materials, which softens hi-res
            // textures to the point that 2048x1024 brushes can look almost
            // identical to the 8x8 tail mip. HighQuality forces the bilinear/
            // bicubic path WPF uses for full-res sampling. Set BEFORE freeze.
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                brush, System.Windows.Media.BitmapScalingMode.HighQuality);
            System.Windows.Media.RenderOptions.SetCachingHint(
                brush, System.Windows.Media.CachingHint.Cache);

            brush.Freeze();
            return brush;
        }
    }
}