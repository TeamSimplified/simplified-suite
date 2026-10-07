using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Simplified.Shared;
using SimplifiedSponsorEditor.Core.Sponsors;
using SimplifiedUi;

namespace SimplifiedSponsorEditor;

/// <summary>
/// One variation, open: the artwork on the left of the question and the two
/// textures it lands in on the right of it.
///
/// Two views rather than one pane with everything on it, because they answer
/// different questions. <b>Canvas</b> is where the artwork is changed — pieces
/// dropped, colours repainted — and it shows the logo alone, as large as the
/// pane allows. <b>Frame</b> is where it is placed, and it shows the logo inside
/// the two textures the game draws it in, at their own proportions, with a scale
/// for each.
///
/// Both views, the chips that swap them and the frames themselves are v1.1's,
/// down to the caption sizes and the arithmetic that shapes a frame — this is
/// that pane in a window of its own rather than a second attempt at it. What is
/// new here is the per-frame scale and the bitmap case.
///
/// Translucent, which is the point of it being its own window rather than a
/// page: the shell stays visible underneath, so this reads as something opened
/// over the sponsor rather than somewhere else you went. That one requirement is
/// why the chrome is built here instead of coming from DialogChrome — see
/// <see cref="Chrome"/>.
/// </summary>
public static class VariationWindow
{
    private static readonly Regex Hex = new("^#?[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>The margin the suite's dialog bodies use.</summary>
    private const double BodyMargin = 18;

    /// <summary>
    /// Open <paramref name="variation"/> and return what it became, or null if
    /// the window was closed without keeping it.
    /// </summary>
    public static Variation? Show(Window owner, Variation variation)
    {
        if (variation.SourcePath is not { } source) return null;

        var working = variation.Copy();

        // Reading somebody else's file is where this goes wrong, and it goes
        // wrong in more ways than a null: the SVG reader throws on a file that
        // has moved, and on one whose contents are not the drawing its extension
        // promises. Both of those are a sentence to read rather than a crash,
        // and the slot is still there to pick something else with.
        ArtworkCanvas canvas;

        try
        {
            canvas = new ArtworkCanvas(source, working.Edits, working.Overlay);
        }
        catch (Exception problem)
        {
            MessageDialog.Show(owner,
                $"{variation.Name} could not be opened.\n\n{problem.Message}",
                "Edit artwork", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (canvas.IsEmpty)
        {
            MessageDialog.Show(owner,
                $"{variation.Name} could not be read as artwork.",
                "Edit artwork", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var frames = new Frames(canvas, working);

        // ── The pane ─────────────────────────────────────────────────
        //
        // The same surface Studio's preview sits in and v1.1's decal pane sits
        // in, so a picture is judged on the same ground wherever it is shown.
        var pane = new Grid();
        canvas.Element.Margin = new Thickness(12);
        pane.Children.Add(canvas.Element);
        pane.Children.Add(frames.Element);
        frames.Element.Visibility = Visibility.Collapsed;

        var surface = new Border { Child = pane };
        surface.SetResourceReference(FrameworkElement.StyleProperty, "Panel");
        surface.SetResourceReference(Panel.BackgroundProperty, "SurfaceSunken");

        // ── The bar under it ─────────────────────────────────────────
        //
        // The house chips, as v1.1 puts them on the pane that comes and goes
        // with the selection.
        var canvasChip = Chip("Canvas", "The artwork, with its pieces to hand", true);
        var frameChip = Chip("Frame", "Both textures the artwork goes into, at their own proportions", false);

        void ShowCanvas(bool wanted)
        {
            canvas.Element.Visibility = wanted ? Visibility.Visible : Visibility.Collapsed;
            frames.Element.Visibility = wanted ? Visibility.Collapsed : Visibility.Visible;

            // Recomposited on the way in rather than on every stroke: two
            // textures is not free, and nothing in the Frame view changes while
            // the Canvas view is the one being used.
            if (!wanted) frames.Refresh();
        }

        canvasChip.Checked += (_, _) => ShowCanvas(true);
        frameChip.Checked += (_, _) => ShowCanvas(false);

        var picked = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        picked.Children.Add(canvasChip);
        picked.Children.Add(frameChip);

        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var remove = new Button { Content = "Delete", Width = 80, IsEnabled = false };
        var reset = new Button { Content = "Reset", Width = 80, IsEnabled = false };

        // One box, showing the colour of whatever is picked — the same control
        // the Grid Editor asks a team colour with, down to the template, so a
        // colour box behaves the same wherever somebody meets one in the suite.
        var colourBox = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Template = FlatButton.Template(cornerRadius: 3),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Margin = new Thickness(16, 0, 0, 0),
            ToolTip = canvas.IsVector
                ? "Click to change the colour of what is picked"
                : "Click to lay a colour over the whole picture",
        };

        colourBox.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");
        remove.Margin = new Thickness(10, 0, 0, 0);
        remove.ToolTip = "Drop the pieces that are picked";
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.ToolTip = "Put the artwork back the way the file has it";

        // A picture has no pieces, so it has nothing to delete. Collapsed
        // rather than disabled: a button that can never be pressed for this
        // kind of file is not a state of the button, it is a tool that does not
        // apply, and leaving it greyed out says the opposite.
        if (!canvas.IsVector) remove.Visibility = Visibility.Collapsed;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(picked);
        tools.Children.Add(colourBox);
        tools.Children.Add(remove);
        tools.Children.Add(reset);

        // ── Judging it against light or dark ─────────────────────────
        //
        // v1.1's slider, and what it is for: a light logo reads against dark and
        // a dark one against light, and neither is a thing you can decide by
        // looking at one ground. It starts at whichever end the theme is on,
        // which is the ground the rest of the app is already showing.
        var backdrop = new SolidColorBrush(Colors.White);

        var slider = new Slider
        {
            Width = 130,
            VerticalAlignment = VerticalAlignment.Center,
            Value = ThemeManager.IsDark ? 1 : 0,
            ToolTip = "Slide from a light background to a dark one — "
                      + "a light logo reads against dark, a dark one against light",
        };

        slider.SetResourceReference(FrameworkElement.StyleProperty, "BackdropSlider");

        var backdropLabel = new TextBlock
        {
            Text = "Background",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        backdropLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var judging = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };

        judging.Children.Add(backdropLabel);
        judging.Children.Add(slider);

        void Judge()
        {
            backdrop.Color = Backdrop(slider.Value);

            // Inside the frames rather than behind everything: what the slider
            // is for is judging a logo against light or dark, and in that view
            // the only parts that are a logo are the two textures. The sizes
            // under them read against the app's own surface.
            frames.Backdrop = backdrop;

            // And behind the artwork on the Canvas view, where the logo is the
            // whole of what is on screen. This is what the checkerboard used to
            // be: the squares said "nothing is here", which a slider says
            // better, because it says it against every ground in turn rather
            // than against one nobody chose.
            canvas.Element.Background = backdrop;
        }

        slider.ValueChanged += (_, _) => Judge();
        Judge();



        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(tools, Dock.Left);
        DockPanel.SetDock(judging, Dock.Right);
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(tools);
        row.Children.Add(judging);
        row.Children.Add(count);

        var bar = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Child = row,
        };

        bar.SetResourceReference(FrameworkElement.StyleProperty, "Panel");

        // Named and called once below rather than only subscribed: the canvas
        // settles itself as it is built, which is before this window exists to
        // hear it. Subscribing alone left the count blank and the button saying
        // Delete over a picture that has nothing to delete, until the first
        // click happened to put it right.
        void Settled()
        {
            // Nothing for a bitmap. It has one piece and it is always the one
            // picked, so a count would read "1 of 1" forever, and explaining
            // that there is nothing to pick is a sentence that stops being news
            // the first time it is read.
            count.Text = canvas.IsVector
                ? canvas.SelectedCount switch
                {
                    0 => $"{canvas.PartCount} part{(canvas.PartCount == 1 ? "" : "s")}  ·  click one to pick it",
                    1 => $"1 of {canvas.PartCount} picked",
                    var many => $"{many} of {canvas.PartCount} picked",
                }
                : "";

            remove.IsEnabled = canvas.CanDelete;
            reset.IsEnabled = canvas.CanReset;

            colourBox.IsEnabled = canvas.SelectedCount > 0;
            colourBox.Background = canvas.Shown is { } one
                ? new SolidColorBrush(one)
                : ThemeBrush("SurfaceSunken");

            // The frames are composited from the canvas, so a colour chosen
            // while they are the view on screen has to be recomposited into
            // them. Only while they are showing: on the Canvas view this would
            // be two textures redrawn for a picture nobody is looking at, and
            // switching to them redraws anyway.
            if (frames.Element.Visibility == Visibility.Visible) frames.Refresh();
        }

        canvas.Changed += Settled;
        Settled();

        remove.Click += (_, _) => canvas.Delete();
        reset.Click += (_, _) => canvas.Reset();

        // ── Keep or don't ────────────────────────────────────────────
        var keep = new Button { Content = "Save", Width = 80, IsDefault = true, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        actions.Children.Add(keep);
        actions.Children.Add(cancel);

        var body = new DockPanel { Margin = new Thickness(BodyMargin) };
        DockPanel.SetDock(actions, Dock.Bottom);
        DockPanel.SetDock(bar, Dock.Bottom);
        body.Children.Add(actions);
        body.Children.Add(bar);
        body.Children.Add(surface);

        var window = new Window
        {
            Title = $"Edit artwork — {variation.Name}",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 860,
            Height = 700,
            MinWidth = 520,
            MinHeight = 420,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            // CanResize, not WithGrip: the grip is the dotted triangle in
            // the corner, and the window still resizes from its edges
            // without it.
            ResizeMode = ResizeMode.CanResize,
            Background = Brushes.Transparent,
        };

        window.Content = Chrome(window, body);

        colourBox.Click += (_, _) =>
        {
            if (canvas.SelectedCount == 0) return;

            var from = canvas.Shown ?? Colors.White;

            var chosen = ColourDialog.Show(
                window, canvas.IsVector ? "Part colour" : "Overlay colour",
                $"#{from.R:X2}{from.G:X2}{from.B:X2}");

            if (chosen is null || !Hex.IsMatch(chosen)) return;

            var text = chosen.TrimStart('#');
            canvas.Paint(Color.FromRgb(
                byte.Parse(text[..2], NumberStyles.HexNumber),
                byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber),
                byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber)));
        };

        keep.Click += (_, _) =>
        {
            if (canvas.IsVector && canvas.PartCount == 0)
            {
                MessageDialog.Show(window,
                    "Everything has been deleted, so there is no artwork left.",
                    "Edit artwork", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            window.DialogResult = true;
        };

        if (window.ShowDialog() != true) return null;

        working.Edits = canvas.Result();
        working.Overlay = canvas.Overlay;
        return working;
    }

    /// <summary>
    /// DialogChrome's window, rebuilt here because this one has to be see
    /// through.
    ///
    /// Transparency and WindowChrome are the two ways to drop the system frame
    /// and they exclude each other: WindowChrome keeps the native resize edges
    /// and Aero Snap but renders opaque whatever alpha the background carries,
    /// and AllowsTransparency is the only thing that gives real alpha. So the
    /// bar, its padding, its title and its close button are DialogChrome's to
    /// the pixel, the corners are rounded by the border rather than by the
    /// compositor, and the one thing lost is the native resize edge — hence the
    /// grip.
    /// </summary>
    private static UIElement Chrome(Window window, UIElement content)
    {
        var ground = new Border { CornerRadius = new CornerRadius(8), Child = content };

        // No bar at all. DialogChrome leaves one out when a window has nothing
        // to put in it, and with the title and the close button both gone that
        // is this one — an empty 38px strip is not nothing, it is a body that
        // looks like it has slipped down the window.
        //
        // Which leaves nothing to drag. So the window moves by any press that
        // nothing else wanted: the canvas marks its own clicks handled, and so
        // do the buttons, the chips and the sliders, so what reaches here is a
        // press on the window's own empty space.
        ground.MouseLeftButtonDown += (_, e) =>
        {
            if (!e.Handled && e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };

        // v1.1's grey, at eight tenths. Read from the palette rather than
        // written down so this follows a theme change like every other dialog,
        // with the alpha put on afterwards — a resource reference cannot do it,
        // because the alpha is this window's rather than the brush's.
        void Paint()
        {
            var grey = ThemeBrush("Surface") is SolidColorBrush brush
                ? brush.Color
                : Color.FromRgb(0x22, 0x22, 0x22);

            ground.Background = new SolidColorBrush(Color.FromArgb(0xCC, grey.R, grey.G, grey.B));
        }

        Paint();
        window.Loaded += (_, _) => Paint();

        return ground;
    }

    private static RadioButton Chip(string text, string why, bool on)
    {
        var chip = new RadioButton
        {
            Content = text,
            GroupName = "VariationView",
            IsChecked = on,
            ToolTip = why,
        };

        chip.SetResourceReference(FrameworkElement.StyleProperty, "ChannelChip");
        return chip;
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>
    /// The slider's ground, between the palette's two ends. The same ramp v1.1
    /// mixes, off the same two keys, so the strip under the thumb and the
    /// colour it produces agree — the style draws those keys as its gradient
    /// stops.
    /// </summary>
    private static Color Backdrop(double mix)
    {
        var light = End("PreviewBackdropLight", Colors.White);
        var dark = End("PreviewBackdropDark", Colors.Black);

        mix = Math.Clamp(mix, 0, 1);

        return Color.FromRgb(
            (byte)Math.Round(light.R + (dark.R - light.R) * mix),
            (byte)Math.Round(light.G + (dark.G - light.G) * mix),
            (byte)Math.Round(light.B + (dark.B - light.B) * mix));
    }

    private static Color End(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) switch
        {
            Color colour => colour,
            SolidColorBrush brush => brush.Color,
            _ => fallback,
        };

    /// <summary>
    /// The two textures a sponsor goes into, side by side at their own
    /// proportions, with each one's size under it — v1.1's frames pane, with a
    /// scale added under each.
    ///
    /// Separate scales because the two are different shapes doing different
    /// jobs: a logo that fills a square on the car is not the same logo that
    /// should fill a wide thumbnail in a menu, and one slider for both would
    /// mean always compromising one of them.
    /// </summary>
    private sealed class Frames
    {
        private readonly ArtworkCanvas _canvas;
        private readonly Variation _variation;

        private readonly Grid _view;
        private readonly Border _carFrame = Frame();
        private readonly Border _menuFrame = Frame();
        private readonly Image _carImage = Picture();
        private readonly Image _menuImage = Picture();
        private readonly StackPanel _carCaption;
        private readonly StackPanel _menuCaption;
        private readonly TextBlock _carSize = Size();
        private readonly TextBlock _menuSize = Size();
        private readonly TextBlock _carSource = Source();
        private readonly TextBlock _menuSource = Source();

        private (uint Width, uint Height)? _carTexture;
        private (uint Width, uint Height)? _menuTexture;

        public UIElement Element => _view;

        /// <summary>
        /// What the artwork is judged against, inside each frame. The frames are
        /// the only part of this view that is a logo; the captions under them
        /// stay on the app's own surface.
        /// </summary>
        public Brush? Backdrop
        {
            set
            {
                _carFrame.Background = value;
                _menuFrame.Background = value;
            }
        }

        public Frames(ArtworkCanvas canvas, Variation variation)
        {
            _canvas = canvas;
            _variation = variation;

            _carFrame.Child = _carImage;
            _menuFrame.Child = _menuImage;

            _carCaption = Caption("On the car", _carSize, _carSource, variation.CarSpan,
                                  span => { variation.CarSpan = span; Refresh(); });

            _menuCaption = Caption("In the menu", _menuSize, _menuSource, variation.MenuSpan,
                                   span => { variation.MenuSpan = span; Refresh(); });

            // The car's texture is the tall one and sets the row's height; the
            // menu's is centred in that height rather than dropped to sit on its
            // own text, so the two pictures share a middle.
            var car = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom };
            DockPanel.SetDock(_carCaption, Dock.Bottom);
            car.Children.Add(_carCaption);
            car.Children.Add(_carFrame);

            var menu = new DockPanel
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(24, 0, 0, 0),
            };

            DockPanel.SetDock(_menuCaption, Dock.Bottom);
            menu.Children.Add(_menuCaption);
            menu.Children.Add(_menuFrame);

            var pair = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            pair.Children.Add(car);
            pair.Children.Add(menu);

            // One row, centred as a whole rather than one frame per half: the
            // car's texture can be several times the menu's across, and halves
            // put the big frame against the outer edge and left the small one
            // adrift.
            //
            // This grid fills the pane, which is what keeps its width
            // independent of the frames inside it — they are sized from that
            // width, and a container sized to its frame would be answering with
            // the number it was just given.
            _view = new Grid { Margin = new Thickness(12) };
            _view.Children.Add(pair);
            _view.SizeChanged += (_, _) => LayOut();
        }

        /// <summary>Recomposite both textures from whatever the Canvas now is.</summary>
        public void Refresh()
        {
            var art = _canvas.Render();

            if (art is null)
            {
                Empty(_carFrame, _carImage, _carSize, _carSource);
                Empty(_menuFrame, _menuImage, _menuSize, _menuSource);
                _carTexture = null;
                _menuTexture = null;
                LayOut();
                return;
            }

            // Both read from the game's own archives rather than from the
            // artwork: these frames exist to say where a logo lands in the
            // texture the game draws, and a frame shaped to the file somebody
            // happened to pick would be answering a different question.
            _carTexture = SponsorTextures.Car;
            _menuTexture = SponsorTextures.Menu;

            Fill(_carFrame, _carImage, _carSize, _carSource,
                 TextureFit.Compose(art, _carTexture.Value.Width, _carTexture.Value.Height, _variation.CarSpan),
                 _carTexture.Value, (art.PixelWidth, art.PixelHeight));

            Fill(_menuFrame, _menuImage, _menuSize, _menuSource,
                 TextureFit.Compose(art, _menuTexture.Value.Width, _menuTexture.Value.Height, _variation.MenuSpan),
                 _menuTexture.Value, (art.PixelWidth, art.PixelHeight));

            LayOut();
        }

        /// <summary>
        /// Half of what is left after the gap, each. Measured off the pane,
        /// which fills it and so has a width of its own; the row the frames sit
        /// in hugs them, and asking that how wide it is would be asking it about
        /// the number it was just given.
        /// </summary>
        private void LayOut()
        {
            const double gap = 24;

            var room = (_view.ActualWidth - gap) / 2;

            // Whichever caption runs longer decides, so the two frames sit the
            // same height off the text whether or not one of them is saying
            // where its artwork came from.
            var captions = Math.Max(_carCaption.ActualHeight, _menuCaption.ActualHeight);
            if (captions <= 0) captions = 86;

            var height = _view.ActualHeight - captions - 8;

            Shape(_carFrame, _carTexture, room, height);
            Shape(_menuFrame, _menuTexture, room, height);
        }

        /// <summary>
        /// One for one at most: shrink a texture to fit the pane, never stretch
        /// one past the pixels it has.
        /// </summary>
        private static void Shape(Border frame, (uint Width, uint Height)? texture,
                                  double room, double height)
        {
            if (texture is not { } size || size.Width == 0 || size.Height == 0
                || room <= 0 || height <= 0)
            {
                frame.Width = double.NaN;
                frame.Height = double.NaN;
                return;
            }

            var scale = Math.Min(1, Math.Min(room / size.Width, height / size.Height));

            frame.Width = Math.Max(1, size.Width * scale);
            frame.Height = Math.Max(1, size.Height * scale);
        }

        private static void Fill(
            Border frame, Image image, TextBlock size, TextBlock source,
            BitmapSource picture, (uint Width, uint Height) texture, (int Width, int Height)? from)
        {
            image.Source = picture;
            frame.Visibility = Visibility.Visible;

            size.Text = $"{texture.Width} × {texture.Height}";

            // Where the artwork came from, when the texture is not that size.
            // The number somebody recognises is the one they picked.
            source.Text = from is { } f && (f.Width != texture.Width || f.Height != texture.Height)
                ? $"from {f.Width} × {f.Height}"
                : "";
        }

        private static void Empty(Border frame, Image image, TextBlock size, TextBlock source)
        {
            image.Source = null;
            frame.Visibility = Visibility.Hidden;
            size.Text = "nothing left to draw";
            source.Text = "";
        }

        private static Border Frame()
        {
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            frame.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");
            return frame;
        }

        private static Image Picture()
        {
            var image = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        private static TextBlock Size()
        {
            var text = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = 12,
            };

            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            return text;
        }

        private static TextBlock Source()
        {
            var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabled");
            return text;
        }

        private static StackPanel Caption(
            string what, TextBlock size, TextBlock source, double span, Action<double> changed)
        {
            var heading = new TextBlock
            {
                Text = what,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            };

            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            var amount = new TextBlock
            {
                Text = Percent(span),
                Width = 38,
                FontSize = 11,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            amount.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

            var slider = new Slider
            {
                Minimum = Variation.MinSpan,
                Maximum = Variation.MaxSpan,
                Value = span,
                Width = 110,
                VerticalAlignment = VerticalAlignment.Center,
                // A tenth of a per cent would be a slider nobody can land on
                // twice. One per cent is finer than the eye can tell at this
                // size and still a number somebody can say out loud.
                SmallChange = 0.01,
                LargeChange = 0.05,
                IsSnapToTickEnabled = true,
                TickFrequency = 0.01,
                ToolTip = "How much of the texture the artwork spans",
            };

            // The suite's slider, same thumb as the one under Background. These
            // were the stock Windows control, which is the one thing in the
            // window that still looked like somebody else's app.
            slider.SetResourceReference(FrameworkElement.StyleProperty, "ValueSlider");

            slider.ValueChanged += (_, e) =>
            {
                amount.Text = Percent(e.NewValue);
                changed(e.NewValue);
            };

            var scale = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };

            scale.Children.Add(slider);
            scale.Children.Add(amount);

            var caption = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            caption.Children.Add(heading);
            caption.Children.Add(size);
            caption.Children.Add(source);
            caption.Children.Add(scale);
            return caption;
        }

        private static string Percent(double span) =>
            (span * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    }
}
