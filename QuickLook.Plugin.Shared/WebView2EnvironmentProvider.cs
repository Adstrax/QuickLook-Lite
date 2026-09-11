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
using System.IO;
using System.Threading.Tasks;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v3.36.0: decides which WebView2 profile (user data folder) the next control is
/// created with.
/// <para>
/// A session that was killed while Chromium was running can leave a profile that
/// makes every controller creation fail with 0x8007139F - the preview then stays
/// blank and the font preview even stalls for its whole 1.5 s timeout. Until now
/// the only cure was deleting the folder by hand.
/// </para>
/// <para>
/// v3.40.0: the app repairs that profile in place instead of replacing it (see
/// <see cref="TryRepairProfile"/>) and closes Chromium on the way out (see
/// <see cref="WebView2Lifecycle.ShutdownAll"/>), so the old "one more folder per
/// failure" behaviour is gone. <see cref="RotateAfterFailure"/> stays as the last
/// resort for the rare case where dropping the stale lock markers was not enough,
/// and whatever folder it leaves behind is swept up by
/// <see cref="CleanupLeftoverProfiles"/>.
/// </para>
/// </summary>
public static class WebView2EnvironmentProvider
{
    private static readonly object Sync = new();

    /// <summary>
    /// v3.40.0: an abandoned profile is only removed once it is clearly stale. A
    /// folder written to recently can be the one a session rotated to (which holds
    /// the warm cache), so it is left alone.
    /// </summary>
    private static readonly TimeSpan LeftoverMinimumAge = TimeSpan.FromDays(1);

    private static int _generation;
    private static bool _maintenanceRan;

    /// <summary>The user data folder the next control should use.</summary>
    public static string UserDataFolder
    {
        get
        {
            lock (Sync)
            {
                RunMaintenance();
                return FolderFor(_generation);
            }
        }
    }

    /// <summary>
    /// Moves to the next profile folder. Called after a controller refused to
    /// initialize; the offending folder is left on disk untouched (it may still be
    /// locked by a dying browser process). Last resort only - see the class remarks.
    /// </summary>
    public static void RotateAfterFailure()
    {
        lock (Sync)
            _generation++;
    }

    /// <summary>The profile folder the current session is using.</summary>
    public static string CurrentProfileFolder
    {
        get
        {
            lock (Sync)
                return FolderFor(_generation);
        }
    }

    /// <summary>
    /// The folder *name* (not the full path) of the profile this session uses;
    /// everything else matching <c>WebView2_Data*</c> is a leftover.
    /// </summary>
    public static string CurrentProfileName
    {
        get
        {
            lock (Sync)
                return ProfileNameFor(_generation);
        }
    }

