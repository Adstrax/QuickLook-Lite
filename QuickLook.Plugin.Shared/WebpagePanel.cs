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

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using QuickLook.Plugin.Shared.NativeMethods;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuickLook.Plugin.Shared;

public class WebpagePanel : UserControl
{
    protected Uri _currentUri;
    protected string _primaryPath;
    protected string _fallbackPath;
    protected WebView2 _webView;
    private bool _disposed;

    public string FallbackPath
    {
        get => _fallbackPath;
        set => _fallbackPath = value;
    }

    public WebpagePanel()
    {
        if (!Helper.IsWebView2Available())
            Content = CreateDownloadButton();
        else
            InitializeComponent();
    }

    protected virtual void InitializeComponent()
    {
        _webView = AcquireWebView();
        Content = _webView;
    }

    /// <summary>
    /// v3.39.0: takes a warm control from <see cref="WebView2ControlPool"/> (or a
    /// fresh one) and wires this panel to it. Creating a Chromium controller costs
    /// ~300-400 ms, which every web preview paid again and again. Derived panels
    /// that build their own content tree must call this instead of "new WebView2()".
    /// </summary>
    protected WebView2 AcquireWebView()
    {
        var control = WebView2ControlPool.Acquire(BrowserArguments);

        control.NavigationStarting += Webview_NavigationStarting;
        control.NavigationCompleted += WebView_NavigationCompleted;
        control.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;

        // v3.29.0: track the control so the idle recycler can shut the Chromium
        // process group down after the last web preview closes. A parked control is
        // unregistered again, so the recycler can still fire while it waits in the
        // pool.
        WebView2Lifecycle.Register(control);

        // A reused control is already initialized, so the initialization-completed
        // event never fires for it - re-apply the per-controller state here.
        ApplyControllerState(control);

        return control;
    }

    /// <summary>
    /// Extra Chromium switches this panel's controller needs (the force-dark switch
    /// the PlantUML panel uses, for example). Evaluated before the controller is
    /// created, so it must not depend on instance fields; controls are pooled per
    /// switch set.
    /// </summary>
    protected virtual string BrowserArguments => null;

