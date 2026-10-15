using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SimplifiedUniversalExporter.Core.Showroom;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// The RM Renderer: the WebGL2 renderer adapted from RM Designs' Steering
    /// Wheel Swapper, hosted in WebView2 in place of the 3D view, behind the
    /// "RM Renderer" box.
    ///
    /// Nothing goes to disk and nothing is fetched from the internet: the page,
    /// its script, scene.json and scene.bin are served from memory at
    /// https://showroom.local/, a name WebView2 resolves only inside this
    /// control. WebView2's own browser data goes under the app's %TEMP%
    /// folder, never beside the exe. The scene is built off the window's
    /// thread; only reading the model's arrays out of the tree happens on it.
    /// </summary>
    public partial class MainWindow
    {
        private const string RmHost = "showroom.local";
        private WebView2 _rmView;
        private ShowroomSceneData _rmScene;
        private Stopwatch _rmClock;
        private bool _rmStarting;
        private readonly object _rmCacheLock = new object();

        private async void OnRmRendererToggle(object sender, RoutedEventArgs e)
        {
            if (RmRendererCheckBox.IsChecked == true) await ShowRmRenderer();
            else HideRmRenderer();
        }

        /// <summary>On by the box, or by SUE_RM_RENDERER=1 for a test run that can't click it.</summary>
        private bool RmRendererWanted
        {
            get
            {
                if (Environment.GetEnvironmentVariable("SUE_RM_RENDERER") == "1" && RmRendererCheckBox.IsChecked != true)
                    RmRendererCheckBox.IsChecked = true;
                return RmRendererCheckBox.IsChecked == true;
            }
        }

        private async Task ShowRmRenderer()
        {
            if (_rmStarting) return;
            if (_currentModel?.Content is not Model3DGroup root || root.Children.Count == 0)
            {
                RmLog("RM Renderer: load a model first.");
                return;
            }
            _rmStarting = true;
            try
            {
                _rmClock = Stopwatch.StartNew();
                var meshes = ShowroomModel.Extract(root);
                var read = _rmClock.Elapsed;
                var sources = ShowroomSources();
                _rmScene = await Task.Run(() => ShowroomScene.Build(meshes, sources));
                var s = _rmScene;
                RmLog($"RM Renderer: model read in {read.TotalSeconds:0.00}s, scene built in {s.Elapsed.TotalSeconds:0.00}s off the window's thread - " +
                      $"{s.Groups} groups, {s.Vertices:N0} vertices, {s.Triangles:N0} triangles, {s.Materials} materials, " +
                      $"{s.Textures} textures ({s.CompressedTextures} in their block format), " +
                      $"scene {s.Json.Length / 1024.0:0} KB + {s.Binary.Length / 1048576.0:0.0} MB.");

                if (_rmView == null)
                {
                    _rmView = new WebView2();
                    var grid = (Grid)Viewport.Parent;
                    grid.Children.Insert(grid.Children.IndexOf(DxViewport) + 1, _rmView);
                    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.TempRoot, "webview2"));
                    await _rmView.EnsureCoreWebView2Async(env);
                    var core = _rmView.CoreWebView2;
                    core.Settings.AreDevToolsEnabled = true;
                    core.Settings.IsStatusBarEnabled = false;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.AddWebResourceRequestedFilter($"https://{RmHost}/*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += ServeRmResource;
                    core.WebMessageReceived += OnRmMessage;
                    RmLog($"RM Renderer: WebView2 runtime {env.BrowserVersionString} started.");
                }

                Viewport.Visibility = Visibility.Collapsed;
                DxViewport.Visibility = Visibility.Collapsed;
                // WebView2 draws over everything in its area, so the WPF camera
                // buttons would sit invisible behind it; the page has its own.
                CameraButtonsPanel.Visibility = Visibility.Collapsed;
                _rmView.Visibility = Visibility.Visible;
                _rmView.CoreWebView2.Navigate($"https://{RmHost}/showroom.html");
            }
            catch (Exception ex)
            {
                RmLog("RM Renderer failed: " + ex.Message);
                HideRmRenderer();
            }
            finally
            {
                _rmStarting = false;
            }
        }

        /// <summary>Materials and decoded textures through the live texture cache, one caller at a time.</summary>
        private ShowroomSources ShowroomSources()
        {
            var cache = F1ModelLoader.ActiveTextureCache ?? F1ModelLoaderClassic.ActiveTextureCache ?? F1ModelLoaderLegacy.ActiveTextureCache;
            return new ShowroomSources
            {
                PackageRoot = cache?.PackageRoot,
                Material = eaid =>
                {
                    if (cache == null) return null;
                    lock (_rmCacheLock) return cache.GetMaterial(eaid);
                },
                Decode = (eaid, normal) =>
                {
                    if (cache == null) return null;
                    lock (_rmCacheLock)
                        return normal ? TextureDecodeLadder.Normal(eaid, cache, _ => { }, out _)
                                      : TextureDecodeLadder.Best(eaid, cache, _ => { }, out _);
                },
                Log = _ => { },
            };
        }

        private void HideRmRenderer()
        {
            if (_rmView != null) _rmView.Visibility = Visibility.Collapsed;
            CameraButtonsPanel.Visibility = Visibility.Visible;
            _rmScene = null;
            RefreshDxView();
        }

        /// <summary>The activity log, and a copy in %TEMP%\simplified-universal-exporter\logs\rm-renderer.log.</summary>
        private void RmLog(string message)
        {
            LogToUi(message);
            try
            {
                var dir = Path.Combine(AppPaths.TempRoot, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "rm-renderer.log"), $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private void ServeRmResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            byte[] body = path switch
            {
                "scene.json" => _rmScene?.Json,
                "scene.bin" => _rmScene?.Binary,
                _ => PageFile(path),
            };
            var env = _rmView.CoreWebView2.Environment;
            if (body == null)
            {
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var type = Path.GetExtension(path) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json",
                _ => "application/octet-stream",
            };
            e.Response = env.CreateWebResourceResponse(new MemoryStream(body, writable: false), 200, "OK",
                $"Content-Type: {type}\r\nCache-Control: no-store");
        }

        /// <summary>The page's own files, embedded in the app.</summary>
        private static byte[] PageFile(string name)
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("ShowroomWeb." + name);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private void OnRmMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var r = doc.RootElement;
                var ext = r.GetProperty("ext");
                long memory = 0;
                foreach (var info in _rmView.CoreWebView2.Environment.GetProcessInfos())
                {
                    try { memory += Process.GetProcessById(info.ProcessId).PrivateMemorySize64; } catch { }
                }
                var missing = string.Join(", ", r.GetProperty("missing").EnumerateArray().Select(x => x.GetString()));
                RmLog($"RM Renderer: drawn {_rmClock.Elapsed.TotalSeconds:0.00}s after the scene build started. " +
                      $"GPU: {r.GetProperty("renderer").GetString()}. Block formats - BC1-3 {(ext.GetProperty("s3tc").GetBoolean() ? "yes" : "no")}, " +
                      $"BC4/5 {(ext.GetProperty("rgtc").GetBoolean() ? "yes" : "no")}, BC7 {(ext.GetProperty("bptc").GetBoolean() ? "yes" : "no")}" +
                      (missing.Length > 0 ? $" (can't show: {missing})" : "") +
                      $". Browser processes: {memory / 1048576.0:0} MB private memory.");
                // the page has its copies; the app doesn't need to hold the scene too
                _rmScene = null;
            }
            catch (Exception ex)
            {
                RmLog("RM Renderer message: " + ex.Message);
            }
        }
    }
}
