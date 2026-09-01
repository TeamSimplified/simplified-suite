using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Media3D;
using F1modding3dtoolV1;

namespace SimplifiedStudio.UvEngine
{
    /// <summary>One texture's UV template, as the app sees it.</summary>
    public sealed class UvTemplate
    {
        internal UvTemplate(string name, int width, int height, int edges, IReadOnlyList<string> sources)
        {
            Name = name; Width = width; Height = height; EdgeCount = edges; Sources = sources;
        }

        /// <summary>Texture name the template belongs to, without extension — e.g. <c>ferrari_paint_d</c>.</summary>
        public string Name { get; }

        /// <summary>Pixel size of the texture, which is the size the PNG comes out at.</summary>
        public int Width { get; }
        public int Height { get; }

        /// <summary>How many UV edges get stroked. Useful only as a progress hint.</summary>
        public int EdgeCount { get; }

        /// <summary>Submeshes that fed this template — the same list the engine's report writes.</summary>
        public IReadOnlyList<string> Sources { get; }
    }

    /// <summary>
    /// The UV-template pipeline, wrapped so the app deals in textures and file
    /// paths rather than the engine's model graph.
    ///
    /// The work is in <see cref="Load"/>: the ERP is unpacked and every model
    /// asset in it is loaded to build the material graph that says which
    /// triangles sample which texture. Rendering a template afterwards is
    /// comparatively quick, so a session is worth keeping while the same ERP
    /// stays open — exporting a second texture's UV map then costs almost
    /// nothing.
    ///
    /// Threading is not incidental here. The loaders build WPF materials, which
    /// are thread-affine — but nothing forces them onto the <em>app's</em>
    /// thread, so both loading and rendering run on dedicated STA threads of
    /// their own and the UI stays responsive through what can be several
    /// seconds of work. What crosses back is plain data: collecting a template
    /// keeps the edge list and pixel size and drops the bitmap.
    /// </summary>
    public sealed class UvMapSession
    {
        private readonly List<UvTemplateExporter.TexGroup> _groups;

        private UvMapSession(string erpPath, List<UvTemplateExporter.TexGroup> groups, int skipped)
        {
            ErpPath = erpPath;
            _groups = groups;
            Skipped = skipped;
            Templates = groups
                .Select(g => new UvTemplate(g.Name, g.Width, g.Height, g.Edges.Count, g.Sources.Distinct().ToList()))
                .ToList();
        }

        /// <summary>The ERP this session was built from.</summary>
        public string ErpPath { get; }

        /// <summary>Every texture in the archive that geometry actually samples.</summary>
        public IReadOnlyList<UvTemplate> Templates { get; }

        /// <summary>Meshes passed over — untextured, or a texture too small to template.</summary>
        public int Skipped { get; }

