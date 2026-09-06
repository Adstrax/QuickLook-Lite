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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace QuickLookNext;

public static class PipeMessages
{
    public const string RunAndClose = "QuickLookNext.App.PipeMessages.RunAndClose";
    public const string Switch = "QuickLookNext.App.PipeMessages.Switch";
    public const string Invoke = "QuickLookNext.App.PipeMessages.Invoke";
    public const string Toggle = "QuickLookNext.App.PipeMessages.Toggle";
    public const string Forget = "QuickLookNext.App.PipeMessages.Forget";
    public const string Close = "QuickLookNext.App.PipeMessages.Close";
    public const string Quit = "QuickLookNext.App.PipeMessages.Quit";
    public const string Fullscreen = "QuickLookNext.App.PipeMessages.Fullscreen";
    public const string Reload = "QuickLookNext.App.PipeMessages.Reload";
}

public class PipeServerManager : IDisposable
{
    // v3.31.0-dev: a shell-spawned preview child listens on its own pipe so
    // QuickLookNext.Shell can send preview requests to the warm child.
    private static readonly string PipeName =
        (App.IsChildInstance ? "QuickLookNext.Child.Pipe." : "QuickLookNext.App.Pipe.")
        + WindowsIdentity.GetCurrent().User?.Value;
    private static PipeServerManager _instance;

    private DispatcherOperation _lastOperation;

    private NamedPipeServerStream _server;

    public PipeServerManager()
    {
        _server = new NamedPipeServerStream(PipeName, PipeDirection.In);

        _ = Task.Factory.StartNew(() =>
        {
            using var reader = new StreamReader(_server);
            Debug.WriteLine("PipeManager: Ready");

            while (true)
            {
                _server.WaitForConnection();
                var msg = reader.ReadLine();

                Debug.WriteLine($"PipeManager: {msg}");

                // dispatch message
                if (MessageReceived(msg))
                    return;

                _server.Disconnect();
            }
        }, TaskCreationOptions.LongRunning);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (_server != null)
            PostMessage(PipeMessages.Quit);
        _server?.Dispose();
        _server = null;
    }

    [SuppressMessage("Style", "IDE0063:Use simple 'using' statement")]
    public static bool PostMessage(string pipeMessage, string path = null, string[] options = null,
        int timeoutMs = 2000)
    {
        path ??= string.Empty;
        options ??= [];

        try
        {
            using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                client.Connect(timeoutMs);

                using (var writer = new StreamWriter(client))
                {
                    writer.WriteLine($"{pipeMessage}|{path}|{string.Join(",", options)}");
                    writer.Flush();
                }
            }

            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.ToString());
            return false;
        }
    }

    public static void SendMessage(string pipeMessage, string path = null, string[] options = null)
    {
        GetInstance().MessageReceived(pipeMessage, path, options);
    }

    private bool MessageReceived(string msg)
    {
        var split = msg.Split('|');
        if (split.Length <= 1)
            return false;

        var pipeMessage = split[0];
        var path = split[1];
        var options = split.Length >= 3 && !string.IsNullOrEmpty(split[2])
            ? split[2].Split(',')
            : null;

        return MessageReceived(pipeMessage, path, options);
    }

    private bool MessageReceived(string pipeMessage, string path = null, string[] options = null)
    {
        if (_lastOperation != null && _lastOperation.Status == DispatcherOperationStatus.Pending)
        {
            _lastOperation.Abort();
            Debug.WriteLine("Dispatcher task canceled");
        }

        var option = options?.Length > 0 ? string.Join(",", options) : null;

        switch (pipeMessage)
        {
            case PipeMessages.RunAndClose:
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().RunAndClosePreview()),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Invoke:
                _lastOperation = Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().InvokePreview(path)),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Switch:
                _lastOperation = Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().SwitchPreview(path)),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Toggle:
                _lastOperation = Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().TogglePreview(path, option)),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Forget:
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().ForgetCurrentWindow()),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Close:
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().ClosePreview()),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Fullscreen:
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().ToggleFullscreen()),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Reload:
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ViewWindowManager.GetInstance().ReloadPreview()),
                    DispatcherPriority.Normal);
                return false;

            case PipeMessages.Quit:
                return true;

            default:
                return false;
        }
    }

    public static PipeServerManager GetInstance()
    {
        return _instance ??= new PipeServerManager();
    }
}
