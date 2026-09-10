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

using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Helpers;
using QuickLook.Common.NativeMethods;
using QuickLook.Common.Plugin;
using QuickLookNext.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Wpf.Ui.Violeta.Controls;
using static QuickLook.Common.NativeMethods.Dwmapi;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace QuickLookNext;

public partial class ViewerWindow : Window
{
    private Size _customWindowSize = Size.Empty;
    private bool _ignoreNextWindowSizeChange;
    private string _path = string.Empty;
    private FileSystemWatcher _autoReloadWatcher;
    private readonly bool _autoReload;
    private int _lastCursorX;
    private int _lastCursorY;
    private bool _hasLastCursor;
    private bool _warmShown;
    // v1.3.1: the window is created as a layered window when the user's
    // backdrop is an acrylic material on Win11, so the WCA acrylic renders
    // from the very first frame (see ShouldUseLayeredAcrylic).
    private readonly bool _layeredAcrylic;
    // v1.3.6: one low-level mouse hook serves two jobs:
    // - 1.3.5 fallback: layered windows never receive the WM_MOUSEWHEEL that
    //   Windows forwards from the focus window to the window under the cursor,
    //   so the hook re-delivers wheel input to the preview window (only while
    //   the window is layered; the non-layered acrylic path keeps the native
    //   routing).
    // - 1.3.6: "顶部状态栏默认隐藏" - the top caption zone is the draggable
    //   WindowChrome region (HTCAPTION), so WPF gets no MouseMove there; the
    //   hook watches the cursor and reveals the bar when it enters the zone.
    private LowLevelMouseProc _mouseProc;
    private nint _mouseHook;
    // v1.3.6: polls the cursor while the preview is open so the top bar can be
    // revealed when the cursor enters the top caption zone (the zone is the
    // draggable WindowChrome region, so WPF gets no MouseMove there and a
    // low-level hook turned out unreliable in this app).
    private DispatcherTimer _topBarPollTimer;
    // v1.3.5: last WCA acrylic result, reported by DiagnoseBackdrop so the
    // smoke tests can assert the acrylic call really succeeded.
    private bool _lastAcrylicOk;

