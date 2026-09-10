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

using QuickLook.Common.NativeMethods;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v3.33.0: follows Explorer's selection while a preview is open.
/// <para>
/// This used to be a plain 500 ms polling loop, which meant every arrow-key move
/// in Explorer waited 0-500 ms (250 ms on average) before the preview even
/// started switching - by far the biggest cost of the most common interaction,
/// now that a warm preview itself only takes 80-160 ms.
/// </para>
/// <para>
/// Explorer raises EVENT_OBJECT_SELECTION* for its list view, so the selection is
/// read when that event arrives (debounced by 40 ms to coalesce selection
/// bursts), and a slow 1.5 s poll is kept as a fallback for shell views that do
/// not raise it (the desktop, third-party file managers).
/// </para>
/// </summary>
internal class FocusMonitor
{
    private static FocusMonitor _instance;

    private readonly object _sync = new();

    private volatile bool _isRunning;
    private string _lastPath = string.Empty;

    private nint _winEventHook;
    private User32.WinEventProc _winEventProc; // keep alive: the OS holds a raw pointer
    private DispatcherTimer _debounce;

    public bool IsRunning => _isRunning;

    public void Start()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _lastPath = GetSelectionSafe();

        // The WinEvent callback is delivered on the thread that installed the hook
        // (the UI thread, which has the message pump), so a DispatcherTimer is the
        // natural coalescer for the selection bursts Explorer produces.
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Long enough to coalesce the bursts Explorer produces while a
            // selection is being dragged, short enough to be invisible.
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = Task.Run(CheckSelection);
        };

        _winEventProc = OnShellSelectionChanged;
        _winEventHook = User32.SetWinEventHook(
            User32.EVENT_OBJECT_SELECTION, User32.EVENT_OBJECT_SELECTIONWITHIN,
            IntPtr.Zero, _winEventProc, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _ = Task.Factory.StartNew(PollLoop, TaskCreationOptions.LongRunning);
    }

    public void Stop()
    {
        _isRunning = false;

        if (_winEventHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _winEventProc = null;

        var timer = _debounce;
        _debounce = null;
        timer?.Stop();
    }

    /// <summary>
    /// Keep this callback cheap: Windows silently drops WinEvent hooks whose
    /// handler is slow. The window-type check is pure Win32 (no COM) and the
    /// selection read is deferred to the debounce timer.
    /// </summary>
    private void OnShellSelectionChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        var timer = _debounce;
        if (!_isRunning || timer == null)
            return;

        if (NativeMethods.QuickLookNext.GetFocusedWindowType() ==
            NativeMethods.QuickLookNext.FocusedWindowType.Invalid)
            return;

        timer.Stop();
        timer.Start();
    }

    private async Task PollLoop()
    {
        while (_isRunning)
        {
            await Task.Delay(1500).ConfigureAwait(false);

            if (!_isRunning)
                break;

            CheckSelection();
        }
    }

    private void CheckSelection()
    {
        if (!_isRunning)
            return;

        var path = GetSelectionSafe();
        if (string.IsNullOrEmpty(path))
            return;

        lock (_sync)
        {
            if (!_isRunning || path == _lastPath)
                return;

            _lastPath = path;
        }

        PipeServerManager.SendMessage(PipeMessages.Switch, path);
    }

    private static string GetSelectionSafe()
    {
        try
        {
            return NativeMethods.QuickLookNext.GetCurrentSelection() ?? string.Empty;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return string.Empty;
        }
    }

    internal static FocusMonitor GetInstance()
    {
        return _instance ??= new FocusMonitor();
    }
}
