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
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v3.32.0: shared WebView2 helpers (availability probe, file URL conversion).
/// Public because the shared kit is consumed by the Html / Markdown / Office /
/// CHM / Mail / Font / SVG plugins.
/// </summary>
public static class Helper
{
    public static bool IsWebView2Available()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Uri FilePathToFileUrl(string filePath)
    {
        var uri = new StringBuilder();
        foreach (var v in filePath)
            if (v >= 'a' && v <= 'z' || v >= 'A' && v <= 'Z' || v >= '0' && v <= '9' ||
                v == '+' || v == '/' || v == ':' || v == '.' || v == '-' || v == '_' || v == '~' ||
                v > '\x80')
                uri.Append(v);
            else if (v == Path.DirectorySeparatorChar || v == Path.AltDirectorySeparatorChar)
                uri.Append('/');
            else
                uri.Append($"%{(int)v:X2}");
        if (uri.Length >= 2 && uri[0] == '/' && uri[1] == '/') // UNC path - Universal Naming Convention
            uri.Insert(0, "file:");
        else
            uri.Insert(0, "file:///");

        try
        {
            return new Uri(uri.ToString());
        }
        catch
        {
            return null;
        }
    }

    public static string GetUrlPath(string url)
    {
        var index = -1;
        var lines = File.ReadAllLines(url);
        foreach (var line in lines)
            if (line.ToLower().Contains("url="))
            {
                index = Array.IndexOf(lines, line);
                break;
            }

        if (index != -1)
        {
            var fullLine = lines.GetValue(index);
            return fullLine.ToString().Substring(fullLine.ToString().LastIndexOf('=') + 1);
        }

        return url;
    }
}
