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

using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using QuickLook.Plugin.Shared.NativeMethods;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v3.29.0: stops the WebView2 (Chromium) process group from lingering after
/// the last web-based preview closes. Panels register their WebView2 control
/// on creation and unregister on dispose; once no control is active, an idle
/// timer (<c>WebView2IdleTimeoutSeconds</c>, default 300, 0 disables) closes
/// any still-registered controls and reaps leftover msedgewebview2.exe
/// processes that belong to this app's WebView2 data folder. The reaping only
/// matches our own user-data-dir, so the user's Edge or other WebView2 apps
/// are never touched.
/// </summary>
public static class WebView2Lifecycle
{
    static WebView2Lifecycle()
    {
        // v3.40.0: close WebView2 on the way out. A process that disappears while
        // Chromium is still running leaves singleton lock markers in the profile,
        // and those are what used to force the profile to be rotated.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownAll();
    }

    private static readonly object Sync = new();
    private static readonly HashSet<WebView2> Active = [];

    private static Timer _idleTimer;

    public static int ActiveCount
    {
        get
        {
            lock (Sync)
                return Active.Count;
        }
    }

    /// <summary>
    /// v3.36.0: recovery for a controller that refuses to initialize.
    /// <para>
    /// A session that was killed while Chromium was running can leave state behind
    /// that makes <c>CoreWebView2Environment.CreateCoreWebView2ControllerAsync</c>
    /// fail with 0x8007139F ("the group or resource is not in the correct state"),
    /// which showed up as a blank font/markdown preview that stalled for the whole
    /// 1.5 s font timeout. Reaping our own WebView2 process group (and dropping
    /// pooled controls first) makes the next attempt start from a clean browser.
    /// </para>
    /// </summary>
    public static void RecoverFromFailedInitialization(bool aggressive = false)
    {
        try
        {
            WebView2ControlPool.ClearIdle();
        }
        catch
        {
            // best effort
        }

        try
        {
            ReapBrowserProcesses();
        }
        catch
        {
            // best effort
        }

        // v3.40.0: first try to *repair* the profile in use - the usual cause is a
        // stale Chromium lock left behind by a session that was killed, and removing
        // those markers brings the same profile back without creating another one.
        if (!aggressive &&
            WebView2EnvironmentProvider.TryRepairProfile(WebView2EnvironmentProvider.CurrentProfileFolder))
            return;

        // The repair did not bring the profile back (or the second attempt failed as
        // well). Only then move on to a fresh profile - the maintenance pass removes
        // the abandoned folder again, so this stays a rare, bounded fallback.
        try
        {
            WebView2EnvironmentProvider.RotateAfterFailure();
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// v3.40.0: whether a running WebView2 browser (of ours) has the given profile
    /// folder open. Matching the folder in the command line is the same trick the
    /// idle reaper uses, so both agree on what "ours" means.
    /// </summary>
    public static bool IsProfileHeldByBrowser(string profileFolder)
    {
        if (string.IsNullOrEmpty(profileFolder))
            return false;

        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    var commandLine = NativeMethods.ProcessCommandLineReader.GetCommandLine(process.Id);
                    if (commandLine != null &&
                        commandLine.IndexOf(profileFolder, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Cannot tell: assume it is free (the caller only repairs lock markers).
        }

        return false;
    }

    /// <summary>
    /// v3.40.0: shuts every WebView2 control down before the process exits, so
    /// Chromium gets a chance to close cleanly and does not leave the stale lock
    /// markers described in
    /// <see cref="WebView2EnvironmentProvider.TryRepairProfile"/> behind.
    /// </summary>
    public static void ShutdownAll()
    {
        List<WebView2> controls;

        lock (Sync)
        {
            controls = [.. Active];
            Active.Clear();
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        foreach (var webView in controls)
        {
            try
            {
                webView.Dispose();
            }
            catch
            {
                // best effort
            }
        }

        WebView2ControlPool.ClearIdle();

        // Give Chromium a moment to exit on its own before the process disappears.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Process.GetProcessesByName("msedgewebview2").Length == 0)
                return;

            Thread.Sleep(100);
        }
    }

    public static void Register(WebView2 webView)
    {
        if (webView is null)
            return;

        lock (Sync)
        {
            Active.Add(webView);

            // Any activity cancels a pending recycle.
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    public static void Unregister(WebView2 webView)
    {
        if (webView is null)
            return;

        lock (Sync)
        {
            Active.Remove(webView);
            if (Active.Count == 0)
                ArmIdleLocked();
        }
    }

    private static void ArmIdleLocked()
    {
        var timeoutSeconds = SettingHelper.Get("WebView2IdleTimeoutSeconds", 300, "QuickLookNext");
        if (timeoutSeconds <= 0)
            return;

        _idleTimer?.Dispose();
        _idleTimer = new Timer(
            static _ => OnIdle(),
            null,
            TimeSpan.FromSeconds(timeoutSeconds),
            Timeout.InfiniteTimeSpan);
    }

    private static void OnIdle()
    {
        // v3.34.0: drop the pooled controls *before* reaping the browser
        // processes, so the pool never hands out a controller whose Chromium side
        // has just been killed.
        try
        {
            WebView2ControlPool.ClearIdle();
        }
        catch
        {
            // best effort
        }

        List<WebView2> leftovers;
        lock (Sync)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;

            // A new preview started while the timer was pending; skip.
            if (Active.Count > 0)
                return;

            leftovers = [.. Active];
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => CloseLeftovers(leftovers));
            }
            else
            {
                CloseLeftovers(leftovers);
            }
        }
        catch
        {
            // Shutdown races; the process reaper below is the hard guarantee.
        }

        ReapBrowserProcesses();

        // v3.40.0: the browser is down now, so this is both the cheapest and the
        // safest moment to drop the profile folders abandoned by earlier failures.
        WebView2EnvironmentProvider.CleanupLeftoverProfiles();
    }

    private static void CloseLeftovers(List<WebView2> leftovers)
    {
        foreach (var webView in leftovers)
        {
            try
            {
                // The WPF control's Dispose closes the underlying controller.
                webView.Dispose();
            }
            catch
            {
                // Best effort; the process reaper is the hard guarantee.
            }
        }
    }

    private static void ReapBrowserProcesses()
    {
        var dataDir = Path.Combine(SettingHelper.LocalDataPath, @"WebView2_Data");
        if (!Directory.Exists(dataDir))
            return;

        foreach (var process in Process.GetProcessesByName("msedgewebview2"))
        {
            try
            {
                var commandLine = ProcessCommandLineReader.GetCommandLine(process.Id);
                if (commandLine is not null &&
                    commandLine.IndexOf(dataDir, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ProcessHelper.WriteLog(
                        $"[WebView2Lifecycle] Reaping idle WebView2 process {process.Id} ({dataDir})");
                    process.Kill();
                }
            }
            catch
            {
                // The process may have exited between enumeration and kill.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
