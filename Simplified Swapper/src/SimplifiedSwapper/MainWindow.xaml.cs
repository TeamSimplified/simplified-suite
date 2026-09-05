using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SimplifiedSwapper.Core.Config;
using SimplifiedSwapper.Core.Project;

namespace SimplifiedSwapper;

/// <summary>
/// The shell around the two swaps: a title bar, an action bar, both panels on
/// one page, and one set of settings.
///
/// Neither swap is implemented here. Each panel is the corresponding app's own
/// window turned into a control, and the engines stay in their own projects —
/// so what a panel does is what that app does, which is what makes the two
/// comparable while they are simplified.
///
/// What is implemented here is the template. A swap doesn't produce a loose ERP
/// any more; it produces a part inside an open .serps, so this window opens,
/// holds and saves that .serps the way Studio does a project.
/// </summary>
public partial class MainWindow : Window, ISerpsHost
{
    /// <summary>
    /// One config for the app, handed to both panels.
    ///
    /// The two swappers each had their own, and both declared the same four
    /// settings and wrote them to the same file name — so two copies of one
    /// thing, which is exactly what would have clobbered each other in one
    /// process.
    /// </summary>
    private readonly UserConfig _config = UserConfig.Load();

    /// <summary>The open template, or null. One per window, shared by both panels.</summary>
    private SerpsTemplate? _template;

    /// <summary>Set when a swap has gone in and the .serps on disk is behind.</summary>
    private bool _dirty;

    public MainWindow()
    {
        InitializeComponent();
        SimplifiedUi.FramelessWindow.Attach(this, WindowMaximizeButton);
        VersionLabel.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(2) ?? "1.0");

        // A template runs to a few hundred megabytes extracted, so a session
        // that was killed rather than closed is worth sweeping up after.
        try { SerpsStore.CleanupStaleTempDirs(TimeSpan.FromDays(1)); } catch { }

        Chassis.Initialise(_config, this);
        Driverwear.Initialise(_config, this);

        // One Swap for two panels, so it has to follow whichever is in front
        // and whatever that one currently thinks of its inputs.
        Chassis.CanSwapChanged += (_, _) => RefreshSwap();
        Driverwear.CanSwapChanged += (_, _) => RefreshSwap();

        OnTemplateStateChanged();

