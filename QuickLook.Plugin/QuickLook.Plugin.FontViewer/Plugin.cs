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
using QuickLook.Common.Plugin;
using QuickLook.Common.Plugin.MoreMenu;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace QuickLook.Plugin.FontViewer;

public sealed partial class Plugin : IViewer, IMoreMenu
{
    private const string ConfigDomain = "QuickLook.Plugin.FontViewer";

    private IFontPreviewPanel _panel;
    private string _currentPath;
    private PreviewMode _previewMode = PreviewMode.Pangram;

    public int Priority => 0;

    public IEnumerable<IMenuItem> MenuItems => GetMenuItems();

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        // The `*.eot` and `*.svg` font types are not supported
        // TODO: Check `*.otc` type
        return !Directory.Exists(path) &&
            (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size { Width = 1300, Height = 650 };
    }

    public void View(string path, ContextObject context)
    {
        _currentPath = path;
        _previewMode = (PreviewMode)SettingHelper.Get("LastPreviewMode", (int)PreviewMode.Pangram, ConfigDomain);

        // v3.37.0: TrueType/OpenType/Collection fonts are rendered by WPF itself -
        // that skips the ~300-400 ms WebView2 controller creation, the shared
        // Chromium profile (which could go stale and blank the preview) and makes
        // fonts the fastest format instead of the slowest. Only the compressed web
        // font formats still need the browser engine.
        var native = !IsWebFont(path);
        _panel = native ? new NativeFontPanel() : new WebfontPanel();
        ApplyPreviewMode(path);

        context.ViewerContent = _panel.View;
        context.Title = Path.GetFileName(path);

        if (native)
        {
            // Nothing to wait for - the WPF render tree is already built.
            context.IsBusy = false;
            return;
        }

        _ = Task.Run(() =>
        {
            _ = _panel.WaitForFontSent();
            context.IsBusy = false;
        });
    }

    private static bool IsWebFont(string path)
    {
        return path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyPreviewMode(string path)
    {
        if (_previewMode == PreviewMode.IconFont)
            _panel.PreviewIconFont(path);
        else
            _panel.PreviewFont(path);
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);

        _panel?.Dispose();
        _panel = null;
    }
}
