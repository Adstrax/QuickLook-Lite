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

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickLookNext.Shell;

/// <summary>
/// Global low-level keyboard hook for the space key. Self-contained P/Invoke
/// (no WPF): the shell must stay free of PresentationFramework so idle memory
/// stays in the 10-30 MB range. Toggles on the first keydown; auto-repeat
/// keydowns and the keyup are ignored.
/// </summary>
internal sealed class SpaceKeyHook : IDisposable
{
    public event EventHandler SpacePressed;

    private Native.KeyboardHookProc _callback;
    private nint _hHook;
    private bool _spaceDown;

    public SpaceKeyHook()
    {
        Hook();
    }

    public void Dispose()
    {
        if (_hHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hHook);
            _hHook = IntPtr.Zero;
        }
        _callback = null;
    }

    private void Hook()
    {
        _callback = HookProc;
        var hInstance = Native.LoadLibrary("user32.dll");
        _hHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _callback, hInstance, 0);
    }

    private nint HookProc(int code, nint wParam, ref Native.KbdLlHookStruct lParam)
    {
        if (code >= 0)
        {
            var key = (Keys)lParam.VkCode;
            if (wParam == Native.WM_KEYDOWN)
            {
                if (key == Keys.Space && Control.ModifierKeys == Keys.None)
                {
                    if (_spaceDown)
                        return 1; // auto-repeat while held down

                    _spaceDown = true;
                    SpacePressed?.Invoke(this, EventArgs.Empty);
                    return 1; // swallow the space so Explorer does not toggle selection
                }
            }
            else if (wParam == Native.WM_KEYUP && key == Keys.Space)
            {
                _spaceDown = false;
            }
        }

        return Native.CallNextHookEx(_hHook, code, wParam, ref lParam);
    }

    private static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookEx(int idHook, KeyboardHookProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, ref KbdLlHookStruct lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern nint LoadLibrary(string lpFileName);

        public delegate nint KeyboardHookProc(int nCode, nint wParam, ref KbdLlHookStruct lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public nint DwExtraInfo;
        }
    }
}