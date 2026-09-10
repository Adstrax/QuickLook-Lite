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
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace QuickLook.Tests;

/// <summary>
/// v3.31.0: the settings store gained a value cache and an atomic save - both
/// have to keep behaving exactly like the plain XML reads they replaced.
/// </summary>
internal class SettingHelperTests : SettingsFixture
{
    public void MissingSettingReturnsFailsafe()
    {
        Assert.Equal(42, SettingHelper.Get("Missing", 42), "int failsafe");
        Assert.Equal(true, SettingHelper.Get("Missing", true), "bool failsafe");
        Assert.Null(SettingHelper.Get<string>("Missing"), "string failsafe");
    }

    public void ValuesRoundTripByType()
    {
        SettingHelper.Set("Count", 7);
        SettingHelper.Set("Theme", "Dark");
        SettingHelper.Set("Flag", true);
        SettingHelper.Set("Ticks", 1234567890123L);

        Assert.Equal(7, SettingHelper.Get("Count", 0), "int");
        Assert.Equal("Dark", SettingHelper.Get("Theme", string.Empty), "string");
        Assert.Equal(true, SettingHelper.Get("Flag", false), "bool");
        Assert.Equal(1234567890123L, SettingHelper.Get("Ticks", 0L), "long");
    }

    public void LaterValueWinsWithinTheCacheWindow()
    {
        SettingHelper.Set("Theme", 1);
        Assert.Equal(1, SettingHelper.Get("Theme", 0), "first value");

        // Written and read again immediately: the cache must be updated by Set
        // even though the file stamp is not re-checked within the same second.
        SettingHelper.Set("Theme", 2);
        Assert.Equal(2, SettingHelper.Get("Theme", 0), "second value");
    }

    public void WrongTypeFallsBackToFailsafe()
    {
        SettingHelper.Set("Number", "abc");

        Assert.Equal(7, SettingHelper.Get("Number", 7), "unparsable int");
    }

    public void DomainsAreSeparate()
    {
        SettingHelper.Set("Value", 1, "DomainA");
        SettingHelper.Set("Value", 2, "DomainB");

        Assert.Equal(1, SettingHelper.Get("Value", 0, "DomainA"), "domain A");
        Assert.Equal(2, SettingHelper.Get("Value", 0, "DomainB"), "domain B");
        Assert.True(File.Exists(ConfigPath("DomainA")), "domain A file");
        Assert.True(File.Exists(ConfigPath("DomainB")), "domain B file");
    }

    public void CorruptConfigIsRecreatedInsteadOfThrowing()
    {
        SettingHelper.Set("A", 1);

        File.WriteAllText(ConfigPath("QuickLookNext"), "<Settings><broken>");

        // Force a reload so the damaged file is actually read.
        SettingHelper.TestRootOverride = Root;

        Assert.Equal(9, SettingHelper.Get("A", 9), "damaged file falls back to the failsafe");
        Assert.True(File.Exists(ConfigPath("QuickLookNext")), "config exists again");

        SettingHelper.Set("A", 1);
        Assert.Equal(1, SettingHelper.Get("A", 0), "the recreated file is usable");
    }

    public void SaveIsAtomicAndLeavesNoTemporaryFile()
    {
        SettingHelper.Set("A", 1);

        var path = ConfigPath("QuickLookNext");

        Assert.True(File.Exists(path), "config written");
        Assert.False(File.Exists(path + ".tmp"), "temporary file removed");

        var doc = new XmlDocument();
        doc.Load(path);

        Assert.NotNull(doc.SelectSingleNode("/Settings/A"), "node present");
        Assert.Equal(1, doc.SelectNodes("/Settings/A").Count, "stored once");
    }

    public void ExternalEditIsPickedUpAfterTheStampCheck()
    {
        SettingHelper.Set("A", 1);
        Assert.Equal(1, SettingHelper.Get("A", 0), "cached value");

        // Simulate another tool (or an older build) editing the config.
        var doc = new XmlDocument();
        doc.Load(ConfigPath("QuickLookNext"));
        doc.SelectSingleNode("/Settings/A").InnerText = "9";
        doc.Save(ConfigPath("QuickLookNext"));

        // The file stamp is re-validated at most once per second.
        Thread.Sleep(1200);

        Assert.Equal(9, SettingHelper.Get("A", 0), "external change is visible");
    }

    public void ConcurrentReadsAndWritesDoNotThrow()
    {
        Exception failure = null;
        var tasks = new Task[4];

        for (var t = 0; t < tasks.Length; t++)
        {
            var value = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 100; i++)
                    {
                        SettingHelper.Set("Shared", value * 1000 + i);
                        _ = SettingHelper.Get("Shared", -1);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref failure, e, null);
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.Null(failure, "no exception under concurrent access");
        Assert.True(SettingHelper.Get("Shared", -1) >= 0, "value still readable");
    }

    public void NonSerializableTypeIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => SettingHelper.Get("Any", new NotSerializable()),
            "only serializable setting types are allowed");

        Assert.Throws<NotSupportedException>(
            () => SettingHelper.Set("Any", new NotSerializable()),
            "only serializable values can be stored");
    }

    /// <summary>
    /// A plain class without [Serializable]; the settings store rejects it, which
    /// is what keeps the config file readable by other tooling.
    /// </summary>
    private sealed class NotSerializable
    {
    }
}
