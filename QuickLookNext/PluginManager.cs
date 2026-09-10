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
using QuickLook.Common.Plugin;
using QuickLook.Plugin.InfoPanel;
using QuickLookNext.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;
using UnblockZoneIdentifier;

namespace QuickLookNext;

public sealed class PluginManager
{
    private static PluginManager _instance;
    private readonly Task _loadTask;
    // v3.3.0: phase-1 gate - previews only wait until plugin ASSEMBLIES are
    // discovered; per-plugin Init continues in the background and the matched
    // plugin's Init is ensured on demand (EnsurePluginReady).
    private readonly ManualResetEventSlim _loadGate = new(false);
    private readonly ConcurrentDictionary<IViewer, InitState> _initStates = new();
    // v3.4.0: rarely-used built-in plugins are NOT loaded at startup. They are
    // loaded on demand when a preview request reaches MatchLazyPlugin (or a
    // More-menu action asks for them by name), which keeps their assemblies
    // and any natives their static ctors touch out of the resident process.
    private static readonly HashSet<string> LazyBuiltinPluginNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "QuickLook.Plugin.AppViewer",
        "QuickLook.Plugin.BinaryViewer",
        "QuickLook.Plugin.CertViewer",
        "QuickLook.Plugin.ChmViewer",
        "QuickLook.Plugin.CLSIDViewer",
        "QuickLook.Plugin.DbViewer",
        "QuickLook.Plugin.DumpViewer",
        "QuickLook.Plugin.ELFViewer",
        "QuickLook.Plugin.FontViewer",
        "QuickLook.Plugin.HelixViewer",
        "QuickLook.Plugin.MailViewer",
        // v3.29.0: Markdown / Office are uncommon in the PDF-first daily flow.
        // Keep their assemblies (mermaid / MathJax resources, OOXML parser,
        // WebView2 dependency chain) out of the resident process until the
        // first preview actually needs them.
        "QuickLook.Plugin.MarkdownViewer",
        "QuickLook.Plugin.MediaInfoViewer",
        "QuickLook.Plugin.OfficeViewer",
        "QuickLook.Plugin.PEViewer",
        "QuickLook.Plugin.PluginInstaller",
        "QuickLook.Plugin.PrefetchViewer",
        "QuickLook.Plugin.ThumbnailViewer",
    };

    // v3.10.0: extension -> lazy built-in plugins that declare it, ordered by
    // (priority desc). Lets the first rare-format preview load only the plugin
    // that can actually handle the file instead of all 15 pending ones.
    // Mirrors each plugin's own extension list; .bin/.dylib overlaps are
    // ordered by priority (ELFViewer 11 > BinaryViewer -10).
    private static readonly Dictionary<string, string[]> LazyExtensionHints = new(StringComparer.OrdinalIgnoreCase)
    {
        [".qlplugin"] = ["QuickLook.Plugin.PluginInstaller"],
        [".chm"] = ["QuickLook.Plugin.ChmViewer"],
        [".pf"] = ["QuickLook.Plugin.PrefetchViewer"],
        [".eml"] = ["QuickLook.Plugin.MailViewer"],
        [".msg"] = ["QuickLook.Plugin.MailViewer"],
        [".db"] = ["QuickLook.Plugin.DbViewer"],
        [".db3"] = ["QuickLook.Plugin.DbViewer"],
        [".lite"] = ["QuickLook.Plugin.DbViewer"],
        [".litedb"] = ["QuickLook.Plugin.DbViewer"],
        [".sdb"] = ["QuickLook.Plugin.DbViewer"],
        [".sqlite"] = ["QuickLook.Plugin.DbViewer"],
        [".sqlite3"] = ["QuickLook.Plugin.DbViewer"],
        [".dmp"] = ["QuickLook.Plugin.DumpViewer"],
        [".dump"] = ["QuickLook.Plugin.DumpViewer"],
        [".hdmp"] = ["QuickLook.Plugin.DumpViewer"],
        [".mdmp"] = ["QuickLook.Plugin.DumpViewer"],
        [".minidump"] = ["QuickLook.Plugin.DumpViewer"],
        [".bin"] = ["QuickLook.Plugin.ELFViewer", "QuickLook.Plugin.BinaryViewer"],
        [".hex"] = ["QuickLook.Plugin.BinaryViewer"],
        [".elf"] = ["QuickLook.Plugin.ELFViewer"],
        [".axf"] = ["QuickLook.Plugin.ELFViewer"],
        [".ko"] = ["QuickLook.Plugin.ELFViewer"],
        [".mod"] = ["QuickLook.Plugin.ELFViewer"],
        [".o"] = ["QuickLook.Plugin.ELFViewer"],
        [".out"] = ["QuickLook.Plugin.ELFViewer"],
        [".prx"] = ["QuickLook.Plugin.ELFViewer"],
        [".puff"] = ["QuickLook.Plugin.ELFViewer"],
        [".so"] = ["QuickLook.Plugin.ELFViewer"],
        [".dylib"] = ["QuickLook.Plugin.ELFViewer"],
        [".uimage"] = ["QuickLook.Plugin.ELFViewer"],
        [".3ds"] = ["QuickLook.Plugin.HelixViewer"],
        [".3mf"] = ["QuickLook.Plugin.HelixViewer"],
        [".blend"] = ["QuickLook.Plugin.HelixViewer"],
        [".dae"] = ["QuickLook.Plugin.HelixViewer"],
        [".dxf"] = ["QuickLook.Plugin.HelixViewer"],
        [".fbx"] = ["QuickLook.Plugin.HelixViewer"],
        [".glb"] = ["QuickLook.Plugin.HelixViewer"],
        [".gltf"] = ["QuickLook.Plugin.HelixViewer"],
        [".lwo"] = ["QuickLook.Plugin.HelixViewer"],
        [".obj"] = ["QuickLook.Plugin.HelixViewer"],
        [".pcd"] = ["QuickLook.Plugin.HelixViewer"],
        [".ply"] = ["QuickLook.Plugin.HelixViewer"],
        [".pmx"] = ["QuickLook.Plugin.HelixViewer"],
        [".stl"] = ["QuickLook.Plugin.HelixViewer"],
        [".cdr"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".fig"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".kra"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".pdn"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".pip"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".pix"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".sketch"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".xd"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".xmind"] = ["QuickLook.Plugin.ThumbnailViewer"],
        [".ttf"] = ["QuickLook.Plugin.FontViewer"],
        [".otf"] = ["QuickLook.Plugin.FontViewer"],
        [".woff"] = ["QuickLook.Plugin.FontViewer"],
        [".woff2"] = ["QuickLook.Plugin.FontViewer"],
        [".ttc"] = ["QuickLook.Plugin.FontViewer"],
        [".apk"] = ["QuickLook.Plugin.AppViewer"],
        [".aab"] = ["QuickLook.Plugin.AppViewer"],
        [".aar"] = ["QuickLook.Plugin.AppViewer"],
        [".appx"] = ["QuickLook.Plugin.AppViewer"],
        [".appxbundle"] = ["QuickLook.Plugin.AppViewer"],
        [".appimage"] = ["QuickLook.Plugin.AppViewer"],
        [".ddeb"] = ["QuickLook.Plugin.AppViewer"],
        [".deb"] = ["QuickLook.Plugin.AppViewer"],
        [".dmg"] = ["QuickLook.Plugin.AppViewer"],
        [".hap"] = ["QuickLook.Plugin.AppViewer"],
        [".har"] = ["QuickLook.Plugin.AppViewer"],
        [".ipa"] = ["QuickLook.Plugin.AppViewer"],
        [".msi"] = ["QuickLook.Plugin.AppViewer"],
        [".msix"] = ["QuickLook.Plugin.AppViewer"],
        [".msixbundle"] = ["QuickLook.Plugin.AppViewer"],
        [".msp"] = ["QuickLook.Plugin.AppViewer"],
        [".nupkg"] = ["QuickLook.Plugin.AppViewer"],
        [".rpm"] = ["QuickLook.Plugin.AppViewer"],
        [".snupkg"] = ["QuickLook.Plugin.AppViewer"],
        [".wgt"] = ["QuickLook.Plugin.AppViewer"],
        [".wgtu"] = ["QuickLook.Plugin.AppViewer"],
        [".cer"] = ["QuickLook.Plugin.CertViewer"],
        [".certSigningRequest"] = ["QuickLook.Plugin.CertViewer"],
        [".crt"] = ["QuickLook.Plugin.CertViewer"],
        [".csr"] = ["QuickLook.Plugin.CertViewer"],
        [".keystore"] = ["QuickLook.Plugin.CertViewer"],
        [".mobileprovision"] = ["QuickLook.Plugin.CertViewer"],
        [".p12"] = ["QuickLook.Plugin.CertViewer"],
        [".p7s"] = ["QuickLook.Plugin.CertViewer"],
        [".pem"] = ["QuickLook.Plugin.CertViewer"],
        [".pfx"] = ["QuickLook.Plugin.CertViewer"],
        [".pkcs7"] = ["QuickLook.Plugin.CertViewer"],
        [".pvk"] = ["QuickLook.Plugin.CertViewer"],
        [".snk"] = ["QuickLook.Plugin.CertViewer"],
        [".spc"] = ["QuickLook.Plugin.CertViewer"],
        [".ax"] = ["QuickLook.Plugin.PEViewer"],
        [".bpl"] = ["QuickLook.Plugin.PEViewer"],
        [".cpl"] = ["QuickLook.Plugin.PEViewer"],
        [".dll"] = ["QuickLook.Plugin.PEViewer"],
        [".drv"] = ["QuickLook.Plugin.PEViewer"],
        [".efi"] = ["QuickLook.Plugin.PEViewer"],
        [".exe"] = ["QuickLook.Plugin.PEViewer"],
        [".mui"] = ["QuickLook.Plugin.PEViewer"],
        [".mun"] = ["QuickLook.Plugin.PEViewer"],
        [".mz"] = ["QuickLook.Plugin.PEViewer"],
        [".ocx"] = ["QuickLook.Plugin.PEViewer"],
        [".scr"] = ["QuickLook.Plugin.PEViewer"],
        [".sys"] = ["QuickLook.Plugin.PEViewer"],
        [".tlb"] = ["QuickLook.Plugin.PEViewer"],
        [".vxd"] = ["QuickLook.Plugin.PEViewer"],
        [".winmd"] = ["QuickLook.Plugin.PEViewer"],
        [".md"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".markdown"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mdx"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mmd"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mkd"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mdwn"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mdown"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mdc"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".qmd"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".rmd"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".rmarkdown"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".apib"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mdtxt"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mdtext"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".adoc"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".asciidoc"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".asc"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".ad"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".ipynb"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".mermaid"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".rst"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".restructuredtext"] = ["QuickLook.Plugin.MarkdownViewer"],
        [".doc"] = ["QuickLook.Plugin.OfficeViewer"],
        [".docx"] = ["QuickLook.Plugin.OfficeViewer"],
        [".docm"] = ["QuickLook.Plugin.OfficeViewer"],
        [".odt"] = ["QuickLook.Plugin.OfficeViewer"],
        [".xls"] = ["QuickLook.Plugin.OfficeViewer"],
        [".xlsx"] = ["QuickLook.Plugin.OfficeViewer"],
        [".xlsm"] = ["QuickLook.Plugin.OfficeViewer"],
        [".xlsb"] = ["QuickLook.Plugin.OfficeViewer"],
        [".ods"] = ["QuickLook.Plugin.OfficeViewer"],
        [".ppt"] = ["QuickLook.Plugin.OfficeViewer"],
        [".pptx"] = ["QuickLook.Plugin.OfficeViewer"],
        [".pptm"] = ["QuickLook.Plugin.OfficeViewer"],
        [".odp"] = ["QuickLook.Plugin.OfficeViewer"],
        [".vsd"] = ["QuickLook.Plugin.OfficeViewer"],
        [".vsdx"] = ["QuickLook.Plugin.OfficeViewer"],
    };

    private readonly object _lazyLock = new();
    private List<(long Seq, string Path)> _pendingLazy = [];
    private readonly Dictionary<IViewer, long> _seqByPlugin = [];

    // v3.32.0: every plugin assembly simple name is loaded at most once. A stale
    // copy of the same plugin (an incremental build that did not clean up, a
    // file left over from an older layout, a user plugin shadowing a built-in)
    // otherwise makes Assembly.LoadFrom throw "Assembly with same name is
    // already loaded", which showed up in the error log on every start.
    private readonly HashSet<string> _loadedPluginAssemblies = new(StringComparer.OrdinalIgnoreCase);

    // v3.22.0: per-extension match cache for the preview hot path. The first
    // preview of an extension pays the full priority-ordered CanHandle scan;
    // later previews re-run the scan only up to the cached winner (inclusive),
    // so a higher-priority plugin whose CanHandle depends on file content can
    // still win for a different file of the same extension - the outcome is
    // identical to a full scan, just with fewer CanHandle calls in the common
    // case. Entries are cleared whenever the plugin list changes (lazy plugin
    // loaded, user plugin uninstalled).
    private readonly object _matchCacheLock = new();
    private readonly Dictionary<string, (IViewer Plugin, int Index)> _matchCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MatchCacheMaxEntries = 128;

    // v3.31.0: serializes matching. Lazy expansion can now run on the thread
    // pool (see FindMatchAsync) while another preview asks for a match, and the
    // expansion mutates LoadedPlugins.
    private readonly object _matchLock = new();

    private PluginManager()
    {
        DefaultPlugin = new Plugin();

        // Loading plugin assemblies does reflection and file I/O that can add
        // hundreds of milliseconds to startup. Run it on a background thread so
        // the tray icon, keyboard hook and pipe server come up first; previews
        // wait for the load task if they arrive before it completes.
        _loadTask = Task.Run(LoadPluginsCore);
    }

    internal IViewer DefaultPlugin { get; }

    internal List<IViewer> LoadedPlugins { get; private set; } = [];

    internal void EnsureLoaded()
    {
        // v3.3.0: only waits for assembly discovery + instance creation, not
        // for every plugin's Init (which runs in the background and is ensured
        // per-plugin by EnsurePluginReady).
        _loadGate.Wait();
    }

    /// <summary>
    /// v3.3.0: makes sure a specific plugin's Init has run. The background
    /// load task initializes plugins in order; if it has not reached this one
    /// yet, the caller runs the Init itself (and any concurrent caller waits
    /// for it). This keeps a preview from waiting on the Init of unrelated
    /// plugins (e.g. TextViewer's ~300 ms syntax-highlighting build).
    /// </summary>
    internal void EnsurePluginReady(IViewer plugin)
    {
        if (plugin is null)
            return;

        var state = _initStates.GetOrAdd(plugin, static _ => new InitState());
        if (Interlocked.Exchange(ref state.Started, 1) == 0)
        {
            try
            {
                var timer = App.IsStartupTimingEnabled ? Stopwatch.StartNew() : null;
                plugin.Init();
                if (timer is { IsRunning: true })
                {
                    timer.Stop();
                    App.RecordPluginInitPhase(
                        plugin.GetType().Assembly.GetName().Name ?? plugin.GetType().Name,
                        timer.ElapsedMilliseconds);
                }
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(e.ToString());
            }
            finally
            {
                state.Done.Set();
            }
        }
        else
        {
            // Another thread is (or has already finished) initializing this
            // plugin; wait for completion so the preview never runs View on
            // half-initialized static state.
            state.Done.Wait();
        }
    }

    internal static PluginManager GetInstance()
    {
        return _instance ??= new PluginManager();
    }

    internal IViewer FindMatch(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var (matched, needsLazyExpansion) = MatchWithoutLazy(path);

        if (needsLazyExpansion)
            matched = ExpandLazyAndMatch(path);

        return PrepareResult(matched);
    }

    /// <summary>
    /// v3.31.0: same result as <see cref="FindMatch"/>, but when the file can
    /// only be claimed by the rarely-used built-ins, their assemblies are loaded
    /// on the thread pool instead of on the caller (UI) thread. The common case
    /// - a match among the already loaded plugins - completes synchronously, so
    /// previews behave exactly as before.
    /// </summary>
    internal async Task<IViewer> FindMatchAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var (matched, needsLazyExpansion) = MatchWithoutLazy(path);

        if (needsLazyExpansion)
            matched = await Task.Run(() => ExpandLazyAndMatch(path)).ConfigureAwait(true);

        return PrepareResult(matched);
    }

    /// <summary>
    /// v3.22.0/v3.31.0: matches a path against the plugins that are already
    /// loaded, plus the per-extension match cache. When a cached winner exists
    /// for the extension, the scan starts from the cached position instead of
    /// the top, so repeated same-format previews skip the CanHandle calls of
    /// every plugin below the winner.
    /// <para>
    /// Returns <c>NeedsLazyExpansion = true</c> when no loaded plugin claims the
    /// file and there are still not-yet-loaded built-ins left, so the caller can
    /// decide where to pay that cost.
    /// </para>
    /// </summary>
    private (IViewer Match, bool NeedsLazyExpansion) MatchWithoutLazy(string path)
    {
        var instance = GetInstance();
        instance.EnsureLoaded();

        lock (_matchLock)
        {
            // Snapshot: loading a lazy plugin inserts into LoadedPlugins and
            // re-sorts, so the list must not be indexed live here.
            var plugins = LoadedPlugins.ToArray();

            var cached = TryGetCachedMatch(path, out var cachedIndex);

            if (cachedIndex >= 0 && cachedIndex < plugins.Length)
            {
                // Re-verify from the top of the priority order up to and
                // including the cached winner, exactly like a full scan would.
                for (var i = 0; i <= cachedIndex; i++)
                {
                    var plugin = plugins[i];
                    if (!CanHandle(plugin, path))
                        continue;

                    if (!ReferenceEquals(plugin, cached))
                        RememberMatch(path, i);
                    return (plugin, false);
                }

                // The cached plugin no longer claims this file; drop the stale
                // entry and finish the scan where it left off.
                ClearMatchCache();
            }

            for (var i = Math.Max(0, cachedIndex + 1); i < plugins.Length; i++)
            {
                var plugin = plugins[i];
                if (CanHandle(plugin, path))
                {
                    RememberMatch(path, i);
                    return (plugin, false);
                }
            }
        }

        return (null, HasPendingLazyPlugin());
    }

    private bool HasPendingLazyPlugin()
    {
        lock (_lazyLock)
            return _pendingLazy.Count > 0;
    }

    /// <summary>
    /// v3.4.0/v3.31.0: no eager plugin claimed the file - load the rarely-used
    /// built-ins on demand and match against them. Runs under the match lock so
    /// a concurrent preview never observes a half-updated plugin list.
    /// </summary>
    private IViewer ExpandLazyAndMatch(string path)
    {
        lock (_matchLock)
        {
            var lazy = MatchLazyPlugin(path);

            if (lazy != null)
            {
                var index = LoadedPlugins.IndexOf(lazy);
                if (index >= 0)
                    RememberMatch(path, index);
            }

            return lazy;
        }
    }

    private IViewer PrepareResult(IViewer matched)
    {
        var instance = GetInstance();
        var selected = matched ?? DefaultPlugin;
        instance.EnsurePluginReady(selected);

        // v3.28.0: local-only usage statistics for re-balancing the eager /
        // lazy plugin split from real data.
        if (matched != null)
        {
            PluginUsageTracker.Record(
                selected.GetType().Assembly.GetName().Name ?? selected.GetType().Name);
        }

        return selected.GetType().CreateInstance<IViewer>();
    }

    private static bool CanHandle(IViewer plugin, string path)
    {
        var can = false;
        try
        {
#if DEBUG
            var timer = new Stopwatch();
            timer.Start();

            can = plugin.CanHandle(path);

            timer.Stop();
            Debug.WriteLine($"{plugin.GetType()}: {can}, {timer.ElapsedMilliseconds}ms");
#else
            can = plugin.CanHandle(path);
#endif
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{plugin.GetType()}: CanHandle failed: {ex}");
        }

        return can;
    }

    private IViewer TryGetCachedMatch(string path, out int index)
    {
        index = -1;
        var key = GetMatchCacheKey(path);
        if (key is null)
            return null;

        lock (_matchCacheLock)
        {
            if (!_matchCache.TryGetValue(key, out var entry))
                return null;

            // The plugin list changes when lazy plugins load or user plugins
            // are uninstalled; both paths clear the cache, but guard anyway.
            if (entry.Index >= LoadedPlugins.Count ||
                !ReferenceEquals(LoadedPlugins[entry.Index], entry.Plugin))
            {
                _matchCache.Remove(key);
                return null;
            }

            index = entry.Index;
            return entry.Plugin;
        }
    }

    private void RememberMatch(string path, int index)
    {
        var key = GetMatchCacheKey(path);
        if (key is null)
            return;

        lock (_matchCacheLock)
        {
            if (_matchCache.Count >= MatchCacheMaxEntries)
                _matchCache.Clear();
            _matchCache[key] = (LoadedPlugins[index], index);
        }
    }

    private void ClearMatchCache()
    {
        lock (_matchCacheLock)
            _matchCache.Clear();
    }

    private static string GetMatchCacheKey(string path)
    {
        // CLSID paths ("::...") behave like a pseudo-extension; directories
        // have no extension and always fall back to the default plugin.
        if (path.StartsWith("::", StringComparison.Ordinal))
            return "::";
        if (Directory.Exists(path))
            return null;

        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? null : ext;
    }

    /// <summary>
    /// v3.4.0: loads the rarely-used built-in plugins one by one (in
    /// discovery order) on the first preview no eager plugin claims, then
    /// matches across the now-complete LoadedPlugins list so the highest
    /// priority plugin wins (same semantics as eager loading). Everything
    /// loaded stays in LoadedPlugins, so later rare previews are instant.
    /// </summary>
    private IViewer MatchLazyPlugin(string path)
    {
        lock (_lazyLock)
        {
            if (_pendingLazy.Count == 0)
                return null;

            // v3.10.0: try the handful of plugins whose declared extensions
            // match this file first, so a .db preview loads DbViewer instead
            // of all 15 pending rare plugins.
            var hinted = new List<string>();
            if (path.StartsWith("::", StringComparison.Ordinal))
            {
                hinted.Add("QuickLook.Plugin.CLSIDViewer");
            }
            else
            {
                var ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext) &&
                    LazyExtensionHints.TryGetValue(ext, out var names))
                {
                    hinted.AddRange(names);
                }
            }

            IViewer matched = null;
            if (hinted.Count > 0)
            {
                foreach (var name in hinted)
                {
                    var index = FindPendingIndex(name);
                    if (index < 0)
                        continue;

                    var plugin = LoadLazyPluginAt(index);
                    if (plugin is null)
                        continue;

                    try
                    {
                        if (plugin.CanHandle(path))
                        {
                            matched = plugin;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{plugin.GetType()}: CanHandle failed: {ex}");
                    }
                }
            }

            // Unmapped extension, or none of the hinted plugins claimed it:
            // fall back to loading everything and matching across the full,
            // priority-sorted list (same semantics as eager loading).
            if (matched is null)
            {
                while (_pendingLazy.Count > 0)
                    LoadLazyPluginAt(0);

                foreach (var plugin in LoadedPlugins)
                {
                    try
                    {
                        if (plugin.CanHandle(path))
                        {
                            matched = plugin;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"{plugin.GetType()}: CanHandle failed: {ex}");
                    }
                }
            }

            return matched;
        }
    }

    private int FindPendingIndex(string assemblyName)
    {
        for (var i = 0; i < _pendingLazy.Count; i++)
        {
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(_pendingLazy[i].Path),
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// v3.4.0: loads one pending lazy plugin by assembly name (used by
    /// More-menu actions such as "show media info" that target a plugin which
    /// never matches in FindMatch, e.g. MediaInfoViewer).
    /// </summary>
    internal IViewer LoadPluginByName(string assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
            return null;

        lock (_lazyLock)
        {
            for (var i = 0; i < _pendingLazy.Count; i++)
            {
                if (!string.Equals(
                        Path.GetFileNameWithoutExtension(_pendingLazy[i].Path),
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return LoadLazyPluginAt(i);
            }
        }

        return null;
    }

    /// <summary>
    /// v3.4.0: loads the plugin at the given index of _pendingLazy, removes it
    /// from the pending list and inserts it into LoadedPlugins keeping the
    /// (priority, discovery-order) sort. Must be called with _lazyLock held.
    /// </summary>
    private IViewer LoadLazyPluginAt(int index)
    {
        var (seq, lib) = _pendingLazy[index];
        _pendingLazy.RemoveAt(index);

        IViewer instance = null;
        try
        {
            foreach (var t in Assembly.LoadFrom(lib).GetExportedTypes())
            {
                if (t.IsInterface || t.IsAbstract || !typeof(IViewer).IsAssignableFrom(t))
                    continue;

                instance = t.CreateInstance<IViewer>();
                break;
            }
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog($"Failed to load plugin {Path.GetFileName(lib)}: {ex}");
            return null;
        }

        if (instance is null)
            return null;

        _seqByPlugin[instance] = seq;
        EnsurePluginReady(instance);
        LoadedPlugins.Add(instance);

        // Keep the priority + discovery-order sort so ties stay deterministic.
        LoadedPlugins.Sort((a, b) =>
        {
            var byPriority = b.Priority.CompareTo(a.Priority);
            if (byPriority != 0)
                return byPriority;

            var seqA = _seqByPlugin.TryGetValue(a, out var sa) ? sa : long.MaxValue;
            var seqB = _seqByPlugin.TryGetValue(b, out var sb) ? sb : long.MaxValue;
            return seqA.CompareTo(seqB);
        });

        // v3.22.0: the plugin list changed, so cached extension -> plugin
        // entries may point at stale indexes; drop them.
        ClearMatchCache();

        return instance;
    }

    private void LoadPluginsCore()
    {
        List<IViewer> loaded = null;
        try
        {
            // v1.3.11: finish uninstalls that were parked because a plugin
            // file was locked while the previous instance was still running.
            CleanupPendingUninstalls(App.UserPluginPath);

            // Keep a monotonic discovery sequence alongside each plugin so the
            // priority sort stays deterministic: parallel discovery changes
            // the raw list order run to run, and 13 plugins share Priority 0,
            // so an unstable sort alone would shuffle which one wins CanHandle
            // for extensions claimed by several plugins.
            var discovered = new List<(long Seq, IViewer Plugin)>();
            var pendingLazy = new List<(long Seq, string Path)>();
            LoadPlugins(App.UserPluginPath, discovered, pendingLazy);
            LoadPlugins(Path.Combine(App.AppPath, @"QuickLook.Plugin\"), discovered, pendingLazy);
            pendingLazy.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
            discovered.Sort(static (a, b) =>
            {
                var byPriority = b.Plugin.Priority.CompareTo(a.Plugin.Priority);
                return byPriority != 0 ? byPriority : a.Seq.CompareTo(b.Seq);
            });
            loaded = new List<IViewer>(discovered.Count);
            foreach (var (seq, plugin) in discovered)
            {
                loaded.Add(plugin);
                _seqByPlugin[plugin] = seq;
            }
            _pendingLazy = pendingLazy;
            App.RecordStartupPhase("plugins-assemblies-loaded");
        }
        catch (Exception e)
        {
            // Never let a plugin-load failure take down startup; FindMatch will
            // simply fall back to the default InfoPanel plugin.
            ProcessHelper.WriteLog(e.ToString());
        }
        finally
        {
            // Publish whatever was discovered and open the gate so previews can
            // start matching even if a plugin failed to load.
            LoadedPlugins = loaded ?? [];
            _loadGate.Set();
        }

        if (loaded is null)
            return;

        // v3.3.0: phase 2 - initialize plugins in the background, but each one
        // lazily: a preview that matches a plugin not yet reached initializes
        // it on demand (EnsurePluginReady) instead of waiting for all of them.
        // v3.22.0: run the Inits in parallel - each plugin initializes only
        // its own static state on this background thread, so the total time
        // drops from sum(Init) to max(Init) and the "not yet initialized"
        // window for early previews shrinks accordingly.
        Parallel.ForEach(loaded, EnsurePluginReady);

        App.RecordStartupPhase("plugins-inited");
    }

    /// <summary>
    /// Tracks one plugin's one-time Init state so a background pre-warm and a
    /// preview-triggered Init never run twice, and callers can wait for an
    /// in-flight Init.
    /// </summary>
    private sealed class InitState
    {
        public int Started;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private void LoadPlugins(string folder, List<(long Seq, IViewer Plugin)> loaded,
        List<(long Seq, string Path)> pendingLazy)
    {
        if (!Directory.Exists(folder))
            return;

        var libs = Directory.GetFiles(folder, "QuickLook.Plugin.*.dll", SearchOption.AllDirectories);
        if (libs.Length == 0)
            return;

        // v3.32.0: load each plugin assembly simple name once - user plugins are
        // scanned before the built-in folder, so a user copy still wins; a stale
        // duplicate is skipped instead of failing that plugin's load.
        var unique = new List<string>(libs.Length);
        foreach (var lib in libs)
        {
            if (_loadedPluginAssemblies.Add(Path.GetFileNameWithoutExtension(lib)))
                unique.Add(lib);
            else
                Debug.WriteLine($"Duplicate plugin assembly skipped: {lib}");
        }
        libs = unique.ToArray();
        if (libs.Length == 0)
            return;

        // v3.2.0: loading 25 plugin assemblies (LoadFrom + GetExportedTypes +
        // instance creation) serially cost ~1.2 s of startup. The assemblies
        // are independent, so discover and instantiate them in parallel and
        // only synchronize the final list add.
        var sync = new object();
        var failedPlugins = new List<(string Plugin, Exception Error)>();

        Parallel.For(0, libs.Length, i =>
        {
            var lib = libs[i];

            // v3.4.0: defer rarely-used built-in plugins to first use instead
            // of loading their assemblies into every resident process.
            if (LazyBuiltinPluginNames.Contains(Path.GetFileNameWithoutExtension(lib)))
            {
                lock (sync)
                {
                    pendingLazy.Add((i, lib));
                }
                return;
            }

            try
            {
                foreach (var t in Assembly.LoadFrom(lib).GetExportedTypes())
                {
                    if (t.IsInterface || t.IsAbstract || !typeof(IViewer).IsAssignableFrom(t))
                        continue;

                    var instance = t.CreateInstance<IViewer>();
                    lock (sync)
                    {
                        loaded.Add((i, instance));
                    }
                }
            }
            // 0x80131515: ERROR_ASSEMBLY_FILE_BLOCKED - Windows blocked the assembly due to security policy
            catch (FileLoadException ex) when (ex.HResult == unchecked((int)0x80131515) && SettingHelper.IsPortableVersion())
            {
                // The unblock-and-restart flow shows dialogs, so run it on the
                // UI thread. BeginInvoke (not Invoke) keeps a preview that is
                // waiting on the load task from deadlocking.
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!HandleSecurityBlockedException())
                        ProcessHelper.WriteLog($"Failed to load blocked plugin {Path.GetFileName(lib)}");
                }));
            }
            catch (Exception ex)
            {
                // Log the error
                ProcessHelper.WriteLog($"Failed to load plugin {Path.GetFileName(lib)}: {ex}");
                lock (sync)
                {
                    failedPlugins.Add((Path.GetFileName(lib), ex));
                }
            }
        });

        if (failedPlugins.Any())
        {
            var message = "The following plugins failed to load:\n\n" +
                string.Join("\n", failedPlugins.Select(f => $"• {f.Plugin}"));

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // The warning needs a visible owner window: during an
                    // auto-start / tray-only startup no window has been shown
                    // yet, and setting an unshown Owner crashes the process.
                    var owner = Application.Current.Windows
                        .OfType<Window>()
                        .FirstOrDefault(w => w.IsLoaded && w.IsVisible);
                    if (owner is null)
                    {
                        ProcessHelper.WriteLog(
                            $"Some plugins failed to load (no window to show warning): {message}");
                        return;
                    }

                    MessageBox.Show(owner, message, "Some Plugins Failed to Load",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception ex)
                {
                    ProcessHelper.WriteLog(ex.ToString());
                }
            }));
        }
    }

    /// <summary>
    /// Handles the case when Windows has blocked plugin files due to security policy.
    /// Attempts automatic unblock first, then shows manual instructions if that fails.
    /// </summary>
    /// <returns>
    /// <para>true if automatic unblock succeeded and app is restarting.</para>
    /// <para>false if manual intervention is needed and exception should be thrown.</para>
    /// </returns>
    private static bool HandleSecurityBlockedException()
    {
        var triedUnblock = SettingHelper.Get("TriedUnblock", false);
        if (!triedUnblock)
        {
            SettingHelper.Set("TriedUnblock", true);
            if (TryUnblockFilesAndRestart()) return true;
        }

        // Show manual unblock instructions if automatic unblock failed or was already attempted
        MessageBox.Show(
            """
            Windows has blocked the plugins.
            To fix this, please follow these steps:
            1. Right-click the downloaded QuickLookNext zip file and select 'Properties'
            2. At the bottom of the Properties window, check 'Unblock'
            3. Click 'Apply' and 'OK'
            4. Extract the zip file again
            QuickLookNext will now close. Please launch it from the unblocked folder.
            """,
            "Security Block Detected",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        return false;
    }

    /// <summary>
    /// Attempts to automatically unblock all files in the application directory using PowerShell's Unblock-File cmdlet.
    /// If successful, restarts the application to apply the changes.
    /// </summary>
    /// <returns>
    /// <para>true if the unblock command succeeded and application restart was initiated.</para>
    /// <para>false if the unblock command failed, in which case manual unblock instructions should be shown.</para>
    /// </returns>
    private static bool TryUnblockFilesAndRestart()
    {
        ProcessHelper.WriteLog("Attempting automatic unblock of plugins...");

        try
        {
            var rootDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!string.IsNullOrEmpty(rootDir) && Directory.Exists(rootDir))
            {
                foreach (var filePath in Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    if (ZoneIdentifierManager.IsZoneBlocked(filePath))
                    {
                        _ = ZoneIdentifierManager.UnblockZone(filePath);
                    }
                }
            }

            MessageBox.Show(
                """
                QuickLookNext has detected that Windows blocked the plugins, and has attempted to unblock them.
                The application will now restart to check if the unblocking was successful.
                """,
                "Security Unblock Attempt",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Restart the application using TrayIconManager
            TrayIconManager.GetInstance().Restart(forced: true);
            return true;
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Failed to perform automatic unblock: {e}");
            return false;
        }
    }

    /// <summary>
    /// v1.3.11: enumerates plugins for the management panel - user-installed
    /// plugins first, then the built-in plugins that ship with the app.
    /// </summary>
    internal List<PluginEntry> EnumerateInstalledPlugins()
    {
        var list = new List<PluginEntry>();
        CollectPlugins(App.UserPluginPath, user: true, list);
        CollectPlugins(Path.Combine(App.AppPath, @"QuickLook.Plugin\"), user: false, list);
        return list;
    }

    /// <summary>
    /// v1.3.11: uninstalls a user plugin. Prefers deleting the folder right
    /// away; when a file is locked, the folder is parked with an
    /// ".uninstalled" suffix and removed on the next launch.
    /// </summary>
    internal bool UninstallUserPlugin(PluginEntry entry, out string error, out bool restartRequired)
    {
        error = null;
        restartRequired = false;

        try
        {
            Directory.Delete(entry.Folder, recursive: true);
            RemovePluginsUnder(entry.Folder);
            return true;
        }
        catch (Exception)
        {
            // The plugin assembly or a native dependency is locked. Park the
            // folder so startup can finish the removal; the panel stays
            // consistent because the loaded plugin is dropped from the list.
            try
            {
                var pending = entry.Folder.TrimEnd('\\', '/') + ".uninstalled";
                if (Directory.Exists(pending))
                    Directory.Delete(pending, recursive: true);
                Directory.Move(entry.Folder, pending);
                RemovePluginsUnder(entry.Folder);
                restartRequired = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// v1.3.11: removes loaded plugin instances whose assembly lives under
    /// the given folder so future previews no longer match them.
    /// </summary>
    internal void RemovePluginsUnder(string folder)
    {
        if (LoadedPlugins is null)
            return;

        var prefix = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        LoadedPlugins.RemoveAll(plugin =>
        {
            try
            {
                var location = plugin.GetType().Assembly.Location;
                return !string.IsNullOrEmpty(location) &&
                    location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        // v3.22.0: the plugin list changed, so cached extension -> plugin
        // entries may point at plugins that no longer exist; drop them.
        ClearMatchCache();
    }

    private static void CollectPlugins(string root, bool user, List<PluginEntry> list)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var folderName = Path.GetFileName(dir);
            if (folderName.EndsWith(".uninstalled", StringComparison.OrdinalIgnoreCase))
                continue;

            // Prefer the DLL named after the plugin folder; a folder can also
            // contain dependency copies of other plugins (e.g. PDFViewer ships
            // an HtmlViewer copy), which must not be treated as its own DLL.
            var dll = Path.Combine(dir, folderName + ".dll");
            if (!File.Exists(dll))
            {
                dll = Directory.GetFiles(dir, "QuickLook.Plugin.*.dll", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => new FileInfo(file).Length)
                    .FirstOrDefault();
            }
            if (dll is null)
                continue;

            var (name, version, description) = ReadPluginMeta(dir, dll, folderName);
            list.Add(new PluginEntry(name, version, description, dir, user));
        }
    }

    private static (string Name, string Version, string Description) ReadPluginMeta(
        string dir, string dll, string folderName)
    {
        var config = Path.Combine(dir, "QuickLook.Plugin.Metadata.config");
        if (File.Exists(config))
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(config);
                var ns = doc.SelectSingleNode("/Metadata/Namespace")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(ns))
                {
                    return (
                        FriendlyName(ns),
                        doc.SelectSingleNode("/Metadata/Version")?.InnerText?.Trim() ?? string.Empty,
                        doc.SelectSingleNode("/Metadata/Description")?.InnerText?.Trim() ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog($"Failed to read plugin metadata {config}: {ex}");
            }
        }

        try
        {
            var assembly = Assembly.LoadFrom(dll);
            // Use the assembly identity, not AssemblyTitle: the Lite fork has
            // copy-paste AssemblyTitle bugs (MarkdownViewer declares ImageViewer).
            var title = assembly.GetName().Name ?? folderName;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? string.Empty;
            var description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
                ?? string.Empty;
            return (FriendlyName(title), version, description);
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog($"Failed to read plugin assembly {dll}: {ex}");
            return (FriendlyName(folderName), string.Empty, string.Empty);
        }
    }

    private static string FriendlyName(string namespaceOrTitle)
    {
        const string prefix = "QuickLook.Plugin.";
        var name = namespaceOrTitle?.Trim() ?? string.Empty;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];
        return name.Replace('.', ' ');
    }

    private static void CleanupPendingUninstalls(string userPluginPath)
    {
        if (!Directory.Exists(userPluginPath))
            return;

        foreach (var dir in Directory.GetDirectories(userPluginPath, "*.uninstalled", SearchOption.TopDirectoryOnly))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog($"Failed to finish uninstall of {dir}: {ex}");
            }
        }
    }
}

/// <summary>
/// v1.3.11: one row in the plugin management panel.
/// </summary>
internal sealed record PluginEntry(string Name, string Version, string Description, string Folder, bool IsUserPlugin);
