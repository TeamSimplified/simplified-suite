using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SimplifiedHelmetSwapper.Core.Config;
using SimplifiedHelmetSwapper.Core.Helmet;

namespace SimplifiedHelmetSwapper;

public partial class MainWindow : Window
{
    private static Brush ErrorBrush => Themed("Danger", 0xB0, 0x2A, 0x2A);
    private static Brush HintBrush => Themed("TextMuted", 0x8A, 0x92, 0x9C);
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static Brush OkBrush => Themed("Accent", 0x2E, 0x7D, 0x4F);

    private readonly UserConfig _config = UserConfig.Load();

    private List<HelmetCatalogue.Entry> _helmets = new();

    /// <summary>
    /// What each helmet turned out to hold, once it has been opened.
    ///
    /// Reading one means unpacking an ERP, which is far too slow to do for all
    /// 159 of them at startup and quick enough to do for the two on screen. The
    /// files don't change while the app is open, so once is enough.
    /// </summary>
    private readonly Dictionary<string, HelmetCatalogue.Contents?> _contents = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);
        VersionLabel.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(2) ?? "1.0");

        RefreshHelmetList();
        RefreshStatus();
        RefreshSwapButton();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!HelmetSettings.Show(this, _config)) return;

        _config.Save();
        _contents.Clear();          // a different install is a different set of helmets
        RefreshHelmetList();
        RefreshStatus();
        RefreshSwapButton();
    }

    /// <summary>
    /// Fill both dropdowns from the same list. Either side can be any driver:
    /// which helmet you want and who should wear it are independent choices, and
    /// the only pairing that means nothing is a driver with their own.
    /// </summary>
    private void RefreshHelmetList()
    {
        _helmets = HelmetCatalogue.Scan(_config.GameDir).ToList();

        HelmetBox.ItemsSource = _helmets;
        WearerBox.ItemsSource = _helmets;
        HelmetBox.IsEnabled = _helmets.Count > 0;
        WearerBox.IsEnabled = _helmets.Count > 0;

        HelmetHint.Text = "The helmet you want to use — shell, attachments, visor and paint.";
        WearerHint.Text = "The driver who will wear it. Their own helmet is replaced.";
    }

    /// <summary>The contents of one helmet, read on first use and remembered after.</summary>
    private HelmetCatalogue.Contents? ContentsOf(HelmetCatalogue.Entry entry)
    {
        if (_contents.TryGetValue(entry.Folder, out var cached)) return cached;
        var read = HelmetCatalogue.Read(entry);
        _contents[entry.Folder] = read;
        return read;
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HelmetHint is null) return;   // fires during InitializeComponent

        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            HelmetHint.Text = Describe(HelmetBox.SelectedItem as HelmetCatalogue.Entry,
                "The helmet you want to use — shell, attachments, visor and paint.");
            WearerHint.Text = Describe(WearerBox.SelectedItem as HelmetCatalogue.Entry,
                "The driver who will wear it. Their own helmet is replaced.");
        }
        finally { Cursor = null; }

        SyncOutputPath();
        RefreshStatus();
        RefreshSwapButton();
    }

    /// <summary>
    /// Say what a pick is actually made of — the shell it is built on and which
    /// of the four parts the driver has of their own. Both decide what a swap
    /// can carry across, so they are worth seeing before pressing Swap.
    /// </summary>
    private string Describe(HelmetCatalogue.Entry? entry, string fallback)
    {
        if (entry is null) return fallback;

        var contents = ContentsOf(entry);
        if (contents is null)
            return $"{Path.GetFileName(entry.ErpPath)} — not a helmet this app can read.";

        var parts = contents.Families.Count > 0
            ? string.Join(", ", contents.Families.Select(f => f.Replace('_', ' ')))
            : "nothing of their own";

        return $"{Shell(contents.Shell)} shell — {parts}.";
    }

    /// <summary>
    /// The five shells, as the helmets are actually called.
    ///
    /// Spelled out rather than derived: these are brand names, and no rule that
    /// turns <c>hp7</c> into HP7 also turns <c>pro</c> into Pro and
    /// <c>gp_6_01</c> into GP-6.
    /// </summary>
    private static readonly Dictionary<string, string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["helmet_bell_hp7"] = "Bell HP7",
        ["helmet_arai_gp_6_01"] = "Arai GP-6",
        ["helmet_schuberth_sf2_pro"] = "Schuberth SF2 Pro",
        ["helmet_stilo_st5"] = "Stilo ST5",
        ["helmet_stilo_st6"] = "Stilo ST6",
    };

    /// <summary>"helmet_bell_hp7" reads as "Bell HP7"; one we don't know reads as itself.</summary>
    private static string Shell(string shell) =>
        ShellNames.TryGetValue(shell, out var name) ? name : shell;

    // ── Output ──────────────────────────────────────────────────────────────

    /// <summary>The path we filled in ourselves, so a typed one is never clobbered.</summary>
    private string _autoOutput = "";

    /// <summary>
    /// Named after the driver who will wear it, because the file it replaces is
    /// theirs — the game looks for <c>&lt;driver&gt;_helmet.erp</c> and nothing else.
    /// </summary>
    private static string DefaultOutputName(HelmetCatalogue.Entry wearer) => $"{wearer.Driver}_helmet.erp";

    private void SyncOutputPath()
    {
        if (WearerBox.SelectedItem is not HelmetCatalogue.Entry wearer) return;
        if (OutputBox.Text.Length != 0 && OutputBox.Text != _autoOutput) return;

        var folder = FirstExistingDir(_config.LastOutputFolder)
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try { _autoOutput = Path.Combine(folder, DefaultOutputName(wearer)); }
        catch { return; }   // characters the filesystem won't take
        OutputBox.Text = _autoOutput;
    }

    private void Output_Changed(object sender, TextChangedEventArgs e)
    {
        if (OutputHint is null) return;   // fires during InitializeComponent
        RefreshStatus();
        RefreshSwapButton();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var wearer = WearerBox.SelectedItem as HelmetCatalogue.Entry;

        var dlg = new SaveFileDialog
        {
            Title = "Save the swapped helmet",
            Filter = "ERP archives (*.erp)|*.erp",
            DefaultExt = ".erp",
            AddExtension = true,
            FileName = SafeFileName(OutputBox.Text) ?? (wearer is null ? "" : DefaultOutputName(wearer)),
            InitialDirectory = FirstExistingDir(SafeDirName(OutputBox.Text), _config.LastOutputFolder) ?? "",
            OverwritePrompt = false,   // confirmed at swap time instead
        };
        if (dlg.ShowDialog(this) != true) return;

        OutputBox.Text = dlg.FileName;
        _autoOutput = "";              // an explicit choice — stop re-deriving it
        _config.LastOutputFolder = Path.GetDirectoryName(dlg.FileName);
        _config.Save();
    }

    private void RefreshStatus()
    {
        var gameDir = _config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            SetStatus("Set your game folder in Settings to begin.", HintBrush);
            return;
        }

        if (_helmets.Count == 0)
        {
            SetStatus("No helmets found — expected character_package/drivers under the game folder.", ErrorBrush);
            return;
        }

        var from = HelmetBox.SelectedItem as HelmetCatalogue.Entry;
        var to = WearerBox.SelectedItem as HelmetCatalogue.Entry;

        if (from is null || to is null)
        {
            SetStatus($"{_helmets.Count} helmets found. Choose one, and who should wear it.", MutedBrush);
            return;
        }

        if (from.Folder.Equals(to.Folder, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"{Name(to)} already wears that helmet.", MutedBrush);
            return;
        }

        if (OutputBox.Text.Trim().Length == 0)
        {
            SetStatus("Choose where to save the swapped helmet.", MutedBrush);
            return;
        }

        var donor = ContentsOf(from);
        var wearer = ContentsOf(to);

        if (donor is not null && wearer is not null && donor.Shell != wearer.Shell)
        {
            SetStatus($"{Name(from)}'s helmet on {Name(to)} — a {Shell(donor.Shell)} shell " +
                      $"in place of their {Shell(wearer.Shell)}.", MutedBrush);
            return;
        }

        SetStatus($"{Name(from)}'s helmet on {Name(to)}.", MutedBrush);
    }

    /// <summary>Just the driver, for a line that has already said which team.</summary>
    private static string Name(HelmetCatalogue.Entry entry) => entry.Label.Split(" — ")[0];

    /// <summary>
    /// Both sides must be chosen and different — giving a driver back their own
    /// helmet is a no-op, not a swap.
    /// </summary>
    private void RefreshSwapButton()
    {
        var from = HelmetBox.SelectedItem as HelmetCatalogue.Entry;
        var to = WearerBox.SelectedItem as HelmetCatalogue.Entry;

        SwapButton.IsEnabled =
            from is not null && to is not null &&
            !from.Folder.Equals(to.Folder, StringComparison.OrdinalIgnoreCase) &&
            OutputBox.Text.Trim().Length > 0;
    }

    // ── Swap ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The swap folder, beside the exe. Kept next to the app rather than in temp
    /// so the extracts are easy to find and inspect between runs.
    /// </summary>
    private static string WorkingDir => Path.Combine(AppContext.BaseDirectory, "swap");

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (HelmetBox.SelectedItem is not HelmetCatalogue.Entry from ||
            WearerBox.SelectedItem is not HelmetCatalogue.Entry to) return;

        var outputPath = OutputBox.Text.Trim();
        if (File.Exists(outputPath))
        {
            var answer = MessageBox.Show(this,
                $"{Path.GetFileName(outputPath)} already exists. Replace it?",
                "Simplified Helmet Swapper", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        var log = new List<string>();
        void Log(string line)
        {
            log.Add(line);
            PiecesList.ItemsSource = null;
            PiecesList.ItemsSource = log;
            if (log.Count > 0) PiecesList.ScrollIntoView(log[^1]);
        }

        SetBusy(true);
        PiecesHint.Text = $"Working in {WorkingDir}";
        SetStatus($"Building {Name(from)}'s helmet for {Name(to)}…", MutedBrush);

        try
        {
            var result = await HelmetPrepare.RunAsync(new HelmetPrepare.PrepareOptions
            {
                GameDir = _config.GameDir!,
                Donor = from,
                Target = to,
                WorkingDir = WorkingDir,
                OutputPath = outputPath,
            }, Log);

            Log("");
            foreach (var w in result.Warnings) Log($"warning: {w}");

            PiecesHint.Text = $"{result.Outputs.Count} piece(s) in {WorkingDir}";

            if (result.OutputPath is null)
                SetStatus("The helmet wasn't written — see the list.", ErrorBrush);
            else if (result.Warnings.Count > 0)
                SetStatus($"Wrote {Path.GetFileName(result.OutputPath)} " +
                          $"({result.OutputResources} resources) with {result.Warnings.Count} warning(s).",
                          ErrorBrush);
            else
            {
                _config.LastOutputFolder = Path.GetDirectoryName(result.OutputPath);
                _config.Save();

                var shell = result.ShellChanged
                    ? $" on a {Shell(result.DonorShell)} shell in place of the {Shell(result.TargetShell)},"
                    : "";
                SetStatus($"Wrote {Path.GetFileName(result.OutputPath)}{shell} " +
                          $"{result.Replaced} replaced and {result.Added} added, " +
                          $"{result.Kept} of {Name(to)}'s own left untouched. " +
                          $"It replaces the file of the same name.", OkBrush);
            }
        }
        catch (Exception ex)
        {
            Log("");
            Log($"FAILED: {ex.Message}");
            SetStatus($"The swap failed: {ex.Message}", ErrorBrush);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var c in new Control[] { HelmetBox, WearerBox, OutputBox })
            c.IsEnabled = !busy;

        SwapButton.Content = busy ? "Working…" : "Swap";
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;

        if (busy) SwapButton.IsEnabled = false;
        else
        {
            HelmetBox.IsEnabled = _helmets.Count > 0;
            WearerBox.IsEnabled = _helmets.Count > 0;
            RefreshSwapButton();
        }
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = brush;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? FirstExistingDir(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return c;
        return null;
    }

    private static string? SafeFileName(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path); }
        catch { return null; }
    }

    private static string? SafeDirName(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path); }
        catch { return null; }
    }

    // ── Window chrome ───────────────────────────────────────────────────────
    //
    // The bar at the top is ours, so these three are too. Everything native
    // that a frameless window still needs - rounded corners, and a maximise
    // that respects the taskbar - is in SimplifiedUi.FramelessWindow.

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Palette lookup with a fallback, for the few brushes set in code.</summary>
    private static Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));
}
