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

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v3.34.0: keeps a warm WebView2 control so a preview does not have to create a
/// brand new Chromium controller every time.
/// <para>
/// Measured before this pool: every web based preview paid ~300-400 ms just for
/// controller creation (.ttf previews took 465 ms consistently, twice in a row),
/// because each panel created - and disposed - its own WebView2. Reusing the
/// controller removes that cost, while the existing idle recycler still shuts the
/// Chromium process group down after <c>WebView2IdleTimeoutSeconds</c> of no web
/// previews, so idle memory is unchanged.
/// </para>
/// <para>
/// Everything here must run on the UI thread: <see cref="WebView2"/> is a
/// DispatcherObject and <c>CoreWebView2</c> can only be touched from it.
/// </para>
/// </summary>
public static class WebView2ControlPool
{
    /// <summary>One warm control is enough for sequential previews; two covers a
    /// quick switch that overlaps the previous panel's teardown.</summary>
    private const int MaxIdle = 2;

    private static readonly object Sync = new();
    private static readonly List<(WebView2 Control, string Arguments)> Idle = [];

    // v3.39.0: parked controls need a live HWND. A WebView2 whose parent window
    // disappears ends up in a state where the next controller creation fails with
    // 0x8007139F, so parked controls wait in this off-screen window instead of
    // being left parentless.
    private static Window _parkingWindow;
    private static Grid _parkingGrid;

    public static int IdleCount
    {
        get
        {
            lock (Sync)
                return Idle.Count;
        }
    }

    /// <summary>
    /// Returns a warm control when one is available, otherwise a fresh one. The
    /// caller is responsible for calling <see cref="Release"/> when done.
    /// </summary>
    /// <param name="browserArguments">
    /// Extra Chromium switches the control must have been created with (the
    /// image panels ask for force-dark, for example). Controls are pooled per
    /// argument set, so a preview never inherits another panel's switches.
    /// </param>
    public static WebView2 Acquire(string browserArguments = null)
    {
        var wanted = browserArguments ?? string.Empty;

        lock (Sync)
        {
            for (var i = Idle.Count - 1; i >= 0; i--)
            {
                var candidate = Idle[i];

                if (candidate.Control == null)
                {
                    Idle.RemoveAt(i);
                    continue;
                }

                // Leave controls made with other switches in the pool: a later
                // preview with those switches can still use them.
                if (!string.Equals(candidate.Arguments, wanted, StringComparison.Ordinal))
                    continue;

                Idle.RemoveAt(i);

                if (IsWarm(candidate.Control))
                {
                    DetachFromParent(candidate.Control);
                    return candidate.Control;
                }

                DisposeSafely(candidate.Control);
            }
        }

        return CreateControl(browserArguments);
    }

    /// <summary>
    /// Parks a control for reuse, or disposes it when it cannot be reused (no
    /// controller, or the pool is full).
    /// </summary>
    public static void Release(WebView2 control)
    {
        if (control == null)
            return;

        var arguments = GetBrowserArguments(control);

        if (!IsWarm(control) || !TryPark(control))
        {
            DisposeSafely(control);
            return;
        }

        lock (Sync)
        {
            if (Idle.Count < MaxIdle && !Idle.Exists(entry => ReferenceEquals(entry.Control, control)))
            {
                Idle.Add((control, arguments));
                return;
            }
        }

        DisposeSafely(control);
    }

    /// <summary>
    /// Drops every parked control. Called by the idle recycler right before it
    /// reaps the browser processes, so the pool never hands out a control whose
    /// Chromium side has been killed.
    /// </summary>
    public static void ClearIdle()
    {
        List<WebView2> parked;

        lock (Sync)
        {
            parked = [];
            foreach (var entry in Idle)
                parked.Add(entry.Control);

            Idle.Clear();
        }

        foreach (var control in parked)
            DisposeSafely(control);
    }

    private static WebView2 CreateControl(string browserArguments)
    {
        var creation = new CoreWebView2CreationProperties
        {
            // v3.36.0: the profile folder is chosen by WebView2EnvironmentProvider so
            // a broken profile can be abandoned after a failed initialization.
            UserDataFolder = WebView2EnvironmentProvider.UserDataFolder,
        };

        if (!string.IsNullOrEmpty(browserArguments))
            creation.AdditionalBrowserArguments = browserArguments;

        return new WebView2
        {
            CreationProperties = creation,

            // Transparent background so the window's Mica/Acrylic backdrop shows
            // through the web content (both light and dark themes).
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0),
        };
    }

    private static string GetBrowserArguments(WebView2 control)
    {
        try
        {
            return control.CreationProperties?.AdditionalBrowserArguments ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>UI thread only - <c>CoreWebView2</c> is not thread safe.</summary>
    private static bool IsWarm(WebView2 control)
    {
        try
        {
            return control.CoreWebView2 != null;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebView2ControlPool] {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Detaches the control from the panel that is going away and blanks the page,
    /// so a reused control never flashes the previous document and does not keep
    /// loading the old page's resources in the background.
    /// </summary>
    private static bool TryPark(WebView2 control)
    {
        try
        {
            DetachFromParent(control);

            var core = control.CoreWebView2;
            if (core != null)
            {
                // Host objects are registered per panel ("external" is the name the
                // image panels use); re-adding the same name on reuse would fail.
                try { core.RemoveHostObjectFromScript("external"); } catch { /* not registered */ }
            }

            if (control.Source != null)
                control.Source = new Uri("about:blank");

            // Keep the control inside a real (off-screen) window so its controller
            // survives the time it spends in the pool.
            ParkingGrid().Children.Add(control);

            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebView2ControlPool] park failed: {e.Message}");
            return false;
        }
    }

    private static Grid ParkingGrid()
    {
        if (_parkingWindow is { IsLoaded: true } && _parkingGrid != null)
            return _parkingGrid;

        _parkingGrid = new Grid();
        _parkingWindow = new Window
        {
            Content = _parkingGrid,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
        };

        _parkingWindow.Show();

        return _parkingGrid;
    }

    private static void DetachFromParent(WebView2 control)
    {
        switch (LogicalTreeHelper.GetParent(control))
        {
            case UserControl userControl when ReferenceEquals(userControl.Content, control):
                userControl.Content = null;
                break;

            case Border border when ReferenceEquals(border.Child, control):
                border.Child = null;
                break;

            case ContentControl contentControl when ReferenceEquals(contentControl.Content, control):
                contentControl.Content = null;
                break;

            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;

            case Panel panel when panel.Children.Contains(control):
                panel.Children.Remove(control);
                break;
        }
    }

    private static void DisposeSafely(WebView2 control)
    {
        if (control == null)
            return;

        try
        {
            WebView2Lifecycle.Unregister(control);
        }
        catch
        {
            // best effort
        }

        try
        {
            DetachFromParent(control);
            control.Dispose();
        }
        catch
        {
            // best effort
        }
    }
}
