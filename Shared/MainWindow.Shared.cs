using SimplifiedUi;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SimplifiedStudio.Core.Config;
using EgoEngineLibrary.Texconv;
using SimplifiedStudio.Core.Util;
using SimplifiedStudio.Model;
using SimplifiedStudio.Util;

namespace SimplifiedStudio;

/// <summary>
/// The half of the window that Simplified Studio and Simplified ERP have in
/// common.
///
/// This file is BYTE-IDENTICAL in both apps. Changing it means copying it
/// across, not editing it twice - which is the entire reason it exists. The
/// same logic living inline in each app's MainWindow had to be ported by hand
/// every time it changed, and drifted a little on each pass: a badge that
/// silently stopped working in one app, a dialog styled two different ways, a
/// resource operation that leaked into an app with no use for it.
///
/// It is a partial class rather than a library so the move could stay verbatim.
/// These methods use the window's own fields and named controls, and turning
/// them into free functions would have meant redesigning them - a bigger
/// change, with more room to break something, for no gain today. A separate
/// project would enforce the boundary properly and is the natural next step if
/// this holds up.
///
/// The boundary is enforced in the meantime by the file being identical: a
/// method here that reaches for something only one app has will fail to
/// compile in the other as soon as it is synced.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The app's own dialog in place of a system message box: a headline, a
    /// muted body and a right-aligned row of named buttons, themed along with
    /// the rest of the window.
    ///
    /// The labels say what each button does, which is what lets the body drop
    /// the "Yes — save, No — discard" gloss the message-box version needed.
    ///
    /// Choices are given in reading order. The first answers Enter; the last
    /// answers Esc and the title-bar close, so it should always be the choice
    /// that changes nothing. Only the others close the window themselves — the
    /// last one is left to the built-in cancel handling, which would fault if it
    /// tried to set a result on an already-closed window.
    ///
    /// The result comes back as a MessageBoxResult so call sites keep switching
    /// on the values they already used.
    /// </summary>
    /// <summary>
    /// The app's own dialog in place of <c>MessageBox.Show</c>, taking the same
    /// arguments in the same order so the call sites read as they did.
    ///
    /// These messages were already written as a headline, a blank line, then the
    /// detail, so the first paragraph becomes the headline and the rest the
    /// body — the shape every other dialog here uses.
    ///
    /// The icon argument is kept for the call sites' sake but only Error is
    /// expressed, as a red headline. A warning icon on what is really a
    /// confirmation reads as alarm.
    /// </summary>
    private MessageBoxResult ShowMessage(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        // MessageBox.Show tolerated being called off the UI thread; a Window does
        // not. Several of these report the outcome of background work, so marshal
        // rather than leave that to each call site.
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => ShowMessage(message, title, buttons, icon));

        var split = message.IndexOf("\n\n", StringComparison.Ordinal);
        var headline = (split < 0 ? message : message[..split]).Trim();
        var body = split < 0 ? "" : message[(split + 2)..].Trim('\n');

        var choices = buttons switch
        {
            MessageBoxButton.OKCancel => new[]
            {
                ("OK", MessageBoxResult.OK),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxButton.YesNo => new[]
            {
                ("Yes", MessageBoxResult.Yes),
                ("No", MessageBoxResult.No),
            },
            MessageBoxButton.YesNoCancel => new[]
            {
                ("Yes", MessageBoxResult.Yes),
                ("No", MessageBoxResult.No),
                ("Cancel", MessageBoxResult.Cancel),
            },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };

        return RunChoiceDialog(title, headline, body, icon == MessageBoxImage.Error, choices);
    }

    private MessageBoxResult RunChoiceDialog(string title, string headline, string body,
        params (string Label, MessageBoxResult Result)[] choices)
        => RunChoiceDialog(title, headline, body, danger: false, choices);

    private MessageBoxResult RunChoiceDialog(string title, string headline, string body,
        bool danger, params (string Label, MessageBoxResult Result)[] choices)
    {
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = headline,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = danger ? ThemeBrush("Danger", System.Windows.Media.Brushes.Firebrick)
                                : ThemeBrush("TextPrimary", System.Windows.Media.Brushes.Black),
            Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            // Palette lookup rather than a literal grey, so the body text follows
            // the theme instead of staying light-mode grey.
            Foreground = ThemeBrush("TextMuted"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var win = new Window
        {
            Title = title,
            Content = root,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 520, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        DialogChrome.Apply(win);

        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var picked = (MessageBoxResult?)null;
        for (var i = 0; i < choices.Length; i++)
        {
            var (label, result) = choices[i];
            var last = i == choices.Length - 1;
            var button = new System.Windows.Controls.Button
            {
                Content = label,
                MinWidth = 110,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 18, last ? 0 : 8, 0),
                IsDefault = i == 0,
                IsCancel = last,
            };
            if (!last) button.Click += (_, _) => { picked = result; win.DialogResult = true; };
            row.Children.Add(button);
        }
        root.Children.Add(row);

        win.ShowDialog();
        return picked ?? choices[^1].Result;
    }

    /// <summary>
    /// Show a .material as what it describes rather than as a hex dump: the
    /// shader it drives, the textures bound to each sampler, and the parameter
    /// names. A material that won't parse falls back to the hex view.
    /// </summary>
    private void ShowMaterialPreview(MaterialVm vm)
    {
        // The toggle belongs to whichever resource is on screen.
        if (!string.Equals(_structuredRawFor, vm.Identifier, StringComparison.Ordinal))
        {
            _structuredShowRaw = false;
            _structuredRawFor = vm.Identifier;
        }

        var material = vm.Parsed;
        if (material is null)
        {
            ShowUnparsedStructured(vm, "Material", vm.ParseError);
            Log($"  material {vm.FileName} didn't parse ({vm.ParseError}) — showing raw bytes.");
            return;
        }

        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        StructuredPanel.Visibility = Visibility.Visible;

        ClearMeta();
        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = "Material";
        MetaResolution.Text = $"{material.Samplers.Count} sampler(s), {material.UniformNames.Count} uniform(s)";
        MetaMips.Text       = material.Header.ParameterCount.ToString();
        MetaArraySize.Text  = material.Lod1IsExactDuplicate ? "LOD1 duplicates LOD0" : "LOD1 differs";
        MetaMipFile.Text    = vm.ShaderName;
        MetaIdentifier.Text = vm.Identifier;

        BuildMaterialPanel(vm, material);
    }

    private void BuildMaterialPanel(MaterialVm vm, SimplifiedStudio.Core.Material.MaterialFile material)
    {
        StructuredContent.Children.Clear();

        StructuredContent.Children.Add(
            BuildPanelHeader(vm.FileName, () => BuildMaterialPanel(vm, material)));

        if (_structuredShowRaw)
        {
            StructuredContent.Children.Add(BuildRawBytesView(vm));
            return;
        }

        StructuredContent.Children.Add(SectionHeading("Shader"));
        StructuredContent.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = vm.ShaderName,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        });
        StructuredContent.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = material.ShaderEaid,
            Foreground = ThemeBrush("TextMuted"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // The samplers are the point of the whole view.
        StructuredContent.Children.Add(SectionHeading(
            material.Samplers.Count == 1 ? "1 texture" : $"{material.Samplers.Count} textures"));
        if (material.Samplers.Count == 0)
        {
            StructuredContent.Children.Add(Muted("This material binds no textures."));
        }
        else
        {
            foreach (var sampler in material.Samplers)
                StructuredContent.Children.Add(BuildBindingRow(sampler.Name, sampler.TextureEaid));
        }

        if (material.UniformNames.Count > 0)
        {
            StructuredContent.Children.Add(SectionHeading($"{material.UniformNames.Count} parameters", top: 16));
            StructuredContent.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = string.Join("   ", material.UniformNames),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextMuted"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono"),
                FontSize = 11,
            });
        }

        // The float-defaults pool is not shown. It cannot be paired with the
        // parameter names — 9 uniforms against 50 values in one sample — and the
        // format spec does not yet say how it maps, so it could only ever be a
        // wall of unattributed numbers. FloatDefaults stays on the parse for
        // whenever that is decoded.

        StructuredContent.Children.Add(new System.Windows.Controls.Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            BorderBrush = ThemeBrush("BorderSoft"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Muted("Read-only. The per-parameter records aren't decoded, so Export and "
                        + "Import round-trip the original bytes rather than rebuilding the file."),
        });
    }

    /// <summary>
    /// Show a .context as the table of material slots it is: "body" bound to the
    /// team's paint material, "wheel" to its wheel material, and so on. Each
    /// material links through to its own row, which in turn links on to the
    /// textures it binds.
    /// </summary>
    private void ShowContextPreview(ContextVm vm)
    {
        // The toggle belongs to whichever resource is on screen.
        if (!string.Equals(_structuredRawFor, vm.Identifier, StringComparison.Ordinal))
        {
            _structuredShowRaw = false;
            _structuredRawFor = vm.Identifier;
        }

        var context = vm.Parsed;
        if (context is null)
        {
            ShowUnparsedStructured(vm, "Context", vm.ParseError);
            Log($"  context {vm.FileName} didn't parse ({vm.ParseError}) — showing raw bytes.");
            return;
        }

        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        StructuredPanel.Visibility = Visibility.Visible;

        ClearMeta();
        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = "Context";
        MetaResolution.Text = context.Slots.Count == 1 ? "1 slot" : $"{context.Slots.Count} slots";
        MetaIdentifier.Text = vm.Identifier;

        BuildContextPanel(vm, context);
    }

    private void BuildContextPanel(ContextVm vm, SimplifiedStudio.Core.Context.ContextFile context)
    {
        StructuredContent.Children.Clear();
        StructuredContent.Children.Add(BuildPanelHeader(vm.FileName, () => BuildContextPanel(vm, context)));

        if (_structuredShowRaw)
        {
            StructuredContent.Children.Add(BuildRawBytesView(vm));
            return;
        }

        // Say when slots share, rather than leaving a repeated material looking
        // like a mistake in the view.
        var heading = context.Slots.Count == 1 ? "1 material slot" : $"{context.Slots.Count} material slots";
        if (context.DistinctMaterialCount < context.Slots.Count)
            heading += $"  ·  {context.DistinctMaterialCount} distinct materials";
        StructuredContent.Children.Add(SectionHeading(heading));

        foreach (var slot in context.Slots)
            StructuredContent.Children.Add(BuildBindingRow(slot.Name, slot.MaterialEaid));

        StructuredContent.Children.Add(new System.Windows.Controls.Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            BorderBrush = ThemeBrush("BorderSoft"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Muted("Read-only. A context's slot table is understood well enough to read, "
                        + "not to rebuild, so Export and Import round-trip the original bytes."),
        });
    }

    /// <summary>
    /// A material or context the reader refused. Shown as the structured panel
    /// with the reason at the top and the bytes below, rather than a bare hex
    /// dump: the difference between "this file is unusual" and "this app cannot
    /// read this kind of file" matters, and a dump alone doesn't say which.
    /// </summary>
    private void ShowUnparsedStructured(ResourceListItem vm, string kindLabel, string? reason)
    {
        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        StructuredPanel.Visibility = Visibility.Visible;

        long bytes = -1;
        try { bytes = (long)vm.Resource.Fragments[0].Size; } catch { /* leave -1 */ }

        ClearMeta();
        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = $"{kindLabel} (unrecognised layout)";
        MetaResolution.Text = bytes >= 0 ? $"{bytes:N0} bytes" : "";
        MetaIdentifier.Text = vm.Identifier;

        StructuredContent.Children.Clear();
        StructuredContent.Children.Add(
            BuildPanelHeader(vm.FileName, () => ShowUnparsedStructured(vm, kindLabel, reason)));

        var notice = new System.Windows.Controls.Border
        {
            Background = ThemeBrush("SurfaceSunken"),
            BorderBrush = ThemeBrush("BorderSoft"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"This {kindLabel.ToLowerInvariant()} doesn't match the layout this app knows.",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        stack.Children.Add(Muted(string.IsNullOrWhiteSpace(reason)
            ? "The bytes are shown below."
            : $"{reason}\n\nThe bytes are shown below. Export writes the file unchanged, so nothing is lost."));
        notice.Child = stack;
        StructuredContent.Children.Add(notice);
        StructuredContent.Children.Add(BuildRawBytesView(vm));
    }

    /// <summary>
    /// One binding — a material's sampler to its texture, or a context's slot to
    /// its material — as a link when the target is in this archive.
    /// </summary>
    private UIElement BuildBindingRow(string bindingName, string targetEaid)
    {
        var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new System.Windows.Controls.TextBlock
        {
            Text = bindingName,
            Foreground = ThemeBrush("TextMuted"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = bindingName,
        };
        System.Windows.Controls.Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var target = FindResourceRow(targetEaid);
        // The whole identifier, not just the file name: two teams' materials
        // share names constantly, and the path is what says which one this is.
        var label = targetEaid;

        System.Windows.Controls.TextBlock value;
        if (target is not null)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(label))
            {
                Foreground = ThemeBrush("Accent"),
                TextDecorations = null,
                ToolTip = "Go to this resource",
            };
            link.Click += (_, _) => SelectResource(target);
            // Wrapping rather than trimming: an ellipsis eats the tail of the
            // path, which is the part that identifies it.
            value = new System.Windows.Controls.TextBlock(link) { TextWrapping = TextWrapping.Wrap };
        }
        else
        {
            // Not in this archive: a binding routinely points somewhere else,
            // so this is ordinary rather than an error.
            value = new System.Windows.Controls.TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
                ToolTip = "Not in this ERP.",
            };
        }
        // Top-aligned now that a long path can wrap onto a second line.
        value.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Controls.Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    /// <summary>
    /// The title row every structured preview carries: what the resource is, and
    /// the switch to the bytes underneath. Rebuilding is the caller's business,
    /// since only it knows what it is showing.
    /// </summary>
    private UIElement BuildPanelHeader(string title, Action rebuild)
    {
        var header = new System.Windows.Controls.DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var rawToggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "Raw bytes",
            Style = FindResource("FilterChip") as Style,
            Margin = new Thickness(0),
            IsChecked = _structuredShowRaw,
            ToolTip = "Show the file as hex instead of the parsed view",
        };
        rawToggle.Checked += (_, _) => { _structuredShowRaw = true; rebuild(); };
        rawToggle.Unchecked += (_, _) => { _structuredShowRaw = false; rebuild(); };
        System.Windows.Controls.DockPanel.SetDock(rawToggle, System.Windows.Controls.Dock.Right);
        header.Children.Add(rawToggle);

        var label = new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        System.Windows.Controls.DockPanel.SetDock(label, System.Windows.Controls.Dock.Left);
        header.Children.Add(label);

        return header;
    }

    private UIElement BuildRawBytesView(ResourceListItem vm)
    {
        string dump;
        try
        {
            long bytes = -1;
            try { bytes = (long)vm.Resource.Fragments[0].Size; } catch { /* leave -1 */ }
            using var stream = vm.Resource.Fragments[0].GetDataStream(true);
            var buffer = new byte[Math.Min(HexPreviewByteCap, bytes < 0 ? HexPreviewByteCap : (int)bytes)];
            var read = ReadFully(stream, buffer);
            dump = BuildHexDump(buffer.AsSpan(0, read));
            if (bytes > read)
                dump += $"\n... truncated — showing first {read:N0} of {bytes:N0} bytes.";
        }
        catch (Exception ex)
        {
            dump = $"(couldn't read bytes: {ex.Message})";
        }

        return new System.Windows.Controls.TextBlock
        {
            Text = dump,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono"),
            FontSize = 12,
            Foreground = ThemeBrush("TextMuted"),
        };
    }

    /// <summary>
    /// "offset: 16 hex bytes  |ASCII|" classic hex-dump formatter. One line
    /// per 16 bytes, with a half-line gap after byte 8 to match the layout
    /// hex editors use.
    /// </summary>
    private static string BuildHexDump(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 4);
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var run = bytes[i..Math.Min(i + 16, bytes.Length)];
            sb.Append($"0x{i:X6}  ");
            for (var j = 0; j < 16; j++)
            {
                if (j < run.Length) sb.Append($"{run[j]:X2} ");
                else                sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (var j = 0; j < run.Length; j++)
                sb.Append(run[j] is >= 0x20 and < 0x7F ? (char)run[j] : '.');
            sb.Append('|');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private System.Windows.Controls.TextBlock SectionHeading(string text, double top = 0) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = ThemeBrush("TextMuted"),
        FontSize = 11,
        Margin = new Thickness(0, top, 0, 6),
    };

    private System.Windows.Controls.TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = ThemeBrush("TextMuted"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
    };

    private static int ReadFully(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            var n = s.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>The row for an eaid, if this archive holds it.</summary>
    private ResourceListItem? FindResourceRow(string eaid)
    {
        if (string.IsNullOrEmpty(eaid)) return null;
        return _textures.FirstOrDefault(t =>
            string.Equals(t.Identifier, eaid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Select a row and bring it into view — what a sampler link does.</summary>
    private void SelectResource(ResourceListItem item)
    {
        // The filter or a type chip may be hiding it, which would make the click
        // do nothing at all. Clear both rather than leave the user guessing.
        if (!TextureList.Items.Contains(item))
        {
            FilterBox.Text = "";
            _kindFilter.Clear();
            RebuildKindChips();
            _texturesView?.Refresh();
            UpdateCountLabel();
        }

        TextureList.SelectedItem = item;
        TextureList.ScrollIntoView(item);
        TextureList.Focus();
    }

    /// <summary>
    /// Put text in the preview pane, coloured when it's a language we scan and
    /// short enough to be worth it.
    /// </summary>
    private void SetPreviewText(string text, PreviewLanguage language = PreviewLanguage.None)
    {
        var paragraph = new System.Windows.Documents.Paragraph
        {
            Margin = new Thickness(0),
        };

        var spans = language != PreviewLanguage.None && text.Length <= HighlightCharCap
            ? language == PreviewLanguage.Json
                ? Util.JsonHighlighter.Scan(text)
                : Util.XmlHighlighter.Scan(text)
            : null;

        if (spans is not null && spans.Count <= HighlightSpanCap)
        {
            var runs = new System.Windows.Documents.Run[spans.Count];
            for (var i = 0; i < spans.Count; i++)
            {
                var span = spans[i];
                runs[i] = new System.Windows.Documents.Run(text.Substring(span.Start, span.Length))
                {
                    Foreground = SyntaxBrush(span.Kind),
                    // Italic as well as coloured: it separates a comment from a
                    // number by more than hue, which matters where the two
                    // greens sit close together.
                    FontStyle = span.Kind == Util.SyntaxKind.Comment
                        ? FontStyles.Italic
                        : FontStyles.Normal,
                };
            }
            // AddRange rather than Add in a loop: each Add reflows the document,
            // which turns a linear build into a quadratic one.
            paragraph.Inlines.AddRange(runs);
        }
        else
        {
            paragraph.Inlines.Add(new System.Windows.Documents.Run(text));
        }

        var document = new System.Windows.Documents.FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            // Wide enough that lines don't wrap — JSON and XML are read by their
            // indentation, and wrapped lines destroy it. The horizontal scrollbar
            // takes over instead.
            PageWidth = 4000,
        };
        PreviewText.Document = document;
        PreviewText.ScrollToHome();
    }

    private void ApplyPreviewChannel()
    {
        PreviewImage.Source = _previewOriginal is null || _previewChannel == PreviewChannel.Rgb
            ? _previewOriginal
            : IsolateChannel(_previewOriginal, _previewChannel);

        var hasImage = _previewOriginal is not null;
        ChannelBar.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        // The judging backdrop only applies behind an actual image. With nothing
        // previewed the panel goes back to the themed surface, so the placeholder
        // text isn't left as grey-on-white or grey-on-near-black.
        if (hasImage)
        {
            _previewBackdropBrush.Color = BackdropColour(_previewBackdropMix);
            PreviewSurface.Background = _previewBackdropBrush;
        }
        else
        {
            PreviewSurface.Background = ThemeBrush("SurfaceSunken");
        }
    }

    /// <summary>
    /// Flatten one channel to greyscale — the value goes to R, G and B alike, and
    /// alpha is forced opaque so an isolated channel is never itself invisible.
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource IsolateChannel(
        System.Windows.Media.Imaging.BitmapSource source, PreviewChannel channel)
    {
        // Normalise first: textures arrive in whatever format the decoder chose,
        // and reading channels means knowing the byte order.
        var bgra = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(
                source, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * (long)height];
        bgra.CopyPixels(pixels, stride, 0);

        var offset = channel switch
        {
            PreviewChannel.Blue  => 0,
            PreviewChannel.Green => 1,
            PreviewChannel.Red   => 2,
            _                    => 3,   // Alpha
        };

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var value = pixels[i + offset];
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        var result = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, bgra.DpiX, bgra.DpiY,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Mix the two backdrop ends. Straight sRGB interpolation: both ends are
    /// neutral greys, so there is no hue to shift and no third colour to drift
    /// through on the way.
    /// </summary>
    private System.Windows.Media.Color BackdropColour(double mix)
    {
        var light = EndColour("PreviewBackdropLight", System.Windows.Media.Colors.White);
        var dark = EndColour("PreviewBackdropDark", System.Windows.Media.Colors.Black);
        mix = Math.Clamp(mix, 0, 1);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(light.R + (dark.R - light.R) * mix),
            (byte)Math.Round(light.G + (dark.G - light.G) * mix),
            (byte)Math.Round(light.B + (dark.B - light.B) * mix));
    }

    /// <summary>
    /// Put an image on the preview, keeping the undecorated original so the
    /// channel buttons have something to work from. Every path that shows or
    /// clears the image goes through here.
    /// </summary>
    private void SetPreviewImage(System.Windows.Media.Imaging.BitmapSource? bmp)
    {
        _previewOriginal = bmp;
        ApplyPreviewChannel();
    }

    private void UpdateSliceBar(TextureVm? vm)
    {
        if (vm is null || vm.ArraySize <= 1)
        {
            SliceBar.Visibility = Visibility.Collapsed;
            return;
        }
        if (_sliceIndex >= vm.ArraySize) _sliceIndex = 0;
        SliceBar.Visibility = Visibility.Visible;
        SliceLabel.Text = $"Slice {_sliceIndex + 1} / {vm.ArraySize}";
    }

    private void ClearMeta()
    {
        MetaName.Text = "";
        MetaFormat.Text = "";
        MetaResolution.Text = "";
        MetaMips.Text = "";
        MetaArraySize.Text = "";
        MetaMipFile.Text = "";
        MetaIdentifier.Text = "";
    }

    /// <summary>
    /// Rebuild the resource-type chips from the kinds actually loaded, so a
    /// project without materials never shows a MAT chip that filters to nothing.
    /// A single kind gets no chips at all — there would be nothing to narrow.
    /// </summary>
    private void RebuildKindChips()
    {
        KindChips.Children.Clear();

        var kinds = _textures.Select(t => t.Kind).Distinct().OrderBy(k => (int)k).ToList();

        // Drop selections for kinds that are no longer present, or the list would
        // filter against a chip the user can no longer see or clear.
        _kindFilter.RemoveWhere(k => !kinds.Contains(k));

        if (kinds.Count < 2)
        {
            KindChips.Visibility = Visibility.Collapsed;
            return;
        }
        KindChips.Visibility = Visibility.Visible;

        foreach (var kind in kinds)
        {
            var chip = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = ResourceListItem.TagFor(kind),
                Tag = kind,
                Style = (Style)FindResource("FilterChip"),
                IsChecked = _kindFilter.Contains(kind),
                ToolTip = $"Show only {ResourceListItem.PluralFor(kind)}",
            };
            chip.Checked += KindChip_Changed;
            chip.Unchecked += KindChip_Changed;
            KindChips.Children.Add(chip);
        }
    }

    /// <summary>
    /// Point the backdrop slider at the end opposite the app's own theme, which
    /// is the useful default: artwork is judged against its opposite. Only the
    /// starting position — the slider is the user's from then on.
    /// </summary>
    private void ApplyThemeToBackdrop()
    {
        if (BackdropSlider is null) return;
        BackdropSlider.Value = ThemeManager.IsDark ? 1 : 0;
    }

    private void Backdrop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent, before the preview elements exist.
        if (PreviewSurface is null) return;
        _previewBackdropMix = e.NewValue;
        ApplyPreviewChannel();
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your game install folder" };
        if (dlg.ShowDialog() == true) GameDirBox.Text = dlg.FolderName;
        // Auto-save fires via TextChanged.
    }

    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the preview elements exist.
        if (PreviewImage is null || ChannelBar is null) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<PreviewChannel>(tag, out var channel)) return;

        // Deliberately sticky across selections: comparing the same channel over
        // several textures is the reason to have it.
        _previewChannel = channel;
        ApplyPreviewChannel();
    }

    private void ClearDirty()
    {
        _isDirty = false;
        // Saved — nothing is outstanding any more, so the row dots go with it.
        _modifiedIds.Clear();
        foreach (var item in _textures) item.IsModified = false;
        UpdateDirtyLabel();
    }

    /// <summary>
    /// Ask what to do about unsaved work. <paramref name="action"/> names what
    /// Studio goes on to do once the question is settled — "open another
    /// project", "close".
    /// </summary>
    private MessageBoxResult ConfirmUnsavedChanges(string headline, string action)
        => RunChoiceDialog(
            "Unsaved changes",
            headline,
            "Save writes the changes to the project first. Discard throws them away. "
          + $"Either way Studio will then {action}.",
            ("Save", MessageBoxResult.Yes),
            ("Discard", MessageBoxResult.No),
            ("Cancel", MessageBoxResult.Cancel));

    /// <summary>
    /// Export every slice of the currently-selected texture array as
    /// <c>&lt;name&gt;.sliceN.png</c> into a chosen folder. Visible only when
    /// the selected texture's ArraySize is > 1.
    /// </summary>
    private async void ExportAllSlices_Click(object sender, RoutedEventArgs e)
    {
        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null || vm.ArraySize <= 1) return;

        if (!TexconvRunner.IsSupportedForPngConversion(vm.ImageType))
        {
            ShowMessage(
                $"This texture is {vm.ImageType}, which can't be converted to PNG in this version (BC6H/HDR).",
                "Unsupported format", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = $"Folder to export {vm.ArraySize} slices of {vm.FileName}"
        };
        if (dlg.ShowDialog() != true) return;
        var outDir = dlg.FolderName;
        var gd = GameDir;
        var safeBase = vm.FileName.Replace("?", "%3F");
        var sliceCount = vm.ArraySize;

        ExportAllSlicesButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        try
        {
            int ok = 0, fail = 0;
            await Task.Run(() =>
            {
                for (uint i = 0; i < sliceCount; i++)
                {
                    var idx = i; // capture for the dispatcher closures below
                    try
                    {
                        var pngPath = Path.Combine(outDir, $"{safeBase}.slice{idx}.png");
                        var r = TextureIO.ExportPng(vm, pngPath, _runner, gd, idx, _pendingMips);
                        ok++;
                        Dispatcher.BeginInvoke(() =>
                            Log($"  ✓ slice {idx + 1}/{sliceCount}  {r.Width}×{r.Height}"));
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Dispatcher.BeginInvoke(() => Log($"  ✗ slice {idx}: {ex.Message}"));
                    }
                }
            });
            Log($"Exported {ok} of {sliceCount} slice(s) of {vm.FileName} → {outDir}" +
                (fail > 0 ? $", {fail} failed" : ""));
        }
        finally
        {
            ExportAllSlicesButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
        }
    }

    private void ExportPkg(PkgVm vm)
    {
        if (vm.Parsed is null) { ShowMessage( "Nothing parsed to export.", "Export PKG", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dlg = new SaveFileDialog
        {
            Title = $"Export {vm.FileName} as JSON",
            Filter = "JSON|*.json",
            FileName = ResourceFileIO.FileNameFor(vm),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ResourceFileIO.Export(vm, dlg.FileName);
            Log($"Exported PKG → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log($"!! Export PKG failed: {ex.Message}");
            ShowMessage( ex.Message, "Export PKG failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Save the first-fragment bytes of <paramref name="vm"/> to disk, using
    /// the resource's existing extension on the suggested file name.
    /// </summary>
    private void ExportRaw(ResourceListItem vm, string ext)
    {
        var label = KindLabel(ext);
        if (vm.Resource.Fragments.Count == 0)
        {
            ShowMessage( "Resource has no fragments.", $"Export {label}",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = $"Export {vm.FileName}",
            Filter = $"{label} files|*.{ext}|All files|*.*",
            // FileName already carries the .material / .context extension in
            // the observed corpus — FileNameFor only appends when missing.
            FileName = ResourceFileIO.FileNameFor(vm),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ResourceFileIO.Export(vm, dlg.FileName);
            Log($"Exported {label} → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log($"!! Export {label} failed: {ex.Message}");
            ShowMessage( ex.Message, $"Export {label} failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Export a binary-XML resource as text. Handles VTF too — it's XML on
    /// disk either way, only the label the user sees differs.
    /// </summary>
    private void ExportXml(XmlVm vm)
    {
        var label = vm is VtfVm ? "VTF" : "XML";
        if (vm.Parsed is null) { ShowMessage( "Nothing parsed to export.", $"Export {label}", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dlg = new SaveFileDialog
        {
            Title = $"Export {vm.FileName} as XML",
            Filter = "XML|*.xml|All files|*.*",
            FileName = ResourceFileIO.FileNameFor(vm),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ResourceFileIO.Export(vm, dlg.FileName);
            Log($"Exported {label} → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log($"!! Export {label} failed: {ex.Message}");
            ShowMessage( ex.Message, $"Export {label} failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _texturesView?.Refresh();
        UpdateCountLabel();
    }

    private void GameDirBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Persist the new value automatically — no Save button.
        try
        {
            var cfg = UserConfig.Load();
            cfg.GameDir = GameDir;
            cfg.Save();
        }
        catch
        {
            // Don't pop a dialog on every keystroke; a transient write failure is harmless
            // because we re-save on the next change.
        }

        // Re-render the current preview so it picks up / drops the external mips.
        if (IsLoaded) ShowPreview(TextureList.SelectedItem as TextureVm);
    }

    private static bool HasNameSegment(string fileName, string segment)
    {
        var parts = fileName.Split(NameSegmentDelimiters);
        for (var i = 1; i < parts.Length; i++)   // skip the base name (index 0)
            if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a file this row can be replaced
    /// from, judged on the extension alone.
    /// </summary>
    private static bool ImportAccepts(ResourceListItem vm, string path) =>
        ImportExtensionsFor(vm).Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a resource can be imported from. Textures take either image
    /// format and the file itself says which, so a DDS in hand works whatever
    /// Settings is set to; every other kind takes exactly one.
    /// </summary>
    private static string[] ImportExtensionsFor(ResourceListItem vm) => vm switch
    {
        TextureVm  => new[] { ".dds", ".png" },
        PkgVm      => new[] { ".json" },
        // VtfVm derives from XmlVm and is XML on disk either way.
        XmlVm      => new[] { ".xml" },
        MaterialVm => new[] { ".material" },
        ContextVm  => new[] { ".context" },
        _          => Array.Empty<string>(),
    };

    /// <summary>
    /// An Open dialog filter covering every kind in <paramref name="selected"/>,
    /// so a mixed selection can still be given a file, with
    /// <paramref name="preferred"/> leading — the configured texture format
    /// first, in the common case.
    ///
    /// There is no "All files" entry on purpose. One file going into many rows
    /// only stays predictable while the extension says which rows it belongs
    /// to, and these are exactly the names a bulk export writes.
    /// </summary>
    private static string ImportFilterFor(IEnumerable<ResourceListItem> selected, string preferred)
    {
        var parts = selected
            .SelectMany(ImportExtensionsFor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(e => $"{ImportFilterLabel(e)}|*{e}")
            .ToList();

        return parts.Count == 0 ? "All files|*.*" : string.Join("|", parts);
    }

    /// <summary>Dialog label for an import extension: ".dds" reads "DDS image".</summary>
    private static string ImportFilterLabel(string ext) => ext switch
    {
        ".dds"      => "DDS image",
        ".png"      => "PNG image",
        ".json"     => "JSON",
        ".xml"      => "XML",
        ".material" => "Material",
        ".context"  => "Context",
        _           => ext.TrimStart('.').ToUpperInvariant(),
    };

    private void ImportPkg(PkgVm vm)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Import JSON into {vm.FileName}",
            Filter = "JSON|*.json|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TakeUndoSnapshot($"import PKG {vm.FileName}");
            ResourceFileIO.Import(vm, dlg.FileName);
            MarkDirty(vm);
            Log($"Imported PKG ← {dlg.FileName} → {vm.FileName}");
            // Re-render preview from the new bytes via a fresh PkgVm in place.
            RebuildTextureList();
            ReselectByIdentifier(vm.Identifier);
        }
        catch (Exception ex)
        {
            Log($"!! Import PKG failed: {ex.Message}");
            ShowMessage( ex.Message, "Import PKG failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Replace the first-fragment bytes of <paramref name="vm"/> with the
    /// contents of a user-chosen file. Snapshots undo state and marks the
    /// project dirty on success.
    /// </summary>
    private void ImportRaw(ResourceListItem vm, string ext)
    {
        var label = KindLabel(ext);
        var dlg = new OpenFileDialog
        {
            Title = $"Import bytes into {vm.FileName}",
            Filter = $"{label} files|*.{ext}|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TakeUndoSnapshot($"import {label} {vm.FileName}");
            ResourceFileIO.Import(vm, dlg.FileName);
            MarkDirty(vm);
            var bytes = new FileInfo(dlg.FileName).Length;
            Log($"Imported {label} ← {dlg.FileName} → {vm.FileName}  ({bytes:N0} bytes)");
            RebuildTextureList();
            ReselectByIdentifier(vm.Identifier);
        }
        catch (Exception ex)
        {
            Log($"!! Import {label} failed: {ex.Message}");
            ShowMessage( ex.Message, $"Import {label} failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportXml(XmlVm vm)
    {
        var label = vm is VtfVm ? "VTF" : "XML";
        var dlg = new OpenFileDialog
        {
            Title = $"Import XML into {vm.FileName}",
            Filter = "XML|*.xml|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TakeUndoSnapshot($"import {label} {vm.FileName}");
            ResourceFileIO.Import(vm, dlg.FileName);
            MarkDirty(vm);
            Log($"Imported {label} ← {dlg.FileName} → {vm.FileName}");
            RebuildTextureList();
            ReselectByIdentifier(vm.Identifier);
        }
        catch (Exception ex)
        {
            Log($"!! Import {label} failed: {ex.Message}");
            ShowMessage( ex.Message, $"Import {label} failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void KindChip_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: ResourceKind kind } chip) return;

        if (chip.IsChecked == true) _kindFilter.Add(kind);
        else _kindFilter.Remove(kind);

        _texturesView?.Refresh();
        UpdateCountLabel();
    }

    /// <summary>"TEX 95, MAT 44" — per-kind counts for a summary line.</summary>
    private static string KindSummary(IEnumerable<ResourceListItem> items) =>
        string.Join(", ", items
            .GroupBy(r => r.Kind)
            .OrderBy(g => Array.IndexOf(BulkKindOrder, g.Key))
            .Select(g => $"{ResourceListItem.TagFor(g.Key)} {g.Count()}"));

    private void Log(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(Log), DispatcherPriority.Background, line);
            return;
        }
        LogBox.AppendText(line);
        LogBox.AppendText("\r\n");
        LogBox.ScrollToEnd();
    }

    private void MarkDirty()
    {
        _isDirty = true;
        UpdateDirtyLabel();
    }

    private void NextSlice_Click(object sender, RoutedEventArgs e)
    {
        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null || vm.ArraySize <= 1) return;
        _sliceIndex = (_sliceIndex + 1) % vm.ArraySize;
        ShowPreview(vm);
    }

    private void PrevSlice_Click(object sender, RoutedEventArgs e)
    {
        var vm = TextureList.SelectedItem as TextureVm;
        if (vm is null || vm.ArraySize <= 1) return;
        if (_sliceIndex == 0) _sliceIndex = vm.ArraySize - 1;
        else _sliceIndex--;
        ShowPreview(vm);
    }

    /// <summary>
    /// Round off the preview's contents to match the panel around them.
    ///
    /// A Border paints its own rounded background but doesn't clip what's inside
    /// it, so an image scaled to fill would lay square corners over the rounded
    /// ones. The radius is one less than the panel's, and the rect is the inner
    /// area, so the clip sits inside the 1px stroke rather than on top of it.
    /// </summary>
    private void PreviewClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        host.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 5, 5);
    }

    /// <summary>
    /// Repaint the parts a palette swap cannot reach on its own.
    ///
    /// XAML and styles follow the palette, because they name their colours with
    /// DynamicResource. Anything painted from code holds the brush it was handed
    /// and keeps it — the preview surface, the structured material and context
    /// panels, the file label. Re-running the paths that build them is enough;
    /// dialogs need nothing, since each is built afresh when it opens.
    /// </summary>
    private void RefreshThemedUi()
    {
        ApplyThemeToBackdrop();
        ApplyPreviewChannel();

        var selected = SelectedResources();
        if (selected.Count > 1) ShowMultiSelectionPreview(selected);
        else ShowPreviewForItem(selected.Count == 1 ? selected[0] : null);

        UpdateDirtyLabel();
    }

    /// <summary>Re-select the row whose Identifier matches after a list rebuild.</summary>
    private void ReselectByIdentifier(string identifier)
    {
        var match = _textures.FirstOrDefault(r => r.Identifier == identifier);
        if (match is null) return;
        TextureList.SelectedItem = match;
        TextureList.ScrollIntoView(match);
    }

    /// <summary>
    /// Re-select every row named in <paramref name="identifiers"/> after a
    /// rebuild, which hands back fresh row objects that the old selection no
    /// longer points at. Rows that are gone are simply not selected.
    /// </summary>
    private void ReselectByIdentifiers(IReadOnlyCollection<string> identifiers)
    {
        if (identifiers.Count == 0) return;

        var wanted = new HashSet<string>(identifiers, StringComparer.Ordinal);
        var matches = _textures.Where(r => wanted.Contains(r.Identifier)).ToList();
        if (matches.Count == 0) return;

        TextureList.SelectedItems.Clear();
        foreach (var row in matches) TextureList.SelectedItems.Add(row);
        TextureList.ScrollIntoView(matches[0]);
    }

    /// <summary>Stamp <see cref="ResourceListItem.IsModified"/> across the list from
    /// <see cref="_modifiedIds"/>. Called after any rebuild, which hands back fresh
    /// rows that know nothing of what has been edited.</summary>
    private void RestoreModifiedFlags()
    {
        if (_modifiedIds.Count == 0) return;
        foreach (var item in _textures) item.IsModified = _modifiedIds.Contains(item.Identifier);
    }

    /// <summary>
    /// Show/hide the log row at the bottom of the window. Visibility +
    /// RowDefinition height are flipped together so the row also reclaims
    /// (or releases) its 140px slot.
    /// </summary>
    private void SetLogVisible(bool visible)
    {
        LogPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LogRow.Height = visible ? new GridLength(140) : GridLength.Auto;
    }

    private void ShowBinaryPlaceholderPreview(ResourceListItem vm, string kindLabel)
    {
        StructuredPanel.Visibility = Visibility.Collapsed;
        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        ClearMeta();

        long bytes = -1;
        try { bytes = (long)vm.Resource.Fragments[0].Size; } catch { /* leave -1 */ }

        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = $"{kindLabel} (raw binary)";
        MetaResolution.Text = bytes >= 0 ? $"{bytes:N0} bytes" : "";
        MetaIdentifier.Text = vm.Identifier;

        try
        {
            using var s = vm.Resource.Fragments[0].GetDataStream(true);
            var buf = new byte[Math.Min(HexPreviewByteCap, bytes < 0 ? HexPreviewByteCap : (int)bytes)];
            var read = ReadFully(s, buf);
            var dump = BuildHexDump(buf.AsSpan(0, read));
            if (bytes > read)
                dump += $"\n... truncated — showing first {read:N0} of {bytes:N0} bytes. Use Export to inspect the full file.";

            SetPreviewText(dump);
            PreviewMessage.Visibility = Visibility.Collapsed;
            PreviewText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewMessage.Visibility = Visibility.Visible;
            PreviewMessage.Text = $"Couldn't render hex preview:\n{ex.Message}";
        }
    }

    private void ShowPkgPreview(PkgVm vm)
    {
        StructuredPanel.Visibility = Visibility.Collapsed;
        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        ClearMeta();
        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = "PKG (JSON)";
        MetaIdentifier.Text = vm.Identifier;
        try
        {
            using var sw = new System.IO.StringWriter();
            vm.Parsed?.WriteJson(sw);
            SetPreviewText(sw.ToString(), PreviewLanguage.Json);
        }
        catch (Exception ex) { SetPreviewText($"(couldn't render PKG: {ex.Message})"); }
    }

    /// <summary>
    /// Dispatch the preview pane based on the selected row's kind. Textures
    /// keep the existing image+meta flow; PKG/XML show a scrollable text
    /// dump. The Import/Export buttons relabel + reroute via UpdateActionButtons.
    /// </summary>
    private void ShowPreviewForItem(ResourceListItem? item)
    {
        switch (item)
        {
            case TextureVm tex:  ShowPreview(tex); break;
            case PkgVm pkg:      ShowPkgPreview(pkg); break;
            case XmlVm xml:      ShowXmlPreview(xml); break;
            case MaterialVm mat: ShowMaterialPreview(mat); break;
            case ContextVm ctx:  ShowContextPreview(ctx); break;
            default:             ShowPreview(null); break;
        }
        UpdateActionButtons(item);
    }

    private void ShowXmlPreview(XmlVm vm)
    {
        StructuredPanel.Visibility = Visibility.Collapsed;
        SetPreviewImage(null);
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        ExportAllSlicesButton.Visibility = Visibility.Collapsed;
        UpdateSliceBar(null);
        ClearMeta();
        MetaName.Text       = vm.FileName;
        MetaFormat.Text     = vm is VtfVm ? "VTF (XML)" : "XML";
        MetaIdentifier.Text = vm.Identifier;
        try
        {
            using var sw = new System.IO.StringWriter();
            vm.Parsed?.WriteXml(sw);
            SetPreviewText(sw.ToString(), PreviewLanguage.Xml);
        }
        catch (Exception ex) { SetPreviewText($"(couldn't render XML: {ex.Message})"); }
    }

    private System.Windows.Media.Brush SyntaxBrush(Util.SyntaxKind kind)
    {
        var key = kind switch
        {
            Util.SyntaxKind.Key     => "SyntaxKey",
            Util.SyntaxKind.String  => "SyntaxString",
            Util.SyntaxKind.Number  => "SyntaxNumber",
            Util.SyntaxKind.Keyword => "SyntaxKeyword",
            Util.SyntaxKind.Tag     => "SyntaxTag",
            Util.SyntaxKind.Comment => "SyntaxComment",
            Util.SyntaxKind.Text    => "TextPrimary",
            _                       => "TextMuted",
        };
        return ThemeBrush(key, System.Windows.Media.Brushes.Black);
    }

    private bool TextureFilter(object item)
    {
        if (item is not ResourceListItem vm) return false;
        if (_kindFilter.Count > 0 && !_kindFilter.Contains(vm.Kind)) return false;
        var q = FilterBox.Text;
        return string.IsNullOrWhiteSpace(q) ||
               vm.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               vm.Identifier.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               vm.KindTag.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Palette lookup for anything painted outside XAML — the dialogs built in
    /// code, and the group headers built from element factories.
    ///
    /// Goes through Application.Current rather than this window so static
    /// callers can use it too; every palette key lives in application resources,
    /// which is where a code-built dialog can see them anyway.
    /// </summary>
    private static System.Windows.Media.Brush ThemeBrush(
        string key, System.Windows.Media.Brush? fallback = null)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
           ?? fallback ?? System.Windows.Media.Brushes.Gray;

    private static bool TryParsePkg(EgoEngineLibrary.Archive.Erp.ErpFragment frag, out EgoEngineLibrary.Data.Pkg.PkgFile? pkg)
    {
        try { using var s = frag.GetDataStream(true); pkg = EgoEngineLibrary.Data.Pkg.PkgFile.ReadPkg(s); return true; }
        catch { pkg = null; return false; }
    }

    private static bool TryParseXml(EgoEngineLibrary.Archive.Erp.ErpFragment frag, out EgoEngineLibrary.Xml.XmlFile? xml)
    {
        try { using var s = frag.GetDataStream(true); xml = new EgoEngineLibrary.Xml.XmlFile(s); return true; }
        catch { xml = null; return false; }
    }

    /// <summary>
    /// The bar at the top is ours, so this is too. Everything native that a
    /// frameless window still needs — rounded corners, a maximise that respects
    /// the taskbar, and a glyph that follows the state rather than the click —
    /// is in SimplifiedUi.FramelessWindow, which each app attaches on the way up.
    /// </summary>
    private void WindowMaximize_Click(object sender, RoutedEventArgs e) =>
        FramelessWindow.ToggleMaximize(this);
}
