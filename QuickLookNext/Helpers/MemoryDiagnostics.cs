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

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace QuickLookNext.Helpers;

/// <summary>
/// v3.32.0: hidden test hook (/test-memory) that records where the process
/// memory actually goes over a session - startup, every preview open/close and
/// an idle tick - into ql-smoke\memory.txt.
///
/// The point is to replace guesswork in the "how do we get the resident memory
/// down" discussions: the numbers here show whether the managed heap, the
/// working set or the WebView2 process group is responsible, before anything
/// (aggressive GC, killing Chromium, trimming caches) is changed.
/// </summary>
internal static class MemoryDiagnostics
{
    private const int TickSeconds = 10;

    private static readonly object Sync = new();
    private static Timer _timer;
    private static bool _started;

    internal static void Start()
    {
        lock (Sync)
        {
            if (_started)
                return;

            _started = true;
            _timer = new Timer(static _ => Snapshot("tick"), null,
                TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
        }

        Snapshot("startup");
    }

    internal static void Snapshot(string label)
    {
        if (!_started)
            return;

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            var heap = GC.GetGCMemoryInfo();
            var loh = heap.GenerationInfo.Length > 3 ? heap.GenerationInfo[3].SizeAfterBytes : 0;

            var line =
                $"{DateTime.Now:HH:mm:ss.fff}|{label}" +
                $"|private={ToMb(process.PrivateMemorySize64)}MB" +
                $"|workingSet={ToMb(process.WorkingSet64)}MB" +
                $"|managed={ToMb(GC.GetTotalMemory(false))}MB" +
                $"|loh={ToMb(loh)}MB" +
                $"|gen0={GC.CollectionCount(0)}|gen1={GC.CollectionCount(1)}|gen2={GC.CollectionCount(2)}" +
                $"|assemblies={AppDomain.CurrentDomain.GetAssemblies().Length}" +
                $"|webview2={CountWebView2Processes()}";

            var dir = App.SmokeDir;
            Directory.CreateDirectory(dir);

            lock (Sync)
            {
                File.AppendAllText(Path.Combine(dir, "memory.txt"),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never affect the app.
        }
    }

    private static long ToMb(long bytes) => bytes / (1024 * 1024);

    /// <summary>
    /// The count is machine wide (every WebView2 app contributes), which is why
    /// the log also carries the private/heap numbers - the interesting signal for
    /// us is whether this count drops when the previews close.
    /// </summary>
    private static int CountWebView2Processes()
    {
        try
        {
            return Process.GetProcessesByName("msedgewebview2").Length;
        }
        catch
        {
            return -1;
        }
    }
}
