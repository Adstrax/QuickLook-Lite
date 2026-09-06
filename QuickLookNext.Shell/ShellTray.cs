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
using System.Drawing;
using System.Windows.Forms;

namespace QuickLookNext.Shell;

/// <summary>
/// Minimal WinForms tray icon for the shell prototype. (The full acrylic tray
/// menu still lives in the WPF app; a later iteration can render it in a UI
/// child process.)
/// </summary>
internal sealed class ShellTray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ChildPreviewHost _host;

    public ShellTray(ChildPreviewHost host)
    {
        _host = host;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("预览当前选中", null, (_, _) => _host.ToggleSelection());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Application.Exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "QuickLook-Next Shell (原型)",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _host.ToggleSelection();
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _menu?.Dispose();
    }
}