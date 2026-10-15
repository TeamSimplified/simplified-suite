using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SimplifiedUniversalExporter.Core.Showroom
{
    /// <summary>One drawable piece of the loaded model, in model space.</summary>
    public sealed class ShowroomMesh
    {
        public string Slot = "";
        public string MaterialEaid;
        /// <summary>A decal overlay: drawn blended, from its own sheet.</summary>
        public bool Overlay;
        public float[] Positions;
        /// <summary>One per vertex, or null to derive from the triangles.</summary>
        public float[] Normals;
        public float[] Uvs;
        public int[] Indices;
        /// <summary>The colour the exporter itself draws this piece with, or null.</summary>
        public ShowroomImage Colour;
    }

    /// <summary>A decoded picture the exporter already holds, read when the scene needs it.</summary>
    public sealed class ShowroomImage
    {
        /// <summary>Identity: pieces drawn with the same picture share one texture.</summary>
        public object Key;
        /// <summary>BGRA8 pixels, row by row, and their size. Called once, off the window's thread.</summary>
        public Func<(byte[] Bgra, int Width, int Height)> Read;
        /// <summary>
        /// The texture this picture is an unchanged decode of, when it is one:
        /// its block data is then sent instead of the pixels (BC1, BC3 and BC7
        /// only - the formats the exporter decodes without altering).
        /// </summary>
        public string TextureEaid;
    }

    /// <summary>Where the scene gets what isn't on the meshes.</summary>
    public sealed class ShowroomSources
    {
        /// <summary>The extracted package root, for the textures' block data.</summary>
        public string PackageRoot;
        public Func<string, MaterialFile> Material;
        /// <summary>A texture decoded to BGRA (normal maps as R = X, G = Y), for one that has no block data here.</summary>
        public Func<string, bool, DecodedTexture> Decode;
        public Action<string> Log;
    }

    /// <summary>What the page loads: scene.json and the binary it points into.</summary>
    public sealed class ShowroomSceneData
    {
        public byte[] Json;
        public byte[] Binary;
        public int Groups, Materials, Textures, CompressedTextures, Vertices, Triangles;
        public TimeSpan Elapsed;
        /// <summary>One line per texture: name, format, size and levels - for the log.</summary>
        public List<string> TextureLines = new List<string>();
    }

    /// <summary>
    /// The RM Renderer's scene: the loaded model in the data layout of the
    /// renderer adapted from RM Designs' Steering Wheel Swapper (its
    /// PreviewScene), so the page's shader reads it unchanged.
    ///
    /// <para>Geometry: model-space float positions, half normal (w = vertex
    /// AO, 1 here), half tangent (w = sign) and half uv0 + uv1 - uv1 being
    /// the decal sheet, which only overlays use. Materials: the page's kind,
    /// up to seven textures and a 24-value parameter block, from the game
    /// material through the texture cache's parser. Textures: the game's own
    /// block data where it can be read (BC1/2/3/5/7, uploaded by the browser
    /// as is), decoded RGBA otherwise.</para>
    ///
    /// <para>All binary data goes into one buffer the page slices, as
    /// [offset, length] pairs; nothing is compressed or encoded, since it
    /// never leaves the app.</para>
    /// </summary>
    public static class ShowroomScene
    {
        // the page's material kinds (PreviewScene.KindOf in RM's swapper)
        public const int KGeneric = 0, KPaint = 1, KCarbon = 2, KGlass = 3, KScreen = 4, KMirror = 5, KDecal = 6, KTyre = 7, KLight = 8;

        /// <summary>
        /// Added to RM's kinds: a helmet's visor, tear-off or clear plastic, decided by
        /// <see cref="SeeThroughMaterial"/> exactly as the exporter's own preview decides it.
        /// </summary>
        public const int KSeeThrough = 10;

        /// <summary>
        /// Largest texture side, by area: paint and decal sheets, colour maps,
        /// normal and surface maps. RM's preview capped these at 2048 / 1024 /
        /// 512 for a page saved to disk; here nothing leaves the app and the
        /// textures stay block-compressed, so they go at the game's own full
        /// size - a helmet's 2048x1024 sheet halved was visibly soft.
        /// </summary>
        private const int PaintSize = 4096, ColourSize = 4096, DetailSize = 4096;

        public static ShowroomSceneData Build(IReadOnlyList<ShowroomMesh> meshes, ShowroomSources sources)
        {
            var watch = Stopwatch.StartNew();
            var b = new Builder(sources);
            foreach (var m in meshes) b.Add(m);

            var car = new Dictionary<string, object>
            {
                ["title"] = "model",
                ["box"] = b.Box, ["wbox"] = b.Box,
                ["body"] = b.Groups,
                ["after"] = new List<object>(), ["before"] = new List<object>(),
                ["toWheel"] = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
                ["afterName"] = "", ["beforeName"] = "",
            };
            var data = new Dictionary<string, object>
            {
                ["tex"] = b.Tex, ["mats"] = b.Mats, ["geo"] = b.Geo, ["cars"] = new List<object> { car },
            };
            return new ShowroomSceneData
            {
                Json = JsonSerializer.SerializeToUtf8Bytes(data),
                Binary = b.Bin.ToArray(),
                Groups = b.Groups.Count, Materials = b.Mats.Count, Textures = b.Tex.Count,
                CompressedTextures = b.Compressed, Vertices = b.Vertices, Triangles = b.Triangles,
                Elapsed = watch.Elapsed,
                TextureLines = b.Tex.Select(t => $"{t["name"],-48} {t["f"],-5} {t["w"],5}x{t["h"],-5} {((System.Collections.ICollection)t["l"]).Count} level(s)").ToList(),
            };
        }

        /// <summary>The page's kind for a shader, or -1 for one that draws nothing here (shadows, rain).</summary>
        public static int KindOf(string shader)
        {
            if (shader.Contains("shadow") || shader.Contains("rain_beads")) return -1;
            if (shader.StartsWith("vehicle_carbon")) return KCarbon;
            if (shader.StartsWith("vehicle_floating_decal")) return KDecal;
            if (shader.Contains("paint")) return KPaint;
            if (shader == "vehicle_glass") return KGlass;
            if (shader == "vehicle_lcd_screen") return KScreen;
            if (shader.Contains("mirror")) return KMirror;
            if (shader.StartsWith("tyre_")) return KTyre;
            if (shader.Contains("revs") || shader.Contains("indicator") || shader.Contains("rear_light")) return KLight;
            return KGeneric;
        }

        private sealed class Builder
        {
            private readonly ShowroomSources _src;
            public readonly List<Dictionary<string, object>> Tex = new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Mats = new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Geo = new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> Groups = new List<Dictionary<string, object>>();
            public readonly MemoryStream Bin = new MemoryStream();
            public readonly float[] Box = { 1e9f, 1e9f, 1e9f, -1e9f, -1e9f, -1e9f };
            public int Vertices, Triangles, Compressed;
            private readonly Dictionary<object, int> _tex = new Dictionary<object, int>();
            private readonly Dictionary<string, int> _mat = new Dictionary<string, int>();

            public Builder(ShowroomSources src) { _src = src; }

            /// <summary>Bytes into the buffer, 4-aligned; the page gets [offset, length].</summary>
            private int[] Put(ReadOnlySpan<byte> bytes)
            {
                while (Bin.Length % 4 != 0) Bin.WriteByte(0);
                int at = (int)Bin.Length;
                Bin.Write(bytes);
                return new[] { at, bytes.Length };
            }

            // ------------------------------------------------------------ geometry

            public void Add(ShowroomMesh mesh)
            {
                var p = mesh.Positions;
                var idx = mesh.Indices;
                if (p == null || p.Length < 9 || idx == null || idx.Length < 3) return;
                int n = p.Length / 3;

                int mat = Material(mesh);
                if (mat < 0) return;

                var nrm = new Vector3[n];
                var uv = new Vector2[n];
                bool hasNormals = mesh.Normals != null && mesh.Normals.Length == n * 3;
                for (int i = 0; i < n; i++)
                {
                    if (hasNormals) nrm[i] = new Vector3(mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2]);
                    if (mesh.Uvs != null && mesh.Uvs.Length >= i * 2 + 2) uv[i] = new Vector2(mesh.Uvs[i * 2], mesh.Uvs[i * 2 + 1]);
                    Box[0] = Math.Min(Box[0], p[i * 3]); Box[1] = Math.Min(Box[1], p[i * 3 + 1]); Box[2] = Math.Min(Box[2], p[i * 3 + 2]);
                    Box[3] = Math.Max(Box[3], p[i * 3]); Box[4] = Math.Max(Box[4], p[i * 3 + 1]); Box[5] = Math.Max(Box[5], p[i * 3 + 2]);
                }

                // tangents (and normals, when there are none) from the triangles
                var tan = new Vector3[n];
                var bit = new Vector3[n];
                var tris = new List<int>(idx.Length);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if ((uint)a >= n || (uint)b >= n || (uint)c >= n || a == b || b == c || a == c) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    var pa = new Vector3(p[a * 3], p[a * 3 + 1], p[a * 3 + 2]);
                    var e1 = new Vector3(p[b * 3], p[b * 3 + 1], p[b * 3 + 2]) - pa;
                    var e2 = new Vector3(p[c * 3], p[c * 3 + 1], p[c * 3 + 2]) - pa;
                    if (!hasNormals)
                    {
                        var fn = Vector3.Cross(e1, e2);
                        nrm[a] += fn; nrm[b] += fn; nrm[c] += fn;
                    }
                    var d1 = uv[b] - uv[a];
                    var d2 = uv[c] - uv[a];
                    float r = d1.X * d2.Y - d2.X * d1.Y;
                    if (Math.Abs(r) < 1e-12f) continue;
                    r = 1f / r;
                    var sd = (e1 * d2.Y - e2 * d1.Y) * r;
                    var td = (e2 * d1.X - e1 * d2.X) * r;
                    tan[a] += sd; tan[b] += sd; tan[c] += sd;
                    bit[a] += td; bit[b] += td; bit[c] += td;
                }
                if (tris.Count == 0) return;

                var N = new Half[n * 4];
                var T = new Half[n * 4];
                var U = new Half[n * 4];
                for (int i = 0; i < n; i++)
                {
                    var nn = nrm[i].LengthSquared() > 1e-12f ? Vector3.Normalize(nrm[i]) : Vector3.UnitY;
                    var tt = tan[i] - nn * Vector3.Dot(nn, tan[i]);
                    tt = tt.LengthSquared() > 1e-12f ? Vector3.Normalize(tt) : Vector3.UnitX;
                    float sign = Vector3.Dot(Vector3.Cross(nn, tt), bit[i]) < 0 ? -1f : 1f;
                    N[i * 4] = (Half)nn.X; N[i * 4 + 1] = (Half)nn.Y; N[i * 4 + 2] = (Half)nn.Z; N[i * 4 + 3] = (Half)1f;
                    T[i * 4] = (Half)tt.X; T[i * 4 + 1] = (Half)tt.Y; T[i * 4 + 2] = (Half)tt.Z; T[i * 4 + 3] = (Half)sign;
                    U[i * 4] = (Half)uv[i].X; U[i * 4 + 1] = (Half)uv[i].Y;
                    U[i * 4 + 2] = (Half)(mesh.Overlay ? uv[i].X : 0f); U[i * 4 + 3] = (Half)(mesh.Overlay ? uv[i].Y : 0f);
                }

                bool w32 = n > 65535;
                Geo.Add(new Dictionary<string, object>
                {
                    ["count"] = tris.Count, ["w32"] = w32,
                    ["pos"] = Put(MemoryMarshal.AsBytes(p.AsSpan())),
                    ["nrm"] = Put(MemoryMarshal.AsBytes(N.AsSpan())),
                    ["tan"] = Put(MemoryMarshal.AsBytes(T.AsSpan())),
                    ["uv"] = Put(MemoryMarshal.AsBytes(U.AsSpan())),
                    ["idx"] = w32 ? Put(MemoryMarshal.AsBytes(tris.Select(x => (uint)x).ToArray().AsSpan()))
                                  : Put(MemoryMarshal.AsBytes(tris.Select(x => (ushort)x).ToArray().AsSpan())),
                });
                Groups.Add(new Dictionary<string, object> { ["slot"] = mesh.Slot ?? "", ["m"] = mat, ["g"] = Geo.Count - 1 });
                Vertices += n;
                Triangles += tris.Count / 3;
            }

            // ------------------------------------------------------------ materials

            private int Material(ShowroomMesh mesh)
            {
                if (mesh.Overlay)
                {
                    var key = "overlay|" + (mesh.Colour?.Key?.GetHashCode() ?? 0);
                    if (_mat.TryGetValue(key, out var oi)) return oi;
                    var t = Slots(d: Image(mesh.Colour, PaintSize));
                    Mats.Add(new Dictionary<string, object> { ["name"] = "overlay", ["k"] = KDecal, ["t"] = t, ["p"] = new float[24], ["blend"] = true });
                    return _mat[key] = Mats.Count - 1;
                }

                MaterialFile mf = null;
                if (!string.IsNullOrEmpty(mesh.MaterialEaid))
                {
                    try { mf = _src.Material?.Invoke(mesh.MaterialEaid); } catch { }
                }
                string shader = mf?.ShaderEaid != null ? Path.GetFileNameWithoutExtension(mf.ShaderEaid.Split('?')[0]) : "";
                int kind = mf == null ? KGeneric : KindOf(shader);
                if (kind < 0) return -1;
                var see = mf == null ? null : SeeThroughMaterial.For(mf);
                if (see != null) kind = KSeeThrough;

                var matKey = (mesh.MaterialEaid ?? "none") + "|" + (mesh.Colour?.Key?.GetHashCode() ?? 0);
                if (_mat.TryGetValue(matKey, out var mi)) return mi;

                bool paint = kind == KPaint, tyre = kind == KTyre;
                string B(params string[] names)
                {
                    if (mf?.BindingsBySlot == null) return null;
                    foreach (var s in names)
                        if (mf.BindingsBySlot.TryGetValue(s, out var e) && !string.IsNullOrEmpty(e)) return e;
                    return null;
                }

                var tex = Slots(
                    d: mesh.Colour != null ? Image(mesh.Colour, paint || tyre ? PaintSize : ColourSize)
                                           : Texture(B("me_albedoMap", "me_diffuseMap", "me_diffuseTex"), paint || tyre ? PaintSize : ColourSize, false),
                    n: paint ? -1 : Texture(B("me_normalMap", "me_normalTex"), tyre ? ColourSize : DetailSize, true),
                    s: Texture(paint ? B("me_paintSurfaceMap") : B("me_surfaceMap", "me_surfaceTex"), paint || tyre ? ColourSize : DetailSize, false),
                    l: tyre ? Texture(B("me_logoSurfaceMap"), ColourSize, false) : Texture(B("me_logoMap"), paint ? PaintSize : ColourSize, false),
                    c: paint ? Texture(B("me_metallicMap"), ColourSize, false) : -1,
                    f: paint && V(mf, "me_flakesStrength", 0) > 0 ? Texture(B("me_flakesMap"), DetailSize, true) : -1,
                    ls: tyre ? -1 : Texture(B("me_logoSurfaceMap"), DetailSize, false));

                var uvTile = see != null ? new[] { see.Opacity, see.Roughness } : Vec(mf, "me_uvTile", 2, 1);
                if (shader == "tyre_tread") uvTile = new[] { 1f, -1f };   // strip sheets: the page picks each sheet's first strip
                var p = new List<float>
                {
                    // p0: uv tile and carbon offset - for the see-through kind, opacity, roughness and index of refraction
                    uvTile[0], uvTile[1], see != null ? see.Ior : V(mf, "me_uOffset", 0), V(mf, "me_vOffset", 0),
                    // the exporter's UVs already place the paint sheet, so no paint offset on top
                    0, 0, V(mf, "me_carbonTile", 1), V(mf, "me_lacquerRoughness", 0.05f),
                    V(mf, "me_logoRoughness", 0.35f), paint ? 1f : V(mf, "me_lacquerLevel", 0), V(mf, "me_screenSelfIllumination", 0), V(mf, "me_vertexAo", 0),
                };
                p.AddRange(Vec(mf, "me_logoColour", 3, 1)); p.Add(V(mf, "me_cubeMapScale", 1));
                p.AddRange(new[] { V(mf, "me_flakesStrength", 0), V(mf, "me_flakesTile", 1), 0f, 0f });
                p.AddRange(Vec(mf, "me_flakesColourInfluence", 3, 0)); p.Add(0);

                Mats.Add(new Dictionary<string, object>
                {
                    ["name"] = string.IsNullOrEmpty(mesh.MaterialEaid) ? "untagged" : mesh.MaterialEaid.Split('/').Last(),
                    ["k"] = kind, ["t"] = tex, ["p"] = p.ToArray(), ["blend"] = kind == KGlass || kind == KDecal || kind == KSeeThrough,
                });
                return _mat[matKey] = Mats.Count - 1;
            }

            private static Dictionary<string, int> Slots(int d = -1, int n = -1, int s = -1, int l = -1, int c = -1, int f = -1, int ls = -1) =>
                new Dictionary<string, int> { ["d"] = d, ["n"] = n, ["s"] = s, ["l"] = l, ["c"] = c, ["f"] = f, ["ls"] = ls };

            private static float V(MaterialFile mf, string name, float fallback) => mf?.GetFloat(name) ?? fallback;

            private static float[] Vec(MaterialFile mf, string name, int k, float fallback)
            {
                float[] v = null;
                if (mf?.Parameters != null && mf.Parameters.TryGetValue(name, out var prm)) v = prm.Floats;
                return Enumerable.Range(0, k).Select(i => v != null && i < v.Length ? v[i] : fallback).ToArray();
            }

            // ------------------------------------------------------------ textures

            /// <summary>A texture by eaid: its block data when it can be read, decoded RGBA otherwise.</summary>
            private int Texture(string eaid, int max, bool normal)
            {
                if (string.IsNullOrEmpty(eaid)) return -1;
                if (_tex.TryGetValue(eaid, out var i)) return i;
                var chain = Chain(eaid);
                if (chain != null) return _tex[eaid] = AddChain(eaid, chain, max);

                DecodedTexture d = null;
                try { d = _src.Decode?.Invoke(eaid, normal); } catch { }
                if (d?.Bgra == null || d.Width <= 0 || d.Height <= 0) return _tex[eaid] = -1;
                return _tex[eaid] = Rgba(d.Bgra, d.Width, d.Height, max, eaid.Split('/').Last());
            }

            private GenericMipmapsSidecarLoader.CompressedChain Chain(string eaid)
            {
                if (string.IsNullOrEmpty(_src.PackageRoot)) return null;
                try { return GenericMipmapsSidecarLoader.TryLoadCompressedChain(eaid, _src.PackageRoot, _src.Log); }
                catch { return null; }
            }

            private int AddChain(string eaid, GenericMipmapsSidecarLoader.CompressedChain chain, int max)
            {
                // from the first level of at most max² pixels, by area (tall strip sheets keep their width)
                int first = 0;
                while (first < chain.Levels.Count - 1 &&
                       (long)Math.Max(1, chain.Width >> first) * Math.Max(1, chain.Height >> first) > (long)max * max)
                    first++;
                var levels = chain.Levels.Skip(first).Select(l => Put(l)).ToList();
                Tex.Add(new Dictionary<string, object>
                {
                    ["name"] = eaid.Split('/').Last(), ["f"] = chain.Format,
                    ["w"] = Math.Max(1, chain.Width >> first), ["h"] = Math.Max(1, chain.Height >> first), ["l"] = levels,
                });
                Compressed++;
                return Tex.Count - 1;
            }

            /// <summary>A picture the exporter holds: its texture's block data when it is an unchanged decode of one, RGBA otherwise.</summary>
            private int Image(ShowroomImage image, int max)
            {
                if (image?.Read == null) return -1;
                if (_tex.TryGetValue(image.Key, out var i)) return i;
                if (!string.IsNullOrEmpty(image.TextureEaid))
                {
                    if (_tex.TryGetValue(image.TextureEaid, out var t) && t >= 0) return _tex[image.Key] = t;
                    var chain = Chain(image.TextureEaid);
                    if (chain != null && chain.Format is "bc1" or "bc3" or "bc7")
                        return _tex[image.Key] = _tex[image.TextureEaid] = AddChain(image.TextureEaid, chain, max);
                }
                (byte[] Bgra, int Width, int Height) px;
                try { px = image.Read(); } catch { return _tex[image.Key] = -1; }
                if (px.Bgra == null || px.Width <= 0 || px.Height <= 0) return _tex[image.Key] = -1;
                return _tex[image.Key] = Rgba(px.Bgra, px.Width, px.Height, max, "colour");
            }

            /// <summary>BGRA to the page's RGBA, halved (2x2 average) until it fits <paramref name="max"/>.</summary>
            private int Rgba(byte[] bgra, int w, int h, int max, string name)
            {
                while ((long)w * h > (long)max * max && w > 1 && h > 1)
                    (bgra, w, h) = Halve(bgra, w, h);
                var rgba = new byte[w * h * 4];
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = bgra[i + 3];
                }
                Tex.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["f"] = "rgba", ["w"] = w, ["h"] = h, ["l"] = new List<int[]> { Put(rgba) },
                });
                return Tex.Count - 1;
            }

            private static (byte[], int, int) Halve(byte[] src, int w, int h)
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var dst = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                        for (int c = 0; c < 4; c++)
                        {
                            int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                            int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                            int s = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                            dst[(y * nw + x) * 4 + c] = (byte)((s + 2) >> 2);
                        }
                return (dst, nw, nh);
            }
        }
    }
}
