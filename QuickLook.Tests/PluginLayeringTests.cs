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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuickLook.Tests;

/// <summary>
/// v3.32.0: guards the plugin layering rules. Plugins used to reference each
/// other (and even the app project), which made them impossible to add or remove
/// independently and forced the loader to keep a recursive DLL search as a
/// safety net. New plugin-to-plugin references must be intentional, so this test
/// fails until the allow-list below is extended on purpose.
/// </summary>
internal class PluginLayeringTests
{
    /// <summary>
    /// Documented, reviewed plugin-to-plugin references. Anything else fails.
    /// </summary>
    private static readonly HashSet<string> AllowedPluginReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        // Both render through ImageViewer's ImagePanel (XAML user control).
        // Removing them means extracting that panel into QuickLook.Plugin.Shared.
        "QuickLook.Plugin.PDFViewer -> QuickLook.Plugin.ImageViewer",
        "QuickLook.Plugin.ThumbnailViewer -> QuickLook.Plugin.ImageViewer",
    };

    public void PluginsOnlyDependOnTheSdkAndTheSharedKit()
    {
        var root = FindRepositoryRoot();
        var pluginsRoot = Path.Combine(root, "QuickLook.Plugin");

        Assert.True(Directory.Exists(pluginsRoot), $"plugin folder exists: {pluginsRoot}");

        var violations = new List<string>();

        foreach (var project in Directory.GetFiles(pluginsRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var references = ReadProjectReferences(project);

            foreach (var reference in references)
            {
                if (reference.Equals("QuickLook.Common", StringComparison.OrdinalIgnoreCase) ||
                    reference.Equals("QuickLook.Plugin.Shared", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (reference.Equals("QuickLookNext", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{name} references the application project (QuickLookNext)");
                    continue;
                }

                if (reference.StartsWith("QuickLook.Plugin.", StringComparison.OrdinalIgnoreCase))
                {
                    var pair = $"{name} -> {reference}";
                    if (!AllowedPluginReferences.Contains(pair))
                        violations.Add($"{pair} is not in the documented allow-list");
                }
            }
        }

        Assert.Equal(0, violations.Count,
            "plugin layering violations: " + string.Join(" | ", violations.Distinct()));
    }

    public void EveryPluginReferencesTheSdk()
    {
        var root = FindRepositoryRoot();
        var pluginsRoot = Path.Combine(root, "QuickLook.Plugin");
        var missing = new List<string>();

        foreach (var project in Directory.GetFiles(pluginsRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var references = ReadProjectReferences(project);
            if (!references.Contains("QuickLook.Common", StringComparer.OrdinalIgnoreCase))
                missing.Add(Path.GetFileNameWithoutExtension(project));
        }

        Assert.Equal(0, missing.Count,
            "plugins without a QuickLook.Common reference: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Reads the (uncommented) project references of a csproj. Commented-out
    /// references - which this repository uses to document "keep this here for
    /// testing" cases - must not count as real dependencies.
    /// </summary>
    private static List<string> ReadProjectReferences(string projectPath)
    {
        var xml = File.ReadAllText(projectPath);
        xml = Regex.Replace(xml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        return Regex.Matches(xml, "<ProjectReference\\s+Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickLookNext.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new AssertionException($"repository root not found above {AppContext.BaseDirectory}");
    }
}
