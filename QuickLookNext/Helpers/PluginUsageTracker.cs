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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace QuickLookNext.Helpers;

/// <summary>
/// v3.28.0: local-only, per-plugin usage counter. Every preview records which
/// plugin handled the file, so the eager / lazy plugin split can be re-balanced
/// from real usage data instead of guesses. Data never leaves the machine.
///
/// Cost is negligible: one small in-memory dictionary (a few KB) and a ~1 KB
/// JSON file next to the other settings. Disk writes are debounced (at most
/// one save per 5 s while previewing) and run on a background thread, so the
/// preview hot path only does a dictionary increment.
/// </summary>
internal static class PluginUsageTracker
{
    private const string FileName = "plugin-usage.json";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, long> Counts = Load();
    private static readonly bool Disabled = SettingHelper.Get("DisablePluginUsageTracking", false);
    private static bool _dirty;
    private static bool _saveScheduled;

    /// <summary>
    /// Forces the static initialization (dictionary load) so the first preview
    /// never pays the one-time file read on the UI thread.
    /// </summary>
    internal static void Preload()
    {
        _ = Counts.Count;
    }

    internal static void Record(string pluginAssemblyName)
    {
        if (Disabled || string.IsNullOrEmpty(pluginAssemblyName))
            return;

        lock (Sync)
        {
            Counts.TryGetValue(pluginAssemblyName, out var count);
            Counts[pluginAssemblyName] = count + 1;
            _dirty = true;

            if (_saveScheduled)
                return;

            _saveScheduled = true;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => SaveIfDirty());
        }
    }

    /// <summary>
    /// v3.42.0: the plugins this user opens most often, most used first. The
    /// preview warm-up uses it to decide which families are worth preparing in the
    /// background, so a user who reads mostly Markdown pays for nobody else's video
    /// decoder.
    /// </summary>
    internal static IReadOnlyList<string> GetMostUsed(int count)
    {
        lock (Sync)
        {
            return Counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(pair => pair.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Synchronous save on app exit so the last previews are persisted.
    /// </summary>
    internal static void Flush()
    {
        lock (Sync)
        {
            if (!_dirty)
                return;

            _dirty = false;
            SaveLocked();
        }
    }

    private static void SaveIfDirty()
    {
        lock (Sync)
        {
            _saveScheduled = false;
            if (!_dirty)
                return;

            _dirty = false;
            SaveLocked();
        }
    }

    private static void SaveLocked()
    {
        try
        {
            var dir = SettingHelper.LocalDataPath;
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, FileName),
                JsonSerializer.Serialize(Counts));
        }
        catch
        {
            // Statistics are best-effort; never break the preview or startup.
        }
    }

    private static Dictionary<string, long> Load()
    {
        try
        {
            var path = Path.Combine(SettingHelper.LocalDataPath, FileName);
            if (!File.Exists(path))
                return new Dictionary<string, long>();

            var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            return parsed ?? new Dictionary<string, long>();
        }
        catch
        {
            // Corrupt or unreadable stats: start over.
            return new Dictionary<string, long>();
        }
    }
}