        /// <summary>
        /// Unpack <paramref name="erpPath"/>, load every model asset in it and
        /// collect one UV template per texture, on a dedicated STA thread.
        /// Returns null when the ERP carries no model assets at all — a texture
        /// pack or a shader package, say — which isn't an error, just nothing
        /// to template.
        /// </summary>
        public static UvMapSession Load(string erpPath, Action<string> log = null)
        {
            UvMapSession session = null;
            Exception failure = null;
            var done = new SemaphoreSlim(0, 1);
            var worker = new Thread(() =>
            {
                try { session = LoadCore(erpPath, log); }
                catch (Exception ex) { failure = ex; }
                finally { done.Release(); }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            done.Wait();

            if (failure != null) throw failure;
            return session;
        }

        private static UvMapSession LoadCore(string erpPath, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(erpPath) || !File.Exists(erpPath))
                throw new FileNotFoundException("ERP not found.", erpPath);

            log ??= _ => { };
            EngineLog.Sink = log;

            // Locating the install lets textures come in at full resolution from
            // their .mipmaps sidecars; without it they fall back to the low-res
            // tail the ERP carries, and the template comes out that size.
            try { F1PackageLocator.Last = F1PackageLocator.LocateFromErp(erpPath); }
            catch { F1PackageLocator.Last = null; }

            var cacheRoot = ErpIdfExtractor.ExtractEmbeddedIdfToTempFolder(erpPath);
            if (string.IsNullOrEmpty(cacheRoot) || !Directory.Exists(cacheRoot))
                return null;

            // The extractor may hand back the primary idf folder rather than the
            // cache root; walk up so the scan below sees every asset.
            var scanRoot = cacheRoot;
            for (var hop = 0; hop < 6 && scanRoot != null; hop++)
            {
                var parent = Path.GetDirectoryName(scanRoot);
                if (parent != null &&
                    string.Equals(Path.GetFileName(parent), "erp_cache", StringComparison.OrdinalIgnoreCase))
                    break;
                scanRoot = parent;
            }
            if (string.IsNullOrEmpty(scanRoot)) scanRoot = cacheRoot;

            var idfFolders = Directory
                .EnumerateDirectories(scanRoot, "idf", SearchOption.AllDirectories)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (idfFolders.Count == 0) return null;

            // Mesh registries are keyed per instance and must be cleared ONCE
            // per run — clearing them per folder wipes the assets already loaded.
            F1ModelLoader.ClearRegistries();

            var merged = new Model3DGroup();
            var loaded = 0;
            foreach (var folder in idfFolders)
            {
                ModelPartNameRegistry.Clear();
                try
                {
                    var group = F1ModelLoader.LoadFolder(folder);
                    if (group != null && group.Children.Count > 0)
                    {
                        merged.Children.Add(group);
                        loaded++;
                    }
                }
                catch (Exception ex)
                {
                    log($"  UV: couldn't load {Path.GetFileName(folder)}: {ex.Message}");
                }
            }
            if (loaded == 0) return null;

            var (groups, skipped) = UvTemplateExporter.Collect(merged);
            return groups.Count == 0 ? null : new UvMapSession(erpPath, groups, skipped);
        }

        /// <summary>
        /// The template for a texture, matched on name without extension, or
        /// null when that texture isn't sampled by any geometry.
        /// </summary>
        public UvTemplate Find(string textureName)
        {
            var wanted = Normalize(textureName);
            return Templates.FirstOrDefault(t =>
                string.Equals(Normalize(t.Name), wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Render one texture's template to <paramref name="outPath"/> as a
        /// transparent PNG. Returns false when nothing samples that texture.
        /// Blocks until written, on its own STA thread — safe to call from a
        /// background task.
        ///
        /// <para><paramref name="width"/> and <paramref name="height"/> override
        /// the output size. That matters for any texture with external mipmaps:
        /// the engine sizes a template from the bitmap it managed to load, which
        /// is the low-resolution tail the ERP carries whenever the .mipmaps
        /// sidecar wasn't reachable — so a 2048x2048 paint texture would come
        /// out as a 512x512 template, useless as an overlay on the file the
        /// modder actually edits. The layout is vector data in UV space, so the
        /// caller can name the true size and get it drawn at that. Pass 0 to
        /// keep whatever the engine determined.</para>
        /// </summary>
        public bool Render(string textureName, string outPath, int width = 0, int height = 0)
        {
            var wanted = Normalize(textureName);
            var group = _groups.FirstOrDefault(g =>
                string.Equals(Normalize(g.Name), wanted, StringComparison.OrdinalIgnoreCase));
            if (group is null) return false;

            // Swap the size in for the render and put it back after, so the
            // session keeps reporting what the engine found.
            var originalWidth = group.Width;
            var originalHeight = group.Height;
            if (width > 0) group.Width = width;
            if (height > 0) group.Height = height;

            Exception failure = null;
            var done = new SemaphoreSlim(0, 1);
            var worker = new Thread(() =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    UvTemplateExporter.RenderGroup(group, outPath);
                }
                catch (Exception ex) { failure = ex; }
                finally { done.Release(); }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            done.Wait();

            group.Width = originalWidth;
            group.Height = originalHeight;

            if (failure != null) throw failure;
            return true;
        }

        /// <summary>
        /// Texture names reach us in several shapes — an EAID leaf with a
        /// <c>.tif</c> on it, a name the engine sanitised, a name it had to
        /// uniquify. Comparing on the stem with non-alphanumerics dropped makes
        /// those agree.
        /// </summary>
        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var stem = Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
            var chars = stem.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }
    }
}
