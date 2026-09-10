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

using PdfiumViewer;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuickLook.Plugin.PDFViewer;

public sealed class Plugin : IViewer
{
    private ContextObject _context;
    private string _path;
    private PdfViewerControl _pdfControl;
    private PasswordControl _passwordControl;
    private bool _disposed;

    public int Priority => -1;

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
            return false;

        var extension = Path.GetExtension(path);
        if (extension.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        // v1.2.35: files with a known extension are matched by extension only, so
        // previewing e.g. txt/zip files no longer opens the file on the UI thread
        // for nothing. Magic-number detection is reserved for extensionless files.
        if (extension.Length > 0)
            return false;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[4];
        if (fs.Read(buffer, 0, 4) < 4) return false;
        return buffer[0] == (byte)'%' &&
        buffer[1] == (byte)'P' &&
        buffer[2] == (byte)'D' &&
        buffer[3] == (byte)'F';
    }

    public void Prepare(string path, ContextObject context)
    {
        _context = context;
        _path = path;

        // Don't load the document just to size the window. View loads it once
        // and resizes afterwards, so the PDF is not parsed twice.
        context.PreferredSize = new Size { Width = 800, Height = 600 };
    }

    public void View(string path, ContextObject context)
    {
        _pdfControl = new PdfViewerControl();
        context.ViewerContent = _pdfControl;

        // v3.23.0: parsing a large PDF (Pdfium) can take hundreds of ms. Run
        // it off the UI thread so the preview window stays responsive (close /
        // scroll / switch) while the document loads; the parsed document is
        // applied back on the UI thread when ready.
        _ = Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new PdfDocumentWrapper(stream);
        }).ContinueWith(t =>
        {
            _pdfControl.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                {
                    if (t.IsCompletedSuccessfully)
                        t.Result.Dispose();
                    return;
                }

                if (t.IsFaulted)
                {
                    var error = t.Exception?.GetBaseException();
                    if (error is PdfException pe &&
                        pe.Message == "Password required or incorrect password")
                    {
                        ShowPasswordControl(path, context);
                        return;
                    }

                    ShowError(context, error?.ToString() ?? "Failed to load PDF.");
                    return;
                }

                try
                {
                    _pdfControl.LoadPdf(t.Result);

                    ResizeWindowToContent(context);

                    context.Title = $"1 / {_pdfControl.TotalPages}: {Path.GetFileName(path)}";

                    _pdfControl.CurrentPageChanged += UpdateWindowCaption;
                    context.IsBusy = false;
                }
                catch (Exception ex)
                {
                    ShowError(context, ex.ToString());
                }
            });
        }, TaskScheduler.Default);
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);
        _disposed = true;

        _pdfControl?.Dispose();
        _pdfControl = null;

        _context = null;
    }

    /// <summary>
    /// v3.23.0: password-protected PDF flow. The retry re-reads the file on
    /// the UI thread; it only happens on an explicit user action (typing the
    /// password), so it does not block preview startup.
    /// </summary>
    private void ShowPasswordControl(string path, ContextObject context)
    {
        _passwordControl = new PasswordControl();
        _passwordControl.PasswordRequested += (string password) =>
        {
            try
            {
                context.ViewerContent = _pdfControl;
                context.IsBusy = true;
                _pdfControl.LoadPdf(path, password);

                ResizeWindowToContent(context);

                context.Title = $"1 / {_pdfControl.TotalPages}: {Path.GetFileName(path)}";

                _pdfControl.CurrentPageChanged += UpdateWindowCaption;
                context.IsBusy = false;
            }
            catch (PdfException ex) when (ex.Message == "Password required or incorrect password")
            {
                // This password is not accepted
                return false;
            }

            // This password is accepted
            return true;
        };

        context.ViewerContent = _passwordControl;
        context.Title = $"[PASSWORD PROTECTED] {Path.GetFileName(path)}";
        context.IsBusy = false;
    }

    private static void ShowError(ContextObject context, string message)
    {
        context.ViewerContent = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(24),
        };
        context.IsBusy = false;
    }

    private void UpdateWindowCaption(object sender, EventArgs e2)
    {
        _context.Title = $"{_pdfControl.CurrentPage + 1} / {_pdfControl.TotalPages}: {Path.GetFileName(_path)}";
    }

    private void ResizeWindowToContent(ContextObject context)
    {
        context.SetPreferredSizeFit(_pdfControl.GetDesiredControlSize(), 0.9);

        // v3.31.0: ask the host through the public ContextObject API instead of
        // reflecting into QuickLookNext.ViewerWindow's private
        // ResizeAndCentreExistingWindow method. The plugin only needs
        // QuickLook.Common, so host internals can change freely again.
        context.ApplyPreferredSizeNow();
    }
}
