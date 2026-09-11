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
using System;

namespace QuickLook.Tests;

/// <summary>
/// v3.40.0: the update script swaps the installed files after the app exits.
/// <para>
/// It used to back the folder up with <c>xcopy /EXCLUDE:"&lt;file&gt;"</c>, and xcopy
/// cannot read an exclusion file whose path is quoted - the backup "failed", the
/// update aborted, and the package that had just been downloaded was thrown away.
/// That went unnoticed for several releases because a failed update only looked
/// like "the version number did not change". These tests pin the script to the
/// working copy mechanism and to the cleanup order that keeps %TEMP% from filling
/// up with one package per attempt.
/// </para>
/// </summary>
internal class UpdateScriptTests
{
    private const string AppDir = @"C:\Program Files\QuickLook Next";
    private const string WorkDir = @"C:\Users\tester\AppData\Local\Temp\QuickLookNext.Update";

    private static string Script() => Updater.BuildUpdateScriptForTest(
        AppDir,
        WorkDir + @"\new",
        WorkDir,
        WorkDir + @"\update.log");

    public void TheScriptDoesNotUseXcopyExclusions()
    {
        var script = Script();

        Assert.False(
            script.Contains("/EXCLUDE:", StringComparison.OrdinalIgnoreCase),
            "xcopy cannot read an exclusion file whose path is quoted");
        Assert.True(
            script.Contains("robocopy", StringComparison.OrdinalIgnoreCase),
            "the copy has to go through robocopy");
    }

    public void TheScriptKeepsThePortableUserDataOutOfTheSwap()
    {
        var script = Script();

        Assert.True(
            script.Contains("/XD \"%QL_APP%\\UserData\"", StringComparison.Ordinal),
            "the backup has to exclude UserData");
        Assert.True(
            script.Contains("if /I not \"%%~nxd\"==\"UserData\"", StringComparison.Ordinal),
            "the cleanup has to keep UserData");
    }

    public void TheScriptWaitsForTheAppToExitBeforeReplacingFiles()
    {
        Assert.True(
            Script().Contains("tasklist", StringComparison.OrdinalIgnoreCase),
            "the script has to wait for the running app");
    }

    public void TheScriptRemovesItsWorkFolderBeforeDeletingItself()
    {
        var script = Script();

        var cleanup = script.IndexOf("rd /S /Q \"%QL_WORK%\"", StringComparison.Ordinal);
        var selfDelete = script.IndexOf("del \"%~f0\"", StringComparison.Ordinal);

        Assert.True(cleanup >= 0, "the work folder is removed");
        Assert.True(selfDelete > cleanup,
            "a batch file that deletes itself stops executing, so the cleanup has to come first");
    }

    public void TheScriptFailsTheUpdateWhenTheCopyFails()
    {
        var script = Script();

        // robocopy reports success below 8, so the script must not test errorlevel 1.
        Assert.True(script.Contains("if errorlevel 8 goto rollback", StringComparison.Ordinal),
            "a failed copy rolls back");
        Assert.False(script.Contains("if errorlevel 1 goto rollback", StringComparison.Ordinal),
            "robocopy exits with 1..7 for success");
    }
}
