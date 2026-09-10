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
using System.Text;

namespace QuickLook.Plugin.Shared.NativeMethods;

/// <summary>
/// Reads another process's command line without pulling in System.Management.
/// Uses NtQueryInformationProcess (ProcessCommandLineInformation, supported on
/// Windows 10 1709+) and only needs PROCESS_QUERY_LIMITED_INFORMATION, which
/// same-user processes grant by default.
/// </summary>
internal static class ProcessCommandLineReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out UnicodeString processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    public static string GetCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            if (NtQueryInformationProcess(
                    handle, ProcessCommandLineInformation,
                    out var info, Marshal.SizeOf<UnicodeString>(), out _) != 0)
            {
                return null;
            }

            if (info.Buffer == IntPtr.Zero || info.Length == 0)
                return null;

            var bytes = new byte[info.Length];
            Marshal.Copy(info.Buffer, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
