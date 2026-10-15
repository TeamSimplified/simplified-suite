using HelixToolkit.Wpf;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;

namespace SimplifiedUniversalExporter
{
    /// <summary>
    /// Helmet-only workflow:
    ///   1. Pick an F1 25 helmet ERP.
    ///   2. ErpIdfExtractor explodes it into a temp cache (mirroring the
    ///      EAID directory layout). The "picked" IDF folder is returned;
    ///      additional IDF folders (e.g. attachment overlays) are discovered
    ///      by scanning the cache for sibling *.idf descriptors.
    ///   3. Each IDF folder is loaded via F1ModelLoader (the 2021+ loader the
    ///      car exporter uses) at LOD 0 for the viewport preview.
    ///   4. Export hits F1FbxExporter with CurrentExportFormatKind = Fbx so
    ///      every LOD comes out as an .fbx with sidecar texture PNGs next to it.
    /// </summary>
    public partial class MainWindow : Window
    {
        private string _loadedErpPath = "";
        private string _primaryIdfFolder = "";
        private readonly List<string> _additionalIdfFolders = new List<string>();
        private ModelVisual3D _currentModel;

        /// <summary>
        /// Livery binding context the user picked from the top-bar
        /// ComboBox. F1 2017-2025 car ERPs ship one .emb context file
        /// per driver number (1..99 + default); only the "number"
        /// and "carbon number" slots vary across them, so changing
        /// context swaps the driver-number atlas without touching the
        /// rest of the livery. -1 means "auto" (let the loader pick
        /// the first numeric context it finds).
        /// </summary>
        private int _selectedContextId = -1;

        /// <summary>
        /// Set to true while RepopulateContextList is mutating the
        /// ComboBox so the SelectionChanged handler doesn't kick off
        /// an immediate Reload Preview (the user hasn't actually
        /// touched anything yet).
        /// </summary>
        private bool _suppressContextAutoReload = false;

        /// <summary>One entry in ContextComboBox. The members are
        /// PROPERTIES (not fields) because WPF data binding -- which
        /// DisplayMemberPath="Label" uses internally -- only sees
        /// properties. A field-backed Label silently renders as
        /// empty rows, which was the symptom of the first cut.</summary>
        public sealed class ContextChoice
        {
            public int Id { get; set; }      // numeric driver number, or
                                             // EmbContextReader.DefaultContextId
                                             // for "default", or -1 for "Auto"
            public string Label { get; set; } // shown in the dropdown
            public override string ToString() => Label;
        }

        /// <summary>
        /// Backing collection for the left-sidebar "Models in ERP" list.
        /// Populated after a successful ERP load; each entry corresponds
        /// to one IDF folder (the primary or one of the discovered
        /// additional folders). The user can untick entries they don't
        /// want included in the next viewport reload / FBX export.
        /// Empty = "include everything" (preserved as the default for
        /// helmet/car/driver ERPs where every IDF is normally wanted).
        /// </summary>
        public ObservableCollection<IdfChoice> Models { get; } =
            new ObservableCollection<IdfChoice>();

        /// <summary>
        /// One row in the Models sidebar. Two-way binding on IsChecked
        /// means flipping the checkbox in the UI updates the model
        /// instantly; we don't need to read the ListBox children at
        /// export time.
        /// </summary>
        public sealed class IdfChoice : INotifyPropertyChanged
        {
            public string FolderPath { get; set; }
            public string DisplayName { get; set; }
            public bool IsPrimary { get; set; }

            /// <summary>
            /// Distinct part names harvested from
            /// ModelPartNameRegistry as this IDF loads. Empty until the
            /// first load completes; populated in-place by the load
            /// loop so the user sees the tree expand as they explore
            /// what's actually inside each IDF. Re-loads honour the
            /// checked state to filter the viewport.
            /// </summary>
            public ObservableCollection<PartChoice> Parts { get; } =
                new ObservableCollection<PartChoice>();

