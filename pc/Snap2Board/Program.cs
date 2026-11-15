using System.Windows.Forms;

namespace Snap2Board;

internal sealed record CliOptions(string? PairCode, string? Host)
{
    public static CliOptions Parse(string[] args)
    {
        string? pairCode = null;
        string? host = null;
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
            }
        }
        return new CliOptions(pairCode, host);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        using var mutex = new Mutex(true, @"Global\Snap2Board.Instance", out var created);
        if (!created)
        {
            AppLog.Info("已有实例在运行，退出");
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(options));
    }
}
