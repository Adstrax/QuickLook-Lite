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

using QuickLookNext.Helpers;

namespace QuickLook.Tests;

/// <summary>
/// v3.31.0: the allow/block list decides whether a file may be previewed at all,
/// and its defaults (blocklist mode, ".insv" blocked, extensionless files always
/// allowed) are load bearing.
/// </summary>
internal class ExtensionFilterHelperTests : SettingsFixture
{
    public override void Setup()
    {
        base.Setup();

        // The helper caches its lists statically; the fixture gives every test a
        // fresh settings root, so the cache has to go with it.
        ExtensionFilterHelper.ClearCache();
    }

    public override void Teardown()
    {
        ExtensionFilterHelper.ClearCache();

        base.Teardown();
    }

    public void DefaultBlocklistBlocksInsv()
    {
        Assert.False(ExtensionFilterHelper.IsExtensionAllowed(@"C:\temp\clip.insv"), ".insv is blocked by default");
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed(@"C:\temp\photo.png"), "other extensions pass");
    }

    public void ExtensionlessFilesAndDirectoriesAreAllowed()
    {
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed(@"C:\temp\README"), "file without extension");
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed(@"C:\temp\folder"), "directory");
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed(null), "empty path");
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed(string.Empty), "empty string");
    }

    public void CustomBlocklistIsNormalized()
    {
        ExtensionFilterHelper.SetBlocklist(["foo", ".BAR", "  .baz  ", "$(ExtensionBlocklist)"]);

        Assert.False(ExtensionFilterHelper.IsExtensionAllowed("x.foo"), "missing dot is added");
        Assert.False(ExtensionFilterHelper.IsExtensionAllowed("x.BaR"), "case insensitive");
        Assert.False(ExtensionFilterHelper.IsExtensionAllowed("x.baz"), "whitespace is trimmed");
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed("x.png"), "unlisted extension passes");
    }

    public void AllowlistModeOnlyAllowsListedExtensions()
    {
        ExtensionFilterHelper.UseAllowlistMode = true;
        ExtensionFilterHelper.SetAllowlist([".png"]);

        Assert.True(ExtensionFilterHelper.IsExtensionAllowed("a.png"), "listed");
        Assert.False(ExtensionFilterHelper.IsExtensionAllowed("a.jpg"), "not listed");
    }

    public void EmptyAllowlistDisablesFiltering()
    {
        ExtensionFilterHelper.UseAllowlistMode = true;
        ExtensionFilterHelper.SetAllowlist([]);

        Assert.True(ExtensionFilterHelper.IsExtensionAllowed("a.jpg"), "empty allowlist allows everything");
    }

    public void InvalidExtensionsAreIgnored()
    {
        ExtensionFilterHelper.SetBlocklist([".", ".a b", "..", ".ok"]);

        Assert.False(ExtensionFilterHelper.IsExtensionAllowed("x.ok"), "valid entry is kept");
        Assert.True(ExtensionFilterHelper.IsExtensionAllowed("x.ab"), "invalid entries are dropped");
    }
}
