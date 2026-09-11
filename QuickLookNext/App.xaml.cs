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
using QuickLookNext.Helpers;
using QuickLookNext.NativeMethods;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Violeta.Appearance;

namespace QuickLookNext;

public partial class App : Application
{
    public static readonly string LocalDataPath = SettingHelper.LocalDataPath;
    public static readonly string UserPluginPath = Path.Combine(SettingHelper.LocalDataPath, @"QuickLook.Plugin\");
    // v1.2.16: Assembly.Location points at QuickLook-Next.dll under the .NET apphost,
    // but everything that launches the app (startup shortcut, shell command,
    // restart) must target the executable. Resolve the .exe next to it.
    private static readonly string AssemblyLocation = Assembly.GetExecutingAssembly().Location;

    public static readonly string AppFullPath =
        Path.ChangeExtension(AssemblyLocation, ".exe") is { } exePath && File.Exists(exePath)
            ? exePath
            : AssemblyLocation;

    public static readonly string AppPath = Path.GetDirectoryName(AppFullPath);
    public static readonly bool Is64Bit = Environment.Is64BitProcess;
    public static readonly bool IsArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    public static readonly bool IsUWP = ProcessHelper.IsRunningAsUWP();
    public static readonly bool IsWin11 = Environment.OSVersion.Version >= new Version(10, 0, 21996);
    public static readonly bool IsWin10 = !IsWin11 && Environment.OSVersion.Version >= new Version(10, 0);
    public static readonly bool IsPortable = SettingHelper.IsPortableVersion();

    // Hidden test hook (/test-timing): the preview window writes a timing entry
    // whenever content becomes ready (IsBusy -> false) so automated benches
    // can measure the real "spinner until content shows" latency.
    internal static bool IsTimingEnabled { get; private set; }

    // Hidden test hook (/test-startup): record the elapsed time of each
    // startup phase to %TEMP%\ql-smoke\startup.txt so startup bottlenecks can
    // be measured instead of guessed.
    internal static bool IsStartupTimingEnabled { get; private set; }
    private static readonly Stopwatch StartupSw = new();
    private static readonly object StartupDiagLock = new();

    // Smoke-test / bench diagnostics directory. The test scripts redirect it
    // into the repository (E:\Codex\QK-Lite\<version>\ql-smoke) with the
    // QL_SMOKE_DIR environment variable; standalone use keeps the default
    // %TEMP%\ql-smoke.
    internal static string SmokeDir =>
        Environment.GetEnvironmentVariable("QL_SMOKE_DIR")
        ?? Path.Combine(Path.GetTempPath(), "ql-smoke");

    // Hidden test hook (/test-no-focusmonitor): disables the selection-follow
    // polling so automated preview benches are not disturbed by Explorer's
    // current selection.
    internal static bool DisableFocusMonitor { get; private set; }

    // Hidden test hook (/test-preview-diag): the preview window writes
    // preview-backdrop.txt (layered flag + WCA accent result) into SmokeDir
    // after every backdrop application, so tests can assert the acrylic
    // render path really succeeded instead of guessing from pixels.
    internal static bool IsPreviewDiagEnabled { get; private set; }

    // Hidden test hook (/test-memory): record private bytes / working set /
    // managed heap / LOH / GC counts / WebView2 process count into
    // ql-smoke\memory.txt at startup, on every preview open/close and every
    // 10 s while the session runs (see Helpers/MemoryDiagnostics.cs).
    internal static bool IsMemoryDiagnosticsEnabled { get; private set; }

    // The WMI video-controller query used by the blacklist check can take
    // hundreds of milliseconds on some machines. Compute it lazily on a
    // background thread (kicked off in OnStartup) so it never blocks the
    // critical startup path; the first preview may still wait for it once if
    // it hasn't finished by then.
    private static readonly Lazy<bool> _gpuInBlacklist = new(
        () => SystemHelper.IsGPUInBlacklist(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsGPUInBlacklist => _gpuInBlacklist.Value;

    private bool _cleanExit = true;
    private Mutex _isRunning;
    // v3.27.0: guards the exception-report window against re-entrancy. If the
    // report itself fails to render, re-showing it from inside the dispatcher
    // exception handler would recurse and flood the log.
    private bool _exceptionReportShowing;

    static App()
    {
        var processRenderMode = SettingHelper.Get("ProcessRenderMode", failsafe: (int)RenderMode.Default, "QuickLookNext");
        if (processRenderMode == (int)RenderMode.SoftwareOnly)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        // Explicitly set to PerMonitor to avoid being overridden by the system
        if (SHCore.SetProcessDpiAwareness(SHCore.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE) is uint result)
        {
            Debug.WriteLine(
                result == 0 ?
                "DPI Awareness applied successfully" :
                $"DPI Awareness manual setup failed. Error Code: {result}"
            );
        }

    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Kick off the GPU blacklist check in the background so it overlaps
        // with the rest of startup instead of blocking it.
        _ = Task.Run(() => _ = _gpuInBlacklist.Value);

        // v3.28.0: preload the plugin-usage counter on a background thread so
        // the first preview never pays the one-time stats file read.
        _ = Task.Run(PluginUsageTracker.Preload);

        IsTimingEnabled = e.Args.Contains("/test-timing");
        IsStartupTimingEnabled = e.Args.Contains("/test-startup");
        DisableFocusMonitor = e.Args.Contains("/test-no-focusmonitor");
        IsPreviewDiagEnabled = e.Args.Contains("/test-preview-diag");
        IsMemoryDiagnosticsEnabled = e.Args.Contains("/test-memory");
        if (IsMemoryDiagnosticsEnabled)
            Helpers.MemoryDiagnostics.Start();
        if (IsPreviewDiagEnabled)
        {
            try
            {
                Directory.CreateDirectory(SmokeDir);
                File.AppendAllText(Path.Combine(SmokeDir, "topbar-hook.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} diag-flag-on{Environment.NewLine}");
            }
            catch
            {
                // diagnostics must never affect startup
            }
        }
        if (IsStartupTimingEnabled)
        {
            StartupSw.Start();
            RecordStartupPhase("onstartup-begin");
        }

        if (!EnsureOSVersion()
         || !EnsureFirstInstance(e.Args)
         || !EnsureFolderWritable(SettingHelper.LocalDataPath))
        {
            _cleanExit = false;
            Shutdown();
            return;
        }

        RunListener(e);
        RecordStartupPhase("after-runlistener");

        // Hidden test hook (/test-update-script): writes the real update script for
        // the paths listed in <smokeDir>\update-request.txt and exits, so the smoke
        // test can run the actual file replacement (wait for exit, backup, swap,
        // keep UserData) without downloading a package from GitHub.
        if (e.Args.Contains("/test-update-script"))
        {
            try
            {
                var request = File.ReadAllLines(Path.Combine(SmokeDir, "update-request.txt"));
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "QuickLookNext-update.cmd"),
                    Helpers.Updater.BuildUpdateScriptForTest(
                        request[0], request[1], request[2], request[3]));
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog($"/test-update-script failed: {ex}");
            }

            _cleanExit = true;
            Shutdown();
            return;
        }

        // Hidden test hook: open the tray menu once so the smoke test can
        // verify the Mica tray menu renders without errors.
        if (e.Args.Contains("/test-tray-menu"))
        {
            Dispatcher.BeginInvoke(new Action(TrayIconManager.ShowTestMenu),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Hidden test hook (/test-plugin-manager): open the plugin management
        // panel once and dump the enumerated plugin list for the smoke test.
        if (e.Args.Contains("/test-plugin-manager"))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var managerWindow = new PluginManagerWindow();
                managerWindow.Show();

                Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(SmokeDir);
                        File.WriteAllText(
                            Path.Combine(SmokeDir, "plugin-manager.txt"),
                            $"title={managerWindow.Title}\nplugins={managerWindow.DiagnosePlugins()}\n" +
                            $"backdrop={managerWindow.DiagnoseBackdrop()}\nuserPath={App.UserPluginPath}");
                    }
                    catch
                    {
                        // diagnostics must never affect startup
                    }
                }));
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Hidden test hook (/test-uninstall-plugin): exercise the user-plugin
        // uninstall happy path against a throwaway folder and record the result.
        if (e.Args.Contains("/test-uninstall-plugin"))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var testDir = Path.Combine(
                        App.UserPluginPath, "QuickLook.Plugin.TestUninstall." + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(testDir);
                    File.WriteAllText(Path.Combine(testDir, "readme.txt"), "test");

                    var entry = new PluginEntry("TestUninstall", "0.0.0", string.Empty, testDir, true);
                    var ok = PluginManager.GetInstance().UninstallUserPlugin(
                        entry, out var error, out var restartRequired);

                    Directory.CreateDirectory(SmokeDir);
                    File.WriteAllText(Path.Combine(SmokeDir, "plugin-manager-uninstall.txt"),
                        $"ok={ok} restart={restartRequired} exists={Directory.Exists(testDir)} " +
                        $"pendingExists={Directory.Exists(testDir + ".uninstalled")} error={error}");
                }
                catch (Exception ex)
                {
                    try
                    {
                        Directory.CreateDirectory(SmokeDir);
                        File.WriteAllText(Path.Combine(SmokeDir, "plugin-manager-uninstall.txt"),
                            "exception=" + ex);
                    }
                    catch
                    {
                        // diagnostics must never affect startup
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Hidden test hook (/test-auto-update): feeds a fake release JSON
        // (ql-smoke\fake-release.json) into the auto-update pipeline so the
        // download / replace / restart flow can be verified end to end.
        // v3.35.0: /test-update-prompt feeds the same fake release into the
        // "update now / ignore" prompt, so the dialog can be exercised (and its
        // answer checked in QuickLookNext.config) without touching GitHub.
        if (e.Args.Contains("/test-update-prompt"))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var fakePath = Path.Combine(SmokeDir, "fake-release.json");
                    var release = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(fakePath));
                    Updater.PromptForTest(release);
                }
                catch (Exception ex)
                {
                    ProcessHelper.WriteLog($"/test-update-prompt failed: {ex}");
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        if (e.Args.Contains("/test-auto-update"))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var fakePath = Path.Combine(SmokeDir, "fake-release.json");
                    var release = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(fakePath));
                    var ok = Updater.RunAutoUpdate(release);
                    Directory.CreateDirectory(SmokeDir);
                    File.WriteAllText(Path.Combine(SmokeDir, "auto-update.txt"), $"ok={ok}");
                    if (ok)
                        Shutdown();
                }
                catch (Exception ex)
                {
                    try
                    {
                        Directory.CreateDirectory(SmokeDir);
                        File.WriteAllText(Path.Combine(SmokeDir, "auto-update.txt"), "exception=" + ex);
                    }
                    catch
                    {
                        // diagnostics must never affect startup
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // First instance: run and preview this file
        if (e.Args.Any())
        {
            try
            {
                var path = Path.GetFullPath(e.Args.First());
                if (Directory.Exists(path) || File.Exists(path))
                    PipeServerManager.PostMessage(PipeMessages.Toggle, path);
            }
            catch
            {
                // Invalid path, ignore
            }
        }

        // Exception handling events which are not caught in the Task thread
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ProcessHelper.WriteLog(e.Exception.ToString());
            e.SetObserved();
        };

        // Exception handling events which are not caught in UI thread
        DispatcherUnhandledException += (_, e) =>
        {
            // https://learn.microsoft.com/en-us/troubleshoot/developer/dotnet/framework/general/wpf-render-thread-failures
            if (e.Exception.Message.StartsWith("UCEERR_RENDERTHREADFAILURE")
             && e.Exception.Message.Contains("0x88980406"))
            {
                ProcessHelper.WriteLog(e.Exception.ToString());

                // Under this exception, WPF rendering has crashed
                // and the user must be notified using native MessageBox
                var result = User32.MessageBoxW(
                    new WindowInteropHelper(Current.MainWindow).Handle,
                    $"""
                    {e.Exception.Message} was most often due to a lack of graphics resources or hardware/driver constraints when attempting to allocate large textures.

                    Although not usually recommended, would you prefer to use software rendering exclusively?
                    """,
                    "Fatal",
                    User32.MessageBoxType.YesNo | User32.MessageBoxType.IconError | User32.MessageBoxType.DefButton2
                );

                if (result == User32.MessageBoxResult.IDYES)
                {
                    SettingHelper.Set("ProcessRenderMode", (int)RenderMode.SoftwareOnly, "QuickLookNext");
                }

                TrayIconManager.GetInstance().Restart(forced: true);
                e.Handled = true;
                return;
            }

            try
            {
                ProcessHelper.WriteLog(e.Exception.ToString());

                if (_exceptionReportShowing)
                {
                    e.Handled = true;
                    return;
                }

                _exceptionReportShowing = true;
                Current?.Dispatcher?.BeginInvoke(() =>
                {
                    try
                    {
                        Wpf.Ui.Violeta.Controls.ExceptionReport.Show(e.Exception);
                    }
                    finally
                    {
                        // Reset on the next idle pass: the nested
                        // DispatcherUnhandledException (when the report itself
                        // throws) is processed before Background priority, so
                        // it sees the flag still set and does not recurse.
                        Current?.Dispatcher?.BeginInvoke(
                            () => _exceptionReportShowing = false,
                            System.Windows.Threading.DispatcherPriority.Background);
                    }
                });
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog(ex.ToString());
            }
            finally
            {
                e.Handled = true;
            }
        };

        // Exception handling events which are not caught in Non-UI thread
        // Such as a child thread created by ourself
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    ProcessHelper.WriteLog(ex.ToString());
                    Current?.Dispatcher?.BeginInvoke(() =>
                    {
                        Wpf.Ui.Violeta.Controls.ExceptionReport.Show(ex);
                    });
                }
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog(ex.ToString());
            }
            finally
            {
                // Ignore
            }
        };

        // We should improve the performance of the CLI application
        // Therefore, the time-consuming initialization code can't be placed before `OnStartup`
        base.OnStartup(e);
        RecordStartupPhase("after-base-onstartup");

        // Set initial theme based on system settings
        ThemeManager.Apply(OSThemeHelper.AppsUseDarkTheme() ? ApplicationTheme.Dark : ApplicationTheme.Light);
        RecordStartupPhase("after-theme");

        // v1.2.33: MessageBox patching (Harmony/MonoMod) costs ~1.2 s of UI
        // thread time on .NET 10, blocking the tray from becoming responsive.
        // Defer it to a background task: until the patch lands, MessageBox
        // calls simply use the default WPF dialog (same behavior, different
        // look), and the patch is process-wide once applied.
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            MessageBoxPatcher.Initialize();
            RecordStartupPhase("messagebox-patch-done");
        });
        RecordStartupPhase("after-messagebox-patch-deferred");

        CheckUpdate();

        // v3.40.0: a failed update used to be silent (the app just came back at the
        // old version); tell the user what happened and where to get the package.
        _ = Task.Delay(4000).ContinueWith(_ =>
        {
            try
            {
                Dispatcher.Invoke(Updater.ReportLastUpdateResult);
            }
            catch
            {
                // Reporting must never affect startup.
            }
        });

        CheckAndRegisterPluginIcon();
        RecordStartupPhase("onstartup-end");

        // v3.42.0: the first preview of each family is the expensive one (panel
        // XAML/JIT, native libraries, WebView2 environment). Prepare the families
        // this user actually opens, in the background, while they are still busy
        // elsewhere - see PreviewWarmUp for the measurements behind it.
        Helpers.PreviewWarmUp.Start();
    }

    internal static void RecordStartupPhase(string phase)
    {
        if (!IsStartupTimingEnabled)
            return;

        try
        {
            var dir = SmokeDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup.txt"),
                $"{StartupSw.ElapsedMilliseconds}|{phase}{Environment.NewLine}");
        }
        catch
        {
            // The hook is for measurement only; never break startup.
        }
    }

    /// <summary>
    /// /test-startup diagnostic: record how long each plugin's Init took so
    /// startup bottlenecks can be attributed to a specific plugin instead of
    /// measured as one opaque "plugins-inited" number.
    /// </summary>
    internal static void RecordPluginInitPhase(string pluginName, long elapsedMs)
    {
        if (!IsStartupTimingEnabled)
            return;

        try
        {
            // Plugin Inits run on parallel worker threads; serialize the
            // append so lines never interleave.
            lock (StartupDiagLock)
            {
                var dir = SmokeDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "plugin-init.txt"),
                    $"{elapsedMs}|{pluginName}{Environment.NewLine}");
            }
        }
        catch
        {
            // The hook is for measurement only; never break startup.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        if (!_cleanExit)
            return;

        PluginUsageTracker.Flush();

        _isRunning.ReleaseMutex();

        PipeServerManager.GetInstance().Dispose();
        TrayIconManager.GetInstance().Dispose();
        KeystrokeDispatcher.GetInstance().Dispose();
        ViewWindowManager.GetInstance().Dispose();
    }

    private bool EnsureOSVersion()
    {
        if (!ProcessHelper.IsOnWindows10S())
            return true;

        MessageBox.Show("This application does not run on Windows 10 S.");

        return false;
    }

    private bool EnsureFolderWritable(string folder)
    {
        try
        {
            var path = FileHelper.CreateTempFile(folder);
            File.Delete(path);
        }
        catch
        {
            MessageBox.Show(string.Format(TranslationHelper.Get("APP_PATH_NOT_WRITABLE"), folder), "QuickLook-Next",
                MessageBoxButton.OK, MessageBoxImage.Error);

            return false;
        }

        return true;
    }

    private bool EnsureFirstInstance(string[] args)
    {
        _isRunning = new Mutex(true, StartupForwarder.MutexName, out bool isFirst);

        if (isFirst)
            return true;

        // Second instance: preview this file
        if (args.Any())
        {
            try
            {
                var path = Path.GetFullPath(args.First());
                if (Directory.Exists(path) || File.Exists(path))
                {
                    PipeServerManager.PostMessage(PipeMessages.Toggle, path, [.. args.Skip(1)]);
                    return false;
                }
            }
            catch
            {
                // Invalid path, continue to show duplicate message
            }
        }

        // Second instance: duplicate
        MessageBox.Show(TranslationHelper.Get("APP_SECOND_TEXT"), TranslationHelper.Get("APP_SECOND"),
            MessageBoxButton.OK, MessageBoxImage.Information);

        return false;
    }

    private void CheckUpdate()
    {
        if (SettingHelper.Get("DisableAutoUpdateCheck", false))
            return;

        // v3.35.0: check once a day instead of once a month. The 30 day window is
        // why a freshly updated install (which stamps the check on every successful
        // start) never noticed the next release for weeks.
        if (DateTime.Now.Ticks - SettingHelper.Get<long>("LastUpdateTicks") < TimeSpan.FromDays(1).Ticks)
            return;

        // v3.31.0: the "last checked" stamp is written by Updater itself, once
        // the release API call actually succeeded (see Updater.CheckForUpdates).
        // Writing it here marked the check as done even when it failed offline.
        _ = Task.Delay(120 * 1000).ContinueWith(_ => Updater.CheckForUpdates(true));
    }

    private void CheckAndRegisterPluginIcon()
    {
        // TODO: only /register-plugin-icon command to register plugin icon immediately, and can be removed
        _ = Task.Delay(3000).ContinueWith(_ => PluginIconRegistrationHelper.CheckAndRegisterPluginIcon());
    }

    private void RunListener(StartupEventArgs e)
    {
        TrayIconManager.Start();
        if (!e.Args.Contains("/autorun") && !IsUWP)
            TrayIconManager.ShowNotification(string.Empty, TranslationHelper.Get("APP_START"));
        if (e.Args.Contains("/first"))
            AutoStartupHelper.CreateAutorunShortcut();

        NativeMethods.QuickLookNext.Init();

        PluginManager.GetInstance();
        ViewWindowManager.GetInstance();
        KeystrokeDispatcher.GetInstance();
        PipeServerManager.GetInstance();
    }
}
