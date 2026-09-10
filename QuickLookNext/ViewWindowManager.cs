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
using QuickLook.Common.Plugin;
using QuickLookNext.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace QuickLookNext;

public class ViewWindowManager : IDisposable
{
    private static ViewWindowManager _instance;

    private string _invokedPath = string.Empty;
    private ViewerWindow _viewerWindow;

    internal ViewWindowManager()
    {
        // Creating the WPF preview window at startup costs UI-thread time that
        // delays the tray icon and keyboard hook. Create it as soon as the
        // dispatcher is idle instead; a preview request that arrives before
        // then creates it on demand (EnsureViewerWindow).
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            // A preview request may have created the window on demand already;
            // do not create a second orphaned instance.
            if (_viewerWindow == null)
                InitNewViewerWindow();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    internal ViewerWindow CurrentViewerWindow => _viewerWindow;

    private ViewerWindow EnsureViewerWindow()
    {
        if (_viewerWindow == null)
            InitNewViewerWindow();

        return _viewerWindow;
    }

    public void Dispose()
    {
        StopFocusMonitor();
    }

    public void RunAndClosePreview()
    {
        if (string.IsNullOrEmpty(_invokedPath))
            return;

        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        // if the current focus is in Desktop or explorer windows, just close the preview window and leave the task to System.
        var focus = NativeMethods.QuickLookNext.GetFocusedWindowType();
        if (focus != NativeMethods.QuickLookNext.FocusedWindowType.Invalid)
        {
            StopFocusMonitor();
            window.Close();
            return;
        }

        // if the focus is in the preview window, run it
        if (!WindowHelper.IsForegroundWindowBelongToSelf())
            return;

        StopFocusMonitor();
        window.RunAndClose();
    }

    public void ClosePreview()
    {
        if (string.IsNullOrEmpty(_invokedPath))
            return;

        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        StopFocusMonitor();
        window.Close();
    }

    public void TogglePreview(string path = null, string options = null)
    {
        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLookNext.GetCurrentSelection();

        if (!string.IsNullOrEmpty(options))
            InvokePreviewWithOption(path, options);
        else
        {
            // v1.2.36: the warm-shown preview window is always "visible";
            // decide open/close from the tracked preview state instead of
            // window visibility.
            var hasActivePreview = !string.IsNullOrEmpty(_invokedPath)
                && (string.IsNullOrEmpty(path) || path == _invokedPath);
            if (hasActivePreview)
                ClosePreview();
            else
                InvokePreview(path);
        }
    }

    private void RunFocusMonitor()
    {
        // v1.2.14 test hook: automated benches can disable the selection-follow
        // polling (it otherwise reacts to Explorer's current selection).
        if (App.DisableFocusMonitor)
            return;

        FocusMonitor.GetInstance().Start();
    }

    private void StopFocusMonitor()
    {
        FocusMonitor.GetInstance().Stop();
    }

    internal void ForgetCurrentWindow()
    {
        StopFocusMonitor();

        EnsureViewerWindow().Pinned = true;

        InitNewViewerWindow();
    }

    public void SwitchPreview(string path = null)
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLookNext.GetCurrentSelection();

        if (string.IsNullOrEmpty(path))
            return;

        InvokePreview(path);
    }

    public void InvokePreviewWithOption(string path = null, string options = null)
    {
        InvokePreview(path);

        if (string.IsNullOrWhiteSpace(options)) return;

        var cli = new CommandLineParser(options.Split(','));

        if (cli.Has("top"))
        {
            var window = EnsureViewerWindow();
            window.Topmost = true;
            window.buttonTop.Tag = "Top";
        }
        if (cli.Has("pin"))
        {
            EnsureViewerWindow().Pinned = true;
            ForgetCurrentWindow();
        }
    }

    public void InvokePreview(string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLookNext.GetCurrentSelection();

        if (string.IsNullOrEmpty(path))
            return;

        var window = EnsureViewerWindow();
        if (window.IsVisible && path == _invokedPath)
            return;

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            if (!path.StartsWith("::")) // CLSID
                return;

        // Check extension filtering before proceeding (skip for directories)
        if (!isDirectory && !ExtensionFilterHelper.IsExtensionAllowed(path))
            return;

        _invokedPath = path;

        RunFocusMonitor();

        // v3.31.0: matching can mean loading plugin assemblies (the first
        // preview of a format handled by a rarely-used built-in). FindMatchAsync
        // moves that work to the thread pool; the common case still completes
        // synchronously, so nothing about a normal preview changes.
        _ = BeginShowAsync(path);
    }