        // Double-clicking a .serps in Explorer, once the type is associated.
        if (StartupTemplatePath() is { } path) Dispatcher.BeginInvoke(() => OpenTemplateAt(path));
    }

    /// <summary>The .serps named on the command line, if there is one that exists.</summary>
    private static string? StartupTemplatePath()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.EndsWith(SerpsStore.FileExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                return a;
        }
        return null;
    }

    // ── The swap ────────────────────────────────────────────────────────────

    /// <summary>Both halves of the page, in the order a swap runs them.</summary>
    private ISwapPanel[] Panels => [Chassis, Driverwear];

    /// <summary>
    /// Swap runs whichever halves have been changed from what the template
    /// already is, and only those.
    ///
    /// A template opens showing its own team on both sides — that is the state
    /// of not having asked for anything — so pressing Swap then would rebuild a
    /// car as itself and re-dress a team in its own wear. Changing one and
    /// leaving the other is the ordinary case, not an edge one.
    /// </summary>
    private ISwapPanel[] Asked => Panels.Where(p => p.IsChanged).ToArray();

    private void RefreshSwap()
    {
        if (_swapping) return;

        var asked = Asked;
        SwapButton.IsEnabled = asked.Length > 0 && asked.All(p => p.CanSwap);
        SwapButton.Content = asked.Length == 2 ? "Swap both" : "Swap";

        // Only needs a wear picked. Not a template, not a swap, not a save.
        ExtractUvButton.IsEnabled = Driverwear.SelectedWearErp is not null;
    }

    // ── UV maps ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Write out every UV layout in the selected driverwear's ERP.
    ///
    /// Nothing to do with swapping: it reads the archive the dropdown names and
    /// draws what its meshes lay out, whether or not a swap has been made or a
    /// template is even open. The same renderer ERP uses, so a template from
    /// here and one from there are the same drawing.
    /// </summary>
    private async void ExtractUv_Click(object sender, RoutedEventArgs e)
    {
        if (Driverwear.SelectedWearErp is not { } erpPath) return;
        var wearName = Driverwear.SelectedWearName ?? "driverwear";

        var dlg = new OpenFolderDialog
        {
            Title = $"Where should the UV maps for {wearName} go?",
            InitialDirectory = FirstExistingDir(_config.LastUvFolder) ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        var folder = dlg.FolderName;
        _config.LastUvFolder = folder;
        _config.Save();

        ExtractUvButton.IsEnabled = false;
        var previousCursor = Cursor;
        Cursor = System.Windows.Input.Cursors.Wait;
        ReportStatus($"Exporting UVs from {wearName}…");
        ReportProgress(0);

        try
        {
            var session = await Task.Run(() => SimplifiedStudio.UvEngine.UvMapSession.Load(erpPath));
            if (session is null || session.Templates.Count == 0)
            {
                ReportStatus($"{wearName} has no meshes that lay out a texture, so there are no UV maps to draw.",
                    SwapStatus.Problem);
                return;
            }

            // Drawn at the size of the file someone will paint on rather than
            // the size the ERP happens to hold — see TextureSizes.
            var sizes = await Task.Run(() => TextureSizes.Read(erpPath));
            var templates = session.Templates.ToList();
            var written = 0;

            for (var i = 0; i < templates.Count; i++)
            {
                var t = templates[i];
                var stem = Path.GetFileNameWithoutExtension(t.Name.Replace("?", "%3F"));
                var target = Path.Combine(folder, "uvmap_" + stem + ".png");

                var size = sizes.TryGetValue(t.Name, out var s) ? s : (Width: 0, Height: 0);
                if (await Task.Run(() => session.Render(t.Name, target, size.Width, size.Height))) written++;

                ReportProgress((i + 1) / (double)templates.Count);
                ReportStatus($"Exporting UVs — {i + 1} of {templates.Count}…");
            }

            ReportProgress(1);
            ReportStatus($"{written} UV map(s) from {wearName} → {folder}", SwapStatus.Done);
        }
        catch (Exception ex)
        {
            ReportProgress(-1);
            ReportStatus($"Couldn't extract the UV maps: {ex.Message}", SwapStatus.Problem);
        }
        finally
        {
            Cursor = previousCursor;
            RefreshSwap();
        }
    }

    /// <summary>Set while a swap runs, so the button can't start a second one.</summary>
    private bool _swapping;

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        var asked = Asked;
        if (asked.Length == 0 || _swapping) return;

        _swapping = true;
        SwapButton.IsEnabled = false;
        SwapButton.Content = "Swapping…";

        try
        {
            // One after another, not both at once: they write into the same
            // template, and the second would be placing its part while the
            // first was still rewriting the folder list.
            foreach (var panel in asked) await panel.RunSwapAsync();
        }
        finally
        {
            _swapping = false;
            RefreshSwap();
        }
    }

    // ── The status line ─────────────────────────────────────────────────────

    public void ReportStatus(string text, SwapStatus kind = SwapStatus.Plain)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = kind switch
        {
            SwapStatus.Problem => Themed("Danger", 0xB0, 0x2A, 0x2A),
            SwapStatus.Done    => Themed("Accent", 0x2E, 0x7D, 0x4F),
            _                  => Themed("TextPrimary", 0xEA, 0xEA, 0xEA),
        };
    }

    public void ReportProgress(double fraction)
    {
        if (fraction < 0)
        {
            SwapProgress.Value = 0;
            ProgressLabel.Text = "";
            return;
        }

        SwapProgress.Value = fraction;
        ProgressLabel.Text = $"{(int)(fraction * 100)}%";
    }

    private static System.Windows.Media.Brush Themed(string key, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
        ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

    // ── ISerpsHost ──────────────────────────────────────────────────────────

    public bool HasTemplate => _template is not null;

    public event EventHandler? TemplateChanged;

    public string? ShaderPackage => _template?.ShaderPackage;

    /// <summary>
    /// Where a swap writes before it is placed. Outside the template, so a run
    /// that fails part-way through leaves nothing behind in it, and cleared on
    /// the way out so a failed run can't be mistaken for a finished one.
    /// </summary>
    public string ScratchErpPath(SwapParts.Destination destination)
    {
        var dir = Path.Combine(Path.GetTempPath(), "simplified-swapper", "scratch");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SwapParts.PartFileName("", destination));
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        return path;
    }

    public string PlaceSwap(string erpPath, SwapParts.Destination destination)
    {
        if (_template is null)
            throw new InvalidOperationException("No template is open.");

        var part = SerpsStore.PlacePart(_template, erpPath, destination);

        try { File.Delete(erpPath); } catch { /* scratch — the next run clears it */ }

        _dirty = true;
        OnTemplateStateChanged();
        return part;
    }

    // ── Template: open / save / close ───────────────────────────────────────

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard("open another template")) return;

        var dlg = new OpenFileDialog
        {
            Title = "Open template",
            Filter = $"Simplified template|*{SerpsStore.FileExtension}|All files|*.*",
            InitialDirectory = FirstExistingDir(_config.LastTemplateFolder),
        };
        if (dlg.ShowDialog(this) != true) return;

        OpenTemplateAt(dlg.FileName);
    }

    /// <summary>
    /// Open a template by path. The dialog and a double-click in Explorer both
    /// come through here, so either route opens it the same way.
    /// </summary>
    private void OpenTemplateAt(string path)
    {
        try
        {
            CloseTemplateInternal();
            _template = SerpsStore.Open(path);
            _dirty = false;
            _config.LastTemplateFolder = Path.GetDirectoryName(path);
            _config.Save();
            OnTemplateStateChanged();
        }
        catch (Exception ex)
        {
            // Blank line, not one newline: the dialog reads the first paragraph
            // as the headline and the rest as the muted body, so a single break
            // would set the whole exception in bold.
            SimplifiedUi.MessageDialog.Show(this, $"Couldn't open the template.\n\n{ex.Message}",
                "Open template failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_template is null) return;
        try
        {
            SerpsStore.Save(_template);
            _dirty = false;
            OnTemplateStateChanged();
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this, $"The template couldn't be saved.\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveTemplateAs_Click(object sender, RoutedEventArgs e)
    {
        if (_template is null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Save template as",
            Filter = $"Simplified template|*{SerpsStore.FileExtension}|All files|*.*",
            DefaultExt = SerpsStore.FileExtension,
            FileName = Path.GetFileName(_template.SerpsPath),
            InitialDirectory = FirstExistingDir(_config.LastTemplateFolder),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            SerpsStore.Save(_template, dlg.FileName);
            _dirty = false;
            _config.LastTemplateFolder = Path.GetDirectoryName(dlg.FileName);
            _config.Save();
            OnTemplateStateChanged();
        }
        catch (Exception ex)
        {
            SimplifiedUi.MessageDialog.Show(this, $"The template couldn't be saved.\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_template is null) return;
        if (!ConfirmDiscard("close this template")) return;
        CloseTemplateInternal();
        OnTemplateStateChanged();
    }

    /// <param name="resetPanels">
    /// False on the way out of the app: the panels are about to go with the
    /// window, and Reset starts work that would have nowhere to land.
    /// </param>
    private void CloseTemplateInternal(bool resetPanels = true)
    {
        if (_template is null) return;
        SerpsStore.Close(_template);
        _template = null;
        _dirty = false;

        if (!resetPanels) return;

        // Every choice in both panels was made for the template that has just
        // gone. Opening another one comes through here too, which is the case
        // that matters most: the leftovers would be for the wrong car.
        Chassis.Reset();
        Driverwear.Reset();
    }

    /// <summary>
    /// Prompt before throwing away swaps that haven't been saved back. Returns
    /// false to abandon whatever asked.
    /// </summary>
    private bool ConfirmDiscard(string action)
    {
        if (_template is null || !_dirty) return true;

        var answer = SimplifiedUi.MessageDialog.Show(this,
            $"The template has swaps that haven't been saved.\n\nSave before you {action}?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                SaveTemplate_Click(this, new RoutedEventArgs());
                return !_dirty;          // a failed save must not discard the work
            case MessageBoxResult.No:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Everything that depends on whether a template is open, in one place so
    /// the bar and the panels can never disagree about it.
    /// </summary>
    private void OnTemplateStateChanged()
    {
        var open = _template is not null;

        SaveTemplateButton.IsEnabled = open && _dirty;
        SaveTemplateAsButton.IsEnabled = open;
        CloseTemplateButton.IsEnabled = open;

        TemplateNameLabel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NoTemplateLabel.Visibility   = open ? Visibility.Collapsed : Visibility.Visible;

        if (open)
            TemplateNameLabel.Text = _template!.DisplayName + (_dirty ? " •" : "");

        // A panel's Swap is only live while there is somewhere to put the result.
        TemplateChanged?.Invoke(this, EventArgs.Empty);
        RefreshSwap();
    }

    private static string? FirstExistingDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;

    // ── Settings ────────────────────────────────────────────────────────────

    /// <summary>
    /// Settings belongs to the window rather than a panel: both swaps read the
    /// same game folder, and having it in one of them would imply otherwise.
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var before = _config.GameDir;
        if (!SwapperSettings.Show(this, _config)) return;

        _config.Save();

        var gameDirChanged = !string.Equals(before, _config.GameDir, StringComparison.OrdinalIgnoreCase);
        Chassis.SettingsChanged(gameDirChanged);
        Driverwear.SettingsChanged(gameDirChanged);
    }

    // ── Keyboard ────────────────────────────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.O when !shift:
                OpenTemplate_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.S when shift && SaveTemplateAsButton.IsEnabled:
                SaveTemplateAs_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.S when !shift && SaveTemplateButton.IsEnabled:
                SaveTemplate_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.W when !shift && CloseTemplateButton.IsEnabled:
                CloseTemplate_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
        }
    }

    // ── Window chrome ───────────────────────────────────────────────────────
    //
    // The bar at the top is ours, so these three are too. Everything native
    // that a frameless window still needs is in SimplifiedUi.FramelessWindow.

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        SimplifiedUi.FramelessWindow.ToggleMaximize(this);

    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard("close")) e.Cancel = true;
    }

    /// <summary>The working folder goes with the window, saved or not.</summary>
    private void OnWindowClosed(object? sender, EventArgs e) => CloseTemplateInternal(resetPanels: false);
}
