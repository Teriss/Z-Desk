using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Interop;
using ZDesk.Services;

namespace ZDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The watchdog must not construct WPF: it only waits for the primary
        // process and restores Explorer's icon layer afterwards.
        if (DesktopIconVisibilityService.TryRunWatchdog(args)) return;

        // WPF is the sole supported desktop presentation path. The native
        // renderer remains internal test code and has no user-facing switch.
        if (args.Any(argument => argument.Equals("--software-rendering", StringComparison.OrdinalIgnoreCase)))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DesktopRenderingOptions.UseNativeTopLevel = false;
        DesktopRenderingOptions.UseNativeDesktop = false;

        var app = new App();
        app.InitializeComponent();
        app.EnsureBaseResources();
        app.RunWithArgs(args);
    }
}