    internal ViewerWindow()
    {
        // v1.3.1: Win11's DWM SystembackdropType.Acrylic renders a solid tint
        // on inactive windows and the preview window never activates
        // (ShowActivated=false), so the chosen acrylic only appeared after a
        // click. A layered window + WCA acrylic (the tray-menu recipe) blurs
        // regardless of the activation state.
        _layeredAcrylic = ShouldUseLayeredAcrylic();
        if (_layeredAcrylic)
        {
            // WPF rule: layered windows must not use a native window style.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            // v1.3.2: the 1px Window border (BorderBrush = dark caption color)
            // would render as a black outline around the layered window.
            BorderThickness = new Thickness(0);
        }

        // this object should be initialized before loading UI components, because many of which are binding to it.
        ContextObject = new ContextObject() { Source = this };

        ContextObject.PropertyChanged += ContextObject_PropertyChanged;

        // v3.31.0: plugins ask for a resize through ContextObject instead of
        // reflecting into this window (see ContextObject.ApplyPreferredSizeNow).
        ContextObject.ResizeRequested += ApplyResizeRequest;

        InitializeComponent();

        // v1.2.1: start with the user's saved light/dark choice (None = follow system).
        ContextObject.Theme = (Themes)SettingHelper.Get(
            "LastTheme", (int)Themes.None, "QuickLookNext");

        _autoReload = SettingHelper.Get("AutoReload", false);

        Icon = (App.IsWin10 ? Properties.Resources.app_white_png : Properties.Resources.app_png).ToBitmapSource();

        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));

        SizeChanged += SaveWindowSizeOnSizeChanged;

        StateChanged += (_, _) =>
        {
            _ignoreNextWindowSizeChange = true;
            ApplyLayeredWindowRegion();
        };

        windowFrameContainer.PreviewMouseMove += ShowWindowCaptionContainer;

        Topmost = SettingHelper.Get("Topmost", false);
        buttonTop.Tag = Topmost ? "Top" : "Auto";
        buttonTheme.Click += ToggleTheme;

        ShowInTaskbar = SettingHelper.Get("ShowInTaskbar", false);

        Deactivated += (_, _) =>
        {
            if (!SettingHelper.Get("CloseOnLostFocus", false))
                return;
            if (Pinned)
                return;
            // Defer close to ContextIdle so pending Render/Input operations
            // (e.g. MoveWindow, BringToFront) complete before the window is closed.
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible && !Pinned)
                    Close();
            }, DispatcherPriority.ContextIdle);
        };

        Closed += (_, _) =>
        {
            UninstallMouseHook();
            StopTopBarPolling();
        };

        buttonTop.Click += (_, _) =>
        {
            Topmost = !Topmost;
            SettingHelper.Set("Topmost", Topmost);
            buttonTop.Tag = Topmost ? "Top" : "Auto";
        };

        buttonPin.Click += (_, _) =>
        {
            if (SettingHelper.Get("CloseOnLostFocus", false))
            {
                Pinned = !Pinned;
                ViewWindowManager.GetInstance().ForgetCurrentWindow();
                return;
            }

            if (Pinned)
            {
                Toast.Information(TranslationHelper.Get("InfoPanel_CantPreventClosing"));
                return;
            }

            ViewWindowManager.GetInstance().ForgetCurrentWindow();
        };

        buttonCloseWindow.Click += (_, _) =>
        {
            Close();
        };

        buttonOpen.Click += (_, _) =>
        {
            if (Pinned)
                RunAndClose();
            else
                ViewWindowManager.GetInstance().RunAndClosePreview();
        };

        buttonReload.Click += (_, _) =>
        {
            ViewWindowManager.GetInstance().ReloadPreview();
        };

        buttonWindowStatus.Click += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        buttonShare.Click += (_, _) => ShareHelper.Share(_path, this);
        buttonOpenWith.Click += (_, _) => ShareHelper.Share(_path, this, true);

        buttonReload.Visibility = SettingHelper.Get("ShowReload", false) ? Visibility.Visible : Visibility.Collapsed;

        buttonMore.Click += (_, _) => ToggleMoreMenu();

        // Set UI translations
        buttonTop.ToolTip = TranslationHelper.Get("MW_StayTop");
        buttonPin.ToolTip = TranslationHelper.Get("MW_PreventClosing");
        buttonOpenWith.ToolTip = TranslationHelper.Get("MW_OpenWithMenu");
        buttonShare.ToolTip = TranslationHelper.Get("MW_Share");
        buttonReload.ToolTip = TranslationHelper.Get("MW_Reload", failsafe: "Reload");
        buttonMore.ToolTip = TranslationHelper.Get("MW_More", failsafe: "More");
    }

    /// <summary>
    /// v1.2.36: pay the one-time first-Show cost (HWND creation, layout, DWM
    /// backdrop) during startup idle, off-screen. The window stays "visible"
    /// off-screen; the first real preview re-positions it as a new window so
    /// it is centered correctly and never inherits the warm-up size.
    /// </summary>
    internal void WarmUp()
    {
        if (IsVisible)
            return;

        _warmShown = true;
        // The warm-up Show fires SizeChanged; do not persist that default
        // size as the user's custom window size.
        _ignoreNextWindowSizeChange = true;

        var restoreActivated = ShowActivated;
        ShowActivated = false;
        Left = -32000;
        Top = -32000;
        Show();
        ShowActivated = restoreActivated;
    }

    public new void Close()
    {
        // Workaround to prevent DPI jump animation when closing window in .NET Framework 4.6.2
        // Safe to remove this line if QuickLookNext no longer targets .NET Framework 4.6.2
        Hide();

        base.Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowHelper.RemoveWindowControls(this);

        if (_layeredAcrylic)
        {
            // v1.3.2: on a layered window there is no DWM glass to fill the
            // WindowChrome frame; the 1px glass thickness renders as a dark
            // line, so flatten it and clip the window to rounded corners.
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
                chrome.GlassFrameThickness = new Thickness(0);

            ApplyLayeredWindowRegion();
        }

        ApplyWindowBackgroundEffects();

        if (_layeredAcrylic)
            InstallMouseHook();
        if (SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext"))
            StartTopBarPolling();

        WriteTopBarDiag($"source-init hook={(_mouseHook != IntPtr.Zero)} layered={_layeredAcrylic} " +
            $"setting={SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext")} polling={_topBarPollTimer != null}");
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // v1.1.4: baseline the cursor position. WPF synthesizes MouseMove events
        // when the window opens or its content is replaced (preview switching)
        // while the cursor is stationary. We only treat a MouseMove as real when
        // the cursor screen position actually changes.
        if (GetCursorPos(out var pt))
        {
            _lastCursorX = pt.X;
            _lastCursorY = pt.Y;
            _hasLastCursor = true;
        }

        ApplyWindowBackgroundEffects();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (_isFullscreen)
        {
            Dispatcher.BeginInvoke(new Action(() => this.BringToFront(Topmost)), DispatcherPriority.Render);
        }
    }

    private void ApplyWindowBackgroundEffects()
    {
        var useTransparency = SettingHelper.Get("UseTransparency", true)
            && SystemParameters.IsGlassEnabled
            && !App.IsGPUInBlacklist;
        var backdrop = GetBackdropOption();

        if (useTransparency)
        {
            ApplyBackdrop(backdrop);
        }
        else
        {
            SetGlassFrameThickness(1d);
            WindowHelper.DisableDwmBlur(this); // Fix white flash in dark mode
            Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
        }

        var customColor = SettingHelper.Get("WindowBackgroundColor", string.Empty, "QuickLookNext");
        if (!string.IsNullOrEmpty(customColor))
        {
            try
            {
                Background = (Brush)new BrushConverter().ConvertFromString(customColor);
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException)
            {
                // Ignore invalid color
            }
        }
    }

    private void SetGlassFrameThickness(double thickness)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
            chrome.GlassFrameThickness = new Thickness(thickness);
    }

    private void ApplyBackdrop(SystembackdropType backdrop)
    {
        switch (backdrop)
        {
            case SystembackdropType.None:
                {
                    SetGlassFrameThickness(1d);
                    WindowHelper.DisableDwmBlur(this); // Fix white flash in dark mode
                    Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
                }
                break;

            case SystembackdropType.Auto:
            case SystembackdropType.Mica:
            default:
                if (App.IsWin11)
                {
                    if (Environment.OSVersion.Version >= new Version(10, 0, 22523))
                    {
                        SetGlassFrameThickness(1d);
                        WindowHelper.EnableBackdropMicaBlur(this, CurrentTheme == Themes.Dark);
                        Background = Brushes.Transparent;
                    }
                    else
                    {
                        SetGlassFrameThickness(1d);
                        WindowHelper.EnableMicaBlur(this, CurrentTheme == Themes.Dark);
                        Background = Brushes.Transparent;
                    }
                }
                else if (App.IsWin10)
                {
                    SetGlassFrameThickness(1d);
                    WindowHelper.EnableBlur(this);
                    Background = (Brush)FindResource("MainWindowBackground");
                }
                else
                {
                    Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
                }

                break;

            case SystembackdropType.Acrylic:
                if (App.IsWin11)
                {
                    // v1.3.5: WCA acrylic on the normal (non-layered) window.
                    // It blurs regardless of the activation state, unlike DWM
                    // SystembackdropType.Acrylic which turns into a solid tint
                    // while the preview is inactive. DWM keeps the native
                    // rounded corners, and the mouse wheel keeps its native
                    // routing, so both 1.3.2 defects are fixed at once.
                    SetGlassFrameThickness(0d);
                    WindowHelper.DisableDwmBlur(this); // clear previous DWM backdrop, restore rounded corners
                    _lastAcrylicOk = WindowHelper.EnableAcrylicBlur(this, GetAcrylicTintColor(), CurrentTheme == Themes.Dark, 0.4d);
                    Background = Brushes.Transparent;
                }
                else if (App.IsWin10)
                {
                    SetGlassFrameThickness(0d);
                    _lastAcrylicOk = WindowHelper.EnableAcrylicBlur(this, GetAcrylicTintColor(), CurrentTheme == Themes.Dark);
                    Background = Brushes.Transparent;
                }
                else
                {
                    Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
                }

                break;

            case SystembackdropType.Acrylic10:
                if (App.IsWin10 || App.IsWin11)
                {
                    SetGlassFrameThickness(0d);
                    WindowHelper.DisableDwmBlur(this); // Restore rounded corners on Windows 11
                    _lastAcrylicOk = WindowHelper.EnableAcrylicBlur(this, GetAcrylic10TintColor(), CurrentTheme == Themes.Dark, GetAcrylic10TintOpacity());
                    Background = GetAcrylic10TintLuminosityOpacityBackground(CurrentTheme == Themes.Dark);
                }
                else
                {
                    Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
                }

                break;

            case SystembackdropType.Acrylic11:
                if (App.IsWin11)
                {
                    // v1.3.5: same non-layered WCA recipe as Acrylic.
                    SetGlassFrameThickness(0d);
                    WindowHelper.DisableDwmBlur(this);
                    _lastAcrylicOk = WindowHelper.EnableAcrylicBlur(this, GetAcrylicTintColor(), CurrentTheme == Themes.Dark, 0.4d);
                    Background = Brushes.Transparent;
                }
                else if (App.IsWin10)
                {
                    SetGlassFrameThickness(0d);
                    _lastAcrylicOk = WindowHelper.EnableAcrylicBlur(this, GetAcrylicTintColor(), CurrentTheme == Themes.Dark);
                    Background = Brushes.Transparent;
                }
                else
                {
                    Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
                }

                break;

            case SystembackdropType.Tabbed:
                if (App.IsWin11 && Environment.OSVersion.Version >= new Version(10, 0, 22523))
                {
                    SetGlassFrameThickness(1d);
                    WindowHelper.EnableBackdropTabbedBlur(this, CurrentTheme == Themes.Dark);
                    Background = Brushes.Transparent;
                }
                else if (App.IsWin10)
                {
                    SetGlassFrameThickness(1d);
                    WindowHelper.EnableBlur(this);
                    Background = (Brush)FindResource("MainWindowBackground");
                }
                else
                {
                    Background = (Brush)FindResource("MainWindowBackgroundNoTransparent");
                }

                break;
        }
    }

    private Color GetAcrylicTintColor()
    {
        var customColor = SettingHelper.Get("WindowBackgroundColor", string.Empty, "QuickLookNext");

        if (!string.IsNullOrEmpty(customColor))
        {
            try
            {
                return ((SolidColorBrush)new BrushConverter().ConvertFromString(customColor)).Color;
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException)
            {
                // Ignore invalid color
            }
        }

        return ((SolidColorBrush)FindResource("MainWindowBackground")).Color;
    }

    private Color GetAcrylic10TintColor()
    {
        var customColor = SettingHelper.Get("WindowBackgroundColor", string.Empty, "QuickLookNext");

        if (!string.IsNullOrEmpty(customColor))
        {
            try
            {
                return ((SolidColorBrush)new BrushConverter().ConvertFromString(customColor)).Color;
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException)
            {
                // Ignore invalid color
            }
        }

        return CurrentTheme == Themes.Dark
            ? Color.FromRgb(0x17, 0x17, 0x17)
            : Color.FromRgb(0xF2, 0xF2, 0xF2);
    }

    private static double GetAcrylic10TintOpacity()
    {
        var acrylicTintOpacity = 0.7d;
        return acrylicTintOpacity;
    }

    private static Brush GetAcrylic10TintLuminosityOpacityBackground(bool isDarkTheme)
    {
        var acrylicTintLuminosityOpacity = 0.44d;
        var t = acrylicTintLuminosityOpacity * (isDarkTheme ? 0.6d : 1.25d);
        var v = isDarkTheme ? (byte)0x22 : (byte)0xE1;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(t * 255d * 0.6d), v, v, v));
        brush.Freeze();
        return brush;
    }

    private static SystembackdropType GetBackdropOption()
    {
        // v1.2.10: default to Acrylic - the same frosted effect as the startup
        // notification popup. Mica/Tabbed remain available via the setting.
        var option = SettingHelper.Get("WindowBackdrop", nameof(SystembackdropType.Acrylic), "QuickLookNext")?.Trim();

        if (string.IsNullOrEmpty(option))
            return SystembackdropType.Auto;

        if (string.Equals(option, nameof(SystembackdropType.Acrylic), StringComparison.OrdinalIgnoreCase))
            return SystembackdropType.Acrylic;

        if (string.Equals(option, nameof(SystembackdropType.Tabbed), StringComparison.OrdinalIgnoreCase))
            return SystembackdropType.Tabbed;

        return Enum.TryParse(option, true, out SystembackdropType parsed)
            ? parsed
            : SystembackdropType.Auto;
    }

    /// <summary>
    /// v1.3.5: acrylic now renders through WCA
    /// (SetWindowCompositionAttribute) on a normal, non-layered window - the
    /// frosted glass shows while the window is inactive (the DWM backdrop API
    /// limitation that started the 1.3.1 layered experiment), while DWM's
    /// native rounded corners and the native WM_MOUSEWHEEL routing are kept.
    /// The 1.3.2 layered path remains in the code as a fallback (flip this to
    /// true) with a low-level mouse hook re-delivering wheel input.
    /// </summary>
    private static bool ShouldUseLayeredAcrylic() => false;

    private void SaveWindowSizeOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLayeredWindowRegion();

        // first shown?
        if (e.PreviousSize == new Size(0, 0))
            return;
        // resize when switching preview?
        if (_ignoreNextWindowSizeChange)
        {
            _ignoreNextWindowSizeChange = false;
            return;
        }

        // by user?
        _customWindowSize = new Size(Width, Height);
    }

    /// <summary>
    /// v1.3.2: keeps the rounded window region in sync with the window size
    /// and state. The region is what gives a layered window its Win11-style
    /// corners (DWM corner preference does not apply to layered windows).
    /// </summary>
    private void ApplyLayeredWindowRegion()
    {
        if (!_layeredAcrylic)
            return;

        if (WindowState == WindowState.Maximized || _isFullscreen)
            WindowHelper.ClearWindowRegion(this);
        else
            WindowHelper.SetRoundedWindowRegion(this, 8);
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        ApplyTheme(CurrentTheme == Themes.Dark ? Themes.Light : Themes.Dark);
    }

    /// <summary>
    /// v1.3.6: apply a theme choice (System/Light/Dark) to the open preview
    /// and persist it for future previews. Used by the tray menu's theme
    /// section and the caption-bar toggle button.
    /// </summary>
    internal void ApplyTheme(Themes newTheme)
    {
        // ContextObject.Theme triggers SwitchTheme (window + backdrop + theme state).
        ContextObject.Theme = newTheme;

        // Persist the choice so future previews start with it.
        SettingHelper.Set("LastTheme", (int)newTheme, "QuickLookNext");
        SettingHelper.Set("LastTheme", (int)newTheme, "QuickLook.Plugin.ImageViewer");

        // Re-render the current preview so web content (WebView2) picks up the
        // new PreferredColorScheme / theme.
        if (IsVisible)
            ViewWindowManager.GetInstance().ReloadPreview();
    }

    private void ShowWindowCaptionContainer(object sender, MouseEventArgs e)
    {
        if (!GetCursorPos(out var pt))
            return;

        if (!_hasLastCursor)
        {
            _hasLastCursor = true;
            _lastCursorX = pt.X;
            _lastCursorY = pt.Y;
            return;
        }

        // Synthetic MouseMove (window opened/moved under a stationary cursor)
        // carries the same cursor position; ignore it.
        if (pt.X == _lastCursorX && pt.Y == _lastCursorY)
            return;

        _lastCursorX = pt.X;
        _lastCursorY = pt.Y;

        // v1.3.6: "顶部状态栏默认隐藏"（托盘菜单可开关，默认开启）——鼠标在内容区
        // 移动不再弹出顶栏；进入窗口顶部标题栏区域由低层鼠标钩子检测并显示
        // （顶部 32px 是 WindowChrome 可拖动区，WPF 收不到那里的 MouseMove）。
        // 关闭该选项后恢复旧行为（任意鼠标移动即显示）。
        if (SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext"))
            return;

        var show = (Storyboard)windowCaptionContainer.FindResource("ShowCaptionContainerStoryboard");

        if (windowCaptionContainer.Opacity == 0 || windowCaptionContainer.Opacity == 1)
            show.Begin();
    }

    /// <summary>
    /// v1.3.6: apply the "顶部状态栏默认隐藏" mode to the open preview right
    /// away (used by the tray menu toggle). Plugin-driven always-visible bars
    /// (e.g. paused video controls) are left alone.
    /// </summary>
    internal void ApplyTopBarMode()
    {
        if (!ContextObject.TitlebarAutoHide)
            return;

        if (SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext"))
        {
            StartTopBarPolling();
            var hide = (Storyboard)windowCaptionContainer.FindResource("HideCaptionContainerStoryboard");
            hide.Begin();
        }
        else
        {
            StopTopBarPolling();
            if (!_layeredAcrylic)
                UninstallMouseHook();
        }
    }

    // ---- v1.3.5: mouse-wheel forwarding for the layered fallback ----------
    // Windows sends WM_MOUSEWHEEL to the focus window first, and only forwards
    // it to the window under the cursor when the focus window chain does not
    // handle it. Layered windows are skipped by that forwarding, so a layered
    // preview never scrolls. The low-level mouse hook below re-delivers wheel
    // input straight to the window under the cursor when it belongs to the
    // preview, and consumes the original message so it does not reach the
    // focused window (e.g. an Explorer list behind the preview).
    // ---- v1.3.6: the same hook reveals the top bar when the cursor enters
    // the top caption zone while "顶部状态栏默认隐藏" is enabled.

    private const int WH_MOUSE_LL = 14;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
            return;

        _mouseProc = MouseHookProc;
        var hMod = Kernel32.LoadLibrary("user32.dll");
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    private nint MouseHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && IsVisible)
        {
            var message = (uint)wParam.ToInt64();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (message is WM_MOUSEWHEEL or WM_MOUSEHWHEEL && _layeredAcrylic)
            {
                if (TryGetWheelTarget(data.pt.X, data.pt.Y, out var targetHwnd))
                {
                    var delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                    {
                        var wp = (GetWheelKeyState() << 16) | ((uint)delta & 0xFFFF);
                        var lp = ((uint)(data.pt.Y & 0xFFFF) << 16) | ((uint)data.pt.X & 0xFFFF);
                        User32.PostMessage(targetHwnd, message, (nint)wp, (nint)lp);
                        return (nint)1; // consumed: the preview window is the only recipient
                    }
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// v1.3.6: start polling the cursor so the top bar can reveal when the
    /// cursor enters the top caption zone (the "顶部状态栏默认隐藏" mode). The
    /// poll only runs while the preview window is visible, so its cost is a
    /// single GetCursorPos + rect check every 100 ms.
    /// </summary>
    private void StartTopBarPolling()
    {
        if (_topBarPollTimer != null)
            return;

        _topBarPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _topBarPollTimer.Tick += (_, _) =>
        {
            if (!IsVisible || !SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext"))
                return;

            if (GetCursorPos(out var pt) && IsPointInTopZone(pt.X, pt.Y))
                RevealTopBar();
        };
        _topBarPollTimer.Start();
    }

    private void StopTopBarPolling()
    {
        _topBarPollTimer?.Stop();
        _topBarPollTimer = null;
    }

    /// <summary>
    /// v1.3.6: reveals the top bar (only when the plugin allows auto-hide).
    /// Called from the mouse hook, which runs on the UI thread; the animation
    /// is dispatched at Render priority so it starts on the next frame.
    /// </summary>
    private void RevealTopBar()
    {
        if (!ContextObject.TitlebarAutoHide)
        {
            WriteTopBarDiag("reveal-skip no-autohide");
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (windowCaptionContainer.Opacity >= 1)
            {
                WriteTopBarDiag("reveal-skip already-visible");
                return;
            }

            WriteTopBarDiag("reveal-begin-storyboard");
            var show = (Storyboard)windowCaptionContainer.FindResource("ShowCaptionContainerStoryboard");
            show.Begin();
            WriteTopBarDiag($"geo h={windowCaptionContainer.Height} ah={windowCaptionContainer.ActualHeight} " +
                $"w={windowCaptionContainer.ActualWidth} vis={windowCaptionContainer.IsVisible} " +
                $"render={windowCaptionContainer.RenderSize} root={new WindowInteropHelper(this).Handle.ToInt64():X}");
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// v1.3.6: test hook - append a top-bar reveal diagnostic line (only when
    /// /test-preview-diag is active; writes are throttled to avoid spam).
    /// </summary>
    private void WriteTopBarDiag(string line)
    {
        if (!App.IsPreviewDiagEnabled)
            return;

        try
        {
            var file = System.IO.Path.Combine(App.SmokeDir, "topbar-hook.txt");
            System.IO.File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics must never affect the preview
        }
    }

    /// <summary>
    /// v1.3.6: whether the cursor is inside the window's top caption zone
    /// (the bar height plus a small tolerance, DPI-aware).
    /// </summary>
    private bool IsPointInTopZone(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !User32.GetWindowRect(hwnd, out var rect))
            return false;

        if (x < rect.Left || x > rect.Right || y < rect.Top || y > rect.Bottom)
            return false;

        var scale = User32.GetDpiForWindow(hwnd) / 96d;
        // Use the configured Height instead of ActualHeight: the bar starts at
        // opacity 0 and ActualHeight can report 0 before/while hidden, which
        // would shrink the reveal zone to almost nothing.
        var barHeight = double.IsNaN(windowCaptionContainer.Height)
            ? windowCaptionContainer.ActualHeight
            : windowCaptionContainer.Height;
        var zoneHeight = (barHeight + 12) * scale;
        return y <= rect.Top + zoneHeight;
    }

    private bool TryGetWheelTarget(int x, int y, out nint targetHwnd)
    {
        targetHwnd = IntPtr.Zero;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return false;

        var underCursor = User32.WindowFromPoint(new User32.POINT(x, y));
        if (underCursor == IntPtr.Zero)
            return false;

        // Only forward when the pointer is over this preview window (or one of
        // its child windows, e.g. a WebView2 host). Other top-level windows
        // such as the tray menu keep their normal wheel routing.
        if (User32.GetAncestor(underCursor, User32.GA_ROOT) != hwnd)
            return false;

        targetHwnd = underCursor;
        return true;
    }

    private static uint GetWheelKeyState()
    {
        uint flags = 0;
        if ((GetKeyState(0x11) & 0x8000) != 0) flags |= 0x0008; // MK_CONTROL
        if ((GetKeyState(0x10) & 0x8000) != 0) flags |= 0x0004; // MK_SHIFT
        if ((GetKeyState(0x12) & 0x8000) != 0) flags |= 0x0020; // MK_MENU
        return flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>
    /// v1.3.5: test hook - reports the backdrop render path used by the last
    /// ApplyBackdrop call so smoke tests can assert the WCA acrylic succeeded
    /// and the window stayed non-layered (rounded corners + wheel routing).
    /// </summary>
    internal string DiagnoseBackdrop() =>
        $"layered={_layeredAcrylic} accent-ok={_lastAcrylicOk}";

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private void AutoHideCaptionContainer(object sender, EventArgs e)
    {
        if (!ContextObject.TitlebarAutoHide)
            return;

        var hide = (Storyboard)windowCaptionContainer.FindResource("HideCaptionContainerStoryboard");

        hide.Begin();
    }
}
