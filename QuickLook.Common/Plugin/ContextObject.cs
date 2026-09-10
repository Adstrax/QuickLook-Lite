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

using QuickLook.Common.Annotations;
using QuickLook.Common.Helpers;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace QuickLook.Common.Plugin;

/// <summary>
/// A runtime object which allows interaction between this plugin and QuickLookNext.
/// </summary>
public class ContextObject : INotifyPropertyChanged
{
    private bool _canResize = true;
    private bool _fullWindowDragging;
    private bool _isBusy;
    private string _title = string.Empty;
    // v1.2.0: all previews default to the modern look — the toolbar auto-hides,
    // the content extends under it (no dark strip), and the bar itself is
    // transparent so the window's Mica backdrop shows through. Plugins that need
    // a different look (e.g. video's glass bar) override these explicitly.
    private bool _titlebarAutoHide = true;
    private bool _titlebarBlurVisibility;
    private bool _titlebarColourVisibility;
    private bool _titlebarOverlap = true;
    private Themes _theme = Themes.None;
    private object _viewerContent;
    private string _colorProfileName = null;
    private bool _isBlocked;

    /// <summary>
    /// Get the instance of Viewer window.
    /// </summary>
    public object Source { get; set; }

    /// <summary>
    /// Get or set the title of Viewer window.
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Get or set the viewer content control.
    /// </summary>
    public object ViewerContent
    {
        get => _viewerContent;
        set
        {
            _viewerContent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// v1.2.14: content a plugin has prepared but wants shown only when it is
    /// actually ready (e.g. the first image frame decoded). The viewer window
    /// applies this when <see cref="IsBusy"/> turns false.
    /// </summary>
    public object PendingViewerContent { get; set; }

    /// <summary>
    /// v1.2.14: when true, the viewer window keeps the window (and the previous
    /// preview) at its current size until the pending content is applied, so a
    /// switch to an image with a different aspect ratio does not resize early
    /// and show gray letterbox bands while the new frame decodes.
    /// </summary>
    public bool DeferResizeUntilReady { get; set; }

    /// <summary>
    /// v1.2.14: whether the busy spinner should be visible. During preview
    /// switches the previous content stays on screen, so the spinner is hidden
    /// and only shown for the initial load of a preview.
    /// </summary>
    public bool ShowBusyIndicator { get; set; } = true;

    /// <summary>
    /// Show or hide the busy indicator icon.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Set the exact size you want.
    /// </summary>
    public Size PreferredSize { get; set; } = new Size { Width = 800, Height = 600 };

    /// <summary>
    /// Set whether user are allowed to resize the viewer window.
    /// </summary>
    public bool CanResize
    {
        get => _canResize;
        set
        {
            _canResize = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Set whether the full viewer window can be used for mouse dragging.
    /// </summary>
    public bool FullWindowDragging
    {
        get => _fullWindowDragging;
        set
        {
            _fullWindowDragging = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Set whether the viewer content is overlapped by the title bar
    /// </summary>
    public bool TitlebarOverlap
    {
        get => _titlebarOverlap;
        set
        {
            _titlebarOverlap = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Set whether the title bar shows a blurred background
    /// </summary>
    public bool TitlebarBlurVisibility
    {
        get => _titlebarBlurVisibility;
        set
        {
            if (value == _titlebarBlurVisibility) return;
            _titlebarBlurVisibility = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Set whether the title bar shows a colour overlay
    /// </summary>
    public bool TitlebarColourVisibility
    {
        get => _titlebarColourVisibility;
        set
        {
            if (value == _titlebarColourVisibility) return;
            _titlebarColourVisibility = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Should the titlebar hides itself after a short period of inactivity?
    /// </summary>
    public bool TitlebarAutoHide
    {
        get => _titlebarAutoHide;
        set
        {
            if (value == _titlebarAutoHide) return;
            _titlebarAutoHide = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Switch to dark theme?
    /// </summary>
    public Themes Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The color profile of the monitor that will host the preview window
    /// </summary>
    public string ColorProfileName
    {
        get => _colorProfileName;
        set
        {
            _colorProfileName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Get or set whether to block showing the preview window
    /// Display "blocked" in the preview window if true
    /// </summary>
    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            _isBlocked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Set the size of viewer window, scale or shrink to fit (to screen resolution).
    /// The window can take maximum (maxRatio*resolution) space.
    /// </summary>
    /// <param name="size">The desired size.</param>
    /// <param name="maxRatio">The maximum percent (over screen resolution) it can take.</param>
    public double SetPreferredSizeFit(Size size, double maxRatio)
    {
        if (maxRatio > 1)
            maxRatio = 1;

        var max = WindowHelper.GetCurrentDesktopSize();

        var widthRatio = max.Width * maxRatio / size.Width;
        var heightRatio = max.Height * maxRatio / size.Height;

        var ratio = Math.Min(widthRatio, heightRatio);
        if (ratio > 1) ratio = 1;

        PreferredSize = new Size { Width = size.Width * ratio, Height = size.Height * ratio };

        return ratio;
    }

    /// <summary>
    /// v3.31.0: raised when a plugin asks the host to re-apply
    /// <see cref="PreferredSize"/> to the preview window right away (see
    /// <see cref="ApplyPreferredSizeNow"/>). The viewer window subscribes to it;
    /// plugins only call the method, so they no longer have to reach into the
    /// host window through reflection.
    /// </summary>
    public event Action<Size> ResizeRequested;

    /// <summary>
    /// v3.31.0: asks the host to resize and recentre the preview window to the
    /// current <see cref="PreferredSize"/> immediately - used after a plugin has
    /// measured its content (e.g. the first page of a PDF) and the window has to
    /// follow before that content is shown.
    /// <para>
    /// Returns false when no host is listening (for example in plugin unit tests,
    /// or when the context is used outside the viewer window); a plugin should
    /// then simply continue without a resize.
    /// </para>
    /// </summary>
    public bool ApplyPreferredSizeNow()
    {
        var handler = ResizeRequested;
        if (handler == null)
            return false;

        handler(PreferredSize);

        return true;
    }

    public void Reset()
    {
        Title = string.Empty;
        // set to False to prevent showing loading icon
        IsBusy = false;
        PreferredSize = new Size();
        CanResize = true;
        FullWindowDragging = false;

        // v1.2.2: reset to the modern defaults (auto-hide, content overlap,
        // transparent toolbar). Previously this hard-coded the old values and
        // silently reverted every non-image preview to the legacy black toolbar.
        TitlebarOverlap = true;
        TitlebarAutoHide = true;
        TitlebarBlurVisibility = false;
        TitlebarColourVisibility = false;

        ViewerContent = null;
        PendingViewerContent = null;
        DeferResizeUntilReady = false;
        ShowBusyIndicator = true;

        ColorProfileName = null;
        IsBlocked = false;
    }

    [NotifyPropertyChangedInvocator]
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
