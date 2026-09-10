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

using Newtonsoft.Json;
using QuickLook.Common.Helpers;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;

namespace QuickLookNext.Helpers;

internal class Updater
{
    // v3.31.0: refuse obviously oversized packages before writing them to disk.
    private const long MaxPackageBytes = 400L * 1024 * 1024;

    // v3.31.0: the release package is only ever downloaded from GitHub. Without
    // this check a malformed API response (or a proxy rewriting it) could point
    // the updater at any host.
    private static readonly string[] TrustedDownloadHosts =
    [
        "github.com",
        "api.github.com",
        "codeload.github.com",
        "objects.githubusercontent.com",
        "raw.githubusercontent.com",
    ];

    private static readonly HttpClient Http = CreateHttpClient(TimeSpan.FromSeconds(15));

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        // v3.31.0: no UseDefaultCredentials - the release endpoints never need
        // Windows credentials, and an authentication challenge would otherwise
        // leak the current user's NTLM/Kerberos identity to the endpoint. The
        // user agent identifies the app instead of impersonating curl.
        var client = new HttpClient
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"QuickLook-Next/{Assembly.GetExecutingAssembly().GetName().Version}");
        return client;
    }

    // "silent" indicates whether this check was automatic/background.
    // When "silent" is true, do not open or invoke UI that shows
    // the full markdown release notes. Only show detailed release
    // notes when the check is user-initiated (silent == false).
    public static void CheckForUpdates(bool silent = false)
    {
        if (App.IsUWP)
        {
            if (!silent)
            {
                // v1.3.9: shell URIs need UseShellExecute on .NET Core.
                try
                {
                    Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NV4BS3L1H4S")
                    {
                        UseShellExecute = true,
                    });
                }
                catch (Win32Exception)
                {
                    // Store not available; ignore.
                }
            }

            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var json = DownloadJson("https://api.github.com/repos/Adstrax/QuickLook-Next/releases/latest");

                // v3.31.0: the "last checked" stamp is written only once the API
                // call succeeded. Previously it was written before the check, so
                // a single offline start (or a GitHub hiccup) silenced update
                // checks for the next 30 days.
                SettingHelper.Set("LastUpdateTicks", DateTime.Now.Ticks);

                var nVersion = (string)json["tag_name"] ?? string.Empty;

                // v3.29.0: tolerate a "v" prefix on release tags (GitHub
                // conventions vary) and never let a malformed tag crash the
                // update check - treat it as "no newer version".
                var cleanVersion = nVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? nVersion[1..]
                    : nVersion;

                if (!Version.TryParse(cleanVersion, out var latestVersion) ||
                    latestVersion <= Assembly.GetExecutingAssembly().GetName().Version)
                {
                    if (!silent)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            TrayIconManager.ShowNotification(string.Empty,
                                TranslationHelper.Get("Update_NoUpdate")));
                    }
                    return;
                }

                if (!silent)
                {
                    // v3.0.4: user-initiated check -> download and install the
                    // release package automatically instead of opening GitHub.
                    Application.Current.Dispatcher.Invoke(() =>
                        TrayIconManager.ShowNotification(string.Empty,
                            string.Format(
                                TranslationHelper.Get("Update_AutoDownloading",
                                    failsafe: "发现新版本 {0}，正在自动下载并更新..."),
                                nVersion),
                            timeout: 20000));

                    if (TryAutoUpdate(json))
                    {
                        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                        return;
                    }

                    // Auto-update unavailable (read-only folder / no package):
                    // fall back to opening the download page.
                    Application.Current.Dispatcher.Invoke(() =>
                        TrayIconManager.ShowNotification(string.Empty,
                            TranslationHelper.Get("Update_AutoUpdateFailed",
                                failsafe: "自动更新失败，点击打开下载页面"),
                            timeout: 20000,
                            clickEvent: OpenReleasesPage));
                    return;
                }

                // Background check: only notify; clicking the notification
                // starts the automatic update.
                Application.Current.Dispatcher.Invoke(() =>
                {
                    TrayIconManager.ShowNotification(string.Empty,
                        string.Format(TranslationHelper.Get("Update_Found"), nVersion),
                        timeout: 20000,
                        clickEvent: () => _ = Task.Run(() =>
                        {
                            if (TryAutoUpdate(json))
                            {
                                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                            }
                            else
                            {
                                OpenReleasesPage();
                            }
                        }));
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);

                // v3.31.0: a background check that fails (offline, GitHub down)
                // must not pop an error toast at the user; keep it in the log.
                if (!silent)
                {
                    Application.Current.Dispatcher.Invoke(
                        () => TrayIconManager.ShowNotification(string.Empty,
                            string.Format(TranslationHelper.Get("Update_Error"), e.Message)));
                }
            }
        });
    }

    /// <summary>
    /// v3.0.4: downloads the release package, stages it next to the app and
    /// hands the actual file replacement to a hidden updater script, so the
    /// running process can exit first and the updater can relaunch the app.
    /// v3.31.0: the download is restricted to GitHub hosts and size capped, the
    /// package hash is written to the update log, and the replacement keeps a
    /// backup so a failed copy is rolled back instead of leaving a folder that
    /// cannot start.
    /// Returns false (without shutting down) when auto-update is impossible.
    /// </summary>
    private static bool TryAutoUpdate(dynamic release)
    {
        try
        {
            var tag = (string)release["tag_name"];

            string downloadUrl = null;
            foreach (var asset in release["assets"])
            {
                var name = (string)asset["name"];
                if (string.IsNullOrEmpty(name) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith("QuickLook-Next-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                downloadUrl = (string)asset["browser_download_url"];
                break;
            }

            if (string.IsNullOrEmpty(downloadUrl))
                return false;

            if (!IsTrustedDownloadUrl(downloadUrl))
            {
                ProcessHelper.WriteLog($"Auto update refused: untrusted download URL ({downloadUrl})");
                return false;
            }

            var appDir = App.AppPath;
            if (string.IsNullOrEmpty(appDir) || !IsWritable(appDir))
                return false;

            var workDir = Path.Combine(Path.GetTempPath(), "QuickLookNext.Update");
            Directory.CreateDirectory(workDir);

            var zipPath = Path.Combine(workDir, $"QuickLook-Next-{tag}.zip");
            var extractDir = Path.Combine(workDir, "new");
            var logPath = Path.Combine(workDir, "update.log");
            var excludePath = Path.Combine(workDir, "exclude.txt");

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            var sha256 = DownloadPackage(downloadUrl, zipPath);
            if (sha256 == null)
                return false;

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // Sanity check: the package must contain the app entry points before
            // anything in the installed folder is touched. The release package
            // produced by Scripts/pack-release.ps1 keeps QuickLook.Common.dll in
            // lib\, a plain build output keeps it next to the exe - accept both.
            if (!File.Exists(Path.Combine(extractDir, "QuickLook-Next.exe")) ||
                (!File.Exists(Path.Combine(extractDir, "QuickLook.Common.dll")) &&
                 !File.Exists(Path.Combine(extractDir, "lib", "QuickLook.Common.dll"))))
            {
                ProcessHelper.WriteLog("Auto update refused: package does not look like a QuickLook-Next build");
                return false;
            }

            File.WriteAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] package {tag}{Environment.NewLine}" +
                $"url     {downloadUrl}{Environment.NewLine}" +
                $"sha256  {sha256}{Environment.NewLine}");

            // xcopy /EXCLUDE matches substrings of the full path; keep the
            // portable user data (settings, WebView2 cache) out of the backup
            // and out of the replacement.
            File.WriteAllText(excludePath, @"\UserData\" + Environment.NewLine);

            var batPath = Path.Combine(workDir, "update.cmd");
            File.WriteAllText(batPath, BuildUpdateScript(appDir, extractDir, workDir, logPath, excludePath));

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Auto update failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// v3.31.0: downloads <paramref name="url"/> into <paramref name="targetPath"/>
    /// and returns the SHA-256 of the downloaded package, or null when the
    /// download was rejected.
    /// </summary>
    private static string DownloadPackage(string url, string targetPath)
    {
        using var download = CreateHttpClient(TimeSpan.FromMinutes(5));
        using var response = download
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();

        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaxPackageBytes)
        {
            ProcessHelper.WriteLog($"Auto update refused: package announces {declaredLength} bytes");
            return null;
        }

        using (var content = response.Content.ReadAsStream())
        using (var file = File.Create(targetPath))
        {
            CopyWithLimit(content, file, MaxPackageBytes);
        }

        using var package = File.OpenRead(targetPath);
        using var sha = SHA256.Create();

        return Convert.ToHexString(sha.ComputeHash(package));
    }

    private static void CopyWithLimit(Stream source, Stream destination, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidDataException($"Update package exceeds the {limit} byte limit");

            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// v3.31.0: only https URLs on GitHub hosts may deliver an update package.
    /// </summary>
    internal static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        foreach (var host in TrustedDownloadHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BuildUpdateScript(string appDir, string extractDir, string workDir,
        string logPath, string excludePath)
    {
        return $"""
            @echo off
            setlocal enableextensions
            set "QL_APP={appDir}"
            set "QL_SRC={extractDir}"
            set "QL_WORK={workDir}"
            set "QL_BAK={workDir}\backup"
            set "QL_LOG={logPath}"
            echo [%DATE% %TIME%] update start>> "%QL_LOG%"
            :wait
            tasklist /FI "IMAGENAME eq QuickLook-Next.exe" 2>nul | find /I "QuickLook-Next.exe" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            if exist "%QL_BAK%" rd /S /Q "%QL_BAK%" >nul 2>&1
            mkdir "%QL_BAK%" >nul 2>&1
            xcopy "%QL_APP%\*" "%QL_BAK%\" /E /Y /Q /I /EXCLUDE:"{excludePath}" >> "%QL_LOG%" 2>&1
            if errorlevel 1 goto abort
            for %%f in ("%QL_APP%\*") do del /Q "%%f" >nul 2>&1
            for /d %%d in ("%QL_APP%\*") do if /I not "%%~nxd"=="UserData" rd /S /Q "%%d" >nul 2>&1
            if exist "%QL_BAK%\portable.lock" copy /Y "%QL_BAK%\portable.lock" "%QL_APP%\portable.lock" >nul 2>&1
            xcopy "%QL_SRC%\*" "%QL_APP%\" /E /Y /Q /I >> "%QL_LOG%" 2>&1
            if errorlevel 1 goto rollback
            if not exist "%QL_APP%\QuickLook-Next.exe" goto rollback
            if exist "%QL_APP%\QuickLook.Common.dll" goto installed
            if exist "%QL_APP%\lib\QuickLook.Common.dll" goto installed
            goto rollback
            :installed
            echo [%DATE% %TIME%] update ok>> "%QL_LOG%"
            goto restart
            :rollback
            echo [%DATE% %TIME%] copy failed - restoring backup>> "%QL_LOG%"
            for %%f in ("%QL_APP%\*") do del /Q "%%f" >nul 2>&1
            for /d %%d in ("%QL_APP%\*") do if /I not "%%~nxd"=="UserData" rd /S /Q "%%d" >nul 2>&1
            xcopy "%QL_BAK%\*" "%QL_APP%\" /E /Y /Q /I >> "%QL_LOG%" 2>&1
            goto restart
            :abort
            echo [%DATE% %TIME%] backup failed - update aborted>> "%QL_LOG%"
            :restart
            copy /Y "%QL_LOG%" "%TEMP%\QuickLookNext-update.log" >nul 2>&1
            start "" "%QL_APP%\QuickLook-Next.exe"
            del "%~f0" >nul 2>&1
            rd /S /Q "%QL_WORK%" >nul 2>&1
            """;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".ql-update-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void OpenReleasesPage()
    {
        try
        {
            // v1.3.9: shell URIs need UseShellExecute on .NET Core.
            Process.Start(new ProcessStartInfo("https://github.com/Adstrax/QuickLook-Next/releases/latest")
            {
                UseShellExecute = true,
            });
        }
        catch (Win32Exception)
        {
            // No default browser; ignore.
        }
    }

    /// <summary>
    /// Test hook for the auto-update pipeline: feeds a (possibly fake) release
    /// object into the same download/install path used by CheckForUpdates.
    /// </summary>
    internal static bool RunAutoUpdate(dynamic release) => TryAutoUpdate(release);

    private static dynamic DownloadJson(string url)
    {
        var json = Http.GetStringAsync(url).GetAwaiter().GetResult();
        return JsonConvert.DeserializeObject<dynamic>(json);
    }
}
