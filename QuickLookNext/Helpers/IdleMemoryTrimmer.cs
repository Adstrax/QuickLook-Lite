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
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLookNext.Helpers;

/// <summary>
/// Test build (v3.31.0-dev): after the user has stopped previewing for a
/// while, runs one blocking full GC with large-object-heap compaction so the
/// memory closed previews left behind (decoded bitmaps, WPF render buffers)
/// is actually returned instead of lingering in the heap.
///
/// Purely idle-time work: never runs while a preview is open, and never
/// before the first preview of the session (startup memory is already lean).
///
/// Config: QuickLookNext.config -> IdleMemoryTrimMinutes (default 5, 0 = off).
/// </summary>
internal static class IdleMemoryTrimmer
{
    private const string SettingId = "IdleMemoryTrimMinutes";
    private const int DefaultMinutes = 5;
    private const int PollSeconds = 30;

    private static readonly object Sync = new();
    private static Timer _timer;
    private static long _idleSinceTicks = -1;
    private static bool _seenActivity;
    private static bool _trimmedSinceActivity;

    public static void Start()
    {
        lock (Sync)
        {
            if (_timer != null)
                return;

            _timer = new Timer(static _ => Check(), null,
                TimeSpan.FromSeconds(PollSeconds), TimeSpan.FromSeconds(PollSeconds));
        }
    }

    private static void Check()
    {
        var minutes = SettingHelper.Get(SettingId, DefaultMinutes, "QuickLookNext");
        if (minutes <= 0)
            return;

        bool active;
        try
        {
            active = ViewWindowManager.GetInstance().HasActivePreview;
        }
        catch
        {
            // Startup race; try again on the next poll.
            return;
        }

        var now = Environment.TickCount64;
        lock (Sync)
        {
            if (active)
            {
                _seenActivity = true;
                _idleSinceTicks = -1;
                _trimmedSinceActivity = false;
                return;
            }

            // Nothing was ever previewed this session; startup is already lean.
            if (!_seenActivity)
                return;

            if (_idleSinceTicks < 0)
                _idleSinceTicks = now;

            // One trim per idle stretch; the next preview re-arms it.
            if (_trimmedSinceActivity)
                return;

            if (now - _idleSinceTicks < TimeSpan.FromMinutes(minutes).TotalMilliseconds)
                return;

            _trimmedSinceActivity = true;
        }

        // Delay so pending teardown work (plugin Cleanup, window close) settles,
        // then compact off the UI thread. Blocking full GC + LOH compaction can
        // take tens to hundreds of milliseconds.
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(static _ =>
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch
            {
                // Idle cleanup must never disturb the app.
            }
        });
    }
}