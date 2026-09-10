using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.Shared;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.16.0/v3.17.0: shared surface for the self-rendered Office previews.
/// The content always sits on a plain white paper-like surface (never
/// theme-adapted) so real documents stay readable in dark mode too.
/// </summary>
public abstract class OfficePanelBase : UserControl, IDisposable
{
    private readonly WebView2 _webView = new();
    private bool _disposed;

    protected OfficePanelBase()
    {
        // v3.26.0: the panel used to be opaque white from the moment it was
        // created, so switching to an Office file flashed a big white box
        // before the page loaded (especially jarring in dark mode). Match the
        // loading surface to the app theme; the white "paper" only appears
        // together with the rendered content. Stays opaque, so the v3.17.0
        // "transparent WebView2 composites to black" issue cannot reappear.
        var tint = IsDarkTheme()
            ? WpfColor.FromRgb(0x17, 0x17, 0x17)
            : WpfColor.FromRgb(0xF2, 0xF2, 0xF2);

        _webView.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(SettingHelper.LocalDataPath, @"WebView2_Data\"),
        };
        // The control background only shows before the page paints; the page
        // itself is an opaque white paper surface (v3.17.0).
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            tint.A, tint.R, tint.G, tint.B);
        Content = new Border
        {
            Background = new SolidColorBrush(tint),
            Child = _webView,
        };

        // v3.29.0: track the control so the idle recycler can shut the
        // Chromium process group down after the last Office preview closes.
        WebView2Lifecycle.Register(_webView);
    }

    private static bool IsDarkTheme()
    {
        var theme = (Themes)SettingHelper.Get("LastTheme", (int)Themes.None, "QuickLookNext");
        return theme switch
        {
            Themes.Dark => true,
            Themes.Light => false,
            _ => OSThemeHelper.AppsUseDarkTheme(),
        };
    }

    protected void Navigate(string html)
    {
        // v3.32.0: guard the navigation on a successfully initialized controller.
        // Navigating after a faulted EnsureCoreWebView2Async threw
        // "Attempted to use WebView2 functionality which requires its
        // CoreWebView2 prior to the CoreWebView2 being initialized". The
        // CoreWebView2 check itself has to happen on the UI thread - the
        // WebView2 control is a DispatcherObject.
        _ = _webView.EnsureCoreWebView2Async().ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                ProcessHelper.WriteLog($"[OfficePanel] CoreWebView2 init failed: {t.Exception?.GetBaseException().Message}");
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null)
                    return;

                _webView.NavigateToString(html);
            });
        });
    }

    /// <summary>
    /// v3.23.0: builds the HTML on a background thread (OOXML parsing can take
    /// hundreds of ms on large documents) and navigates back on the UI thread
    /// when ready, so the preview window stays responsive while the document
    /// parses.
    /// </summary>
    protected void NavigateAsync(Func<string> buildHtml)
    {
        var dispatcher = Dispatcher;

        _ = Task.Run(buildHtml).ContinueWith(t =>
        {
            dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                    return;

                if (t.IsFaulted)
                {
                    Navigate(ErrorHtml(t.Exception?.GetBaseException()));
                    return;
                }

                Navigate(t.Result);
            });
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _disposed = true;

        // v3.29.0: stop tracking first so the idle recycler counts this
        // control as gone even if Dispose below throws.
        WebView2Lifecycle.Unregister(_webView);
        _webView.Dispose();
    }

    private static string ErrorHtml(Exception error)
    {
        return
            """
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <style>body{margin:24px;font-family:'Segoe UI',sans-serif;font-size:14px;color:#C42B1C}</style>
            </head><body><div>无法读取此文档（文件可能已损坏或格式不受支持）。</div></body></html>
            """ + (error is null ? string.Empty : $"<!-- {error.Message} -->");
    }
}
