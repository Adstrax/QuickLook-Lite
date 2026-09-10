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
/// v3.31.0: the parser turns "/top", "-pin:1", "--width=800" and the option
/// strings sent over the named pipe ("top,pin") into the values the viewer
/// window acts on, so its quirks are worth pinning down.
/// </summary>
internal class CommandLineParserTests
{
    public void SingleFlagBecomesTrue()
    {
        var cli = new CommandLineParser(["/top"]);

        Assert.True(cli.Has("top"), "flag is present");
        Assert.True(cli.IsValueBoolean("top"), "flag reads as boolean true");
        Assert.Equal(true, cli.GetValueBoolean("top"), "flag value");
    }

    public void ParameterSeparatorsAreRecognized()
    {
        var cli = new CommandLineParser(["-pin:1", "--width=800", "-flag"]);

        Assert.Equal(1, cli.GetValueInt32("pin"), "colon form");
        Assert.Equal(800, cli.GetValueInt32("width"), "equals form");
        Assert.True(cli.Has("flag"), "single dash flag");
    }

    public void QuotedValueIsUnquoted()
    {
        var cli = new CommandLineParser(["/path=\"C:\\temp\\a.txt\""]);

        Assert.Equal(@"C:\temp\a.txt", cli.Values["path"], "quotes are stripped");
    }

    public void FirstValueWinsOnDuplicates()
    {
        var cli = new CommandLineParser(["/top", "/top=false"]);

        Assert.Equal(true, cli.GetValueBoolean("top"), "the first occurrence wins");
    }

    public void MissingKeyIsSafe()
    {
        var cli = new CommandLineParser(["/top"]);

        Assert.False(cli.Has("nope"), "unknown key");
        Assert.Null(cli.GetValueBoolean("nope"), "unknown boolean");
        Assert.Null(cli.GetValueInt32("nope"), "unknown integer");
        Assert.False(cli.IsValueBoolean("nope"), "unknown boolean as false");
    }

    public void InvalidNumberReturnsNull()
    {
        var cli = new CommandLineParser(["/n=abc"]);

        Assert.Null(cli.GetValueInt32("n"), "non numeric value");
        Assert.Null(cli.GetValueDouble("n"), "non numeric double value");
    }

    public void EmptyArgumentListYieldsNoValues()
    {
        var cli = new CommandLineParser([]);

        Assert.Equal(0, cli.Values.Count, "no values");
    }
}