    /// <summary>
    /// v3.40.0: repairs the profile instead of throwing it away.
    /// <para>
    /// When a session is killed while Chromium runs, the profile keeps Chromium's
    /// singleton markers (SingletonLock / SingletonCookie / SingletonSocket /
    /// lockfile). The next controller creation then fails with 0x8007139F even
    /// though the profile itself is perfectly fine - the old code responded by
    /// abandoning the profile and creating a new one, which slowly littered the
    /// data folder. Removing the stale markers when no browser process owns the
    /// folder brings the very same profile back to life.
    /// </para>
    /// </summary>
    /// <returns>true when the profile was repaired (or needed no repair).</returns>
    public static bool TryRepairProfile(string profileFolder)
    {
        try
        {
            if (!Directory.Exists(profileFolder))
                return true;

            if (WebView2Lifecycle.IsProfileHeldByBrowser(profileFolder))
                return false; // a live browser owns it: not stale, do not touch

            var removed = 0;
            var found = 0;

            foreach (var file in Directory.GetFiles(profileFolder, "Singleton*", SearchOption.AllDirectories))
                removed += TryDelete(file, ref found);

            foreach (var file in Directory.GetFiles(profileFolder, "*.lock", SearchOption.AllDirectories))
                removed += TryDelete(file, ref found);

            foreach (var file in Directory.GetFiles(profileFolder, "lockfile", SearchOption.AllDirectories))
                removed += TryDelete(file, ref found);

            foreach (var folder in Directory.GetDirectories(profileFolder, "Singleton*", SearchOption.AllDirectories))
                removed += TryDelete(folder, ref found);

            if (removed > 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[WebView2] removed {removed} stale lock marker(s) from {Path.GetFileName(profileFolder)}");

            // Only claim success when nothing is left behind - the caller falls back
            // to another profile otherwise, so it must not be told a lie here.
            return removed == found;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v3.40.0: rebuilds the profile in place - the folder stays, its contents go.
    /// <para>
    /// This is the escalation after <see cref="TryRepairProfile"/>. A session that
    /// was killed while Chromium was writing can leave the profile damaged rather
    /// than merely locked, and then no amount of marker removal brings it back.
    /// Rebuilding the very same folder recovers the preview without adding another
    /// folder to the data directory - a WebView2 profile is a cache, Chromium writes
    /// it again on the next start.
    /// </para>
    /// </summary>
    /// <returns>true when the profile is ready to be used again.</returns>
    public static bool ResetProfile(string profileFolder)
    {
        try
        {
            if (!Directory.Exists(profileFolder))
                return true;

            if (WebView2Lifecycle.IsProfileHeldByBrowser(profileFolder))
                return false; // still in use: not ours to empty

            var complete = true;
            foreach (var entry in Directory.GetFileSystemEntries(profileFolder))
                complete &= TryDeletePath(entry);

            return complete;
        }
        catch
        {
            return false;
        }
    }

    private static int TryDelete(string path, ref int found)
    {
        found++;
        return TryDeletePath(path) ? 1 : 0;
    }

    private static bool TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v3.40.0: drops the profile folders that earlier fallbacks abandoned.
    /// <para>
    /// The profile in use is deliberately never touched, and no folder is
    /// recreated or replaced on a schedule - with <see cref="TryRepairProfile"/>
    /// doing the actual recovery, the app has no reason to move to another profile
    /// at all, so this only clears up after the exceptions.
    /// </para>
    /// <para>
    /// Runs once per session, before the first controller of the session exists.
    /// </para>
    /// </summary>
    private static void RunMaintenance()
    {
        if (_maintenanceRan)
            return;

        _maintenanceRan = true;

        // Removing a leftover moves tens of megabytes, so it stays off the thread
        // that is opening the preview which triggered the housekeeping.
        Task.Run(() => CleanupLeftoverProfiles());
    }

    /// <summary>
    /// Removes the profiles abandoned by failed initializations. Safe to call at
    /// any time: a folder that a running browser still owns, or that was written to
    /// within <see cref="LeftoverMinimumAge"/>, is skipped.
    /// </summary>
    public static void CleanupLeftoverProfiles() => CleanupLeftoverProfiles(SettingHelper.LocalDataPath);

    internal static void CleanupLeftoverProfiles(string root)
    {
        try
        {
            var current = CurrentProfileName;

            foreach (var folder in Directory.GetDirectories(root, "WebView2_Data*"))
            {
                // The profile in use is never touched; everything else with that name
                // was left behind by a rotation and is only a cache.
                if (string.Equals(Path.GetFileName(folder), current, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (WebView2Lifecycle.IsProfileHeldByBrowser(folder))
                    continue;

                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < LeftoverMinimumAge)
                        continue;
                }
                catch
                {
                    continue;
                }

                TryDeleteDirectory(folder);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Locked by a running process; the next maintenance pass retries.
        }
    }

    private static string FolderFor(int generation)
        => Path.Combine(SettingHelper.LocalDataPath, ProfileNameFor(generation));

    private static string ProfileNameFor(int generation)
        => generation == 0 ? "WebView2_Data" : $"WebView2_Data_{generation}";
}
