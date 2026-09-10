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

namespace QuickLook.Tests;

/// <summary>
/// Implemented by test classes that need per-test setup/teardown.
/// </summary>
internal interface ITestFixture
{
    void Setup();

    void Teardown();
}

/// <summary>
/// Points the settings store at a throwaway directory so tests never read or
/// write the real user profile.
/// </summary>
internal abstract class SettingsFixture : ITestFixture
{
    private string _root;

    protected string Root => _root;

    public virtual void Setup()
    {
        // A unique name keeps concurrently running test hosts apart.
        _root = Path.Combine(
            Path.GetTempPath(),
            "QuickLookNext.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        SettingHelper.TestRootOverride = _root;
    }

    public virtual void Teardown()
    {
        SettingHelper.TestRootOverride = null;

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }

        _root = null;
    }

    protected string ConfigPath(string domain) => SettingHelper.ResolveConfigPath(domain);
}