    private void ApplyControllerState(WebView2 control)
    {
        try
        {
            var core = control?.CoreWebView2;
            if (core == null)
                return; // still initializing: the completed handler calls us again

            // v1.2.1: make the web content follow the app's manual light/dark toggle
            // (prefers-color-scheme inside the page matches this).
            core.Profile.PreferredColorScheme =
                AppThemeState.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

            // Re-attach per panel so resource interception (fallback directories,
            // embedded pages) always belongs to the panel that is on screen.
            core.WebResourceRequested -= WebView_WebResourceRequested;
            core.WebResourceRequested += WebView_WebResourceRequested;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            OnControllerReady();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebpagePanel] applying controller state failed: {e.Message}");
        }
    }

    /// <summary>
    /// Called whenever this panel gets a usable CoreWebView2 - on a fresh controller
    /// and on a reused one. Derived panels that register controller or control level
    /// handlers override this instead of the one-shot
    /// <c>WebView_CoreWebView2InitializationCompleted</c>.
    /// </summary>
    protected virtual void OnControllerReady()
    {
    }

    /// <summary>
    /// Counterpart of <see cref="OnControllerReady"/>: detach everything added
    /// there. The control outlives this panel in the pool, so a handler left behind
    /// would keep the old panel alive and run against the next document.
    /// </summary>
    protected virtual void OnControllerReleasing()
    {
    }

    private void ReleaseWebView()
    {
        var control = _webView;

        if (control == null)
            return;

        // Derived panels clean up first - _webView is still valid for them.
        try
        {
            OnControllerReleasing();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebpagePanel] releasing controller state failed: {e.Message}");
        }

        _webView = null;

        control.NavigationStarting -= Webview_NavigationStarting;
        control.NavigationCompleted -= WebView_NavigationCompleted;
        control.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;

        try
        {
            if (control.CoreWebView2 != null)
                control.CoreWebView2.WebResourceRequested -= WebView_WebResourceRequested;
        }
        catch
        {
            // best effort - the controller may already be gone
        }

        WebView2Lifecycle.Unregister(control);

        if (ReferenceEquals(Content, control))
            Content = null;

        WebView2ControlPool.Release(control);
    }

    public void NavigateToFile(string path)
    {
        try
        {
            _primaryPath = Path.GetDirectoryName(path);
        }
        catch (Exception e)
        {
            // Omit logging for less important logs
            Debug.WriteLine(e);
        }

        var uri = Path.IsPathRooted(path) ? Helper.FilePathToFileUrl(path) : new Uri(path);

        NavigateToUri(uri);
    }

    public void NavigateToUri(Uri uri)
    {
        if (_webView == null)
            return;

        _webView.Source = uri;
        _currentUri = _webView.Source;
    }

    public void NavigateToHtml(string html)
    {
        // v3.32.0: only navigate when the controller really came up - a faulted
        // EnsureCoreWebView2Async (WebView2 runtime missing or being restarted)
        // used to surface as an unhandled "CoreWebView2 prior to being
        // initialized" exception. Chromium also fails transiently with
        // 0x8007139F right after a controller was torn down, so one retry is
        // enough to keep the preview working. The CoreWebView2 check runs on the
        // UI thread because the WebView2 control is a DispatcherObject.
        _ = NavigateWhenReadyAsync(html);
    }

    private async Task NavigateWhenReadyAsync(string html)
    {
        if (_webView == null)
            return;

        const int attempts = 3;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(
                    $"[WebpagePanel] CoreWebView2 init failed (attempt {attempt}/{attempts}): {e.GetBaseException().Message}");

                if (attempt == attempts)
                {
                    ShowInitializationFailure();
                    return;
                }

                // v3.36.0: give the browser process group a clean restart before
                // trying again - that is what makes a stale profile recoverable.
                WebView2Lifecycle.RecoverFromFailedInitialization();
                await Task.Delay(attempt == 1 ? 300 : 800);
                continue;
            }

            if (_disposed)
                return;

            // Touching CoreWebView2 requires the UI thread; marshalling keeps
            // this working when a plugin calls NavigateToHtml off-thread.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _webView?.CoreWebView2 == null)
                    return;

                _webView.NavigateToString(html);
            });

            return;
        }
    }

    /// <summary>
    /// v3.36.0: replaces the (blank) panel with an explanation when WebView2 could
    /// not be brought up at all, instead of leaving the user with an empty preview.
    /// </summary>
    private void ShowInitializationFailure()
    {
        if (_disposed)
            return;

        try
        {
            Content = new TextBlock
            {
                Text = TranslationHelper.Get("WEBVIEW2_INIT_FAILED",
                    failsafe: "WebView2 组件初始化失败。\n请重启应用；若仍然如此，删除程序目录 UserData\\WebView2_Data 后重试。",
                    domain: Assembly.GetExecutingAssembly().GetName().Name),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
            };
        }
        catch
        {
            // Never let the fallback throw.
        }
    }

    protected virtual void Webview_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:")) // when using NavigateToString
            return;

        var newUri = new Uri(e.Uri);
        if (newUri == _currentUri) return;
        e.Cancel = true;

        // Open in default browser
        try
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            {
                Debug.WriteLine($"Invalid URI format: {e.Uri}");
                return;
            }

            // Safe schemes can open directly
            if (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps ||
                uri.Scheme == Uri.UriSchemeMailto)
            {
                try
                {
                    Process.Start(uri.AbsoluteUri);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            // Ask user for unsafe schemes. Use dispatcher to avoid blocking thread.
            string associatedApp = ShlwApi.GetAssociatedAppForScheme(uri.Scheme);
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // TODO: translation
                var result = MessageBox.Show(
                    !string.IsNullOrEmpty(associatedApp) ?
                    $"The following link will open in {associatedApp}:\n{e.Uri}" : $"The following link will open:\n{e.Uri}",
                    !string.IsNullOrEmpty(associatedApp) ?
                    $"Open {associatedApp}?" : "Open custom URI?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(e.Uri);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open URL: {ex.Message}");
        }
    }

    protected virtual void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Keep the background transparent so Mica stays visible.
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
    }

    protected virtual void WebView_CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        // v3.29.0: the panel may have been disposed while CoreWebView2 was
        // still initializing; drop the freshly created controller so it
        // does not linger until the idle recycler runs.
        if (_disposed)
        {
            try { _webView?.Dispose(); }
            catch { /* best effort */ }
            return;
        }

        if (e.IsSuccess)
        {
            // v1.2.2: keep the page background transparent so the window's Mica
            // backdrop shows through. Runs once the DOM exists and uses
            // !important to beat the page's own background rules.
            // Injected once per controller and inherited by every later document,
            // which is why it stays here rather than in ApplyControllerState.
            _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('DOMContentLoaded',function(){" +
                "var s=document.createElement('style');" +
                "s.textContent='html,body,.markdown-body,#content,.ipynb-notebook,.jp-Notebook,.document{background:transparent!important}" +
                "::-webkit-scrollbar{width:4px;height:4px}" +
                "::-webkit-scrollbar-thumb{background:rgba(128,128,128,0.4);border-radius:2px}" +
                "::-webkit-scrollbar-track{background:transparent}" +
                "::-webkit-scrollbar-corner{background:transparent}';" +
                "document.head.appendChild(s);});");

            // Theme, resource hook and the derived panel's own controller state - the
            // same call the reuse path makes (see AcquireWebView).
            ApplyControllerState(_webView);
        }
        else
        {
            ProcessHelper.WriteLog($"[WebpagePanel] CoreWebView2 init failed: {e.InitializationException}");
        }
    }

    protected virtual void WebView_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(_fallbackPath) || !Directory.Exists(_fallbackPath))
        {
            return;
        }

        try
        {
            var requestedUri = new Uri(args.Request.Uri);

            if (requestedUri.Scheme == "file")
            {
                // Check if the request is for a local file
                if (!File.Exists(requestedUri.LocalPath))
                {
                    // Try loading from fallback directory
                    var fileName = Path.GetFileName(requestedUri.LocalPath);
                    var fileDirectoryName = Path.GetDirectoryName(requestedUri.LocalPath);

                    // Convert the primary path to fallback path
                    if (fileDirectoryName.StartsWith(_primaryPath))
                    {
                        var fallbackFilePath = Path.Combine(
                            _fallbackPath.Trim('/', '\\'), // Make it combinable
                            fileDirectoryName.Substring(_primaryPath.Length).Trim('/', '\\'), // Make it combinable
                            fileName
                        );

                        if (File.Exists(fallbackFilePath))
                        {
                            // Serve the file from the fallback directory
                            var fileStream = new FileStream(fallbackFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                fileStream, 200, "OK", "Content-Type: application/octet-stream");
                            args.Response = response;
                        }
                    }
                }
                // Check if the request exceeds MAX_PATH (260) limitation
                else if (requestedUri.LocalPath.Length >= 260)
                {
                    if (File.Exists(requestedUri.LocalPath))
                    {
                        var fileStream = new FileStream(requestedUri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            fileStream, 200, "OK", MimeTypes.GetContentType(Path.GetExtension(requestedUri.LocalPath)));
                        args.Response = response;
                    }
                }
            }
        }
        catch (Exception e)
        {
            // We don't need to feel burdened by any exceptions
            Debug.WriteLine(e);
        }
    }

    public void Dispose()
    {
        _disposed = true;

        // v3.39.0: the control goes back to the pool instead of being disposed - that
        // is what saves the controller creation on the next web preview.
        ReleaseWebView();
    }

    private object CreateDownloadButton()
    {
        var button = new Button
        {
            Content = TranslationHelper.Get("WEBVIEW2_NOT_AVAILABLE",
                domain: Assembly.GetExecutingAssembly().GetName().Name),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(20, 6, 20, 6)
        };
        button.Click += (sender, e) => Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703");

        return button;
    }

    public static class MimeTypes
    {
        public const string Html = "text/html";
        public const string JavaScript = "application/javascript";
        public const string Css = "text/css";
        public const string Json = "application/json";
        public const string Xml = "application/xml";
        public const string Svg = "image/svg+xml";
        public const string Png = "image/png";
        public const string Jpeg = "image/jpeg";
        public const string Gif = "image/gif";
        public const string Webp = "image/webp";
        public const string Ico = "image/x-icon";
        public const string Avif = "image/avif";
        public const string Woff = "font/woff";
        public const string Woff2 = "font/woff2";
        public const string Ttf = "font/ttf";
        public const string Otf = "font/otf";
        public const string Mp3 = "audio/mpeg";
        public const string Mp4 = "video/mp4";
        public const string Webm = "video/webm";
        public const string Pdf = "application/pdf";
        public const string Binary = "application/octet-stream";
        public const string Text = "text/plain";

        public static string GetContentType(string extension = null) => $"Content-Type: {GetMimeType(extension)}";

        /// <summary>
        /// Only handle known extensions from resources
        /// </summary>
        public static string GetMimeType(string extension = null) => extension?.ToLowerInvariant() switch
        {
            // Core web files
            ".html" or ".htm" => Html,
            ".js" => JavaScript,
            ".css" => Css,
            ".json" => Json,
            ".xml" => Xml,

            // Images
            ".png" => Png,
            ".jpg" or ".jpeg" => Jpeg,
            ".gif" => Gif,
            ".webp" => Webp,
            ".svg" => Svg,
            ".ico" => Ico,
            ".avif" => Avif,

            // Fonts
            ".woff" => Woff,
            ".woff2" => Woff2,
            ".ttf" => Ttf,
            ".otf" => Otf,

            // Media
            ".mp3" => Mp3,
            ".mp4" => Mp4,
            ".webm" => Webm,

            // Documents
            ".pdf" => Pdf,
            ".txt" => Text,

            // Archives
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",

            // Default
            _ => Binary,
        };
    }
}
