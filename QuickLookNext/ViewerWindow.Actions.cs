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
using QuickLook.Common.Plugin;
using QuickLook.Common.Plugin.MoreMenu;
using QuickLookNext.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Wpf.Ui.Violeta.Controls;
using WinForms = System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;

namespace QuickLookNext;

public partial class ViewerWindow
{
    // v1.2.9: plugin-provided More menu items, snapshotted into the unified
    // Mica menu (TrayMenuWindow) every time the menu is opened.
    private readonly List<TrayMenuEntry> _pluginMoreMenuEntries = new();

    internal void Run()
    {
        if (string.IsNullOrEmpty(_path))
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_path)
            {
                WorkingDirectory = Path.GetDirectoryName(_path)
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
    }

    internal void RunAndClose()
    {
        Run();
        Close();
    }

    internal void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            // Exit fullscreen
            _isFullscreen = false;

            // Restore window properties
            WindowStyle = _preFullscreenWindowStyle;
            ResizeMode = _preFullscreenResizeMode;

            // Restore position and size
            Left = _preFullscreenBounds.Left;
            Top = _preFullscreenBounds.Top;
            Width = _preFullscreenBounds.Width;
            Height = _preFullscreenBounds.Height;

            // Restore window state last to avoid flicker
            WindowState = _preFullscreenWindowState;

