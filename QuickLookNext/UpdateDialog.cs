// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v3.35.0: the update prompt. Clicking a "new version" notification (or running
/// a manual update check) used to start the download straight away; the user now
/// chooses between updating now and skipping this version.
/// <para>
/// v3.40.0: the prompt is no longer a stock window. It is a borderless panel that
/// reuses the tray menu's material - the same WCA acrylic, tint, border, corner
/// radius and palette - so the update asks the same way the rest of the app looks.
/// The layout is a header row (icon, title, close), the two versions, one line of
/// explanation and a footer with the release notes link plus the two answers.
/// </para>
/// </summary>
internal sealed class UpdateDialog : Window
{
    private readonly bool _isDark;
    private bool _accentApplied;
    private bool _updateNow;

    private UpdateDialog(string version, string releaseNotesUrl)
    {
        _isDark = TrayIconManager.IsDarkTheme();

        Title = TranslationHelper.Get("Update_Title", failsafe: "软件更新");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        Background = ThemePalette.Tint(_isDark);

        Content = BuildContent(version, releaseNotesUrl);

        // Borderless window: the panel itself is the title bar.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // The button was released before the drag started.
                }
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                _updateNow = true;
                Close();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Re-apply after the first paint, like the tray menu and the plugin manager
        // do - some windows only accept the attribute once they are visible.
        ApplyBackdrop();
    }

    /// <summary>Must be called on the UI thread. Returns true to update now.</summary>
    internal static bool Ask(string version, string releaseNotesUrl = null)
    {
        var dialog = new UpdateDialog(version, releaseNotesUrl);
        dialog.RunSmokeTestHook();
        dialog.ShowDialog();
        return dialog._updateNow;
    }

    /// <summary>
    /// v3.40.0 test hook: when the smoke test set up its directory, close the prompt
    /// by itself (answering "ignore") and record what it looked like, so test.ps1 can
    /// assert the material and the layout without clicking.
    /// </summary>
    private void RunSmokeTestHook()
    {
        var smokeDir = App.SmokeDir;
        if (string.IsNullOrEmpty(smokeDir))
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            try
            {
                System.IO.Directory.CreateDirectory(smokeDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(smokeDir, "update-dialog.txt"),
                    $"title={Title}\nbackdrop={DiagnoseBackdrop()}\n" +
                    $"size={ActualWidth:0}x{ActualHeight:0}\nbuttons={DiagnoseButtons()}\n");
            }
            catch
            {
                // Diagnostics must never affect the prompt.
            }

            Close();
        };

        timer.Start();
    }

    internal string DiagnoseBackdrop() => $"accent-applied={_accentApplied}";

    private string _buttonLabels = string.Empty;

    internal string DiagnoseButtons() => _buttonLabels;

    private FrameworkElement BuildContent(string version, string releaseNotesUrl)
    {
        var accent = ThemePalette.Accent(_isDark);

        var title = new TextBlock
        {
            Text = TranslationHelper.Get("Update_Title", failsafe: "软件更新"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = CreateButton("\uE711", primary: false, subtle: true);
        close.FontFamily = new FontFamily("Segoe MDL2 Assets");
        close.FontSize = 12;
        close.Width = 30;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => Close();

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var icon = new TextBlock
        {
            Text = "\uE896",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        HeaderRow(header, icon, title);

        var headline = new TextBlock
        {
            Text = string.Format(
                TranslationHelper.Get("Update_FoundInline", failsafe: "发现新版本 {0}"), version),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var current = new TextBlock
        {
            Text = string.Format(
                TranslationHelper.Get("Update_CurrentVersion", failsafe: "当前版本 {0}"),
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"),
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 12),
        };

        var body = new TextBlock
        {
            Text = TranslationHelper.Get("Update_Ask",
                failsafe: "现在更新，或忽略这个版本（下次手动检查更新时仍会提示）。"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemePalette.SecondaryText(_isDark),
            LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 18),
        };

        var update = CreateButton(TranslationHelper.Get("Update_Now", failsafe: "立即更新"), primary: true, subtle: false);
        update.IsDefault = true;
        update.Click += (_, _) =>
        {
            _updateNow = true;
            Close();
        };

        var ignore = CreateButton(TranslationHelper.Get("Update_Ignore", failsafe: "忽略更新"), primary: false, subtle: false);
        ignore.IsCancel = true;
        ignore.Margin = new Thickness(8, 0, 0, 0);
        ignore.Click += (_, _) => Close();

        _buttonLabels = $"update={update.Content};ignore={ignore.Content}";

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(ignore);
        actions.Children.Add(update);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());

        if (!string.IsNullOrEmpty(releaseNotesUrl))
        {
            var notes = CreateLink(TranslationHelper.Get("Update_Notes", failsafe: "查看更新内容"));
            notes.Click += (_, _) => OpenUrl(releaseNotesUrl);
            Grid.SetColumn(notes, 0);
            footer.Children.Add(notes);
        }

        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(headline);
        content.Children.Add(current);
        content.Children.Add(body);
        content.Children.Add(footer);

        return new Border
        {
            Background = ThemePalette.Tint(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 18),
            Child = content,
        };
    }

    private static void HeaderRow(DockPanel header, UIElement icon, UIElement title)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);
        DockPanel.SetDock(titleRow, Dock.Left);
        header.Children.Add(titleRow);
    }

    private void ApplyBackdrop()
    {
        // Same pipeline as the tray menu: no DWM backdrop, WCA acrylic instead, so
        // the blur follows the rounded panel and the window has no native frame.
        WindowHelper.DisableDwmBlur(this);
        _accentApplied = WindowHelper.EnableAcrylicBlur(this, GetTintColor(), _isDark, 0.3d);
    }

    private Color GetTintColor()
        => _isDark ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0xF8, 0xF6, 0xF4);

    private Button CreateButton(string label, bool primary, bool subtle)
    {
        var background = primary
            ? ThemePalette.Accent(_isDark)
            : subtle
                ? Brushes.Transparent
                : ThemePalette.ButtonBg(_isDark);

        var hover = primary
            ? WithOpacity(ThemePalette.Accent(_isDark), 0.85)
            : ThemePalette.ButtonHover(_isDark);

        var button = new Button
        {
            Content = label,
            Background = background,
            Foreground = primary ? ContrastText(background) : ThemePalette.Text(_isDark),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            MinWidth = subtle ? 0 : 88,
            Padding = subtle ? new Thickness(6, 4, 6, 4) : new Thickness(16, 7, 16, 7),
            Template = BuildButtonTemplate(hover),
        };

        return button;
    }

    private Button CreateLink(string label)
    {
        var button = CreateButton(label, primary: false, subtle: true);
        button.Foreground = ThemePalette.Accent(_isDark);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    /// <summary>
    /// Rounded, flat button - the stock WPF template paints a grey gradient that
    /// does not belong on the acrylic panel.
    /// </summary>
    private static ControlTemplate BuildButtonTemplate(Brush hover)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover) { TargetName = "bd" });
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8) { TargetName = "bd" });
        template.Triggers.Add(pressedTrigger);

        return template;
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solid)
            return brush;

        var faded = new SolidColorBrush(solid.Color) { Opacity = opacity };
        faded.Freeze();
        return faded;
    }

    /// <summary>White on a dark accent, near-black on a light one.</summary>
    private static Brush ContrastText(Brush background)
    {
        if (background is not SolidColorBrush solid)
            return Brushes.White;

        var c = solid.Color;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255d;
        return luminance > 0.6
            ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
            : Brushes.White;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser; ignore.
        }
    }
}
