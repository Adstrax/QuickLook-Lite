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

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace QuickLookNext.NativeMethods;

/// <summary>
/// The list of display adapters, used to keep the known-bad Intel HD Graphics 4xxx
/// driver off the GPU-accelerated path.
/// <para>
/// v3.40.0: this used to run <c>SELECT * FROM Win32_VideoController</c>. The query
/// measured ~2 s here (it loads System.Management and waits for the WMI service),
/// and the preview window asks for the answer while it is being shown - so the very
/// first preview, image or Markdown alike, waited for WMI before it could render.
/// The display class key holds the same adapter descriptions and reads in
/// microseconds.
/// </para>
/// </summary>
internal static class WMI
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static List<string> GetGPUNames()
    {
        List<string> names = [];

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey == null)
                return names;

            // Adapters live in the 0000, 0001, ... subkeys; everything else under
            // this key (Properties, Configuration, ...) is bookkeeping.
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (subKeyName.Length != 4 || !int.TryParse(subKeyName, out _))
                    continue;

                using var adapter = classKey.OpenSubKey(subKeyName);

                if (adapter?.GetValue("DriverDesc") is string description &&
                    !string.IsNullOrWhiteSpace(description) &&
                    !names.Contains(description))
                {
                    names.Add(description);
                }
            }
        }
        catch (Exception e)
        {
            // No adapter list means "nothing is blacklisted", which is the same
            // answer the old WMI failure path gave.
            Debug.WriteLine($"GPU name lookup failed: {e.Message}");
        }

        return names;
    }
}
