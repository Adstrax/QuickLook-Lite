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

using QuickLook.Common.Plugin;
using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace QuickLookNext.Helpers;

/// <summary>
/// v3.42.0: prepares the preview families this user actually uses, in the
/// background, right after startup.
/// <para>
/// Measured on a warm app, the *second* preview of a family is always much faster
/// than the first: text 289 ms -> 96 ms, csv 205 -> 124, png 170 -> 100,
/// mp4 595 -> 413. The difference is one-time work - the plugin's panel XAML and
/// JIT, its native libraries, the WebView2 environment - that every user pays on
/// the first preview of that kind. This class pays it while the user is still
/// doing something else (the app starts at login), so the first preview feels like
/// the second one.
/// </para>
/// <para>
/// It is usage-driven: the families come from <see cref="PluginUsageTracker"/>,
/// so a user who never opens a video never pays for the video pipeline. Video and
/// Office are deliberately left out - preparing them means starting playback or
/// synthesising a valid OOXML document, and their panels are already the cheapest
/// to reach (the shared WebView2 pool covers Markdown/HTML/Office).
/// </para>
/// <para>
/// Cost, measured on the tray process with nothing previewed: warming text and
/// image adds ~23 MB private / ~36 MB working set over a cold session; adding a
/// WebView2 family (Markdown/HTML) doubles that. The families are therefore capped
/// (<c>WarmUpFamilyCount</c>, default 2) and the whole thing can be switched off
/// with <c>WarmUpPreviewFamilies=false</c>.
/// </para>
/// </summary>
internal static class PreviewWarmUp
{
    private const string EnabledSetting = "WarmUpPreviewFamilies";
    private const string CountSetting = "WarmUpFamilyCount";

    /// <summary>
    /// Families prepared per session when the setting is not set. Two, because the
    /// cost is not the same for every family: measured idle memory of the tray
    /// process with the warm-up off / one family (text) / two (text + image) / three
    /// (text + image + Markdown) was 131 / 148 / 154 / 168-182 MB private. Text
    /// costs ~17 MB, image ~6 MB, and the WebView2 families (Markdown/HTML/Office)
    /// add another ~15-25 MB because they leave a Chromium environment behind - that
    /// one is worth paying only for users who actually open those formats.
    /// </summary>
    private const int DefaultCount = 2;

    /// <summary>Wait before the first family, so startup finishes cleanly.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(3);

