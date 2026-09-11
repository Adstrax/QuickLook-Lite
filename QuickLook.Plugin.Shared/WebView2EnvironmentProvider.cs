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

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v3.36.0: decides which WebView2 profile (user data folder) the next control is
/// created with.
/// <para>
/// A session that was killed while Chromium was running can leave a profile that
/// makes every controller creation fail with 0x8007139F - the preview then stays
/// blank and the font preview even stalls for its whole 1.5 s timeout. Until now
/// the only cure was deleting the folder by hand; after a failed initialization we
/// simply move on to a fresh folder (<c>WebView2_Data_1</c>, <c>_2</c>, ...) so the
/// next preview works again.
/// </para>
/// </summary>
public static class WebView2EnvironmentProvider
{
    private static readonly object Sync = new();
    private static int _generation;

    /// <summary>The user data folder the next control should use.</summary>
    public static string UserDataFolder
    {
        get
        {
            lock (Sync)
                return FolderFor(_generation);
        }
    }

    /// <summary>
    /// Moves to the next profile folder. Called after a controller refused to
    /// initialize; the offending folder is left on disk untouched (it may still be
    /// locked by a dying browser process).
    /// </summary>
    public static void RotateAfterFailure()
    {
        lock (Sync)
            _generation++;
    }

    private static string FolderFor(int generation)
    {
        var name = generation == 0 ? "WebView2_Data" : $"WebView2_Data_{generation}";
        return Path.Combine(SettingHelper.LocalDataPath, name);
    }
}