    /// <summary>
    /// v3.31.0: resolves the plugin for <paramref name="path"/> and shows the
    /// preview. A request that was superseded while the plugin search ran is
    /// dropped, and a failing search is reported like a failing plugin.
    /// </summary>
    private async Task BeginShowAsync(string path)
    {
        IViewer matchedPlugin;

        try
        {
            matchedPlugin = await PluginManager.GetInstance().FindMatchAsync(path).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            CurrentPluginFailed(path, ExceptionDispatchInfo.Capture(e));
            return;
        }

        // A newer preview request (or a close) superseded this one while the
        // search was running on the thread pool.
        if (_invokedPath != path)
            return;

        BeginShowNewWindow(path, matchedPlugin);
    }

    public void InvokePluginPreview(string plugin, string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = _invokedPath;

        if (string.IsNullOrEmpty(path))
            return;

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return;

        // Check extension filtering before proceeding (skip for directories)
        if (!isDirectory && !ExtensionFilterHelper.IsExtensionAllowed(path))
            return;

        RunFocusMonitor();

        var pluginManager = PluginManager.GetInstance();
        pluginManager.EnsureLoaded();

        var matchedPlugin = pluginManager.LoadedPlugins.Find(p =>
        {
            return p.GetType().Assembly.GetName().Name == plugin;
        });

        // v3.4.0: rare built-ins (e.g. MediaInfoViewer) are lazy-loaded; a
        // More-menu action can still target them by assembly name.
        matchedPlugin ??= pluginManager.LoadPluginByName(plugin);

        if (matchedPlugin != null)
        {
            pluginManager.EnsurePluginReady(matchedPlugin);
            BeginShowNewWindow(path, matchedPlugin);
        }
    }

    public void ReloadPreview()
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible || string.IsNullOrEmpty(_invokedPath))
            return;

        _ = BeginShowAsync(_invokedPath);
    }

    public void ToggleFullscreen()
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        window.ToggleFullscreen();
    }

    private void BeginShowNewWindow(string path, IViewer matchedPlugin)
    {
        EnsureViewerWindow().UnloadPlugin();

        _viewerWindow.BeginShow(matchedPlugin, path, CurrentPluginFailed);
    }

    private void CurrentPluginFailed(string path, ExceptionDispatchInfo e)
    {
        var plugin = _viewerWindow.Plugin?.GetType();

        _viewerWindow.Close();

        TrayIconManager.ShowNotification($"Failed to preview {Path.GetFileName(path)}",
            "Consider reporting this incident to QuickLookNext’s author.", true);

        Debug.WriteLine(e.SourceException.ToString());

        ProcessHelper.WriteLog(e.SourceException.ToString());

        if (plugin != PluginManager.GetInstance().DefaultPlugin.GetType())
            BeginShowNewWindow(path, PluginManager.GetInstance().DefaultPlugin);
        else
            e.Throw();
    }

private void InitNewViewerWindow()
{
    _viewerWindow = new ViewerWindow();
    _viewerWindow.Closed += (sender, e) =>
        {
            if (ProcessHelper.IsShuttingDown())
                return;
            if (sender is not ViewerWindow w)
                return;
            // Only skip if the window was already forgotten by ForgetCurrentWindow,
            // which sets Pinned=true AND replaces _viewerWindow with a new instance.
            if (w.Pinned && _viewerWindow != w)
                return;
        // v1.2.36: the warm-shown preview window is always "visible", so the
        // toggle state must be tracked via _invokedPath; reset it when the
        // window closes so the next Space reopens the preview instead of
        // "closing" the off-screen warm window again.
        _invokedPath = string.Empty;
        StopFocusMonitor();
        InitNewViewerWindow();
    };

    // v1.2.36: warm up the first Show during startup idle, off-screen, so the
    // first preview appears instantly instead of waiting ~200 ms.
    _viewerWindow.WarmUp();
}

    public static ViewWindowManager GetInstance()
    {
        return _instance ??= new ViewWindowManager();
    }
}