    /// <summary>Pause between two families, so input is never held up for long.</summary>
    private static readonly TimeSpan BetweenFamilies = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a single family may take. The panels load their content
    /// asynchronously (that async load is exactly what makes the first preview of a
    /// family slow), so the warm-up has to wait for it instead of throwing the panel
    /// away mid-load.
    /// </summary>
    private static readonly TimeSpan FamilyTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The plugin -> sample extension map. Only families whose sample can be built
    /// cheaply and safely belong here: video/audio would start playback, Office
    /// needs a real document.
    /// </summary>
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QuickLook.Plugin.TextViewer"] = ".txt",
        ["QuickLook.Plugin.MarkdownViewer"] = ".md",
        ["QuickLook.Plugin.HtmlViewer"] = ".html",
        ["QuickLook.Plugin.CsvViewer"] = ".csv",
        ["QuickLook.Plugin.ImageViewer"] = ".png",
        ["QuickLook.Plugin.PDFViewer"] = ".pdf",
        ["QuickLook.Plugin.ArchiveViewer"] = ".zip",
        ["QuickLook.Plugin.FontViewer"] = ".ttf",
    };

    /// <summary>
    /// Used when the usage history is too short to choose from (a fresh install, or
    /// a user whose history was just reset): the formats that make up most previews.
    /// </summary>
    private static readonly string[] DefaultOrder =
    [
        "QuickLook.Plugin.TextViewer",
        "QuickLook.Plugin.ImageViewer",
        "QuickLook.Plugin.MarkdownViewer",
        "QuickLook.Plugin.PDFViewer",
        "QuickLook.Plugin.CsvViewer",
        "QuickLook.Plugin.HtmlViewer",
    ];

    private static readonly string[] _warmed = new string[8];
    private static int _warmedCount;
    private static bool _started;

    internal static void Start()
    {
        if (_started)
            return;

        _started = true;

        if (!SettingHelper.Get(EnabledSetting, true, "QuickLookNext"))
            return;

        var count = Math.Clamp(
            SettingHelper.Get(CountSetting, DefaultCount, "QuickLookNext"), 0, 8);
        if (count == 0)
            return;

        var families = ChooseFamilies(count);
        if (families.Count == 0)
            return;

        // Samples of the previous session: dropped here, while nothing can be
        // reading them yet (see WarmUpSamples).
        WarmUpSamples.ResetFolder();

        // ApplicationIdle: the work starts only once the UI thread has nothing else
        // to do, and a preview request always wins the race for the dispatcher. The
        // loop itself awaits, so the UI thread stays free while a panel loads.
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                await Task.Delay(InitialDelay);

                foreach (var family in families)
                {
                    try
                    {
                        await WarmOneAsync(family);
                    }
                    catch
                    {
                        // Warming up is best effort; a preview must never be affected.
                    }

                    await Task.Delay(BetweenFamilies);
                }

                WriteDiagnostics();
            }),
            DispatcherPriority.ApplicationIdle);
    }

    private static List<string> ChooseFamilies(int count)
    {
        var chosen = new List<string>();

        foreach (var plugin in PluginUsageTracker.GetMostUsed(count))
        {
            if (Families.ContainsKey(plugin) && !chosen.Contains(plugin))
                chosen.Add(plugin);
        }

        // Top up with the common formats: a fresh install has no history, and a
        // user with two favourite formats should still get the third slot used.
        foreach (var plugin in DefaultOrder)
        {
            if (chosen.Count >= count)
                break;

            if (!chosen.Contains(plugin))
                chosen.Add(plugin);
        }

        return chosen.Take(count).ToList();
    }

    private static async Task WarmOneAsync(string pluginName)
    {
        var host = ViewWindowManager.GetInstance().CurrentViewerWindow;
        if (host == null)
            return;

        var manager = PluginManager.GetInstance();
        var plugin = manager.LoadedPlugins.FirstOrDefault(p =>
                         string.Equals(p.GetType().Assembly.GetName().Name, pluginName,
                             StringComparison.OrdinalIgnoreCase))
                     ?? manager.LoadPluginByName(pluginName);

        if (plugin == null)
            return;

        manager.EnsurePluginReady(plugin);

        var sample = WarmUpSamples.Create(Families[pluginName]);
        if (sample == null)
            return;

        // A throwaway context: the panel is built and thrown away, so nothing is
        // ever attached to (or shown in) the real preview window.
        var context = new ContextObject { Source = host };

        try
        {
            plugin.Prepare(sample, context);
            plugin.View(sample, context);

            // Let the panel finish its asynchronous load - that is the work the
            // first real preview would otherwise have to wait for.
            var deadline = DateTime.UtcNow + FamilyTimeout;
            while (context.IsBusy && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            // A short grace period: several panels report "ready" and only then
            // finish reading the file in a trailing background task.
            await Task.Delay(200);

            if (_warmedCount < _warmed.Length)
                _warmed[_warmedCount++] = pluginName;
        }
        finally
        {
            try
            {
                if (context.ViewerContent is IDisposable disposable)
                    disposable.Dispose();
            }
            catch
            {
                // Best effort.
            }

            context.ViewerContent = null;
            context.PendingViewerContent = null;

            try
            {
                plugin.Cleanup();
            }
            catch
            {
                // Best effort.
            }

        }
    }

    /// <summary>
    /// Test hook: what the warm-up prepared this session, so the smoke test can
    /// assert the background preparation really ran.
    /// </summary>
    private static void WriteDiagnostics()
    {
        try
        {
            var dir = App.SmokeDir;
            if (string.IsNullOrEmpty(dir))
                return;

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "warmup.txt"),
                string.Join(",", _warmed.Take(_warmedCount)) + Environment.NewLine);
        }
        catch
        {
            // Diagnostics only.
        }
    }
}
