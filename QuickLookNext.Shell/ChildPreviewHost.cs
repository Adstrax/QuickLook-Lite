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
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows.Forms;

namespace QuickLookNext.Shell;

/// <summary>
/// Spawns and manages the warm QuickLook-Next preview child. A first preview
/// pays process startup (~0.5-1 s); while the child stays alive, further
/// requests are forwarded over the child pipe so they are instant. The child
/// is killed after <see cref="IdleKillMinutes"/> without activity so idle
/// memory returns to the shell's own footprint.
/// </summary>
internal sealed class ChildPreviewHost : IDisposable
{
    private const int IdleKillMinutes = 3;
    private const int PipeTimeoutMs = 400;

    private static readonly string ChildExe =
        Path.Combine(AppContext.BaseDirectory, "QuickLook-Next.exe");

    private static readonly string ChildPipeName =
        "QuickLookNext.Child.Pipe." + WindowsIdentity.GetCurrent().User?.Value;

    private readonly Timer _idleTimer;
    private Process _child;
    private long _lastActivityTicks = Environment.TickCount64;

    public ChildPreviewHost()
    {
        _idleTimer = new Timer { Interval = 30_000 };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
    }

    public void Dispose()
    {
        _idleTimer?.Dispose();
        KillChild();
    }

    /// <summary>
    /// Space pressed (or tray double-click) while Explorer / the desktop is
    /// focused: preview the current selection, toggling the warm child.
    /// </summary>
    public void ToggleSelection()
    {
        try
        {
            var focus = global::QuickLookNext.NativeMethods.QuickLookNext.GetFocusedWindowType();
            if (focus == global::QuickLookNext.NativeMethods.QuickLookNext.FocusedWindowType.Invalid)
                return;

            var path = global::QuickLookNext.NativeMethods.QuickLookNext.GetCurrentSelection();
            if (string.IsNullOrEmpty(path))
                return;

            _lastActivityTicks = Environment.TickCount64;

            if (TryPost(global::QuickLookNext.PipeMessages.Toggle, path))
                return;

            // Child is dead or was never started: spawn a fresh one.
            KillChild();
            SpawnChild(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    private void SpawnChild(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(ChildExe)
            {
                UseShellExecute = false,
                Arguments = "--child-instance \"" + path + "\"",
            };
            _child = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            _child = null;
        }
    }

    private void KillChild()
    {
        var child = _child;
        _child = null;

        if (child == null)
            return;

        try
        {
            if (!child.HasExited)
                child.Kill();
        }
        catch
        {
            // already gone
        }
        finally
        {
            child.Dispose();
        }
    }

    private bool TryPost(string message, string path)
    {
        var child = _child;
        if (child == null)
            return false;

        try
        {
            if (child.HasExited)
            {
                _child = null;
                return false;
            }

            using var client = new NamedPipeClientStream(".", ChildPipeName, PipeDirection.Out);
            client.Connect(PipeTimeoutMs);

            using var writer = new StreamWriter(client);
            writer.WriteLine($"{message}|{path}|");
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CheckIdle()
    {
        var child = _child;
        if (child == null)
            return;

        if (child.HasExited)
        {
            _child = null;
            child.Dispose();
            return;
        }

        if (Environment.TickCount64 - _lastActivityTicks >= IdleKillMinutes * 60_000L)
        {
            KillChild();
        }
    }
}