using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SimplifiedUniversalExporter.Core
{
    // =========================================================================
    // EAID -> disk path resolution
    // =========================================================================
    //
    // EAID URIs look like:
    //   eaid://f1_2021_vehicle_package/teams/mclaren/materials/mclaren_paint.material
    //   eaid://f1_2021_vehicle_package/teams/mclaren/textures/mclaren_paint_d.tif
    //   eaid://f1_2021_vehicle_package/shared/generics/textures/cf_pattern_1_d.tif
    //
    // Disk layout under {packageRoot} (e.g. "f1_2021_vehicle_package/"):
    //   teams/mclaren/materials/mclaren_paint!!!temp000.material   <- descriptor
    //   teams/mclaren/textures/mclaren_paint_d!!!temp000.tif        <- descriptor stub
    //   teams/mclaren/textures/mclaren_paint_d.tif!!!temp000.image  <- image metadata
    //   teams/mclaren/textures/mclaren_paint_d.tif!!!temp001.image  <- *** pixel data ***
    //   teams/mclaren/textures/mclaren_paint_d.tif!!!temp000.sampler
    //
    // The "!!!tempNNN" marker is always injected between the base name and
    // the extension. For image payloads the file extension becomes ".image"
    // but the ".tif" stays in the name before the "!!!".

    public static class EaidPathResolver
    {
        /// <summary>
        /// Converts an EAID URI into the "logical" relative path (stripping the
        /// "eaid://{packageName}/" prefix). Returns null on malformed URIs.
        /// </summary>
        public static string StripEaidPrefix(string eaid)
        {
            if (string.IsNullOrEmpty(eaid)) return null;
            const string scheme = "eaid://";
            if (!eaid.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
            int slash = eaid.IndexOf('/', scheme.Length);
            if (slash < 0) return null;
            return eaid.Substring(slash + 1);
        }

        /// <summary>
        /// For an EAID pointing to a descriptor-type resource
        /// (.material, .tif, .image, etc.), build the "{stem}!!!temp000{ext}" path.
        /// </summary>
        public static string ResolveDescriptor(string eaid, string packageRoot)
        {
            string rel = StripEaidPrefix(eaid);
            if (rel == null) return null;
            return ResolveRelDescriptor(rel, packageRoot);
        }

        private static string ResolveRelDescriptor(string rel, string packageRoot)
        {
            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string dir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string file = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;

            int dot = file.LastIndexOf('.');
            string stem = (dot >= 0) ? file.Substring(0, dot) : file;
            string ext = (dot >= 0) ? file.Substring(dot) : "";

            string name = $"{stem}!!!temp000{ext}";
            return Path.Combine(packageRoot, dir.Replace('/', Path.DirectorySeparatorChar), name);
        }

        /// <summary>
        /// For a texture EAID (ending ".tif"), return paired paths:
        /// - descriptorPath: the small "...!!!temp000.tif" header
        /// - imageMetaPath:  the "....tif!!!temp000.image" descriptor
        /// - imagePayloadPath: the "....tif!!!temp001.image" block-compressed data
        /// All three are needed together to decode the texture.
        /// </summary>
        public static (string descriptorPath, string imageMetaPath, string imagePayloadPath)
            ResolveTexturePaths(string textureEaid, string packageRoot)
        {
            string rel = StripEaidPrefix(textureEaid);
            if (rel == null) return (null, null, null);

            int lastSlash = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            string dir = (lastSlash >= 0) ? rel.Substring(0, lastSlash) : "";
            string file = (lastSlash >= 0) ? rel.Substring(lastSlash + 1) : rel;

            int dot = file.LastIndexOf('.');
            string stem = (dot >= 0) ? file.Substring(0, dot) : file;
            string ext = (dot >= 0) ? file.Substring(dot) : "";

            string descriptor = Path.Combine(
                packageRoot, dir.Replace('/', Path.DirectorySeparatorChar),
                $"{stem}!!!temp000{ext}");
            string imageMeta = Path.Combine(
                packageRoot, dir.Replace('/', Path.DirectorySeparatorChar),
                $"{stem}{ext}!!!temp000.image");
            string imagePayload = Path.Combine(
                packageRoot, dir.Replace('/', Path.DirectorySeparatorChar),
                $"{stem}{ext}!!!temp001.image");

            return (descriptor, imageMeta, imagePayload);
        }
    }

    // =========================================================================
    // .material parsing
    // =========================================================================
    //
    // A .material file contains a shader EAID, parameter names, sampler slot
    // names, and one texture EAID per sampler slot (plus a paired .sampler
    // EAID that we ignore). Layout of the strings region (observed order):
    //
    //   [0]   eaid://shader_package/.../<shader>.nefx2
    //   [1..] me_<scalarParam>*           (shader-scalar parameter names)
    //   [k..] me_<samplerName>Map         (sampler slot names, e.g. me_albedoMap)
    //         me_<samplerName>            (paired scalar for each sampler)
    //   [N..] eaid://.../tex.tif          (texture N, matching sampler order)
    //         eaid://.../tex.tif.sampler  (paired sampler EAID)
    //
    // For a WPF preview we only need the DIFFUSE texture. Heuristic order:
    //   1. texture ending "_d.tif" (diffuse)
    //   2. texture ending "_c.tif" (colour variant, e.g. paint_c)
    //   3. first non-normal, non-specular .tif
    //   4. first .tif found

    /// <summary>
    /// A single typed parameter read from the constant buffer of a
    /// .material file. Float scalars / vec2 / vec3 / vec4 land in
    /// <see cref="Floats"/>; u32 heap-index and boolean-config land in
    /// <see cref="U32"/>. Exactly one of the two is populated.
    ///
    /// Decoded from the 36-byte descriptor block format documented in
    /// References/MaterialFormatNotes/. See ParseStructuredCBuffer for
    /// the reader.
    /// </summary>
    public sealed class MaterialParameter
    {
        public string Name { get; internal set; }

        /// <summary>"f32" / "vec2" / "vec3" / "vec4" / "u32".</summary>
        public string Kind { get; internal set; }

        /// <summary>For float-typed params (Kind = f32/vec2/vec3/vec4).
        /// Length is 1 / 2 / 3 / 4 depending on Kind. Null for u32.</summary>
        public float[] Floats { get; internal set; }

        /// <summary>For Kind="u32". Default 0 for non-u32 params.</summary>
        public uint U32 { get; internal set; }
    }

    public sealed class MaterialFile
    {
        public string ShaderEaid { get; internal set; }

        /// <summary>Texture EAIDs in file order, paired 1:1 with SamplerSlotNames.</summary>
        public IReadOnlyList<string> TextureEaids { get; internal set; }

        /// <summary>Sampler-slot names (e.g. "me_albedoMap", "me_logoMap") in file order.</summary>
        public IReadOnlyList<string> SamplerSlotNames { get; internal set; }

        /// <summary>
        /// Sampler-slot name -> texture EAID.
        /// Built by pairing SamplerSlotNames[i] with TextureEaids[i]; verified
        /// against real McLaren materials (paint, carbon, detail, number_N,
        /// mirror, wheel_hub, steeringwheel) -- the index alignment is reliable.
        /// </summary>
        public IReadOnlyDictionary<string, string> BindingsBySlot { get; internal set; }

        /// <summary>
        /// Typed constant-buffer parameters, keyed by their full me_* name.
        /// Populated by the structured parser when the .material file
        /// matches the F1 25 / 26 / character_package layout (verified
        /// against 105 sample files). Stays empty for materials that
        /// only round-trip through the legacy string-extraction path.
        ///
        /// Common entries you can query:
        ///   - "me_logoColour"  (vec3) -- band color of tyre sidewalls,
        ///                                 sponsor logo lacquer
        ///   - "me_lacquerColour"/"me_dirtAlbedo"/"me_flakesColourInfluence"
        ///                       (vec3) -- car paint tints
        ///   - "me_baseColour"/"me_fuzzColour"/"me_targetPrimary/Secondary
        ///                       Colour" (vec3) -- character skin / HSL
        ///   - "me_uOffset"/"me_vOffset" (f32) -- logo-atlas UV translation
        ///   - "me_*HeapIndex"   (u32) -- bindless descriptor-heap slot
        /// </summary>
        public IReadOnlyDictionary<string, MaterialParameter> Parameters { get; internal set; }

        /// <summary>Convenience: read a vec3 parameter, or null if absent.</summary>
        public float[] GetVec3(string name)
        {
            if (Parameters == null || !Parameters.TryGetValue(name, out var p)) return null;
            if (p.Kind != "vec3" || p.Floats == null || p.Floats.Length < 3) return null;
            return new[] { p.Floats[0], p.Floats[1], p.Floats[2] };
        }

        /// <summary>Convenience: read a scalar float parameter, or null if absent.</summary>
        public float? GetFloat(string name)
        {
            if (Parameters == null || !Parameters.TryGetValue(name, out var p)) return null;
            if (p.Kind != "f32" || p.Floats == null || p.Floats.Length < 1) return null;
            return p.Floats[0];
        }

        /// <summary>
        /// Picks the most visually-important texture for a WPF preview.
        ///
        /// Recognised sampler-slot names (from inspection of the 3DSimEd
        /// import plugin's string table):
        ///   me_albedoMap        -- paint atlas (body)
        ///   me_diffuseMap       -- diffuse base (carbon, detail, wheel)
        ///   me_logoMap          -- driver number / team logo decal
        ///   me_sponsorMap       -- sponsor sticker decal
        ///   me_metallicMap      -- metallic mask (auxiliary, skipped)
        ///   me_blendMap         -- blend mask (auxiliary, skipped)
        ///   me_smoothTarmacMap  -- track-only, n/a for cars
        ///
        /// The "material kind" hint (derived from the material EAID stem,
        /// e.g. "mclaren_number_4", "mclaren_paint", "mclaren_carbon1")
        /// steers us toward the right sampler slot:
        ///
        ///   NUMBER materials     -> prefer me_logoMap (the driver decal,
        ///                           e.g. mclaren_driver_4.tif). Without
        ///                           this the number slot renders as base
        ///                           paint and the number itself is
        ///                           invisible.
        ///
        ///   Everything else      -> prefer me_albedoMap (paint family),
        ///                           then me_diffuseMap (carbon/detail/
        ///                           wheel families), then me_sponsorMap,
        ///                           then any non-auxiliary binding.
        /// </summary>
        public string ResolvePreviewTextureEaid(string materialEaidOrNameHint = null)
        {
            if (BindingsBySlot == null || BindingsBySlot.Count == 0) return null;

            bool isNumber = !string.IsNullOrEmpty(materialEaidOrNameHint) &&
                            materialEaidOrNameHint.IndexOf("number_",
                                StringComparison.OrdinalIgnoreCase) >= 0;

            // For "number" materials the body paint (me_albedoMap) is the SAME
            // texture as the regular body, so picking it loses the whole point
            // of the slot -- prefer the driver decal instead.
            if (isNumber && BindingsBySlot.TryGetValue("me_logoMap", out var logo))
                return logo;

            if (BindingsBySlot.TryGetValue("me_albedoMap", out var albedo))
                return albedo;
            if (BindingsBySlot.TryGetValue("me_diffuseMap", out var diffuse))
                return diffuse;

            // Sponsor sticker overlay -- visible decal, prefer it before
            // we give up and grab whichever slot happens to come first.
            if (BindingsBySlot.TryGetValue("me_sponsorMap", out var sponsor))
                return sponsor;

            // Some shaders (generics/vehicle_revs etc.) use other slot names --
            // fall back to any slot that isn't obviously an auxiliary channel.
            foreach (var kv in BindingsBySlot)
            {
                string slot = kv.Key;
                if (slot.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("surface", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("damage", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("dirt", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("flakes", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("metallic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("blend", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (slot.IndexOf("tarmac", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return kv.Value;
            }

            return TextureEaids.Count > 0 ? TextureEaids[0] : null;
        }

        /// <summary>Legacy alias -- kept for API compatibility.</summary>
        public string ResolveDiffuseTextureEaid() => ResolvePreviewTextureEaid(null);
    }

    /// <summary>
    /// FIX 17v2: read shader parameter values directly from a .material file
    /// using the GfxMaterialRes layout from the EgoEngine 010 template.
    /// Returns safe defaults on any parse problem so callers never have to
    /// null-check. Purely additive -- does NOT replace MaterialReader.Parse,
    /// which keeps computing sampler bindings the existing way.
    /// </summary>
    public static class MaterialParameterReader
    {
        // .material binary layout (per the GfxMaterialRes 010 template):
        //   header (16 bytes):
        //     u32 parameterCount
        //     u32 fxNameOffset
        //     u32 paramNameOffset      // rel to data_pool_base
        //     u32 resourceNameOffset   // rel to data_pool_base
        //   then parameterCount * 16-byte ParameterDef entries:
        //     u32 nameOffset           // rel to (data_pool_base + paramNameOffset)
        //     u32 type                 // 1=CONSTANT_BUFFER (float), 2=TEXTURE,
        //                              // 3=SAMPLER_STATE, others = unused here
        //     u32 size                 // bytes (4=float, 12=vec3, 16=vec4)
        //     u32 dataOffset           // for type 1: rel to data_pool_base
        //                              // for type 2/3: rel to (data_pool_base
        //                              //                       + resourceNameOffset)
        //   data_pool_base = 16 + 16 * parameterCount
        //
        // Verified 100% correct on all 207 .material files in the McLaren 2021
        // package by cross-checking each parameter name -> value with the .bt
        // template's offset math.
        //
        // For F1 2021 McLaren the relevant me_uOffset / me_vOffset values are:
        //   mclaren_paint    -> u=+0.10, v=0.00
        //   mclaren_number_* -> u=+0.14, v=0.00
        //   everything else  -> (0, 0)
        // These are additive UV translations the vehicle_metallic_paint
        // shader applies to me_logoMap sample positions. Without them the
        // overlay decals on body panels render with their atlas sub-rect
        // shifted ~100 pixels left of where the game shows them.
        public static (float U, float V) ReadLogoUvOffset(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 16) return (0f, 0f);
                int parameterCount = BitConverter.ToInt32(data, 0);
                int paramNameOffset = BitConverter.ToInt32(data, 8);
                if (parameterCount <= 0 || parameterCount > 256) return (0f, 0f);
                int dataPoolBase = 16 + 16 * parameterCount;
                if (dataPoolBase > data.Length) return (0f, 0f);
                int nameBase = dataPoolBase + paramNameOffset;
                if (nameBase < 0 || nameBase >= data.Length) return (0f, 0f);

                float u = 0f, v = 0f;
                for (int i = 0; i < parameterCount; i++)
                {
                    int eo = 16 + i * 16;
                    if (eo + 16 > data.Length) break;
                    uint nameOfs = BitConverter.ToUInt32(data, eo);
                    uint type = BitConverter.ToUInt32(data, eo + 4);
                    uint size = BitConverter.ToUInt32(data, eo + 8);
                    uint dataOfs = BitConverter.ToUInt32(data, eo + 12);
                    if (type != 1 || size != 4) continue;

                    int np = nameBase + (int)nameOfs;
                    if (np < 0 || np >= data.Length) continue;
                    int ne = np;
                    while (ne < data.Length && data[ne] != 0) ne++;
                    if (ne <= np) continue;
                    string name = Encoding.ASCII.GetString(data, np, ne - np);

                    int vp = dataPoolBase + (int)dataOfs;
                    if (vp < 0 || vp + 4 > data.Length) continue;
                    float val = BitConverter.ToSingle(data, vp);
                    if (float.IsNaN(val) || float.IsInfinity(val)) continue;

                    if (name == "me_uOffset") u = val;
                    else if (name == "me_vOffset") v = val;
                }
                return (u, v);
            }
            catch
            {
                return (0f, 0f);
            }
        }
    }

    public static class MaterialReader
    {
        public static MaterialFile FromFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Parse(bytes);
        }

        public static MaterialFile Parse(byte[] data)
        {
            var strings = ExtractAsciiStrings(data, minLen: 4);

            string shader = null;
            var textureEaids = new List<string>();
            var samplerSlotNames = new List<string>();

            foreach (var s in strings)
            {
                if (s.StartsWith("eaid://", StringComparison.OrdinalIgnoreCase))
                {
                    if (s.EndsWith(".nefx2", StringComparison.OrdinalIgnoreCase))
                    {
                        if (shader == null) shader = s;
                    }
                    else if (s.EndsWith(".sampler", StringComparison.OrdinalIgnoreCase))
                    {
                        // skip -- paired with each texture EAID, we only
                        // need the texture side
                    }
                    else if (s.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                             s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                             s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             s.EndsWith(".texarray", StringComparison.OrdinalIgnoreCase))
                    {
                        // .texarray bindings (added 2026-04-29) carry classic-tyre
                        // diffuse/normal/surface assets that resolve to multi-slice
                        // texture arrays. Treated identically to a .tif binding from
                        // here on -- the slot resolver picks them via the same
                        // me_diffuseMap / me_normalMap key, and the texture cache
                        // routes them through the texarray-aware decoder branch
                        // when the on-disk descriptor's ArraySize > 1.
                        //
                        // Without this branch the tyre material's me_diffuseMap
                        // binding (c_03_sidewall_diffuse.texarray) was silently
                        // dropped here -- the resolver then fell back to the next
                        // available texture, which on tyres is the BC5 normal map,
                        // which the BC1/BC2/BC3 decoder correctly refused. That
                        // chain reads in the loader log as "Unsupported image
                        // format 65" and "no material-EAID mapping" -> gray
                        // fallback. Accepting .texarray here ends that chain at
                        // the parser instead of three layers down.
                        textureEaids.Add(s);
                    }
                }
                else if (s.StartsWith("me_", StringComparison.Ordinal))
                {
                    // Car-family materials use slot names like me_diffuseMap /
                    // me_albedoMap / me_normalMap (suffix "Map"). Character
                    // and helmet materials use the same role names but with
                    // suffix "Tex" instead (me_diffuseTex, me_normalTex,
                    // me_maskTex, me_surfaceTex). Both refer to identical
                    // texture-binding slots; accept either form so the
                    // helmet/character pipelines populate BindingsBySlot the
                    // same way the car pipeline does.
                    //
                    // Skip *TexHeapIndex / *MapHeapIndex variants -- those are
                    // bindless-heap-index integer parameters that appear in the
                    // same file but are NOT sampler slots and don't pair with
                    // a texture EAID.
                    bool isMap = s.EndsWith("Map", StringComparison.Ordinal);
                    bool isTex = s.EndsWith("Tex", StringComparison.Ordinal);
                    bool isHeapIndex = s.EndsWith("HeapIndex", StringComparison.Ordinal);
                    if (!isHeapIndex && (isMap || isTex))
                        samplerSlotNames.Add(s);
                }
            }

            // Pair samplers 1:1 with textures. Verified across the 2021
            // McLaren material set: sampler-slot names appear first as a
            // contiguous alphabetised group, followed by texture EAIDs in
            // the same alphabetical order. The two lists zip cleanly by
            // index. An earlier "adjacency" attempt at this pairing
            // (walk strings in order, pair each me_*Map with the next
            // tif EAID) collapsed to a single binding because the sampler
            // names and texture EAIDs are NOT interleaved in this format.
            int pairCount = Math.Min(samplerSlotNames.Count, textureEaids.Count);
            var bindings = new Dictionary<string, string>(
                pairCount * 2, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pairCount; i++)
            {
                string slot = samplerSlotNames[i];
                string eaid = textureEaids[i];
                bindings[slot] = eaid;

                // Cross-alias me_*Tex <-> me_*Map so downstream resolvers
                // that query under one naming convention still find bindings
                // authored under the other. ResolvePaint / ResolveCarbon /
                // ResolveGeneric all look up by the "Map" form -- without the
                // alias, helmet materials (which only carry "Tex" names)
                // would resolve to nothing even though the texture EAID is
                // sitting right there.
                if (slot.EndsWith("Tex", StringComparison.Ordinal))
                {
                    string aliased = slot.Substring(0, slot.Length - 3) + "Map";
                    if (!bindings.ContainsKey(aliased)) bindings[aliased] = eaid;
                }
                else if (slot.EndsWith("Map", StringComparison.Ordinal))
                {
                    string aliased = slot.Substring(0, slot.Length - 3) + "Tex";
                    if (!bindings.ContainsKey(aliased)) bindings[aliased] = eaid;
                }
            }

            // Structured cbuf parse (new) -- pulls typed parameter VALUES out
            // of the descriptor blocks + constant buffer. Purely additive:
            // returns an empty dictionary if the file's binary layout doesn't
            // match the documented F1 25 format, leaving all existing fields
            // (BindingsBySlot etc.) untouched.
            IReadOnlyDictionary<string, MaterialParameter> parameters =
                ParseStructuredCBuffer(data)
                ?? (IReadOnlyDictionary<string, MaterialParameter>)
                    new Dictionary<string, MaterialParameter>(0, StringComparer.OrdinalIgnoreCase);

            return new MaterialFile
            {
                ShaderEaid = shader,
                TextureEaids = textureEaids,
                SamplerSlotNames = samplerSlotNames,
                BindingsBySlot = bindings,
                Parameters = parameters,
            };
        }

        /// <summary>
        /// Decodes the constant-buffer parameter values from a .material file
        /// following the format documented in References/MaterialFormatNotes/.
        ///
        /// Returns null on ANY sanity-check failure -- the caller falls back
        /// to the legacy string-extraction path with no behavioural change.
        ///
        /// Layout (verified on 104/105 corpus files):
        ///   0x00 .. 0x1F: 32-byte header
        ///     u32 descCount     -- number of descriptor blocks
        ///     u32 bufferCount   -- 0 or 1
        ///     u32 cbufSize      -- bytes of cbuf data
        ///     u32 preStringsBytes
        ///         (= cbufSize + 8 when bufferCount > 0, else 0)
        ///     u32 postShader    -- cumulative offsets through the
        ///     u32 postNames        string region (used to validate the
        ///     u32 postTex1         walk but not directly needed here)
        ///     u32 postTex2
        ///   0x20 + i*36: descriptor block (9 u32 fields)
        ///   blocksEnd + 0: cbuf data (cbufSize bytes)
        ///   blocksEnd + cbufSize: 8-byte gap before strings
        ///   blocksEnd + preStringsBytes: shader EAID, name pool, ...
        ///
        /// Each descriptor block:
        ///   +0x00 nameOffset    -- bytes into the name pool
        ///   +0x04 flag1         -- 1 = cbuf param/heap-idx, 2 = texture ref
        ///   +0x08 typeCode      -- 2=tex, 3=f32, 6=u32,
        ///                          0x20003/0x30003/0x40003 = vec2/3/4
        ///   +0x0C f3            -- 1 = ordinary param, 3 = bindless heap idx
        ///   +0x10 elementSize   -- byte size (4/12/16 for cbuf params)
        ///   +0x14 reserved      -- 0 for params, 0xFFFFFFFF for textures
        ///   +0x18 f6            -- shader register slot (not used here)
        ///   +0x1C cbufOffset    -- byte offset within the cbuf
        ///   +0x20 texEaidOffset -- for heap-idx blocks: byte offset into
        ///                          the texture-EAID set 2 (not used here)
        /// </summary>
        private static IReadOnlyDictionary<string, MaterialParameter> ParseStructuredCBuffer(
            byte[] d)
        {
            if (d == null || d.Length < 32) return null;

            uint descCount = BitConverter.ToUInt32(d, 0x00);
            uint bufferCount = BitConverter.ToUInt32(d, 0x04);
            uint cbufSize = BitConverter.ToUInt32(d, 0x08);
            uint preStrings = BitConverter.ToUInt32(d, 0x0C);

            // Reject implausible header values up front. These bounds are
            // chosen to cover the entire 105-file reference corpus with
            // comfortable margin and reject random binary garbage that
            // would otherwise produce phantom parameter entries.
            if (descCount > 256) return null;
            if (bufferCount > 1) return null;
            if (cbufSize > 4096) return null;
            if (bufferCount == 0 && (cbufSize != 0 || preStrings != 0)) return null;
            if (bufferCount == 1 && preStrings != cbufSize + 8) return null;

            int blockBase = 0x20;
            int blockEnd = blockBase + (int)descCount * 36;
            if (blockEnd > d.Length) return null;

            // Cbuf data sits immediately after the descriptor blocks. The
            // 8-byte gap implied by preStrings = cbufSize + 8 is at the
            // tail of the cbuf region (between data and the shader EAID),
            // NOT at the start -- verified against tire-compound colors
            // (every me_logoColour decoded matches the documented Pirelli
            // palette, see References/MaterialFormatNotes/05_*.md).
            int cbufStart = blockEnd;
            if (cbufStart + (int)cbufSize > d.Length) return null;

            // Shader EAID starts after the full preStrings region.
            int shaderEaidOff = blockEnd + (int)preStrings;
            if (shaderEaidOff < 0 || shaderEaidOff >= d.Length) return null;

            // Name pool sits right after the shader EAID's null terminator.
            string shaderEaid = ReadCStringAt(d, shaderEaidOff);
            if (string.IsNullOrEmpty(shaderEaid) ||
                !shaderEaid.StartsWith("eaid://", StringComparison.OrdinalIgnoreCase))
            {
                // descCount > 0 with a missing shader EAID means the file
                // doesn't match the documented layout -- bail to the
                // legacy parser instead of guessing.
                if (descCount > 0) return null;
            }
            int nameBase = shaderEaidOff + (shaderEaid?.Length ?? -1) + 1;

            var paramsOut = new Dictionary<string, MaterialParameter>(
                (int)descCount, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < (int)descCount; i++)
            {
                int off = blockBase + i * 36;
                if (off + 36 > d.Length) return null;

                uint nameOff = BitConverter.ToUInt32(d, off + 0x00);
                uint flag1 = BitConverter.ToUInt32(d, off + 0x04);
                uint typeCode = BitConverter.ToUInt32(d, off + 0x08);
                uint f3 = BitConverter.ToUInt32(d, off + 0x0C);
                uint elementSize = BitConverter.ToUInt32(d, off + 0x10);
                uint cbufOff = BitConverter.ToUInt32(d, off + 0x1C);

                // We only care about constant-buffer-resident params here
                // (flag1 == 1). Texture-resource blocks (flag1 == 2) carry
                // a string EAID rather than a value, and BindingsBySlot
                // already covers them via the legacy walk.
                if (flag1 != 1) continue;

                // Resolve the parameter name. Defensive bounds-check; on
                // any unreadable name we skip this block rather than
                // failing the whole parse, so a single weird entry doesn't
                // kill the rest of a perfectly-decodable material.
                int namePos = nameBase + (int)nameOff;
                if (namePos < 0 || namePos >= d.Length) continue;
                string name = ReadCStringAt(d, namePos);
                if (string.IsNullOrEmpty(name)) continue;

                int valuePos = cbufStart + (int)cbufOff;
                if (valuePos + (int)elementSize > cbufStart + (int)cbufSize) continue;

                string kind;
                float[] floats = null;
                uint u32 = 0;
                switch (typeCode)
                {
                    case 3:
                        if (elementSize != 4) continue;
                        kind = "f32";
                        floats = new[] { BitConverter.ToSingle(d, valuePos) };
                        break;
                    case 6:
                        if (elementSize != 4) continue;
                        kind = "u32";
                        u32 = BitConverter.ToUInt32(d, valuePos);
                        break;
                    case 0x20003:
                        if (elementSize != 8) continue;
                        kind = "vec2";
                        floats = new[] {
                            BitConverter.ToSingle(d, valuePos),
                            BitConverter.ToSingle(d, valuePos + 4),
                        };
                        break;
                    case 0x30003:
                        if (elementSize != 12) continue;
                        kind = "vec3";
                        floats = new[] {
                            BitConverter.ToSingle(d, valuePos),
                            BitConverter.ToSingle(d, valuePos + 4),
                            BitConverter.ToSingle(d, valuePos + 8),
                        };
                        break;
                    case 0x40003:
                        if (elementSize != 16) continue;
                        kind = "vec4";
                        floats = new[] {
                            BitConverter.ToSingle(d, valuePos),
                            BitConverter.ToSingle(d, valuePos + 4),
                            BitConverter.ToSingle(d, valuePos + 8),
                            BitConverter.ToSingle(d, valuePos + 12),
                        };
                        break;
                    default:
                        // Unknown typeCode -- skip this param but keep
                        // walking the rest.
                        continue;
                }

                paramsOut[name] = new MaterialParameter
                {
                    Name = name,
                    Kind = kind,
                    Floats = floats,
                    U32 = u32,
                };
            }

            return paramsOut;
        }

        private static string ReadCStringAt(byte[] d, int offset)
        {
            if (offset < 0 || offset >= d.Length) return null;
            int end = offset;
            while (end < d.Length && d[end] >= 0x20 && d[end] < 0x7F) end++;
            if (end == offset) return null;
            return Encoding.ASCII.GetString(d, offset, end - offset);
        }

        private static List<string> ExtractAsciiStrings(byte[] data, int minLen = 4)
        {
            var result = new List<string>();
            int i = 0;
            while (i < data.Length)
            {
                int start = i;
                while (i < data.Length && data[i] >= 0x20 && data[i] < 0x7F)
                    i++;
                int len = i - start;
                if (len >= minLen)
                    result.Add(Encoding.ASCII.GetString(data, start, len));
                i++;
            }
            return result;
        }
    }

    // =========================================================================
    // Image descriptor + BC1/BC2/BC3 decoders + texture cache
    // =========================================================================
    //
    // Image descriptor (40 bytes, "...temp000.image"):
    //   @0x00  u32 type       (2 = 2D texture)
    //   @0x04  u32
    //   @0x08  u32 fmtCode    (52/54 = BC1, 55 = BC2, 57 = BC3, 65/69 = BC5)
    //   @0x0C  u32 width
    //   @0x10  u32 height
    //   @0x14  u32
    //   @0x18  u32 mipCount
    //   @0x1C..@0x28 u32 pad
    //
    // Block-compression facts:
    //   BC1 = 8 bytes per 4x4 block  -> 0.5 bytes/pixel base
    //   BC2 = 16 bytes per 4x4 block (uncompressed 4-bit alpha + BC1 colour)
    //                                -- used by cf_pattern_*_d carbon weaves
    //   BC3 = 16 bytes per 4x4 block -> 1.0 bytes/pixel base
    //   BC5 = 16 bytes per 4x4 block (RG two-channel; skipped for WPF)

    public sealed class ImageDescriptor
    {
        public uint FormatCode;
        public int Width;
        public int Height;
        public int MipCount;

        /// <summary>
        /// Number of array elements in this resource. Reads as 1 for plain
        /// textures, &gt;1 for texture arrays (e.g. classic tyre diffuse:
        /// c_03_sidewall_diffuse.texarray reports ArraySize=2 here, and its
        /// payload empirically contains 4 mip-chains stacked back-to-back --
        /// the actual stored slice count differs from this field for
        /// texarrays Codemasters built before F1 22 unified the format. Use
        /// IsTextureArray to gate the multi-slice decoder; never trust this
        /// number alone for slice-offset math, derive it from
        /// payloadSize / sliceMipChainBytes instead).
        /// </summary>
        public int ArraySize;

        public static ImageDescriptor FromFile(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 32) throw new InvalidDataException("Image descriptor too small.");

            return new ImageDescriptor
            {
                FormatCode = BitConverter.ToUInt32(d, 0x08),
                Width = BitConverter.ToInt32(d, 0x0C),
                Height = BitConverter.ToInt32(d, 0x10),
                MipCount = BitConverter.ToInt32(d, 0x18),
                ArraySize = BitConverter.ToInt32(d, 0x1C),
            };
        }

        // Format 70 (0x46) = BC7. Despite the close numeric proximity to
        // the DXGI BC1_TYPELESS code (which is also 70), Codemasters' format
        // enum is custom and not DXGI -- empirically verified on
        // c_03_sidewall_diffuse.texarray:
        //   * header reports format 70, ArraySize=2, 1024x1024, 9 mips
        //   * payload is 2,796,192 bytes
        //   * 1024x1024 with 9-mip chain at 16 bytes/block = 1,398,096 bytes
        //   * 2,796,192 / 1,398,096 = exactly 2 (matches ArraySize)
        // BC7 stores 16 bytes per 4x4 block, which is what makes the math
        // line up. A BC1 interpretation (8 bytes/block) gives 699,048 bytes
        // per slice -- and 2,796,192 / 699,048 = 4, which would need
        // ArraySize=4, but the header says 2. The match with BC7's block
        // size + ArraySize=2 is the deciding evidence.
        //
        // BC7 is a much more complex format than BC1: 8 internal modes,
        // partition tables, per-mode subset counts, p-bits, optional index
        // rotation. Decoded by BlockCompressionDecoder.DecodeBC7.
        public bool IsBC1 => FormatCode == 52 || FormatCode == 54;
        public bool IsBC3 => FormatCode == 57;
        // Format code mapping (verified empirically against the F1 25 corpus):
        //   52, 54 = BC1 (DXT1, RGB)
        //   55     = BC2 (DXT3, RGB + 4-bit alpha)
        //   57     = BC3 (DXT5, RGB + 8-bit alpha)
        //   65     = BC5 (two-channel; normal maps + masks)
        //   69     = BC7 variant (RGBA with alpha, advanced compression);
        //            previously misclassified as BC5 alongside 65, which
        //            collapsed character surface (_s) maps -- whose RGBA
        //            content includes per-pixel sponsor colour overlays
        //            (PicPay / Libertex / KICK on the Hulkenberg helmet) --
        //            into single-channel grayscale.
        //   70     = BC7 (canonical, no SRGB variant)
        public bool IsBC5 => FormatCode == 65;
        public bool IsBC7 => FormatCode == 69 || FormatCode == 70;

        /// <summary>
        /// True when this descriptor announces a multi-slice texture array
        /// (texarray). For our preview we render the FIRST slice only; later
        /// slices are typically alternate compounds (hard/medium/soft/...)
        /// or LOD variants the engine selects at runtime via shader uniforms.
        /// </summary>
        public bool IsTextureArray => ArraySize > 1;

        /// <summary>
        /// Codemasters-specific two-channel format used by shared/generics
        /// textures: cf_pattern_*_d (carbon fibre diffuse), rev_lights, etc.
        /// Same byte layout as BC5 (two 8-byte alpha-style sub-blocks per
        /// 4x4 block), but the two channels aren't really R and G -- the
        /// R channel is a mask with only 2 unique patterns across the
        /// whole texture, and the G channel carries the per-block detail.
        /// The game's shader combines them; for a WPF preview we decode
        /// the G channel as grayscale, which produces a plausible carbon-
        /// fibre-ish look even though it's not the full shader output.
        /// </summary>
        public bool IsBC2 => FormatCode == 55;
    }

    public static class BlockCompressionDecoder
    {
        /// <summary>
        /// Decodes the base mip of a BC1 (DXT1) compressed image into
        /// BGRA32 pixels (row-major, top-left origin, stride = width*4).
        /// </summary>
        public static byte[] DecodeBC1(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = (width + 3) / 4;
            int blocksH = (height + 3) / 4;

            int srcOff = 0;
            for (int by = 0; by < blocksH; by++)
                for (int bx = 0; bx < blocksW; bx++)
                {
                    DecodeBC1Block(src, srcOff, dst, bx * 4, by * 4, width, height);
                    srcOff += 8;
                }
            return dst;
        }

        /// <summary>
        /// Decodes the base mip of a BC3 (DXT5) compressed image into BGRA32.
        /// </summary>
        public static byte[] DecodeBC3(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = (width + 3) / 4;
            int blocksH = (height + 3) / 4;

            int srcOff = 0;
            for (int by = 0; by < blocksH; by++)
                for (int bx = 0; bx < blocksW; bx++)
                {
                    DecodeBC3Block(src, srcOff, dst, bx * 4, by * 4, width, height);
                    srcOff += 16;
                }
            return dst;
        }

        /// <summary>
        /// Decodes the base mip of a BC2 (DXT3) compressed image into BGRA32.
        /// BC2 is used by Codemasters cf_pattern_*_d carbon weave textures
        /// (fmt code 55). Layout per 4x4 block (16 bytes):
        ///   - 8 bytes: 16 * 4-bit uncompressed alpha values (one per pixel)
        ///   - 8 bytes: BC1-style colour block in 4-colour mode (no 1-bit
        ///     alpha punch-through; alpha always comes from the 4-bit half)
        /// Previously this file misclassified fmt 55 as a proprietary
        /// two-channel grayscale format and decoded only the G channel,
        /// which made carbon panels render as dim monochrome noise.
        /// </summary>
        public static byte[] DecodeBC2(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = (width + 3) / 4;
            int blocksH = (height + 3) / 4;

            int srcOff = 0;
            for (int by = 0; by < blocksH; by++)
                for (int bx = 0; bx < blocksW; bx++)
                {
                    DecodeBC2Block(src, srcOff, dst, bx * 4, by * 4, width, height);
                    srcOff += 16;
                }
            return dst;
        }

        private static void DecodeBC2Block(byte[] src, int s, byte[] dst,
                                           int px, int py, int w, int h)
        {
            // Alpha block (first 8 bytes): 16 explicit 4-bit alpha values,
            // one per pixel in row-major order. Expand each nibble to 8
            // bits by replicating: a8 = (nibble << 4) | nibble.
            ulong alphaBits = 0;
            for (int i = 0; i < 8; i++)
                alphaBits |= (ulong)src[s + i] << (i * 8);

            // Colour block (last 8 bytes): like BC1 but always 4-colour mode,
            // never 3-colour + punch-through. Alpha comes only from the
            // explicit alpha block above.
            ushort c0 = (ushort)(src[s + 8] | (src[s + 9] << 8));
            ushort c1 = (ushort)(src[s + 10] | (src[s + 11] << 8));
            uint indices = BitConverter.ToUInt32(src, s + 12);

            byte r0, g0, b0, r1, g1, b1;
            Rgb565ToRgb888(c0, out r0, out g0, out b0);
            Rgb565ToRgb888(c1, out r1, out g1, out b1);

            var pal = new byte[4 * 3];
            pal[0] = b0; pal[1] = g0; pal[2] = r0;
            pal[3] = b1; pal[4] = g1; pal[5] = r1;
            pal[6] = (byte)((2 * b0 + b1) / 3);
            pal[7] = (byte)((2 * g0 + g1) / 3);
            pal[8] = (byte)((2 * r0 + r1) / 3);
            pal[9] = (byte)((b0 + 2 * b1) / 3);
            pal[10] = (byte)((g0 + 2 * g1) / 3);
            pal[11] = (byte)((r0 + 2 * r1) / 3);

            for (int y = 0; y < 4; y++)
            {
                int dy = py + y;
                if (dy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int dx = px + x;
                    if (dx >= w) continue;

                    int pixelIdx = y * 4 + x;
                    int cIdx = (int)((indices >> (pixelIdx * 2)) & 0x3);

                    int aNibble = (int)((alphaBits >> (pixelIdx * 4)) & 0xF);
                    byte alpha = (byte)((aNibble << 4) | aNibble);

                    int outOff = (dy * w + dx) * 4;
                    int palOff = cIdx * 3;
                    dst[outOff + 0] = pal[palOff + 0];
                    dst[outOff + 1] = pal[palOff + 1];
                    dst[outOff + 2] = pal[palOff + 2];
                    dst[outOff + 3] = alpha;
                }
            }
        }

        // ---- block kernels ----

        private static void DecodeBC1Block(byte[] src, int s, byte[] dst,
                                           int px, int py, int w, int h,
                                           byte overrideAlpha = 255)
        {
            ushort c0 = (ushort)(src[s + 0] | (src[s + 1] << 8));
            ushort c1 = (ushort)(src[s + 2] | (src[s + 3] << 8));
            uint indices = BitConverter.ToUInt32(src, s + 4);

            byte r0, g0, b0, r1, g1, b1;
            Rgb565ToRgb888(c0, out r0, out g0, out b0);
            Rgb565ToRgb888(c1, out r1, out g1, out b1);

            var palette = new byte[4 * 4];

            palette[0] = b0; palette[1] = g0; palette[2] = r0; palette[3] = overrideAlpha;
            palette[4] = b1; palette[5] = g1; palette[6] = r1; palette[7] = overrideAlpha;

            if (c0 > c1)
            {
                // 4-colour mode
                palette[8] = (byte)((2 * b0 + b1) / 3);
                palette[9] = (byte)((2 * g0 + g1) / 3);
                palette[10] = (byte)((2 * r0 + r1) / 3);
                palette[11] = overrideAlpha;
                palette[12] = (byte)((b0 + 2 * b1) / 3);
                palette[13] = (byte)((g0 + 2 * g1) / 3);
                palette[14] = (byte)((r0 + 2 * r1) / 3);
                palette[15] = overrideAlpha;
            }
            else
            {
                // 3-colour + transparent mode
                palette[8] = (byte)((b0 + b1) / 2);
                palette[9] = (byte)((g0 + g1) / 2);
                palette[10] = (byte)((r0 + r1) / 2);
                palette[11] = overrideAlpha;
                palette[12] = 0;
                palette[13] = 0;
                palette[14] = 0;
                palette[15] = 0;     // transparent
            }

            for (int y = 0; y < 4; y++)
            {
                int dy = py + y;
                if (dy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int dx = px + x;
                    if (dx >= w) continue;
                    int idx = (int)((indices >> ((y * 4 + x) * 2)) & 0x3);
                    int outOff = (dy * w + dx) * 4;
                    int palOff = idx * 4;
                    dst[outOff + 0] = palette[palOff + 0]; // B
                    dst[outOff + 1] = palette[palOff + 1]; // G
                    dst[outOff + 2] = palette[palOff + 2]; // R
                    dst[outOff + 3] = palette[palOff + 3]; // A
                }
            }
        }

        private static void DecodeBC3Block(byte[] src, int s, byte[] dst,
                                           int px, int py, int w, int h)
        {
            // Alpha block (first 8 bytes)
            byte a0 = src[s + 0];
            byte a1 = src[s + 1];

            var alphaPalette = new byte[8];
            alphaPalette[0] = a0;
            alphaPalette[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++)
                    alphaPalette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++)
                    alphaPalette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                alphaPalette[6] = 0;
                alphaPalette[7] = 255;
            }

            ulong alphaBits = 0;
            for (int i = 0; i < 6; i++)
                alphaBits |= (ulong)src[s + 2 + i] << (i * 8);

            // Colour block (last 8 bytes): same as BC1 but always "4-colour mode"
            ushort c0 = (ushort)(src[s + 8] | (src[s + 9] << 8));
            ushort c1 = (ushort)(src[s + 10] | (src[s + 11] << 8));
            uint indices = BitConverter.ToUInt32(src, s + 12);

            byte r0, g0, b0, r1, g1, b1;
            Rgb565ToRgb888(c0, out r0, out g0, out b0);
            Rgb565ToRgb888(c1, out r1, out g1, out b1);

            var colPalette = new byte[4 * 3];
            colPalette[0] = b0; colPalette[1] = g0; colPalette[2] = r0;
            colPalette[3] = b1; colPalette[4] = g1; colPalette[5] = r1;
            // BC3 always uses 4-colour mode regardless of c0 vs c1.
            colPalette[6] = (byte)((2 * b0 + b1) / 3);
            colPalette[7] = (byte)((2 * g0 + g1) / 3);
            colPalette[8] = (byte)((2 * r0 + r1) / 3);
            colPalette[9] = (byte)((b0 + 2 * b1) / 3);
            colPalette[10] = (byte)((g0 + 2 * g1) / 3);
            colPalette[11] = (byte)((r0 + 2 * r1) / 3);

            for (int y = 0; y < 4; y++)
            {
                int dy = py + y;
                if (dy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int dx = px + x;
                    if (dx >= w) continue;

                    int pixelIdx = y * 4 + x;
                    int cIdx = (int)((indices >> (pixelIdx * 2)) & 0x3);
                    int aIdx = (int)((alphaBits >> (pixelIdx * 3)) & 0x7);

                    int outOff = (dy * w + dx) * 4;
                    int palOff = cIdx * 3;
                    dst[outOff + 0] = colPalette[palOff + 0];
                    dst[outOff + 1] = colPalette[palOff + 1];
                    dst[outOff + 2] = colPalette[palOff + 2];
                    dst[outOff + 3] = alphaPalette[aIdx];
                }
            }
        }

        private static void Rgb565ToRgb888(ushort c, out byte r, out byte g, out byte b)
        {
            int rr = (c >> 11) & 0x1F;
            int gg = (c >> 5) & 0x3F;
            int bb = c & 0x1F;
            r = (byte)((rr << 3) | (rr >> 2));
            g = (byte)((gg << 2) | (gg >> 4));
            b = (byte)((bb << 3) | (bb >> 2));
        }

        // =================================================================
        // BC5 (Codemasters format codes 65 and 69)
        // =================================================================
        //
        // BC5 stores TWO independent unsigned channels per 4x4 block:
        //   * bytes 0-7:  channel 1 (typically R) -- 8-byte BC4 block
        //   * bytes 8-15: channel 2 (typically G) -- 8-byte BC4 block
        // Each BC4 block is laid out identically to BC3's alpha block:
        //   byte 0: endpoint 0 (e0)
        //   byte 1: endpoint 1 (e1)
        //   bytes 2-7: 16 x 3-bit indices selecting from an 8-entry palette
        //
        // For preview purposes we extract ONLY the R channel and write it
        // to all three BGR colour bytes (grayscale-R), with a small gain
        // applied so dark-but-detailed treads remain readable. Alpha is
        // forced opaque (BC5 doesn't store alpha).
        //
        // Why grayscale-R rather than (R, G, 0):
        //   * For tread/detail diffuse atlases the R channel IS the
        //     visible pattern. Grayscale-R gives clean dark grooves on
        //     lighter rubber.
        //   * For normal maps, decoding (R, G) literally as colour
        //     produces the classic purple-blue look that doesn't show
        //     surface shape clearly. Grayscale-R is a stable preview that
        //     shows the dominant variation.
        //
        // The 1.5x luminance gain mirrors the same approach used for
        // BC2/carbon previews -- helps readability without clipping low
        // detail.

        public static byte[] DecodeBC5AsGrayscaleR(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    int srcOff = (by * blocksW + bx) * 16;
                    if (srcOff + 16 > src.Length) return dst;
                    DecodeBC5BlockGrayscaleR(src, srcOff, dst,
                                             bx * 4, by * 4, width, height);
                }
            }
            return dst;
        }

        /// <summary>
        /// Decode BC5 by extracting the R channel raw, no value remapping.
        /// Output: (R, R, R, 255) per pixel where R is the BC4-decoded
        /// byte value (0..255). Use this for surface / mask / generic
        /// roughness textures where the actual R-channel values matter
        /// and shouldn't be remapped into a narrow band.
        ///
        /// Distinct from DecodeBC5AsGrayscaleR, which collapses R into
        /// 14..27 for the WPF tyre-rubber preview look. That clamping
        /// destroys roughness data on character / vehicle surface
        /// textures (e.g. nico_hulkenberg_helmet_s.tif came out as flat
        /// dark grey when decoded that way -- the actual data has full
        /// 0..255 spread, just compressed to a 13-level band).
        /// </summary>
        public static byte[] DecodeBC5AsRawR(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    int srcOff = (by * blocksW + bx) * 16;
                    if (srcOff + 16 > src.Length) return dst;
                    DecodeBC5BlockRawR(src, srcOff, dst,
                                       bx * 4, by * 4, width, height);
                }
            }
            return dst;
        }

        private static void DecodeBC5BlockRawR(byte[] src, int s, byte[] dst,
                                               int px, int py, int w, int h)
        {
            // Same BC4-block decode as DecodeBC5BlockGrayscaleR, but the
            // output R byte goes straight to the BGR channels without
            // the tyre-band 14..27 remap.
            byte e0 = src[s + 0];
            byte e1 = src[s + 1];

            var pal = new byte[8];
            pal[0] = e0;
            pal[1] = e1;
            if (e0 > e1)
            {
                for (int i = 1; i < 7; i++)
                    pal[i + 1] = (byte)(((7 - i) * e0 + i * e1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++)
                    pal[i + 1] = (byte)(((5 - i) * e0 + i * e1) / 5);
                pal[6] = 0;
                pal[7] = 255;
            }

            ulong indexBits = 0;
            for (int i = 0; i < 6; i++)
                indexBits |= (ulong)src[s + 2 + i] << (i * 8);

            for (int y = 0; y < 4; y++)
            {
                int dy = py + y;
                if (dy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int dx = px + x;
                    if (dx >= w) continue;

                    int pixelIdx = y * 4 + x;
                    int idx = (int)((indexBits >> (pixelIdx * 3)) & 0x7);
                    byte r = pal[idx];

                    int outOff = (dy * w + dx) * 4;
                    dst[outOff + 0] = r;   // B
                    dst[outOff + 1] = r;   // G
                    dst[outOff + 2] = r;   // R
                    dst[outOff + 3] = 255; // A
                }
            }
        }

        /// <summary>
        /// Decode BC5 as a proper tangent-space normal map. Unlike the
        /// grayscale-R variant (which is for WPF previews), this one
        /// extracts BOTH the R and G channels (the two BC4 sub-blocks)
        /// AND computes a Z channel via the standard normal-vector
        /// reconstruction:
        ///
        ///   nx = (R-128) / 127     (range -1..+1)
        ///   ny = (G-128) / 127
        ///   nz = sqrt(max(0, 1 - nx^2 - ny^2))
        ///   B_byte = nz * 127 + 128
        ///
        /// The result is the canonical tangent-space encoding every DCC
        /// expects on a normal-map texture: R = X, G = Y, B = Z, all in
        /// 0..255 with 128 representing zero. Used by the FBX export
        /// when writing <c>_n.png</c> sidecars so Blender's principled
        /// BSDF picks them up correctly.
        /// </summary>
        public static byte[] DecodeBC5AsNormalMap(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    int srcOff = (by * blocksW + bx) * 16;
                    if (srcOff + 16 > src.Length) return dst;
                    DecodeBC5BlockNormalMap(src, srcOff, dst,
                                            bx * 4, by * 4, width, height);
                }
            }
            return dst;
        }

        // Decode one 16-byte BC5 block raw -- the two BC4-style sub-blocks
        // are extracted into the PNG's R and G channels untouched.
        // No (-128)/127 sign-mapping, no Z reconstruction, no flipping --
        // whatever bytes the game stores are exactly what the PNG carries.
        //
        // The previous version reconstructed Z = sqrt(1 - X^2 - Y^2) and
        // wrote it to B, baking a "centered at 128" sign convention into
        // the file. Users who want to do that in Blender / their own
        // pipeline can compute it themselves; raw output is what the
        // game ships, so that's what we ship.
        //
        // B is forced to 0 (BC5 carries no third channel; setting it to
        // anything else would be invention). A is opaque.
        private static void DecodeBC5BlockNormalMap(byte[] src, int s, byte[] dst,
                                                    int px, int py, int w, int h)
        {
            var palR = BuildBC4Palette(src[s + 0], src[s + 1]);
            var palG = BuildBC4Palette(src[s + 8], src[s + 9]);
            ulong idxR = 0, idxG = 0;
            for (int i = 0; i < 6; i++)
            {
                idxR |= (ulong)src[s + 2 + i] << (i * 8);
                idxG |= (ulong)src[s + 10 + i] << (i * 8);
            }

            for (int y = 0; y < 4; y++)
            {
                int dy = py + y;
                if (dy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int dx = px + x;
                    if (dx >= w) continue;

                    int pi = y * 4 + x;
                    int iR = (int)((idxR >> (pi * 3)) & 0x7);
                    int iG = (int)((idxG >> (pi * 3)) & 0x7);
                    byte rB = palR[iR];
                    byte gB = palG[iG];

                    // BGRA order: B at +0, G at +1, R at +2, A at +3.
                    int dOff = (dy * w + dx) * 4;
                    dst[dOff + 0] = 0;    // B: unused (BC5 has no third channel)
                    dst[dOff + 1] = gB;   // G: raw BC5 G byte
                    dst[dOff + 2] = rB;   // R: raw BC5 R byte
                    dst[dOff + 3] = 255;
                }
            }
        }

        private static byte[] BuildBC4Palette(byte e0, byte e1)
        {
            var pal = new byte[8];
            pal[0] = e0;
            pal[1] = e1;
            if (e0 > e1)
            {
                for (int i = 1; i < 7; i++)
                    pal[i + 1] = (byte)(((7 - i) * e0 + i * e1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++)
                    pal[i + 1] = (byte)(((5 - i) * e0 + i * e1) / 5);
                pal[6] = 0;
                pal[7] = 255;
            }
            return pal;
        }

        private static void DecodeBC5BlockGrayscaleR(byte[] src, int s, byte[] dst,
                                                     int px, int py, int w, int h)
        {
            // Decode only the R channel (BC4 block at bytes 0-7).
            byte e0 = src[s + 0];
            byte e1 = src[s + 1];

            var pal = new byte[8];
            pal[0] = e0;
            pal[1] = e1;
            if (e0 > e1)
            {
                for (int i = 1; i < 7; i++)
                    pal[i + 1] = (byte)(((7 - i) * e0 + i * e1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++)
                    pal[i + 1] = (byte)(((5 - i) * e0 + i * e1) / 5);
                pal[6] = 0;
                pal[7] = 255;
            }

            ulong indexBits = 0;
            for (int i = 0; i < 6; i++)
                indexBits |= (ulong)src[s + 2 + i] << (i * 8);

            for (int y = 0; y < 4; y++)
            {
                int dy = py + y;
                if (dy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int dx = px + x;
                    if (dx >= w) continue;

                    int pixelIdx = y * 4 + x;
                    int idx = (int)((indexBits >> (pixelIdx * 3)) & 0x7);
                    byte r = pal[idx];

                    // BC5 detail flatten + sidewall-colour-match. The BC5
                    // R channel for tread diffuse is high-contrast detail
                    // noise (median ~111/255, full 0-255 spread, per-block
                    // contrast ~89 levels). A direct render or a moderate
                    // attenuation produces visible static. The game's
                    // shader normally composites this with a base rubber
                    // colour and a normal map -- the shader's job is to
                    // hide the per-pixel chaos in micro-shading, not to
                    // make the R channel itself visible.
                    //
                    // For the preview we approximate the shader's output
                    // by collapsing the BC5 R value into a narrow band
                    // centred on the sidewall's authored rubber colour
                    // (~16/255 darkness, verified earlier from BC7 pixel
                    // samples on c_03_sidewall_diffuse). The formula is:
                    //
                    //   lum = 14 + (r * 13) / 255
                    //
                    // which maps:
                    //   R = 0   -> 14   (slightly darker than sidewall)
                    //   R = 128 -> 20   (matches sidewall mid-tone)
                    //   R = 255 -> 27   (slight rubber highlight)
                    //
                    // Total range of variation across the tread surface:
                    // 13 levels, ~5% of full luminance. The tread pattern
                    // becomes barely-visible micro-shading, the colour
                    // matches the sidewall, and the eye reads the whole
                    // tyre as a single uniform black rubber surface --
                    // exactly what the game produces with full shading.
                    int lumI = 14 + ((r * 13) / 255);
                    if (lumI > 255) lumI = 255;
                    byte lum = (byte)lumI;

                    int outOff = (dy * w + dx) * 4;
                    dst[outOff + 0] = lum; // B
                    dst[outOff + 1] = lum; // G
                    dst[outOff + 2] = lum; // R
                    dst[outOff + 3] = 255; // A (opaque)
                }
            }
        }

        // =================================================================
        // BC7 (DXGI BC7_UNORM / Codemasters format code 70)
        // =================================================================
        //
        // Reference: D3D11 Block Compression specification, BC7 section
        // (https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format)
        // and the public Microsoft reference decoder shipped with the
        // DirectXTex library.
        //
        // BC7 is a 16-byte-per-block 4x4-pixel format with EIGHT internal
        // modes. Each block's first non-zero bit (counting from LSB) selects
        // the mode:
        //   mode 0  -> 1 bit  set    (binary 00000001)  : 3 subsets, 4-bit RGB endpoints, 3-bit indices
        //   mode 1  -> 2nd bit set   (binary 00000010)  : 2 subsets, 6-bit RGB endpoints, 3-bit indices
        //   mode 2  -> 3rd bit set   (binary 00000100)  : 3 subsets, 5-bit RGB endpoints, 2-bit indices
        //   mode 3  -> 4th bit set   (binary 00001000)  : 2 subsets, 7-bit RGB endpoints, 2-bit indices
        //   mode 4  -> 5th bit set   (binary 00010000)  : 1 subset,  5-bit RGB + 6-bit A, two index sets
        //   mode 5  -> 6th bit set   (binary 00100000)  : 1 subset,  7-bit RGB + 8-bit A, two index sets
        //   mode 6  -> 7th bit set   (binary 01000000)  : 1 subset,  7-bit RGBA endpoints, 4-bit indices
        //   mode 7  -> 8th bit set   (binary 10000000)  : 2 subsets, 5-bit RGBA endpoints, 2-bit indices
        //
        // After mode bits comes (depending on mode): partition index,
        // optional rotation/index-selection bits, color & alpha endpoints,
        // optional p-bits, color indices, optional alpha indices.
        //
        // The decoder is heavy on bit-fiddling but mechanical: every step
        // is just "read N bits at position P, do something with them".
        // To keep the code readable we use a stateful BitReader struct
        // that walks the 128-bit block.
        //
        // OUTPUT: Same BGRA32 row-major layout as the other BC decoders
        // (B, G, R, A per pixel; stride = width*4; top-left origin).
        // The base mip (top of the chain) is the only thing we decode --
        // payload is assumed to start at the base mip's first block.

        public static byte[] DecodeBC7(byte[] src, int width, int height)
        {
            byte[] dst = new byte[width * height * 4];
            int blocksW = Math.Max(1, (width + 3) / 4);
            int blocksH = Math.Max(1, (height + 3) / 4);

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    int srcOff = (by * blocksW + bx) * 16;
                    if (srcOff + 16 > src.Length) return dst;
                    DecodeBC7Block(src, srcOff, dst, bx * 4, by * 4, width, height);
                }
            }
            return dst;
        }

        // Per-mode parameter table. Indices match the mode number 0..7.
        // Field meanings, indexed in order:
        //   subsetCount     1, 2, or 3
        //   partitionBits   number of bits for the partition index field (0 if 1 subset)
        //   rotationBits    bits for component-rotation field (modes 4 & 5 only)
        //   idxSelBits      bits for index-set selection (mode 4 only)
        //   colorBits       per-component color endpoint bit count
        //   alphaBits       per-component alpha endpoint bit count (0 if no separate alpha)
        //   epPBits         number of "p-bits" (shared LSBs of endpoints, 0/1)
        //   idxBits         primary index bit-width
        //   idxBits2        secondary index bit-width (modes 4 & 5)
        private static readonly int[,] Bc7ModeParams =
        {
            //                       subsets, partBits, rotBits, idxSelBits, colorBits, alphaBits, epPBits, idxBits, idxBits2
            /* mode 0 */ {           3,         4,        0,         0,           4,         0,        1,        3,        0 },
            /* mode 1 */ {           2,         6,        0,         0,           6,         0,        1,        3,        0 },
            /* mode 2 */ {           3,         6,        0,         0,           5,         0,        0,        2,        0 },
            /* mode 3 */ {           2,         6,        0,         0,           7,         0,        1,        2,        0 },
            /* mode 4 */ {           1,         0,        2,         1,           5,         6,        0,        2,        3 },
            /* mode 5 */ {           1,         0,        2,         0,           7,         8,        0,        2,        2 },
            /* mode 6 */ {           1,         0,        0,         0,           7,         7,        1,        4,        0 },
            /* mode 7 */ {           2,         6,        0,         0,           5,         5,        1,        2,        0 },
        };

        // Anchor index table for partitions. The "anchor" pixel of each
        // subset has its index bit-width reduced by 1 (the high bit is
        // implicit zero). Subset 0's anchor is always pixel 0; subsets 1
        // and 2 have data-dependent anchor positions tabled below.
        //
        // From the BC7 spec, "Section: BC7 Partitions and Anchor Indices".

        // Anchors for subset 1 in 2-subset partitions (indexed 0..63):
        private static readonly byte[] Bc7AnchorSubset1of2 =
        {
            15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
            15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
            15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,
             6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
        };

        // Anchors for subset 1 in 3-subset partitions (indexed 0..63):
        private static readonly byte[] Bc7AnchorSubset1of3 =
        {
             3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,
             3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
             8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
             3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3,
        };

        // Anchors for subset 2 in 3-subset partitions (indexed 0..63):
        private static readonly byte[] Bc7AnchorSubset2of3 =
        {
            15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
            15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
            15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
            15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
        };

        // Partition tables: 2-subset (0..63) and 3-subset (0..63).
        // Each entry is a 16-pixel array of subset assignments (0/1, or
        // 0/1/2). Pixel order is row-major within the 4x4 block.
        // Compact storage: pack 16 2-bit values into one ulong per partition.
        private static readonly ulong[] Bc7Partitions2 =
        {
            0xCCCC_CCCCu, 0x8888_8888u, 0xEEEE_EEEEu, 0xECC8_ECC8u,
            0xC880_C880u, 0xFEEC_FEECu, 0xFEC8_FEC8u, 0xEC80_EC80u,
            0xC800_C800u, 0xFFEC_FFECu, 0xFE80_FE80u, 0xE800_E800u,
            0xFFE8_FFE8u, 0xFF00_FF00u, 0xFFF0_FFF0u, 0xF000_F000u,
            0xF710_F710u, 0x008E_008Eu, 0x7100_7100u, 0x08CE_08CEu,
            0x008C_008Cu, 0x7310_7310u, 0x3100_3100u, 0x8CCE_8CCEu,
            0x088C_088Cu, 0x3110_3110u, 0x6666_6666u, 0x366C_366Cu,
            0x17E8_17E8u, 0x0FF0_0FF0u, 0x718E_718Eu, 0x399C_399Cu,
            0xAAAA_AAAAu, 0xF0F0_F0F0u, 0x5A5A_5A5Au, 0x33CC_33CCu,
            0x3C3C_3C3Cu, 0x55AA_55AAu, 0x9696_9696u, 0xA55A_A55Au,
            0x73CE_73CEu, 0x13C8_13C8u, 0x324C_324Cu, 0x3BDC_3BDCu,
            0x6996_6996u, 0xC33C_C33Cu, 0x9966_9966u, 0x0660_0660u,
            0x0272_0272u, 0x04E4_04E4u, 0x4E40_4E40u, 0x2720_2720u,
            0xC936_C936u, 0x936C_936Cu, 0x39C6_39C6u, 0x639C_639Cu,
            0x9336_9336u, 0x9CC6_9CC6u, 0x817E_817Eu, 0xE718_E718u,
            0xCCF0_CCF0u, 0x0FCC_0FCCu, 0x7744_7744u, 0xEE22_EE22u,
        };

        // 3-subset partitions: each pixel uses 2 bits, 16 pixels = 32 bits per partition.
        private static readonly uint[] Bc7Partitions3 =
        {
            0xAA685050u, 0x6A5A5040u, 0x5A5A4200u, 0x5450A0A8u,
            0xA5A50000u, 0xA0A05050u, 0x5555A0A0u, 0x5A5A5050u,
            0xAA550000u, 0xAA555500u, 0xAAAA5500u, 0x90909090u,
            0x94949494u, 0xA4A4A4A4u, 0xA9A59450u, 0x2A0A4250u,
            0xA5945040u, 0x0A425054u, 0xA5A5A500u, 0x55A0A0A0u,
            0xA8A85454u, 0x6A6A4040u, 0xA4A45000u, 0x1A1A0500u,
            0x0050A4A4u, 0xAAA59090u, 0x14696914u, 0x69691400u,
            0xA08585A0u, 0xAA821414u, 0x50A4A450u, 0x6A5A0200u,
            0xA9A58000u, 0x5090A0A8u, 0xA8A09050u, 0x24242424u,
            0x00AA5500u, 0x24924924u, 0x24499224u, 0x50A50A50u,
            0x500AA550u, 0xAAAA4444u, 0x66660000u, 0xA5A0A5A0u,
            0x50A050A0u, 0x69286928u, 0x44AAAA44u, 0x66666600u,
            0xAA444444u, 0x54A854A8u, 0x95809580u, 0x96969600u,
            0xA85454A8u, 0x80959580u, 0xAA141414u, 0x96960000u,
            0xAAAA1414u, 0xA05050A0u, 0xA0A5A5A0u, 0x96000000u,
            0x40804080u, 0xA9A8A9A8u, 0xAAAAAA44u, 0x2A4A5254u,
        };

        // Index interpolation weight tables, from the BC7 spec.
        // A 2-bit index uses weights[0..3], 3-bit uses weights[0..7],
        // 4-bit uses weights[0..15]. Weights are out of 64.
        private static readonly byte[] Bc7Weights2 = { 0, 21, 43, 64 };
        private static readonly byte[] Bc7Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly byte[] Bc7Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        private static void DecodeBC7Block(byte[] src, int s, byte[] dst,
                                           int px, int py, int w, int h)
        {
            // Block as a 128-bit integer split into two ulongs (low 64 bits,
            // high 64 bits). The mode bits live at the bottom of the low word.
            ulong lo = BitConverter.ToUInt64(src, s);
            ulong hi = BitConverter.ToUInt64(src, s + 8);

            // Bit reader: a single function that pulls N bits starting at
            // a global bit position (0..127), advancing the position.
            int bitPos = 0;
            ulong ReadBits(int n)
            {
                if (n == 0) return 0;
                ulong result;
                if (bitPos + n <= 64)
                {
                    result = (lo >> bitPos) & ((1UL << n) - 1);
                }
                else if (bitPos >= 64)
                {
                    result = (hi >> (bitPos - 64)) & ((1UL << n) - 1);
                }
                else
                {
                    int loCount = 64 - bitPos;
                    ulong loPart = (lo >> bitPos) & ((1UL << loCount) - 1);
                    ulong hiPart = hi & ((1UL << (n - loCount)) - 1);
                    result = loPart | (hiPart << loCount);
                }
                bitPos += n;
                return result;
            }

            // Mode = position of first 1-bit in the low byte.
            int mode = -1;
            for (int i = 0; i < 8; i++)
            {
                if (((lo >> i) & 1) != 0) { mode = i; break; }
            }
            // Skip over the (mode+1) leading mode bits.
            ReadBits(mode + 1);

            if (mode < 0)
            {
                // All zero in the mode field is invalid in BC7. Fill with
                // opaque black; matches Microsoft's reference behaviour.
                FillBC7BlockSolid(dst, px, py, w, h, 0, 0, 0, 255);
                return;
            }

            int subsets = Bc7ModeParams[mode, 0];
            int partBits = Bc7ModeParams[mode, 1];
            int rotBits = Bc7ModeParams[mode, 2];
            int idxSelBits = Bc7ModeParams[mode, 3];
            int colorBits = Bc7ModeParams[mode, 4];
            int alphaBits = Bc7ModeParams[mode, 5];
            int pBits = Bc7ModeParams[mode, 6];
            int idxBits = Bc7ModeParams[mode, 7];
            int idxBits2 = Bc7ModeParams[mode, 8];

            int partition = (partBits > 0) ? (int)ReadBits(partBits) : 0;
            int rotation = (rotBits > 0) ? (int)ReadBits(rotBits) : 0;
            int idxMode = (idxSelBits > 0) ? (int)ReadBits(idxSelBits) : 0;

            // Endpoints: 2 endpoints per subset, RGB and (optionally) A.
            // Endpoint storage order is component-major: all R values
            // first, then all G, then all B, then all A. Within each
            // component the order is endpoint-major: subset0.ep0,
            // subset0.ep1, subset1.ep0, subset1.ep1, ...
            int epCount = subsets * 2;
            byte[] r = new byte[epCount];
            byte[] g = new byte[epCount];
            byte[] b = new byte[epCount];
            byte[] a = new byte[epCount];

            for (int i = 0; i < epCount; i++) r[i] = (byte)ReadBits(colorBits);
            for (int i = 0; i < epCount; i++) g[i] = (byte)ReadBits(colorBits);
            for (int i = 0; i < epCount; i++) b[i] = (byte)ReadBits(colorBits);
            if (alphaBits > 0)
                for (int i = 0; i < epCount; i++) a[i] = (byte)ReadBits(alphaBits);
            else
                for (int i = 0; i < epCount; i++) a[i] = 255;

            // P-bits: one extra LSB per endpoint (mode 0, 1, 3, 6, 7).
            // Either "shared" (one p-bit covers two endpoints in same subset,
            // mode 1) or "unique" (each endpoint has its own p-bit, modes 0/3/6/7).
            // After applying the p-bit, all endpoint components shift left by 1
            // and the p-bit becomes the new LSB. This bumps the effective
            // endpoint precision up by 1 bit before scaling to 8-bit.
            int colorBitsTotal = colorBits;
            int alphaBitsTotal = alphaBits;
            if (pBits > 0)
            {
                if (mode == 1)
                {
                    // Mode 1: 2 subsets, 1 shared p-bit per subset (2 p-bits total).
                    // Shared means endpoint0 and endpoint1 of the same subset share it.
                    int p0 = (int)ReadBits(1);
                    int p1 = (int)ReadBits(1);
                    int[] pp = { p0, p0, p1, p1 };
                    for (int i = 0; i < epCount; i++)
                    {
                        r[i] = (byte)((r[i] << 1) | pp[i]);
                        g[i] = (byte)((g[i] << 1) | pp[i]);
                        b[i] = (byte)((b[i] << 1) | pp[i]);
                    }
                    colorBitsTotal++;
                }
                else
                {
                    // Modes 0, 3, 6, 7: one unique p-bit per endpoint.
                    int[] pp = new int[epCount];
                    for (int i = 0; i < epCount; i++) pp[i] = (int)ReadBits(1);
                    for (int i = 0; i < epCount; i++)
                    {
                        r[i] = (byte)((r[i] << 1) | pp[i]);
                        g[i] = (byte)((g[i] << 1) | pp[i]);
                        b[i] = (byte)((b[i] << 1) | pp[i]);
                        if (alphaBits > 0) a[i] = (byte)((a[i] << 1) | pp[i]);
                    }
                    colorBitsTotal++;
                    if (alphaBits > 0) alphaBitsTotal++;
                }
            }

            // Scale endpoints from "colorBitsTotal" bits to 8 bits using
            // bit-replication ("xx xxxx" -> "xxxxxx xx" by repeating high bits).
            for (int i = 0; i < epCount; i++)
            {
                r[i] = ScaleEndpoint(r[i], colorBitsTotal);
                g[i] = ScaleEndpoint(g[i], colorBitsTotal);
                b[i] = ScaleEndpoint(b[i], colorBitsTotal);
                if (alphaBits > 0) a[i] = ScaleEndpoint(a[i], alphaBitsTotal);
            }

            // Color indices come next. Each pixel gets idxBits bits, EXCEPT
            // anchor pixels which get (idxBits-1) bits (the high bit of an
            // anchor index is implicitly zero).
            int[] colorIdx = new int[16];
            ReadBC7IndexBlock(colorIdx, idxBits, subsets, partition, ReadBits);

            // For modes 4 & 5 there's a SECOND set of indices (alpha) with
            // a different bit-width. The index-mode bit (mode 4 only)
            // selects which set is used for color and which for alpha.
            int[] alphaIdx = null;
            if (idxBits2 > 0)
            {
                alphaIdx = new int[16];
                ReadBC7IndexBlock(alphaIdx, idxBits2, subsets, partition, ReadBits);
            }

            // For each pixel: pick its subset, look up endpoints, interpolate.
            for (int py4 = 0; py4 < 4; py4++)
            {
                int yy = py + py4;
                if (yy >= h) break;
                for (int px4 = 0; px4 < 4; px4++)
                {
                    int xx = px + px4;
                    if (xx >= w) continue;

                    int pixelIdx = py4 * 4 + px4;
                    int subset = GetBC7Subset(subsets, partition, pixelIdx);

                    int e0 = subset * 2;
                    int e1 = e0 + 1;

                    int ci = colorIdx[pixelIdx];
                    int ai = (alphaIdx != null) ? alphaIdx[pixelIdx] : ci;

                    // Modes 4 & 5: rotation field swaps the alpha channel
                    // with one of the color channels AFTER decode. Modes 4's
                    // index-mode bit also selects which of (color, alpha)
                    // index sets drives RGB vs A.
                    int cBits = (idxBits2 == 0) ? idxBits :
                                (mode == 4 && idxMode == 1) ? idxBits2 : idxBits;
                    int aBits = (idxBits2 == 0) ? idxBits :
                                (mode == 4 && idxMode == 1) ? idxBits : idxBits2;

                    int cI = (mode == 4 && idxMode == 1) ? ai : ci;
                    int aI = (mode == 4 && idxMode == 1) ? ci : ai;

                    byte rr = InterpolateBC7(r[e0], r[e1], cI, cBits);
                    byte gg = InterpolateBC7(g[e0], g[e1], cI, cBits);
                    byte bb = InterpolateBC7(b[e0], b[e1], cI, cBits);
                    byte aa = (alphaBits > 0)
                              ? InterpolateBC7(a[e0], a[e1], aI, aBits)
                              : (byte)255;

                    // Component rotation (modes 4, 5). After decoding,
                    // optionally swap one of the RGB channels with alpha.
                    if (rotBits > 0)
                    {
                        switch (rotation)
                        {
                            case 0: break;             // no rotation
                            case 1: { byte t = rr; rr = aa; aa = t; break; } // swap R<->A
                            case 2: { byte t = gg; gg = aa; aa = t; break; } // swap G<->A
                            case 3: { byte t = bb; bb = aa; aa = t; break; } // swap B<->A
                        }
                    }

                    int outOff = (yy * w + xx) * 4;
                    dst[outOff + 0] = bb; // B
                    dst[outOff + 1] = gg; // G
                    dst[outOff + 2] = rr; // R
                    dst[outOff + 3] = aa; // A
                }
            }
        }

        // Reads N indices for a 4x4 block. Anchor positions get (bits-1)
        // bits; everyone else gets `bits` bits. The "anchor" of subset 0
        // is always pixel 0; subsets 1 & 2 use partition-table-driven
        // anchor positions.
        private static void ReadBC7IndexBlock(
            int[] dest, int bits, int subsets, int partition, Func<int, ulong> readBits)
        {
            int anchor1 = -1, anchor2 = -1;
            if (subsets == 2) anchor1 = Bc7AnchorSubset1of2[partition];
            else if (subsets == 3)
            {
                anchor1 = Bc7AnchorSubset1of3[partition];
                anchor2 = Bc7AnchorSubset2of3[partition];
            }

            for (int i = 0; i < 16; i++)
            {
                bool isAnchor = (i == 0) || (i == anchor1) || (i == anchor2);
                int n = isAnchor ? (bits - 1) : bits;
                dest[i] = (int)readBits(n);
            }
        }

        // Returns the subset (0/1/2) a given pixel belongs to under the
        // active partition. Looks up the appropriate partition table.
        private static int GetBC7Subset(int subsets, int partition, int pixelIdx)
        {
            if (subsets == 1) return 0;
            if (subsets == 2)
            {
                ulong p = Bc7Partitions2[partition];
                return (int)((p >> pixelIdx) & 1UL);
            }
            // 3 subsets
            uint p3 = Bc7Partitions3[partition];
            return (int)((p3 >> (pixelIdx * 2)) & 3u);
        }

        // BC7 endpoint interpolation: the index selects a weight from 0..64
        // (where 0 = full ep0, 64 = full ep1, intermediate values blend).
        // Formula from spec: out = ((64 - w) * ep0 + w * ep1 + 32) >> 6.
        private static byte InterpolateBC7(byte ep0, byte ep1, int index, int indexBits)
        {
            byte[] table = (indexBits == 2) ? Bc7Weights2
                         : (indexBits == 3) ? Bc7Weights3
                         : Bc7Weights4;
            if ((uint)index >= (uint)table.Length) index = 0;
            int w = table[index];
            int v = ((64 - w) * ep0 + w * ep1 + 32) >> 6;
            if (v < 0) v = 0; else if (v > 255) v = 255;
            return (byte)v;
        }

        // Scale a value from `bits` bits to 8 bits via bit-replication.
        // 7-bit value 0x7E (0111 1110) -> 0xFD (1111 1101): repeat the
        // top (8-bits) of the original into the new low bits. Standard
        // for matching how GPUs unpack low-bit-depth endpoints.
        private static byte ScaleEndpoint(byte value, int bits)
        {
            if (bits >= 8) return value;
            int shift = 8 - bits;
            return (byte)((value << shift) | (value >> (bits - shift)));
        }

        // Mode-0/all-zero fallback: paint a 4x4 block with a single colour.
        private static void FillBC7BlockSolid(byte[] dst, int px, int py,
                                              int w, int h,
                                              byte r, byte g, byte b, byte a)
        {
            for (int y = 0; y < 4; y++)
            {
                int yy = py + y;
                if (yy >= h) break;
                for (int x = 0; x < 4; x++)
                {
                    int xx = px + x;
                    if (xx >= w) continue;
                    int o = (yy * w + xx) * 4;
                    dst[o + 0] = b; dst[o + 1] = g; dst[o + 2] = r; dst[o + 3] = a;
                }
            }
        }
    }


    /// <summary>
    /// Decoded texture payload: raw BGRA32 pixel buffer plus dimensions
    /// and a format label (for logging only). Produced by
    /// TextureCache.GetPixelsForTexture and consumed by DecalCompositor.
    /// The Bgra array is owned by the cache -- do not mutate it in place.
    /// </summary>
    public sealed class DecodedTexture
    {
        public byte[] Bgra;
        public int Width;
        public int Height;
        public string FormatName;
    }
}
