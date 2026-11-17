namespace LabelPrinter.Agent;

/// <summary>
/// The control-panel process: the agent, its local web page, and (unless started with --background) the app-mode
/// window. A second launch does not start a second agent; it just opens the window of the running one.
/// </summary>
internal static class UiHost
{
    public static int Run(string[] args)
    {
        var background = args.Contains("--background");
        var host = new AgentHost(AgentHost.DefaultDataDir());
        var platform = AgentHost.CreatePlatform();
        if (!OperatingSystem.IsWindows() && !AgentHost.DevInsecure) { Console.Error.WriteLine("error: the control window is implemented for Windows only"); return 5; }
        host.EnsureDataDir();

        using var instance = host.TryAcquireInstance();
        if (instance is null)
        {
            if (!background) OpenRunning(host, platform);
            return 0;
        }

        var log = new FileLog(host.DataDir, echo: !OperatingSystem.IsWindows());
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cancel.Cancel(); } catch (ObjectDisposedException) { } };

        using var service = new AgentService(host, platform, log, TimeProvider.System);
        service.Start(); // not paired / no printer is a normal first-run state, shown in the window
        using var stop = host.CreateStopEvent();
        using var ui = new UiServer(service);
        ui.Start();
        host.WriteUiInfo(new UiInfo(ui.Port, ui.Token));
        log.Write($"control panel on {ui.BaseUrl}");
        if (!background) platform.OpenWindow(ui.OpenUrl, host.BrowserProfileDir);

        WaitHandle.WaitAny([cancel.Token.WaitHandle, stop, ui.QuitRequested]);
        log.Write("shutting down");
        ui.Dispose();
        service.StopWorker();
        try { File.Delete(host.UiInfoPath); } catch (IOException) { }
        return 0;
    }

    private static void OpenRunning(AgentHost host, IPlatform platform)
    {
        if (host.ReadUiInfo() is not { } info) return;
        platform.OpenWindow($"http://127.0.0.1:{info.Port}/?t={Uri.EscapeDataString(info.Token)}", host.BrowserProfileDir);
    }
}
