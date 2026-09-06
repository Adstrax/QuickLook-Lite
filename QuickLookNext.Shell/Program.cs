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
using System.Threading;
using System.Windows.Forms;

namespace QuickLookNext.Shell;

/// <summary>
/// v3.31.0-dev prototype: the resident "thin shell". It owns the tray icon,
/// the global space key and Explorer selection reading, and spawns / keeps
/// warm a QuickLook-Next preview child. WPF is deliberately never loaded in
/// this process, so idle memory stays in the 10-30 MB range.
/// </summary>
internal static class Program
{
    private const string MutexName = "QuickLookNext.Shell.Mutex";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
            return; // a shell instance is already running

        ApplicationConfiguration.Initialize();

        using var host = new ChildPreviewHost();
        using var hook = new SpaceKeyHook();
        using var tray = new ShellTray(host);

        hook.SpacePressed += (_, _) => host.ToggleSelection();

        Application.Run();
    }
}