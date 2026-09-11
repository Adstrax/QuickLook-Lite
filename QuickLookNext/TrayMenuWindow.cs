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
using QuickLook.Common.NativeMethods;
using QuickLookNext.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// An Acrylic-backed, self-drawn context menu used by the system tray icon.
/// The built-in TrayIconHost menu is a native Win32 popup which cannot render
/// Acrylic, so it is replaced by this borderless WPF window that reuses the
/// same backdrop pipeline as the preview window.
/// v1.3.10: the window is non-layered and borderless (WindowStyle=None), so
/// DWM rounds the whole window - blur and content together - without the
/// large native frame shadow that wrapped the menu like a second background.
/// The window is intentionally non-activating (WS_EX_NOACTIVATE), so opening
/// or using the tray menu never steals focus from a live preview. A low-level
/// mouse hook dismisses the menu on outside clicks and a keyboard hook closes
/// it on Escape.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private static TrayMenuWindow _current;

    private readonly IReadOnlyList<TrayMenuEntry> _entries;
    private readonly bool _isDark;
    private readonly StackPanel _root;

    private readonly Brush _textBrush;
    private readonly Brush _disabledTextBrush;
    private readonly Brush _hoverBrush;
    private readonly Brush _separatorBrush;
    private readonly Brush _borderBrush;
    private readonly Brush _checkBrush;
    private readonly Brush _tintBrush;

    private LowLevelMouseProc _mouseProc;
    private LowLevelKeyboardProc _keyboardProc;
    private nint _mouseHook;
    private nint _keyboardHook;
    private readonly DispatcherTimer _autoCloseTimer;
    private long _generation;
    private bool _closing;
    private bool _accentApplied;
    // v1.3.7: the flyout opened by a submenu row (theme/backdrop/options).
    private TrayMenuWindow _childMenu;
    // v3.19.0: the menu that opened this flyout, so clicks over the parent
    // menu are not treated as "outside" by the child's mouse hook.
    private TrayMenuWindow _parentMenu;

    private TrayMenuWindow(IReadOnlyList<TrayMenuEntry> entries, bool isDark, int autoCloseMs)
    {
        _entries = entries;
        _isDark = isDark;

        Title = "QuickLook-Next Tray Menu";
        // v1.3.10: non-layered + borderless. The layered path can never
        // remove the square WCA blur (SetWindowRgn clips content, not the
        // blur), so the menu uses WCA acrylic on a normal window and lets DWM
        // round the whole window (blur included). WindowStyle=None avoids the
        // native frame's large DWM shadow, which showed up as an outer
        // background layer.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        // v3.18.0: paint the panel tint on the window itself so the very
        // first frame already shows the menu surface (never a blank/black
        // background while the content lays out).
        Background = _tintBrush;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;

        // v3.11.0: shared palette - identical colors across the tray menu and
        // the plugin manager panel, accent follows the system accent.
        _textBrush = ThemePalette.Text(isDark);
        _disabledTextBrush = ThemePalette.SecondaryText(isDark);
        _hoverBrush = ThemePalette.Hover(isDark);
        _separatorBrush = ThemePalette.Separator(isDark);
        _borderBrush = ThemePalette.Border(isDark);
        _checkBrush = ThemePalette.Accent(isDark);
        // v1.2.37: translucent overlay over the frosted acrylic - keep the
        // alpha moderate so the blur shows through while text stays readable.
        _tintBrush = ThemePalette.Tint(isDark);
        // v3.20.0: theme-aware scrollbar thumb (the implicit ScrollBar style
        // resolves this key through the window resources).
        Resources["ScrollBarThumbBrush"] = ThemePalette.ScrollbarThumb(isDark);

        _root = BuildMenu(entries);

        Content = new Border
        {
            // v1.3.10: the rounded panel fills the window exactly; DWM rounds
            // the window itself, so the acrylic blur follows the rounded
            // corners. No native frame means no extra outer shadow layer.
            Background = _tintBrush,
            BorderBrush = _borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            // v1.5.0: long submenus (e.g. the language picker) scroll instead
            // of overflowing the screen; small menus keep their natural size.
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 620,
                Content = _root,
            },
        };

        if (autoCloseMs > 0)
        {
            _autoCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(autoCloseMs),
            };
            _autoCloseTimer.Tick += (_, _) => CloseMenu();
            _autoCloseTimer.Start();
        }

        Closed += (_, _) =>
        {
            _autoCloseTimer?.Stop();
            UninstallHooks();
        };
    }

    /// <summary>
    /// Reads back the DWM backdrop attributes of the currently open menu
    /// window so tests can prove Mica is actually applied.
    /// </summary>
    public static string DiagnoseBackdrop()
    {
        var menu = _current;
        if (menu is null || !menu.IsVisible)
            return "no menu window open";

        var hwnd = new WindowInteropHelper(menu).Handle;
        return $"hwnd=0x{hwnd.ToInt64():X} accent-applied={menu._accentApplied}";
    }

    /// <summary>
    /// v1.3.6: test hook - dump the headers of the currently open menu so the
    /// smoke test can assert the theme/backdrop entries are present.
    /// </summary>
    public static string DiagnoseEntries()
    {
        var menu = _current;
        if (menu is null)
            return string.Empty;

        return string.Join("|", menu._entries.Select(e =>
            e.Children is { Count: > 0 }
                ? $"{e.Header}[{string.Join("|", e.Children.Select(c => c.Header))}]"
                : e.Header));
    }

    /// <summary>
    /// Show the tray menu at the current cursor position. Any previously open
    /// menu is closed first.
    /// </summary>
    public static bool IsOpen => _current?.IsVisible == true;

    public static void CloseCurrentMenu()
    {
        _current?.CloseMenu();
    }

    /// <summary>
    /// Show a menu at the current cursor position (used by the tray icon).
    /// </summary>
    public static void ShowMenu(IReadOnlyList<TrayMenuEntry> entries, bool isDark, int autoCloseMs = 0)
    {
        ShowMenuCore(entries, isDark, anchor: null, autoCloseMs);
    }

    /// <summary>
    /// Show a menu anchored below a control (used by the preview window's
    /// "More" button), right-aligned like a Win11 flyout.
    /// </summary>
    public static void ShowMenu(IReadOnlyList<TrayMenuEntry> entries, bool isDark, FrameworkElement anchor,
        int autoCloseMs = 0)
    {
        ShowMenuCore(entries, isDark, anchor, autoCloseMs);
    }

    private static void ShowMenuCore(IReadOnlyList<TrayMenuEntry> entries, bool isDark, FrameworkElement anchor,
        int autoCloseMs)
    {
        _current?.CloseMenu();

        var menu = new TrayMenuWindow(entries, isDark, autoCloseMs);
        _current = menu;

        if (anchor is not null && TryGetAnchorRect(anchor, out var anchorRect))
            menu.ShowAt(anchorRect);
        else
            menu.ShowAtCursor();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // v3.18.0: only re-apply the backdrop when the first attempt failed -
        // re-applying after every first paint made the menu flash its
        // background before the items were visible.
        if (!_accentApplied)
            ApplyBackdrop();
    }

    private StackPanel BuildMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        var panel = new StackPanel
        {
            MinWidth = 232,
            Margin = new Thickness(0, 4, 0, 4),
        };

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = _separatorBrush,
                    Margin = new Thickness(12, 4, 12, 4),
                });
                continue;
            }

            panel.Children.Add(BuildItem(entry));
        }

        return panel;
    }

    private Border BuildItem(TrayMenuEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hasChildren = entry.Children is { Count: > 0 };

        var icon = BuildIcon(entry.Icon, entry.IsEnabled);
        if (icon is not null)
        {
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var text = new TextBlock
        {
            // v3.39.0: the translations carry WinForms style access-key markers
            // ("重启 (&R)"), which this hand drawn WPF menu renders literally -
            // they showed up as a stray "&" in front of the shortcut letter.
            Text = entry.Header?.Replace("&", string.Empty),
            Foreground = entry.IsEnabled ? _textBrush : _disabledTextBrush,
            FontWeight = entry.IsBold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(icon is null ? 12 : 8, 0, 12, 0),
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var check = new TextBlock
        {
            Text = "\uE73B", // Segoe MDL2 Assets: CheckMark
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = _checkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = entry.IsChecked && !hasChildren ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(check, 2);
        grid.Children.Add(check);

        // v1.3.7: submenu rows show a chevron instead of a checkmark.
        var chevron = new TextBlock
        {
            Text = "\uE76C", // Segoe MDL2 Assets: ChevronRight
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = _disabledTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = hasChildren ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var item = new Border
        {
            Child = grid,
            Height = 32,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            IsEnabled = entry.IsEnabled,
            Opacity = entry.IsEnabled ? 1d : 0.5d,
            ToolTip = string.IsNullOrEmpty(entry.ToolTip) ? null : entry.ToolTip,
        };

        if (entry.IsEnabled)
        {
            item.Cursor = Cursors.Hand;
            item.MouseEnter += (_, _) => item.Background = _hoverBrush;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) =>
            {
                if (hasChildren)
                {
                    OpenSubmenu(item, entry.Children);
                    return;
                }

                var command = entry.Command;
                CloseMenu();
                command?.Invoke();
            };
        }

        return item;
    }

    /// <summary>
    /// v1.3.7: opens a nested flyout to the right of a submenu row. Clicking
    /// the row again replaces the flyout; clicking anywhere outside (or a
    /// child item) closes it together with the parent menu.
    /// </summary>
    private void OpenSubmenu(Border item, IReadOnlyList<TrayMenuEntry> children)
    {
        if (_childMenu != null)
        {
            _childMenu.Closed -= ChildMenu_Closed;
            _childMenu.CloseMenu();
            _childMenu = null;
        }

        var child = new TrayMenuWindow(children, _isDark, autoCloseMs: 0);
        child._parentMenu = this;
        _childMenu = child;
        child.Closed += ChildMenu_Closed;

        var topLeft = item.PointToScreen(new Point(0, 0));
        var bottomRight = item.PointToScreen(new Point(item.ActualWidth, item.ActualHeight));
        child.ShowSubmenuAt(new Rect(topLeft, bottomRight));
    }

    private void ChildMenu_Closed(object sender, EventArgs e)
    {
        _childMenu = null;
        CloseMenu();
    }

    private FrameworkElement BuildIcon(object icon, bool isEnabled)
    {
        if (icon is string glyph && !string.IsNullOrWhiteSpace(glyph))
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)(Application.Current?.Resources["SymbolThemeFontFamily"]
                    ?? new FontFamily("Segoe Fluent Icons")),
                FontSize = 14,
                Foreground = isEnabled ? _textBrush : _disabledTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
        }

        if (icon is ImageSource image)
        {
            return new Image
            {
                Source = image,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
        }

        if (icon is FrameworkElement element)
        {
            element.Margin = new Thickness(12, 0, 0, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
            return element;
        }

        return null;
    }

    private void ShowAtCursor()
    {
        if (!GetCursorPos(out var pt))
            return;

        // width == 0 marks cursor placement (as opposed to an anchored menu).
        ShowAt(new Rect(pt.X, pt.Y, 0, 0));
    }

    private void ShowAt(Rect anchorPx)
    {
        _generation++;

        var hwnd = new WindowInteropHelper(this).Handle; // forces HWND creation
        this.SetNoactivate();

        var hMonitor = MonitorFromPoint(new POINT
        {
            X = (int)anchorPx.X,
            Y = (int)anchorPx.Y,
        }, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);

        var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref monitor);

        var scaleX = dpiX / 96d;
        var scaleY = dpiY / 96d;

        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.UpdateLayout();
        var menuWidth = content.DesiredSize.Width;
        var menuHeight = content.DesiredSize.Height;

        // Working area in DIPs, clamped so the whole menu stays on screen.
        var workLeft = monitor.rcWork.Left / scaleX;
        var workTop = monitor.rcWork.Top / scaleY;
        var workRight = monitor.rcWork.Right / scaleX;
        var workBottom = monitor.rcWork.Bottom / scaleY;

        double left;
        double top;

        if (anchorPx.Width > 0)
        {
            // Anchored below a control, right-aligned (Win11 flyout style).
            left = (anchorPx.Right / scaleX) - menuWidth;
            top = (anchorPx.Bottom / scaleY) + (4 / scaleY);
        }
        else
        {
            // Cursor placement (tray icon).
            left = anchorPx.X / scaleX;
            top = anchorPx.Y / scaleY;
        }

        left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - menuWidth));
        top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - menuHeight));

        Left = left;
        Top = top;

        InstallHooks();
        Show();

        // Correct to exact physical pixels (handles mixed-DPI monitors where
        // the pre-show DIP placement may have been interpreted on another
        // monitor's scale). Same values in the common case, so no flicker.
        // v3.18.0: bRepaint=false - the post-show MoveWindow must not force a
        // repaint right after Show, which caused a background-only frame.
        MoveWindow(hwnd,
            (int)Math.Round(left * scaleX),
            (int)Math.Round(top * scaleY),
            (int)Math.Round(menuWidth * scaleX),
            (int)Math.Round(menuHeight * scaleY),
            false);
    }

    /// <summary>
    /// v1.3.7: positions a submenu flyout to the right of the parent row,
    /// aligned with the row top (Win11 flyout style), clamped to the work area.
    /// </summary>
    internal void ShowSubmenuAt(Rect rowRectPx)
    {
        _generation++;

        var hwnd = new WindowInteropHelper(this).Handle; // forces HWND creation
        this.SetNoactivate();

        var hMonitor = MonitorFromPoint(new POINT
        {
            X = (int)rowRectPx.Left,
            Y = (int)rowRectPx.Top,
        }, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);

        var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref monitor);

        var scaleX = dpiX / 96d;
        var scaleY = dpiY / 96d;

        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.UpdateLayout();
        var menuWidth = content.DesiredSize.Width;
        var menuHeight = content.DesiredSize.Height;

        var workLeft = monitor.rcWork.Left / scaleX;
        var workTop = monitor.rcWork.Top / scaleY;
        var workRight = monitor.rcWork.Right / scaleX;
        var workBottom = monitor.rcWork.Bottom / scaleY;

        var left = (rowRectPx.Right / scaleX) + (4 / scaleX);
        var top = rowRectPx.Top / scaleY;

        left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - menuWidth));
        top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - menuHeight));

        Left = left;
        Top = top;

        InstallHooks();
        Show();

        // v3.19.0: no forced repaint after Show - same as ShowAt, so the
        // submenu does not flash its background before the items are visible.
        MoveWindow(hwnd,
            (int)Math.Round(left * scaleX),
            (int)Math.Round(top * scaleY),
            (int)Math.Round(menuWidth * scaleX),
            (int)Math.Round(menuHeight * scaleY),
            false);
    }

    private static bool TryGetAnchorRect(FrameworkElement anchor, out Rect rect)
    {
        rect = default;
        try
        {
            if (anchor.IsLoaded && PresentationSource.FromVisual(anchor) is not null)
            {
                var topLeft = anchor.PointToScreen(new Point(0, 0));
                var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
                rect = new Rect(topLeft, bottomRight);
                return true;
            }
        }
        catch
        {
            // Fall back to cursor placement below.
        }

        return false;
    }

    private void ApplyBackdrop()
    {
        // v1.3.10: non-layered WCA acrylic - clear any DWM backdrop and
        // restore rounded corners, then enable the acrylic. DWM rounds the
        // blur with the borderless window, so there is no square frosted
        // frame and no native frame shadow around the menu.
        WindowHelper.DisableDwmBlur(this);
        _accentApplied = WindowHelper.EnableAcrylicBlur(this, GetTintColor(), _isDark, 0.3d);
    }

    private Color GetTintColor()
    {
        return _isDark ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0xF8, 0xF6, 0xF4);
    }

    private void InstallHooks()
    {
        if (_mouseHook != IntPtr.Zero || _keyboardHook != IntPtr.Zero)
            return;

        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;

        var hMod = Kernel32.LoadLibrary("user32.dll");
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
    }

    private void UninstallHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _mouseProc = null;
        _keyboardProc = null;
    }

    private nint MouseHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_closing)
        {
            var message = (uint)wParam.ToInt64();
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // v3.18.0: clicks on the taskbar / tray icon area do not close
                // the menu - the tray icon's own left/right click handlers
                // toggle it instead, so clicking the icon no longer makes the
                // menu disappear (and right-click no longer flickers).
                if (!IsPointInsideWindow(data.pt.X, data.pt.Y) &&
                    !IsPointOverTaskbar(data.pt.X, data.pt.Y))
                {
                    ScheduleClose();
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsPointOverTaskbar(int x, int y)
    {
        var hwnd = User32.WindowFromPoint(new User32.POINT(x, y));
        if (hwnd == IntPtr.Zero)
            return false;

        var root = User32.GetAncestor(hwnd, User32.GA_ROOT);
        if (root == IntPtr.Zero)
            return false;

        var buffer = new System.Text.StringBuilder(128);
        User32.GetClassName(root, buffer, buffer.Capacity);
        return buffer.ToString() is "Shell_TrayWnd" or "NotifyIconOverflowWindow";
    }

    private nint KeyboardHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_closing && ((uint)wParam.ToInt64() is WM_KEYDOWN or WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == VK_ESCAPE)
            {
                ScheduleClose();
                return (nint)1; // swallow Escape while the menu is open
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideWindow(int x, int y)
    {
        if (!IsVisible)
            return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            return false;

        if (x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom)
            return true;

        // v3.19.0: clicks over any related QuickLook menu window (this window,
        // an open child flyout, or the parent menu that opened this flyout)
        // are never "outside". Without this, clicking the parent menu while a
        // submenu is open closed the submenu - and closing the submenu also
        // closed the parent (ChildMenu_Closed), so the whole menu vanished.
        var under = User32.WindowFromPoint(new User32.POINT(x, y));
        if (under == IntPtr.Zero)
            return false;

        var root = User32.GetAncestor(under, User32.GA_ROOT);
        return root != IntPtr.Zero && IsRelatedMenuHwnd(root);
    }

    private bool IsRelatedMenuHwnd(nint hwnd)
    {
        if (hwnd == new WindowInteropHelper(this).Handle)
            return true;

        if (_childMenu is { IsVisible: true } child &&
            hwnd == new WindowInteropHelper(child).Handle)
        {
            return true;
        }

        if (_parentMenu is { IsVisible: true } parent &&
            hwnd == new WindowInteropHelper(parent).Handle)
        {
            return true;
        }

        return false;
    }

    private void ScheduleClose()
    {
        var generation = _generation;
        Dispatcher.BeginInvoke(() =>
        {
            // Ignore stale close requests queued before the menu was reopened
            // (e.g. the right-click that opens a new menu also goes through
            // the mouse hook first).
            if (generation == _generation && IsVisible)
                CloseMenu();
        }, DispatcherPriority.Background);
    }

    private void CloseMenu()
    {
        if (_closing)
            return;

        _closing = true;
        _generation++;
        UninstallHooks();

        // v1.3.7: close any open submenu flyout together with this menu.
        if (_childMenu != null)
        {
            _childMenu.Closed -= ChildMenu_Closed;
            var child = _childMenu;
            _childMenu = null;
            child.CloseMenu();
        }

        if (IsVisible)
            Close();
    }

    // ---- P/Invoke ---------------------------------------------------------

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint VK_ESCAPE = 0x1B;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);
    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);
}

/// <summary>
/// Describes one row of the tray menu.
/// </summary>
internal sealed record TrayMenuEntry
{
    public string Header { get; init; }
    public Action Command { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsChecked { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsBold { get; init; }
    public object Icon { get; init; }
    public string ToolTip { get; init; }
    // v1.3.7: when set, the row opens a nested flyout with these entries
    // instead of executing a command.
    public IReadOnlyList<TrayMenuEntry> Children { get; init; }

    public static TrayMenuEntry Separator => new() { IsSeparator = true };
}
