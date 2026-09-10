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

namespace QuickLook.Common.Plugin;

/// <summary>
/// Interface implemented by every QuickLook.Plugin
/// <para>
/// v3.31.0: the lifecycle is worth spelling out explicitly, because it is not
/// obvious from the member list and getting it wrong fails silently:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="Init"/> runs <b>once per plugin type</b>, on the
/// instance the host keeps for matching, on a background thread (possibly in
/// parallel with other plugins). Anything it prepares must therefore live in
/// static state - instance fields set by Init are <b>not</b> visible to the
/// instance that later renders a preview.</description></item>
/// <item><description><see cref="CanHandle"/> is called on that same long-lived
/// instance, for any file the user previews. Keep it cheap and free of side
/// effects; it may also be called on a thread pool thread.</description></item>
/// <item><description><see cref="Prepare"/>, <see cref="View"/> and
/// <see cref="Cleanup"/> run on a <b>fresh instance created for every preview</b>.
/// Never rely on instance fields surviving between previews.</description></item>
/// <item><description><see cref="View"/> must eventually set
/// <c>context.IsBusy = false</c>; do slow work on a background thread and assign
/// <c>context.ViewerContent</c> (or <c>context.PendingViewerContent</c>) when the
/// content is ready.</description></item>
/// </list>
/// </summary>
public interface IViewer
{
    /// <summary>
    /// Set the priority of this plugin. A plugin with a higher priority may override one with lower priority.
    /// Set this to int.MaxValue for a maximum priority, int.MinValue for minimum.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Do ont-time job when application starts. You may extract nessessary resource here.
    /// </summary>
    public void Init();

    /// <summary>
    /// Determine whether this plugin can open this file. Please also check the file header, if applicable.
    /// </summary>
    /// <param name="path">The full path of the target file.</param>
    public bool CanHandle(string path);

    /// <summary>
    /// Do some preparation stuff before the window is showing. Please not do any work that costs a lot of time.
    /// </summary>
    /// <param name="path">The full path of the target file.</param>
    /// <param name="context">A runtime object which allows interaction between this plugin and QuickLookNext.</param>
    public void Prepare(string path, ContextObject context);

    /// <summary>
    /// Start the loading process. During the process a busy indicator will be shown. Finish by setting context.IsBusy to
    /// false.
    /// </summary>
    /// <param name="path">The full path of the target file.</param>
    /// <param name="context">A runtime object which allows interaction between this plugin and QuickLookNext.</param>
    public void View(string path, ContextObject context);

    /// <summary>
    /// Release any unmanaged resource here.
    /// </summary>
    public void Cleanup();
}
