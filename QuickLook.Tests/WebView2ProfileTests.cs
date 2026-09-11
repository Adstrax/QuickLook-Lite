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

using QuickLook.Plugin.Shared;
using System.IO;

namespace QuickLook.Tests;

/// <summary>
/// v3.40.0: the app used to answer a WebView2 profile it could not open by
/// moving to the next folder, so a folder was added on every recovery. The
/// profile is now repaired in place, and only the folders abandoned by the old
/// behaviour (or by the rarely used fallback) are removed - never the one the
/// session is using.
/// </summary>
internal class WebView2ProfileTests : SettingsFixture
{
    public void RepairRemovesOnlyTheStaleLockMarkers()
    {
        var profile = Path.Combine(Root, "WebView2_Data", "EBWebView");
        Directory.CreateDirectory(Path.Combine(profile, "Default"));
        File.WriteAllText(Path.Combine(profile, "lockfile"), string.Empty);
        File.WriteAllText(Path.Combine(profile, "SingletonLock"), string.Empty);
        File.WriteAllText(Path.Combine(profile, "SingletonSocket"), string.Empty);
        File.WriteAllText(Path.Combine(profile, "Default", "Cookies"), "profile data");

        Assert.True(WebView2EnvironmentProvider.TryRepairProfile(profile), "a stale profile can be repaired");

        Assert.False(File.Exists(Path.Combine(profile, "lockfile")), "the Chromium lock file is gone");
        Assert.False(File.Exists(Path.Combine(profile, "SingletonLock")), "SingletonLock is gone");
        Assert.False(File.Exists(Path.Combine(profile, "SingletonSocket")), "SingletonSocket is gone");
        Assert.True(File.Exists(Path.Combine(profile, "Default", "Cookies")), "the profile itself is kept");
    }

    public void RepairOfAHealthyProfileTouchesNothing()
    {
        var profile = Path.Combine(Root, "WebView2_Data", "EBWebView");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");

        Assert.True(WebView2EnvironmentProvider.TryRepairProfile(profile), "nothing to repair");
        Assert.True(File.Exists(Path.Combine(profile, "Preferences")), "the profile is left alone");
    }

    public void RepairOfAMissingProfileIsANoOp()
    {
        Assert.True(
            WebView2EnvironmentProvider.TryRepairProfile(Path.Combine(Root, "WebView2_Data")),
            "a profile that does not exist yet needs no repair");
    }

    public void ResetEmptiesTheProfileInPlace()
    {
        var profile = Path.Combine(Root, "WebView2_Data");
        var eb = Path.Combine(profile, "EBWebView");
        Directory.CreateDirectory(Path.Combine(eb, "Default"));
        File.WriteAllText(Path.Combine(eb, "Local State"), "half written");
        File.WriteAllText(Path.Combine(eb, "Default", "Preferences"), "half written");

        Assert.True(WebView2EnvironmentProvider.ResetProfile(profile), "a damaged profile can be rebuilt");

        Assert.True(Directory.Exists(profile), "the profile folder itself is kept");
        Assert.Equal(0, Directory.GetFileSystemEntries(profile).Length, "its contents are gone");
    }

    public void ResetOfAMissingProfileIsANoOp()
    {
        Assert.True(
            WebView2EnvironmentProvider.ResetProfile(Path.Combine(Root, "WebView2_Data")),
            "a profile that does not exist yet needs no rebuild");
    }

    public void CleanupKeepsTheProfileInUseAndDropsTheRotatedOnes()
    {
        var inUse = Path.Combine(Root, WebView2EnvironmentProvider.CurrentProfileName);
        var rotated1 = Path.Combine(Root, "WebView2_Data_1");
        var rotated7 = Path.Combine(Root, "WebView2_Data_7");
        var rotatedToday = Path.Combine(Root, "WebView2_Data_9");
        var unrelated = Path.Combine(Root, "plugins");

        foreach (var folder in new[] { inUse, rotated1, rotated7, rotatedToday, unrelated })
            Directory.CreateDirectory(folder);

        // The leftovers are only swept once they are old; the one from today may
        // still be the profile a session rotated to, so it is kept.
        Directory.SetLastWriteTimeUtc(rotated1, System.DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(rotated7, System.DateTime.UtcNow.AddDays(-30));

        WebView2EnvironmentProvider.CleanupLeftoverProfiles(Root);

        Assert.True(Directory.Exists(inUse), "the profile in use survives");
        Assert.False(Directory.Exists(rotated1), "the first rotated profile is removed");
        Assert.False(Directory.Exists(rotated7), "the seventh rotated profile is removed");
        Assert.True(Directory.Exists(rotatedToday), "a profile written to today is left alone");
        Assert.True(Directory.Exists(unrelated), "unrelated data is not touched");
    }
}
