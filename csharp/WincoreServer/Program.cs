using WincoreServer.Plugins;
using WincoreServer.Server;

namespace WincoreServer;

class Program
{
    static void Main(string[] args)
    {
        // Must run before any UIA/GDI calls: without this the process is DPI-unaware,
        // so BoundingRectangle and CopyFromScreen disagree on scale at != 100% display scaling.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string? recordingPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--record" && i + 1 < args.Length)
            {
                recordingPath = args[i + 1];
                i++;
            }
        }

        // Load plugins (built-in Java + .NET bridges, plus anything on
        // WINCORE_SERVER_PLUGINS) before the server starts so the dispatcher can
        // merge their command handlers.
        var plugins = PluginHost.Create(msg => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}"));

        // UIAutomation COM objects require STA threading.
        // [STAThread] on async Main doesn't reliably set the apartment state,
        // so we create a dedicated STA thread and run the server on it.
        var staThread = new Thread(() =>
        {
            var server = new JsonRpcServer(plugins, recordingPath);
            server.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
    }
}
