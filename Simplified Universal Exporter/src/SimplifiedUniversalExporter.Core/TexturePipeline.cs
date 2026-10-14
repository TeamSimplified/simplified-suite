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
        // rotation. Decoded by EgoEngineLibrary.Graphics.BlockCompressionDecoder.DecodeBC7.
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