            private bool _isChecked = true;
            /// <summary>
            /// Cascade rule: toggling the parent IDF's checkbox
            /// propagates to every child PartChoice. Otherwise the
            /// user has to untick both the parent AND every part
            /// underneath to fully disable a section, which is
            /// confusing (the parent checkbox reads as "everything
            /// under here off" but wasn't actually enforcing that).
            /// </summary>
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    OnChanged();
                    foreach (var p in Parts)
                        p.IsChecked = value;
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged([CallerMemberName] string n = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        /// <summary>
        /// One distinct part name inside an IDF (e.g. "kerb_01",
        /// "crowd_lo"). The export side ignores this -- it's only a
        /// preview filter: unticked parts are excluded from the
        /// viewport rebuild but the FBX still ships the whole IDF.
        /// </summary>
        public sealed class PartChoice : INotifyPropertyChanged
        {
            /// <summary>Descriptor-level part name (from MeshFamilyRegistry).
            /// Multiple PartChoices can share this when one descriptor
            /// holds many material slots (e.g. driver body has one
            /// descriptor with boots/gloves/harness slots).</summary>
            public string PartName { get; set; }

            /// <summary>Material/slot leaf name (e.g. "boots", "gloves").
            /// Null when no material EAID was registered for the mesh,
            /// in which case filtering falls back to PartName only.</summary>
            public string SlotName { get; set; }

            /// <summary>Label shown in the tree. Either the slot name
            /// alone (when distinctive) or "part / slot" when needed
            /// to disambiguate.</summary>
            public string DisplayName { get; set; }

            private bool _isChecked = true;
            public bool IsChecked
            {
                get => _isChecked;
                set { if (_isChecked != value) { _isChecked = value; OnChanged(); } }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged([CallerMemberName] string n = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        /// <summary>
        /// Everything the user sets, persisted as config.json beside the exe.
        /// Loaded before the first window paints, saved whenever a setting
        /// changes and again on close.
        /// </summary>
        private readonly Core.Config.UserConfig _config = Core.Config.UserConfig.Load();

        // The export picker's ticks, remembered between runs.
        private bool _exportTexturesPref = true;
        private bool _exportUvMapsPref = false;
        private bool _exportFbxPref = true;

        /// <summary>
        /// The F1 25 install, from Settings. Empty when it isn't set, which the
        /// loaders read as "use the small mips inside the ERP".
        /// </summary>
        private string GameFolder => _config.GameDir ?? "";

        public MainWindow()
        {
            InitializeComponent();
            VersionLabel.Text = Util.AppVersion.Display;
            ApplyWindowIcon();
            // Bind the models sidebar to our ObservableCollection so the
            // CheckBox.IsChecked two-way binding flows back into the
            // IdfChoice / PartChoice items without us reading the
            // visual tree.
            ModelsTreeView.ItemsSource = Models;
            // Route LogToStatic() calls from background classes (the
            // loader, extractor, exporter) at this MainWindow's LogToUi.
            ExporterLog.Sink = LogToUi;
            try
            {
                var cfg = _config;
                _suppressSettingsSave = true;
                try
                {
                    // Assemble is hidden now -- always on; an old config
                    // that saved it off must not silently disable it.
                    AssembleCheckBox.IsChecked = true;
                    DecalZFightCheckBox.IsChecked = cfg.FixDecalZFighting;
                    RmRendererCheckBox.IsChecked = cfg.RmRenderer;
                    foreach (var item in GenderComboBox.Items)
                    {
                        if (item is System.Windows.Controls.ComboBoxItem ci &&
                            string.Equals(ci.Content as string, cfg.Gender,
                                StringComparison.OrdinalIgnoreCase))
                        { ci.IsSelected = true; break; }
                    }
                    _exportTexturesPref = cfg.ExportTextures;
                    _exportUvMapsPref = cfg.ExportUvMaps;
                    _exportFbxPref = cfg.ExportFbx;
                    if (cfg.SidebarWidth >= 140)
                        _savedSidebarWidth = new GridLength(cfg.SidebarWidth);
                    if (!cfg.SidebarVisible)
                        SetSidebarVisible(false);
                }
                finally { _suppressSettingsSave = false; }
            }
            catch { /* settings load is best-effort */ }

            // Found at startup when it wasn't set (App.OnStartup); said here so
            // the log shows where the full-resolution textures come from.
            LogToUi(string.IsNullOrWhiteSpace(GameFolder)
                ? "No game folder set: exports use the small textures inside each ERP. Set it in Settings."
                : "Game folder: " + GameFolder);

            // Frameless-window chrome (Simplified Suite look): taskbar-
            // aware maximise and a maximise glyph that follows the state.
            SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);

            UpdateModeButtons();   // 3D is the starting mode

            // Inset the axis gizmo once the viewport template is applied.
            Viewport.Loaded += (_, __) => PadCoordinateGizmo();

            // The DX renderer is the default view from the very first
            // frame -- an empty DX viewport (black + view cube) fronts
            // until a model loads; the WPF viewport and its axis gizmo
            // only ever appear as the no-GPU fallback.
            try
            {
                DxViewport.EffectsManager = new HelixToolkit.SharpDX.DefaultEffectsManager();
                Viewport.Visibility = Visibility.Collapsed;
                DxViewport.Visibility = Visibility.Visible;
            }
            catch { /* fallback: WPF viewport stays visible */ }

            // Debug/automation hook: pass an .erp path on the command line
            // to auto-load it once the window is up. Lets scripted runs
            // exercise the full load pipeline without driving the file
            // dialog by hand.
            Loaded += async (_, __) =>
            {
                try
                {
                    var args = Environment.GetCommandLineArgs();
                    if (args.Length > 1 &&
                        args[1].EndsWith(".erp", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(args[1]))
                    {
                        LogToUi($"Auto-load (command line): {args[1]}");
                        await LoadErpFromPath(args[1]);

                        // Optional second arg: headless FBX export after
                        // the load completes.
                        if (args.Length > 2 &&
                            args[2].EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        {
                            LogToUi($"Auto-export (command line): {args[2]}");
                            await ExportFbxToPath(args[2]);
                        }
                    }
                }
                catch { /* auto-load is best-effort */ }
            };
        }

        /// <summary>
        /// The window icon, from the .ico shipped in runtime\. The embedded
        /// application icon gives the taskbar its picture but not the Alt-Tab
        /// entry, which reads the window's own Icon and otherwise falls back to
        /// a blank page.
        /// </summary>
        private void ApplyWindowIcon()
        {
            try
            {
                var path = AppPaths.IconFile;
                if (!File.Exists(path)) return;

                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.EndInit();
                Icon = image;
            }
            catch
            {
                // A missing or unreadable icon is not worth refusing to open over.
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var before = GameFolder;
            if (!ExporterSettings.Show(this, _config)) return;
            SaveSettings();
            if (!string.Equals(before, GameFolder, StringComparison.OrdinalIgnoreCase))
                LogToUi(string.IsNullOrWhiteSpace(GameFolder)
                    ? "Game folder cleared: exports use the small textures inside each ERP."
                    : "Game folder: " + GameFolder + " (applies from the next ERP opened)");
        }

        /// <summary>Collapse / restore the Models sidebar. The dragged
        /// width survives the round trip.</summary>
        private GridLength _savedSidebarWidth = new GridLength(260);

        private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
        {
            SetSidebarVisible(SidebarPanel.Visibility != Visibility.Visible);
            SaveSettings();
        }

        private void SetSidebarVisible(bool visible)
        {
            if (visible)
            {
                SidebarColumn.MinWidth = 140;
                SidebarColumn.Width = _savedSidebarWidth;
                SplitterColumn.Width = new GridLength(10);
                SidebarPanel.Visibility = Visibility.Visible;
                SidebarSplitter.Visibility = Visibility.Visible;
                SidebarToggleButton.Content = "";   // chevron left: will collapse
            }
            else
            {
                _savedSidebarWidth = SidebarColumn.Width;
                SidebarColumn.MinWidth = 0;
                SidebarColumn.Width = new GridLength(0);
                SplitterColumn.Width = new GridLength(0);
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarSplitter.Visibility = Visibility.Collapsed;
                SidebarToggleButton.Content = "";   // chevron right: will reopen
            }
        }

        /// <summary>Shared change handler for settings-bearing controls
        /// that need nothing else done.</summary>
        private void OnSettingChanged(object sender, RoutedEventArgs e) => SaveSettings();

        private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
            SimplifiedUi.FramelessWindow.ToggleMaximize(this);

        private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// Append a timestamped line to the live activity log panel
        /// and to the status bar (so the most recent event is visible
        /// without expanding the log). Thread-safe: calls from worker
        /// threads marshal onto the UI thread via the dispatcher. The
        /// panel auto-scrolls to the bottom.
        /// </summary>
        public void LogToUi(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => LogToUi(message)));
                return;
            }
            try
            {
                if (LogTextBox != null)
                {
                    LogTextBox.AppendText(stamped + Environment.NewLine);
                    LogTextBox.ScrollToEnd();
                }
                if (StatusText != null) StatusText.Text = message;
            }
            catch { /* never let logging crash the app */ }
        }

        /// <summary>
        /// Static bridge so non-UI classes (loaders, extractor, etc.)
        /// can publish to the UI log without holding a MainWindow
        /// reference. Set in the ctor; nulled on close. When null,
        /// LogToStatic is a no-op so worker threads that survive a
        /// window close don't blow up.
        /// </summary>
        public static void LogToStatic(string message) => ExporterLog.Write(message);

        /// <summary>Guards the ctor's apply pass: control setters fire
        /// their change handlers, which would save the half-applied
        /// config over the loaded one.</summary>
        private bool _suppressSettingsSave;

        private void SaveSettings()
        {
            if (_suppressSettingsSave) return;
            try
            {
                bool sidebarVisible = SidebarPanel == null ||
                    SidebarPanel.Visibility == Visibility.Visible;
                _config.FixDecalZFighting = DecalZFightCheckBox?.IsChecked == true;
                _config.RmRenderer = RmRendererCheckBox?.IsChecked == true;
                _config.Gender = (GenderComboBox?.SelectedItem as
                    System.Windows.Controls.ComboBoxItem)?.Content as string ?? "Male";
                _config.SidebarVisible = sidebarVisible;
                _config.SidebarWidth = sidebarVisible && SidebarColumn != null
                    ? SidebarColumn.Width.Value
                    : _savedSidebarWidth.Value;
                _config.ExportTextures = _exportTexturesPref;
                _config.ExportUvMaps = _exportUvMapsPref;
                _config.ExportFbx = _exportFbxPref;
                _config.Save();
            }
            catch { /* best-effort persistence */ }
        }

        /// <summary>Every setting is written once more on the way out --
        /// it catches the ones with no change event (the dragged sidebar
        /// width in particular).</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }

        /// <summary>
        /// Populates F1PackageLocator.Last so MipmapsSidecarLoader and
        /// GenericMipmapsSidecarLoader can scan the game install for the
        /// high-res .mipmaps files that go with helmet textures. We point
        /// both GameRoot and VehiclePackageRoot at the user-picked folder
        /// because helmets don't sit inside a *_vehicle_package; the
        /// recursive sidecar scan will find the helmet's .mipmaps files
        /// anywhere under the install. No-op when no game folder is set
        /// (the loaders short-circuit on null Last).
        /// </summary>
        private static void ConfigurePackageLocator(string gameFolder, string erpPath = null)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                F1PackageLocator.Last = null;
                return;
            }

            // Walk up from the picked ERP looking for a vehicle-package
            // root folder named like "<prefix>_NNNN_<rest>_package".
            // That folder is the SCOPE the high-res mipmaps loader
            // must anchor at -- the foreign-package exclusion in
            // GenericMipmapsSidecarLoader only kicks in when
            // VehiclePackageRoot points at the actual package, not
            // the game install root. Pointing it at the game root
            // makes the recursive scan return whichever year's
            // mipmaps file the filesystem yields first.
            string vehiclePackageRoot = gameFolder;
            string packageLeaf = Path.GetFileName(
                gameFolder.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(erpPath))
            {
                var rx = new System.Text.RegularExpressions.Regex(
                    @"^[a-z][a-z0-9]*_\d{4}_.+_package$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string cur = Path.GetDirectoryName(erpPath);
                for (int hop = 0; hop < 12 && !string.IsNullOrWhiteSpace(cur); hop++)
                {
                    string leaf = Path.GetFileName(cur) ?? "";
                    if (rx.IsMatch(leaf))
                    {
                        vehiclePackageRoot = cur;
                        packageLeaf = leaf;
                        break;
                    }
                    string parent = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(parent) ||
                        string.Equals(parent, cur, StringComparison.Ordinal))
                        break;
                    cur = parent;
                }
            }

            F1PackageLocator.Last = new F1PackageLocator.Located
            {
                GameRoot = gameFolder,
                VehiclePackageRoot = vehiclePackageRoot,
                Kind = F1PackageLocator.PackageKind.F1_2025,
                PackageFolderName = packageLeaf,
            };
        }
    }
}
