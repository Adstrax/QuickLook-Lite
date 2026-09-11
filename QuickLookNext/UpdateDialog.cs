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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLookNext;

/// <summary>
/// v3.35.0: the update prompt. Clicking a "new version" notification (or running
/// a manual update check) used to start the download straight away; the user now
/// chooses between updating now and skipping this version.
/// </summary>
internal static class UpdateDialog
{
    /// <summary>Must be called on the UI thread. Returns true to update now.</summary>
    internal static bool Ask(string version)
    {
        var update = new Button
        {
            Content = TranslationHelper.Get("Update_Now", failsafe: "立即更新"),
            MinWidth = 96,
            Padding = new Thickness(16, 6, 16, 6),
            IsDefault = true,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var ignore = new Button
        {
            Content = TranslationHelper.Get("Update_Ignore", failsafe: "忽略更新"),
            MinWidth = 96,
            Padding = new Thickness(16, 6, 16, 6),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ignore);
        buttons.Children.Add(update);

        var body = new TextBlock
        {
            Text = string.Format(
                TranslationHelper.Get("Update_Ask",
                    failsafe: "发现新版本 {0}。\n\n现在更新，或忽略这个版本（下次手动检查更新时仍会提示）？"),
                version),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        };

        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = TranslationHelper.Get("Update_Title", failsafe: "软件更新"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });
        content.Children.Add(body);
        content.Children.Add(buttons);

        var window = new Window
        {
            Title = "QuickLook-Next",
            Content = content,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            Topmost = true,
        };

        if (Application.Current.TryFindResource("MainWindowBackground") is Brush background)
            window.Background = background;

        var updateNow = false;
        update.Click += (_, _) =>
        {
            updateNow = true;
            window.Close();
        };
        ignore.Click += (_, _) => window.Close();

        window.ShowDialog();

        return updateNow;
    }
}