            // Restore caption height and resize border thickness saved before fullscreen
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = _preFullscreenCaptionHeight;
                chrome.ResizeBorderThickness = _preFullscreenResizeBorderThickness;
            }

            // Restore rounded corners on Windows 11
            WindowHelper.SetWindowCorner(this, Dwmapi.WindowCornerStyle.Round);
            ApplyLayeredWindowRegion();
        }
        else
        {
            // Enter fullscreen
            _isFullscreen = true;

            // Save current window properties before any changes
            _preFullscreenWindowState = WindowState;
            _preFullscreenWindowStyle = WindowStyle;
            _preFullscreenResizeMode = ResizeMode;

            // Get current bounds (account for maximized state)
            if (WindowState == WindowState.Maximized)
            {
                _preFullscreenBounds = RestoreBounds;
            }
            else
            {
                _preFullscreenBounds = new Rect(Left, Top, Width, Height);
            }

            // Get the screen bounds where the window is currently located
            var screen = WinForms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var screenBounds = screen.Bounds;

            // Get DPI scale factor for proper coordinate conversion
            // scale.Horizontal and scale.Vertical contain the DPI scaling ratios (e.g., 1.5 for 150%)
            var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(this);

            // Set to normal state first to allow manual positioning
            WindowState = WindowState.Normal;

            // Save and remove caption height and resize border so title area is not draggable and window is not resizable in fullscreen
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                _preFullscreenCaptionHeight = chrome.CaptionHeight;
                _preFullscreenResizeBorderThickness = chrome.ResizeBorderThickness;
                chrome.CaptionHeight = 0d;
                chrome.ResizeBorderThickness = new Thickness(0d);
            }

            // Hide window chrome for true fullscreen
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Convert screen bounds from physical pixels to DIPs for WPF
            var dipWidth = screenBounds.Width / scale.Horizontal;
            var dipHeight = screenBounds.Height / scale.Vertical;

            // Use MoveWindow to set position and size with proper DPI handling
            this.MoveWindow(screenBounds.Left, screenBounds.Top, dipWidth, dipHeight);

            // Remove rounded corners on Windows 11 for true fullscreen
            WindowHelper.SetWindowCorner(this, Dwmapi.WindowCornerStyle.DoNotRound);
            // Layered window: the rounded region would clip the fullscreen
            // surface, so drop it for the duration of fullscreen.
            WindowHelper.ClearWindowRegion(this);
        }
    }

    private void PositionWindow(Size size)
    {
        // If the window is now maximized, do not move it
        if (WindowState == WindowState.Maximized)
            return;

        size = new Size(Math.Max(MinWidth, size.Width), Math.Max(MinHeight, size.Height));

        // v1.2.36: the first real preview after the off-screen warm-up must
        // still use new-window centering (the warm-up rect is off-screen and
        // must not become the "old centre" for the resize math).
        var newRect = IsLoaded && !_warmShown
            ? ResizeAndCentreExistingWindow(size)
            : ResizeAndCentreNewWindow(size);

        this.MoveWindow(newRect.Left, newRect.Top, newRect.Width, newRect.Height);
    }

    private Rect ResizeAndCentreExistingWindow(Size size)
    {
        // Align window just like in macOS ...
        //
        // |10%|    80%    |10%|
        // |---|-----------|---|---
        // |TL |     T     |TR |10%
        // |---|-----------|---|---
        // |   |           |   |
        // |L  |     C     | R |80%
        // |   |           |   |
        // |---|-----------|---|---
        // |LB |     B     |RB |10%
        // |---|-----------|---|---

        var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(this);

        var limitPercentX = 0.1 * scale.Horizontal;
        var limitPercentY = 0.1 * scale.Vertical;

        // Use absolute pixels for calculation
        var pxSize = new Size(scale.Horizontal * size.Width, scale.Vertical * size.Height);
        var pxOldRect = this.GetWindowRectInPixel();

        // Scale to new size, maintain centre
        var pxNewRect = Rect.Inflate(pxOldRect,
            (pxSize.Width - pxOldRect.Width) / 2,
            (pxSize.Height - pxOldRect.Height) / 2);

        var desktopRect = WindowHelper.GetDesktopRectFromWindowInPixel(this);

        var leftLimit = desktopRect.Left + desktopRect.Width * limitPercentX;
        var rightLimit = desktopRect.Right - desktopRect.Width * limitPercentX;
        var topLimit = desktopRect.Top + desktopRect.Height * limitPercentY;
        var bottomLimit = desktopRect.Bottom - desktopRect.Height * limitPercentY;

        if (pxOldRect.Left < leftLimit && pxOldRect.Right < rightLimit) // L
            pxNewRect.Location = new Point(Math.Max(pxOldRect.Left, desktopRect.Left), pxNewRect.Top);
        else if (pxOldRect.Left > leftLimit && pxOldRect.Right > rightLimit) // R
            pxNewRect.Location = new Point(Math.Min(pxOldRect.Right, desktopRect.Right) - pxNewRect.Width, pxNewRect.Top);
        else // C, fix window boundary
            pxNewRect.Offset(
                Math.Max(0, desktopRect.Left - pxNewRect.Left) + Math.Min(0, desktopRect.Right - pxNewRect.Right), 0);

        if (pxOldRect.Top < topLimit && pxOldRect.Bottom < bottomLimit) // T
            pxNewRect.Location = new Point(pxNewRect.Left, Math.Max(pxOldRect.Top, desktopRect.Top));
        else if (pxOldRect.Top > topLimit && pxOldRect.Bottom > bottomLimit) // B
            pxNewRect.Location = new Point(pxNewRect.Left,
                Math.Min(pxOldRect.Bottom, desktopRect.Bottom) - pxNewRect.Height);
        else // C, fix window boundary
            pxNewRect.Offset(0,
                Math.Max(0, desktopRect.Top - pxNewRect.Top) + Math.Min(0, desktopRect.Bottom - pxNewRect.Bottom));

        // Return absolute location and relative size
        return new Rect(pxNewRect.Location, size);
    }

    private Rect ResizeAndCentreNewWindow(Size size)
    {
        var desktopRect = WindowHelper.GetCurrentDesktopRectInPixel();
        var scale = DisplayDeviceHelper.GetCurrentScaleFactor();
        var pxSize = new Size(scale.Horizontal * size.Width, scale.Vertical * size.Height);

        var pxLocation = new Point(
            desktopRect.X + (desktopRect.Width - pxSize.Width) / 2,
            desktopRect.Y + (desktopRect.Height - pxSize.Height) / 2);

        // Return absolute location and relative size
        return new Rect(pxLocation, size);
    }

    /// <summary>
    /// v3.31.0: handles <see cref="ContextObject.ApplyPreferredSizeNow"/> - a
    /// plugin that measured its content asks the window to follow the new
    /// <see cref="ContextObject.PreferredSize"/> immediately. Replaces the
    /// reflection call plugins used to make into this window.
    /// </summary>
    private void ApplyResizeRequest(Size size)
    {
        // A maximized window keeps its size (same rule as PositionWindow).
        if (!IsLoaded || WindowState == WindowState.Maximized)
            return;

        var newRect = ResizeAndCentreExistingWindow(size);

        this.MoveWindow(newRect.Left, newRect.Top, newRect.Width, newRect.Height);
    }

    internal void UnloadPlugin()
    {
        // The focused element will not processed by GC: https://stackoverflow.com/questions/30848939/memory-leak-due-to-window-efectivevalues-retention
        FocusManager.SetFocusedElement(this, null);
        Keyboard.DefaultRestoreFocusMode =
            RestoreFocusMode.None; // WPF will put the focused item into a "_restoreFocus" list ... omg
        Keyboard.ClearFocus();

        _canOldPluginResize = ContextObject.CanResize;

        // v1.2.14: keep the old content fully rendered until the next preview's
        // content takes over. Cleanup is deferred when this plugin's content is
        // on screen; a plugin that never got shown (superseded while loading)
        // can be disposed right away.
        if (_contentPlugin != null && ReferenceEquals(_contentPlugin, Plugin))
        {
            _pendingPluginCleanup = Plugin;
            _staleViewerContent = ContextObject.ViewerContent;
        }
        else
        {
            try
            {
                Plugin?.Cleanup();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }

        ContextObject.Reset();

        if (_staleViewerContent != null)
            ContextObject.ViewerContent = _staleViewerContent;

        if (_autoReloadWatcher != null)
        {
            _autoReloadWatcher.EnableRaisingEvents = false;
            _autoReloadWatcher.Dispose();
            _autoReloadWatcher = null;
        }

        Plugin = null;

        _path = string.Empty;
    }

    internal void BeginShow(IViewer matchedPlugin, string path,
        Action<string, ExceptionDispatchInfo> exceptionHandler)
    {
        _path = path;
        Plugin = matchedPlugin;

        ContextObject.Reset();

        // v1.2.14: keep the previous content visible until the new one takes
        // over (avoids the blank gray window during switches); _staleViewerContent
        // is cleared by the ViewerContent change handler at the swap.
        if (_staleViewerContent != null)
            ContextObject.ViewerContent = _staleViewerContent;

        // Assign monitor color profile
        ContextObject.ColorProfileName = DisplayDeviceHelper.GetMonitorColorProfileFromWindow(this);

        // v1.2.27: show the window right away (before the potentially slow
        // Prepare) so previews feel instant. Prepare can still block briefly,
        // but the window is already on screen.
        // v1.2.36: after the off-screen warm-up the window is already
        // "visible"; still run the initial centering, but as a new-window
        // placement so it lands on screen centered, not on the warm-up spot.
        if (!IsVisible || _warmShown)
        {
            if (_customWindowSize != Size.Empty)
            {
                PositionWindow(_customWindowSize);
            }
            else
            {
                ContextObject.PreferredSize = new Size(800, 600);
                PositionWindow(ComputeWindowSize());
            }

            _warmShown = false;

            if (!IsVisible)
            {
                if (!SettingHelper.Get("ShowWindowTransition", true, "QuickLookNext"))
                    this.ShowWithoutTransition();
                else
                    Show();
            }
        }

        // v3.0.1: raise the preview above other windows on every open or
        // switch. The 1.2.36 off-screen warm-up makes IsVisible true before
        // the first real preview, so the old `if (!IsVisible)` guard skipped
        // BringToFront and the preview could open behind other windows.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            this.BringToFront(Topmost);
            if (SettingHelper.Get("FocusWindowOnOpen", false))
                Activate();
        }), DispatcherPriority.Render);

        // Get the content size (and do any slow prepare work).
        try
        {
        Plugin.Prepare(path, ContextObject);
        }
        catch (Exception e)
        {
            exceptionHandler(path, ExceptionDispatchInfo.Capture(e));
            return;
        }

        if (ContextObject.IsBlocked)
        {
            ContextObject.ViewerContent = new System.Windows.Controls.TextBlock
            {
                Text = TranslationHelper.Get("MW_FileBlocked", failsafe: "This file type is blocked."),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            };
            ContextObject.IsBusy = false;
            return;
        }

        // v3.22.0: the "Open / Open with" tooltip and share-button visibility
        // come from the shell-association lookup (AssocQueryString /
        // FileVersionInfo), which can take a few milliseconds on a cold
        // registry/disk cache. Defer it to Background priority so it never
        // delays the spinner / first content frame; the click handlers still
        // resolve the association themselves, so the buttons stay fully
        // usable while the hint updates.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _path == path)
                SetOpenWithButtonAndPath();
        }, DispatcherPriority.Background);

        // Show the spinner only for plugins that did not opt out (images and
        // videos set ShowBusyIndicator=false in Prepare to avoid the spinner).
        ContextObject.ShowBusyIndicator &= _staleViewerContent == null;
        ContextObject.IsBusy = true;

        var newSize = ComputeWindowSize();
        if (_customWindowSize == Size.Empty)
            _ignoreNextWindowSizeChange = true;

        // v1.2.14: when switching to an image, keep the window (and the old
        // image) at its current size until the new first frame is decoded -
        // resizing early re-fits the old image into the new aspect ratio and
        // shows gray letterbox bands during the load.
        var deferResize = ContextObject.DeferResizeUntilReady && _staleViewerContent != null;
        if (!deferResize)
        {
            PositionWindow(newSize);
            ContextObject.DeferResizeUntilReady = false;
        }

        // v1.2.14: re-apply the backdrop so a change made from the tray menu
        // takes effect on the current preview (the setting is read here, not
        // cached at startup).
        ApplyWindowBackgroundEffects();

        // v1.3.5: hidden test hook (/test-preview-diag) - write the backdrop
        // render path so automated checks can assert acrylic is really on.
        if (App.IsPreviewDiagEnabled)
        {
            try
            {
                var diagDir = App.SmokeDir;
                Directory.CreateDirectory(diagDir);
                File.WriteAllText(Path.Combine(diagDir, "preview-backdrop.txt"), DiagnoseBackdrop());
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        if (_autoReload && File.Exists(path))
        {
            _autoReloadWatcher?.Dispose();
            _autoReloadWatcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _autoReloadWatcher.Changed += (_, _) =>
                // Executed asynchronously to avoid deadlock
                Dispatcher.BeginInvoke(() => ViewWindowManager.GetInstance().ReloadPreview());
            _autoReloadWatcher.EnableRaisingEvents = true;
        }

        // Load plugin, do not block UI
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Avoid main thread scheduling lag
                if (Plugin is null) return;

                Plugin.View(path, ContextObject);

                // Initialize the more menu
                ClearMoreMenuEntries();
                var pluginManager = PluginManager.GetInstance();
                pluginManager.EnsureLoaded();
                foreach (var plugin in
                    pluginManager.LoadedPlugins
                        .GroupBy(x => x.ToString()).Select(g => g.First()) // DistinctBy plugin name
                        .OrderBy(p => p.Priority)) // OrderBy plugin priority
                {
                    if (plugin.ToString() == Plugin.ToString())
                    {
                        if (Plugin is IMoreMenu moreMenu && moreMenu.MenuItems is not null)
                        {
                            AddPluginMoreMenu(moreMenu.MenuItems);
                        }
                        continue;
                    }
                    else
                    {
                        if (plugin is IMoreMenuExtended moreMenu && moreMenu.MenuItems is not null)
                        {
                            AddPluginMoreMenu(moreMenu.MenuItems);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                exceptionHandler(path, ExceptionDispatchInfo.Capture(e));
            }
        }, DispatcherPriority.Input);
    }

    private Size ComputeWindowSize()
    {
        var newHeight = ContextObject.PreferredSize.Height + BorderThickness.Top + BorderThickness.Bottom +
                        (ContextObject.TitlebarOverlap ? 0 : windowCaptionContainer.Height);
        var newWidth = ContextObject.PreferredSize.Width + BorderThickness.Left + BorderThickness.Right;

        var newSize = new Size(newWidth, newHeight);

        // If the user has adjusted the window size, keep it
        if (_customWindowSize != Size.Empty)
            newSize = _customWindowSize;

        return newSize;
    }

    private void ClearMoreMenuEntries()
    {
        _pluginMoreMenuEntries.Clear();
    }

    private void AddPluginMoreMenu(IEnumerable<IMenuItem> moreMenu)
    {
        foreach (IMenuItem item in moreMenu)
        {
            if (item is null) continue;

            if (item.IsSeparator)
            {
                _pluginMoreMenuEntries.Add(TrayMenuEntry.Separator);
                continue;
            }

            if (!item.IsVisible)
                continue;

            var command = item.Command;
            var parameter = item.CommandParameter;

            _pluginMoreMenuEntries.Add(new TrayMenuEntry
            {
                Header = item.Header?.ToString(),
                Icon = ResolveMenuIcon(item.Icon),
                IsEnabled = item.IsEnabled,
                ToolTip = item.ToolTip,
                Command = () =>
                {
                    if (command?.CanExecute(parameter) == true)
                        command.Execute(parameter);
                },
            });
        }
    }

    private static object ResolveMenuIcon(object icon)
    {
        return icon is string or ImageSource or FrameworkElement ? icon : null;
    }

    private List<TrayMenuEntry> BuildMoreMenuEntries()
    {
        var entries = new List<TrayMenuEntry>
        {
            new()
            {
                Header = TranslationHelper.Get("MW_Reload"),
                Icon = FontSymbols.Refresh,
                Command = () => ViewWindowManager.GetInstance().ReloadPreview(),
            },
            new()
            {
                Header = TranslationHelper.Get("InfoPanelMoreItem_CopyAsPath"),
                Icon = FontSymbols.Copy,
                Command = CopyPathToClipboard,
            },
        };

        if (_pluginMoreMenuEntries.Count > 0)
        {
            entries.Add(TrayMenuEntry.Separator);
            entries.AddRange(_pluginMoreMenuEntries);
        }

        return entries;
    }

    private void CopyPathToClipboard()
    {
        try
        {
            Clipboard.SetText($"\"{(_path.Length >= 260 ? @"\\?\" + _path : _path)}\"");
            Toast.Success(TranslationHelper.Get("InfoPanelMoreItem_CopySucc"));
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void ToggleMoreMenu()
    {
        if (TrayMenuWindow.IsOpen)
        {
            TrayMenuWindow.CloseCurrentMenu();
            return;
        }

        ShowMoreMenu();
    }

    private void ShowMoreMenu()
    {
        TrayMenuWindow.ShowMenu(BuildMoreMenuEntries(), CurrentTheme == Themes.Dark, buttonMore);
    }

    internal void ShowMoreMenuForTest()
    {
        TrayMenuWindow.ShowMenu(BuildMoreMenuEntries(), CurrentTheme == Themes.Dark, buttonMore, autoCloseMs: 3000);
    }

    private void SetOpenWithButtonAndPath()
    {
        // Share icon
        buttonShare.Visibility = ShareHelper.IsShareSupported(_path) ? Visibility.Visible : Visibility.Collapsed;

        // Open icon
        if (Directory.Exists(_path))
        {
            buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_BrowseFolder"), Path.GetFileName(_path));
            return;
        }

        var isExe = FileHelper.IsExecutable(_path, out var appFriendlyName);
        if (isExe)
        {
            buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_Run"), appFriendlyName);
            return;
        }

        // Not an exe
        var found = FileHelper.GetAssocApplication(_path, out appFriendlyName);
        if (found)
        {
            buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_OpenWith"), appFriendlyName);
            return;
        }

        // Assoc not found
        buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_Open"), Path.GetFileName(_path));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        UnloadPlugin();

        // v1.2.14: no new preview will take over, so dispose the deferred
        // plugin now (its content was kept on screen until the window closes).
        if (_pendingPluginCleanup != null)
        {
            try
            {
                _pendingPluginCleanup.Cleanup();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            _pendingPluginCleanup = null;
            _staleViewerContent = null;
        }

        base.OnClosing(e);

        ProcessHelper.PerformAggressiveGC();
    }
}
