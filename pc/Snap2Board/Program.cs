using System.Windows.Forms;

namespace Snap2Board;

internal sealed record CliOptions(string? PairCode, string? Host, string? AutoStart, bool ShowPanel, bool Diag)
{
    public static CliOptions Parse(string[] args)
    {
        string? pairCode = null;
        string? host = null;
        string? autoStart = null;
        var showPanel = false;
        var diag = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pair" when i + 1 < args.Length:
                    pairCode = args[++i];
                    break;
                case "--host" when i + 1 < args.Length:
                    host = args[++i];
                    break;
                case "--autostart" when i + 1 < args.Length:
                    autoStart = args[++i];
                    break;
                case "--panel":
                    showPanel = true;
                    break;
                case "--diag":
                    diag = true;
                    break;
            }
        }
        return new CliOptions(pairCode, host, autoStart, showPanel, diag);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.Diag)
        {
            Diagnostics.Run();
            return;
        }
        if (options.AutoStart is { } autoStart)
        {
            AutoStart.Set(autoStart.Equals("on", StringComparison.OrdinalIgnoreCase));
            return;
        }
        using var mutex = new Mutex(true, @"Global\Snap2Board.Instance", out var created);
        if (!created)
        {
            AppLog.Info("已有实例在运行，退出");
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Info($"UI 未处理异常: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Info($"未处理异常: {e.ExceptionObject}");
        Application.Run(new TrayApp(options));
    }
}
