using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Media3D = System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// Which game shader family a material runs. Classified from the
    /// material's fx reference (eaid://shader_package/...), which the
    /// full shader-package sweep confirmed as the authoritative list:
    /// vehicle_paint_v2 hosts metallic/gloss/custom paint techniques,
    /// vehicle_generic hosts glass/wheel-hub/brakedisc/etc.
    /// </summary>
    internal enum GameShaderKind
    {
        Unknown,
        MetallicPaint,   // vehicle_paint_v2/vehicle_metallic_paint (the MCL33 body)
        GlossPaint,      // vehicle_paint_v2/vehicle_gloss_paint
        CustomPaint,     // vehicle_paint_v2/vehicle_custom_paint
        PaintOther,      // any other vehicle_paint_v2 technique
        Carbon,          // vehicle_carbon/* (anisotropic weave under lacquer)
        Generic,         // vehicle_generic/vehicle_generic (detail parts)
        Wheel,           // vehicle_generic/vehicle_wheel + vehicle_wheels/*
        Glass,           // vehicle_generic/vehicle_glass
        Mirror,          // f1_car_mirror/*
        TyreSidewall,    // tyre_sidewall/*
        TyreTread,       // tyre_tread/*
        RainBeads,       // vehicle_rain_beads/*
        Steering,        // vehicle_steering/*
    }

    /// <summary>
    /// One decoded GfxMaterialRes: the shader it runs, every PerMaterial
    /// float parameter by name, and every me_*Map texture binding.
    /// Values come straight from the car's own ERP -- these are the
    /// numbers the game feeds the shader (e.g. the McLaren paint:
    /// me_lacquerIor 1.5, me_lacquerRoughness 0.23, me_lacquerThickness
    /// 0.2, me_useMappedMetallicIor with me_metallicMap =
    /// mclaren_main_paint_c.tif).
    /// </summary>
    internal sealed class GameMaterialInfo
    {
        public string Name = "";        // "mclaren_paint"
        public string Eaid = "";        // full material eaid (query stripped)
        public string ShaderEaid = "";  // eaid://shader_package/<family>/<technique>.nefx2
        public GameShaderKind Kind = GameShaderKind.Unknown;
        public readonly Dictionary<string, float[]> Params =
            new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Bindings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public float Scalar(string name, float fallback)
        {
            return Params.TryGetValue(name, out var v) && v != null && v.Length > 0
                ? v[0] : fallback;
        }
    }

    /// <summary>
    /// v17.0 TIER-1 GAME MATERIALS. Decodes every GfxMaterialRes in the
    /// loaded car ERP into <see cref="GameMaterialInfo"/> and lets the
    /// DX showroom look them up per converted mesh material, so the
    /// paint/carbon/rubber/glass response comes from the game's own
    /// parameter values instead of one guessed constant set.
    ///
    /// Binary layout (the GfxMaterialRes 010-template layout already
    /// documented in MaterialParameterReader, generalised here; it was
    /// additionally cross-checked name-for-name and size-for-size
    /// against the shader packages' DXBC reflection -- zero mismatches
    /// across all 111 McLaren materials):
    ///   u32 parameterCount
    ///   u32 fxNameOffset         -- rel to data_pool_base (shader eaid)
    ///   u32 paramNameOffset      -- rel to data_pool_base
    ///   u32 resourceNameOffset   -- rel to data_pool_base
    ///   parameterCount x 16-byte entries:
    ///     u32 nameOffset         -- rel to (pool + paramNameOffset)
    ///     u32 type               -- 1=float param, 2=texture, 3=sampler
    ///     u32 size               -- bytes (4=float, 12=vec3, 16=vec4)
    ///     u32 dataOffset         -- type 1: rel to pool
    ///                            -- type 2/3: rel to (pool + resourceNameOffset)
    ///   data_pool_base = 16 + 16 * parameterCount
    ///
    /// Decoding is fully standalone (ErpContainerReader only) -- no
    /// dependency on the extracted-package pipeline -- so the catalog
    /// works even when only the .erp exists. Aux textures (normal maps
    /// etc.) DO go through the live TextureCache via
    /// <see cref="TextureResolver"/>, wired by F1ModelLoader.
    /// </summary>
    internal static class GameMaterialCatalog
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, GameMaterialInfo> _byKey =
            new Dictionary<string, GameMaterialInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConditionalWeakTable<Media3D.Material, GameMaterialInfo> _tags =
            new ConditionalWeakTable<Media3D.Material, GameMaterialInfo>();

        /// <summary>
        /// Decodes a texture eaid to a brush -- set by the loaders'
        /// SetTextureCache/AutoConfigure to TextureCache.GetBrushForTexture.
        /// Null when no cache is live; the showroom degrades gracefully.
        /// </summary>
        public static Func<string, ImageBrush> TextureResolver;

        // v17.1: driver numbers offered by this package -- the
        // {team}_driver_{n}.tif textures (GfxSRVResource) found while
        // walking the ERP. Drives the driver-number selector: F1 2018
        // ships one per multiplayer number (1..99), so the list is
        // exactly what the game itself can bind via emb?context={n}.
        private static readonly List<int> _driverNumbers = new List<int>();
        private static readonly Regex _driverTexRx = new Regex(
            @"([A-Za-z0-9]+)_driver_(\d+)\.tif",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Sorted driver numbers found in the loaded ERP.</summary>
        public static IReadOnlyList<int> AvailableDriverNumbers
        {
            get { lock (_lock) return _driverNumbers.ToArray(); }
        }

        /// <summary>Team stem of the driver textures (e.g. "mclaren").</summary>
        public static string DriverTeamStem { get; private set; } = "";

        public static int Count { get { lock (_lock) return _byKey.Count; } }

        /// <summary>
        /// Parse every GfxMaterialRes in the ERP. Best-effort: a material
        /// that fails to decode is skipped, never fatal.
        /// </summary>
        public static void LoadFromErp(string erpPath)
        {
            lock (_lock) { _byKey.Clear(); _driverNumbers.Clear(); }
            DriverTeamStem = "";
            ErpContainer container;
            try { container = ErpContainerReader.Read(erpPath); }
            catch { return; }

            foreach (var r in container.Resources)
            {
                // v17.1: collect {team}_driver_{n}.tif while we're walking
                // the TOC anyway -- the driver-number selector is built
                // from these.
                if (string.Equals(r.Type, "GfxSRVResource", StringComparison.OrdinalIgnoreCase))
                {
                    var dm = _driverTexRx.Match(r.Name ?? "");
                    if (dm.Success && int.TryParse(dm.Groups[2].Value, out int dn))
                    {
                        lock (_lock)
                            if (!_driverNumbers.Contains(dn)) _driverNumbers.Add(dn);
                        if (DriverTeamStem.Length == 0)
                        {
                            string stem = dm.Groups[1].Value;
                            int us = stem.LastIndexOf('/');
                            DriverTeamStem = us >= 0 ? stem.Substring(us + 1) : stem;
                        }
                    }
                    continue;
                }

                if (!string.Equals(r.Type, "GfxMaterialRes", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (r.Sections == null || r.Sections.Count == 0) continue;
                byte[] data;
                try { data = ErpContainerReader.ReadSectionBytes(erpPath, container, r.Sections[0]); }
                catch { continue; }

                var info = Decode(data);
                if (info == null) continue;

                string eaid = (r.Name ?? "").TrimEnd('\0');
                int q = eaid.IndexOf('?');
                if (q >= 0) eaid = eaid.Substring(0, q);
                info.Eaid = eaid;
                info.Name = StemOf(eaid);

                lock (_lock)
                {
                    _byKey[info.Name] = info;
                    if (!string.IsNullOrEmpty(eaid)) _byKey[eaid] = info;
                }
            }
            lock (_lock) _driverNumbers.Sort();
        }

        /// <summary>
        /// Associate a WPF material instance (the loader's DiffuseMaterial)
        /// with its game material. Called at the creation site in
        /// F1ModelLoader; the DX showroom reads it back in ConvertMaterial.
        /// Works on frozen materials (ConditionalWeakTable keys by identity).
        /// </summary>
        public static void Tag(Media3D.Material material, string materialEaidOrName)
        {
            if (material == null || string.IsNullOrEmpty(materialEaidOrName)) return;
            var info = Find(materialEaidOrName);
            if (info == null) return;
            try
            {
                if (_tags.TryGetValue(material, out _)) return;
                _tags.Add(material, info);
            }
            catch { /* duplicate add race -- fine */ }
        }

        public static bool TryGet(Media3D.Material material, out GameMaterialInfo info)
        {
            info = null;
            return material != null && _tags.TryGetValue(material, out info);
        }

        private static readonly ConditionalWeakTable<Media3D.Material, SeeThroughMaterial> _seeThrough =
            new ConditionalWeakTable<Media3D.Material, SeeThroughMaterial>();

        /// <summary>
        /// Mark a WPF material as one of the game's see-through character
        /// materials (visor, tear-off, clear plastic), so the DX showroom
        /// blends it the way the game does. Set beside <see cref="Tag"/>.
        /// </summary>
        public static void TagSeeThrough(Media3D.Material material, SeeThroughMaterial seeThrough)
        {
            if (material == null || seeThrough == null) return;
            try { _seeThrough.AddOrUpdate(material, seeThrough); }
            catch { }
        }

        public static bool TryGetSeeThrough(Media3D.Material material, out SeeThroughMaterial seeThrough)
        {
            seeThrough = null;
            return material != null && _seeThrough.TryGetValue(material, out seeThrough);
        }

        public static GameMaterialInfo Find(string materialEaidOrName)
        {
            if (string.IsNullOrEmpty(materialEaidOrName)) return null;
            string key = materialEaidOrName.TrimEnd('\0');
            int q = key.IndexOf('?');
            if (q >= 0) key = key.Substring(0, q);
            lock (_lock)
            {
                if (_byKey.TryGetValue(key, out var byFull)) return byFull;
                if (_byKey.TryGetValue(StemOf(key), out var byStem)) return byStem;
            }
            return null;
        }

        /// <summary>
        /// Fetch and decode an auxiliary texture binding (e.g.
        /// "me_normalMap") through the live texture cache. Null when the
        /// slot isn't bound, the cache isn't live, or decode fails.
        /// </summary>
        public static BitmapSource TryGetAuxBitmap(GameMaterialInfo info, string slot)
        {
            var resolver = TextureResolver;
            if (info == null || resolver == null) return null;
            if (!info.Bindings.TryGetValue(slot, out var eaid) || string.IsNullOrEmpty(eaid))
                return null;
            try { return resolver(eaid)?.ImageSource as BitmapSource; }
            catch { return null; }
        }

        // ---------------------------------------------------------------

        private static string StemOf(string eaid)
        {
            if (string.IsNullOrEmpty(eaid)) return "";
            int slash = eaid.LastIndexOf('/');
            string tail = slash >= 0 ? eaid.Substring(slash + 1) : eaid;
            if (tail.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
                tail = tail.Substring(0, tail.Length - ".material".Length);
            return tail;
        }

        private static string CString(byte[] d, int off)
        {
            if (off < 0 || off >= d.Length) return null;
            int e = off;
            while (e < d.Length && d[e] != 0) e++;
            if (e <= off || e - off > 260) return null;
            return Encoding.ASCII.GetString(d, off, e - off);
        }

        private static GameMaterialInfo Decode(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 16) return null;
                int count = BitConverter.ToInt32(data, 0);
                int fxOfs = BitConverter.ToInt32(data, 4);
                int pNameOfs = BitConverter.ToInt32(data, 8);
                int rNameOfs = BitConverter.ToInt32(data, 12);
                if (count <= 0 || count > 512) return null;
                int pool = 16 + 16 * count;
                if (pool >= data.Length) return null;

                var info = new GameMaterialInfo();
                info.ShaderEaid = CString(data, pool + fxOfs) ?? "";
                info.Kind = Classify(info.ShaderEaid);
                int nameBase = pool + pNameOfs;
                int resBase = pool + rNameOfs;

                for (int i = 0; i < count; i++)
                {
                    int eo = 16 + i * 16;
                    if (eo + 16 > data.Length) break;
                    uint nameOfs = BitConverter.ToUInt32(data, eo);
                    uint type = BitConverter.ToUInt32(data, eo + 4);
                    uint size = BitConverter.ToUInt32(data, eo + 8);
                    uint dataOfs = BitConverter.ToUInt32(data, eo + 12);

                    string name = CString(data, nameBase + (int)nameOfs);
                    if (string.IsNullOrEmpty(name)) continue;

                    if (type == 1 && size >= 4 && size <= 64 && (size & 3) == 0)
                    {
                        int vp = pool + (int)dataOfs;
                        if (vp < 0 || vp + (int)size > data.Length) continue;
                        var vals = new float[size / 4];
                        bool ok = true;
                        for (int k = 0; k < vals.Length; k++)
                        {
                            float f = BitConverter.ToSingle(data, vp + k * 4);
                            if (float.IsNaN(f) || float.IsInfinity(f)) { ok = false; break; }
                            vals[k] = f;
                        }
                        if (ok) info.Params[name] = vals;
                    }
                    else if (type == 2 && name.StartsWith("me_", StringComparison.OrdinalIgnoreCase))
                    {
                        string tex = CString(data, resBase + (int)dataOfs);
                        if (!string.IsNullOrEmpty(tex) &&
                            tex.StartsWith("eaid://", StringComparison.OrdinalIgnoreCase))
                            info.Bindings[name] = tex;
                    }
                }
                return string.IsNullOrEmpty(info.ShaderEaid) &&
                       info.Params.Count == 0 && info.Bindings.Count == 0
                    ? null : info;
            }
            catch { return null; }
        }

        private static GameShaderKind Classify(string shaderEaid)
        {
            if (string.IsNullOrEmpty(shaderEaid)) return GameShaderKind.Unknown;
            string s = shaderEaid.ToLowerInvariant();
            if (s.Contains("metallic_paint") || s.Contains("matallic_paint"))
                return GameShaderKind.MetallicPaint;   // incl. Codemasters' typo twin
            if (s.Contains("gloss_paint")) return GameShaderKind.GlossPaint;
            if (s.Contains("custom_paint")) return GameShaderKind.CustomPaint;
            if (s.Contains("vehicle_paint")) return GameShaderKind.PaintOther;
            if (s.Contains("vehicle_carbon")) return GameShaderKind.Carbon;
            if (s.Contains("vehicle_wheel")) return GameShaderKind.Wheel;
            if (s.Contains("vehicle_glass")) return GameShaderKind.Glass;
            if (s.Contains("car_mirror")) return GameShaderKind.Mirror;
            if (s.Contains("tyre_sidewall")) return GameShaderKind.TyreSidewall;
            if (s.Contains("tyre_tread")) return GameShaderKind.TyreTread;
            if (s.Contains("rain_beads")) return GameShaderKind.RainBeads;
            if (s.Contains("vehicle_steering")) return GameShaderKind.Steering;
            if (s.Contains("vehicle_generic")) return GameShaderKind.Generic;
            return GameShaderKind.Unknown;
        }
    }
}
