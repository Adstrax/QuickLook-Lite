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
using System.Windows;

namespace QuickLook.Plugin.FontViewer;

/// <summary>
/// v3.37.0: the two font preview implementations - <see cref="NativeFontPanel"/>
/// (WPF, for TrueType/OpenType/Collection) and <see cref="WebfontPanel"/> (WebView2,
/// only needed for the compressed WOFF/WOFF2 formats).
/// </summary>
public interface IFontPreviewPanel : IDisposable
{
    UIElement View { get; }

    void PreviewFont(string path);

    void PreviewIconFont(string path);

    /// <summary>True when the font has been handed to the renderer.</summary>
    bool WaitForFontSent();
}
