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
/// v3.31.0: the updater only installs packages downloaded from GitHub over
/// https; everything else must be refused before a single byte is written.
/// </summary>
internal class UpdaterUrlTests
{
    public void GitHubUrlsAreAccepted()
    {
        Assert.True(Updater.IsTrustedDownloadUrl(
            "https://github.com/Adstrax/QuickLook-Next/releases/download/3.31.0/QuickLook-Next-3.31.0.zip"),
            "release asset on github.com");
        Assert.True(Updater.IsTrustedDownloadUrl(
            "https://objects.githubusercontent.com/github-production-release-asset/x/y.zip"),
            "asset mirror");
        Assert.True(Updater.IsTrustedDownloadUrl(
            "https://api.github.com/repos/Adstrax/QuickLook-Next/releases/latest"),
            "release api");
    }

    public void ForeignHostsAreRejected()
    {
        Assert.False(Updater.IsTrustedDownloadUrl("https://example.com/QuickLook-Next-3.31.0.zip"), "foreign host");
        Assert.False(Updater.IsTrustedDownloadUrl("https://github.com.evil.example/x.zip"), "lookalike host");
        Assert.False(Updater.IsTrustedDownloadUrl("https://raw.githubusercontent.com.evil.example/x.zip"), "lookalike subdomain");
    }

    public void InsecureOrMalformedUrlsAreRejected()
    {
        Assert.False(Updater.IsTrustedDownloadUrl("http://github.com/x.zip"), "plain http");
        Assert.False(Updater.IsTrustedDownloadUrl("file:///C:/x.zip"), "file url");
        Assert.False(Updater.IsTrustedDownloadUrl("ftp://github.com/x.zip"), "ftp");
        Assert.False(Updater.IsTrustedDownloadUrl(""), "empty");
        Assert.False(Updater.IsTrustedDownloadUrl(null), "null");
        Assert.False(Updater.IsTrustedDownloadUrl("not a url"), "garbage");
    }
}
