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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLook.Plugin.FontViewer;

/// <summary>
/// v3.37.0: renders a font preview with WPF instead of WebView2.
/// <para>
/// The previous preview built a small web page and loaded the font through a
/// WebView2 resource hook: that cost a Chromium controller (~300-400 ms) per
/// preview, needed the shared WebView2 profile (which could go stale and leave the
/// preview blank for the whole 1.5 s font timeout) and made fonts the slowest
/// format in the whole app. WPF can read TrueType/OpenType/Collection files
/// directly, so the common cases now render in a few milliseconds with no browser
/// involved. WOFF/WOFF2 stay on <see cref="WebfontPanel"/>.
/// </para>
/// </summary>
internal sealed class NativeFontPanel : UserControl, IFontPreviewPanel
{
    private const string Pangram = "The quick brown fox jumps over the lazy dog. 0123456789";
    private const string Mixed = "汉字 かな カナ 한글 Привет ¡Hola! ÀÉÎÕÜ";
    private const int MaxIconGlyphs = 512;

    private readonly StackPanel _content = new() { Margin = new Thickness(28, 20, 28, 28) };

    private FontFamily _family;
    private GlyphTypeface _glyphTypeface;

    public NativeFontPanel()
    {
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _content,
        };
    }

    public UIElement View => this;

    public void PreviewFont(string path)
    {
        _content.Children.Clear();

        if (!TryLoad(path))
            return;

        _content.Children.Add(BuildTitle(path));
        _content.Children.Add(BuildSubtitle($"{Path.GetFileName(path)}  ·  {_glyphTypeface?.CharacterToGlyphMap.Count ?? 0} glyphs"));

        foreach (var size in new[] { 12d, 16d, 24d, 36d, 48d })
            _content.Children.Add(BuildSample(size));

        _content.Children.Add(BuildCaption(TranslationHelper.Get("FontPreview_Charset", failsafe: "字符集")));
        _content.Children.Add(BuildCharset());
    }

    public void PreviewIconFont(string path)
    {
        _content.Children.Clear();

        if (!TryLoad(path))
            return;

        _content.Children.Add(BuildTitle(path));
        _content.Children.Add(BuildSubtitle(Path.GetFileName(path)));

        var glyphs = _glyphTypeface?.CharacterToGlyphMap.Keys
            .Where(codePoint => codePoint > 0x1F)
            .OrderBy(codePoint => codePoint)
            .Take(MaxIconGlyphs)
            .ToList();

        if (glyphs is null || glyphs.Count == 0)
        {
            _content.Children.Add(BuildCaption(TranslationHelper.Get("FontPreview_NoGlyphs",
                failsafe: "该字体没有可枚举的字符。")));
            return;
        }

        var grid = new WrapPanel();
        foreach (var codePoint in glyphs)
        {
            var cell = new StackPanel { Width = 72, Margin = new Thickness(0, 0, 6, 10) };
            cell.Children.Add(new TextBlock
            {
                Text = char.ConvertFromUtf32(codePoint),
                FontFamily = _family,
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            cell.Children.Add(new TextBlock
            {
                Text = $"U+{codePoint:X4}",
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            grid.Children.Add(cell);
        }

        _content.Children.Add(grid);
    }

    /// <summary>Native rendering is finished when Preview* returns.</summary>
    public bool WaitForFontSent() => true;

    public void Dispose()
    {
        _content.Children.Clear();
    }

    private bool TryLoad(string path)
    {
        try
        {
            var family = Fonts.GetFontFamilies(new Uri(path)).FirstOrDefault();
            if (family is null)
            {
                ShowError(TranslationHelper.Get("FontPreview_LoadFailed", failsafe: "无法读取该字体文件。"));
                return false;
            }

            _family = family;
            _glyphTypeface = family.GetTypefaces()
                .Select(typeface => typeface.TryGetGlyphTypeface(out var glyphTypeface) ? glyphTypeface : null)
                .FirstOrDefault(glyphTypeface => glyphTypeface != null);

            return true;
        }
        catch (Exception e)
        {
            ShowError($"{TranslationHelper.Get("FontPreview_LoadFailed", failsafe: "无法读取该字体文件。")}\n{e.Message}");
            return false;
        }
    }

    private void ShowError(string message)
    {
        _content.Children.Clear();
        _content.Children.Add(BuildCaption(message));
    }

    private TextBlock BuildTitle(string path)
    {
        var name = SafeFamilyName(path) ?? _family.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileName(path);

        return new TextBlock
        {
            Text = name,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static string SafeFamilyName(string path)
    {
        try
        {
            return FreeTypeApi.GetFontFamilyName(path);
        }
        catch
        {
            return null;
        }
    }

    private TextBlock BuildSubtitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };
    }

    private FrameworkElement BuildSample(double size)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        stack.Children.Add(new TextBlock
        {
            Text = $"{size:0} pt",
            FontSize = 11,
            Opacity = 0.55,
        });

        stack.Children.Add(new TextBlock
        {
            Text = size >= 24 ? Pangram : Mixed,
            FontFamily = _family,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        });

        if (size >= 24 && size < 48)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Mixed,
                FontFamily = _family,
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return stack;
    }

    private FrameworkElement BuildCharset()
    {
        var grid = new WrapPanel();
        const string charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
                               + "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

        foreach (var ch in charset)
        {
            grid.Children.Add(new TextBlock
            {
                Text = ch.ToString(),
                FontFamily = _family,
                FontSize = 20,
                Width = 22,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 2, 4),
            });
        }

        return grid;
    }

    private TextBlock BuildCaption(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 10),
        };
    }
}
