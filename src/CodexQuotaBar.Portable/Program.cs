namespace CodexQuotaBar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var homeReportIndex = Array.FindIndex(args, value => value.Equals("--home-report", StringComparison.OrdinalIgnoreCase));
        if (homeReportIndex >= 0 && homeReportIndex + 1 < args.Length)
        {
            HomePageDiagnostic.WriteReport(args[homeReportIndex + 1]);
            return;
        }

        if (args.Contains("--install-startup", StringComparer.OrdinalIgnoreCase))
        {
            AutoStartManager.EnsureInstalled();
            Environment.ExitCode = AutoStartManager.GetStatus().IsHealthy ? 0 : 1;
            return;
        }

        var startupReportIndex = Array.FindIndex(args, value => value.Equals("--startup-report", StringComparison.OrdinalIgnoreCase));
        if (startupReportIndex >= 0 && startupReportIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteStartupReport(args[startupReportIndex + 1]);
            return;
        }

        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            ProbeRunner.RunAsync().GetAwaiter().GetResult();
            return;
        }

        var probeReportIndex = Array.FindIndex(args, value => value.Equals("--probe-report", StringComparison.OrdinalIgnoreCase));
        if (probeReportIndex >= 0 && probeReportIndex + 1 < args.Length)
        {
            ProbeRunner.WriteReportAsync(args[probeReportIndex + 1]).GetAwaiter().GetResult();
            return;
        }

        var captureIndex = Array.FindIndex(args, value => value.Equals("--capture", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0 && captureIndex + 1 < args.Length)
        {
            DiagnosticRunner.CaptureVisibleOverlay(args[captureIndex + 1], includeContext: false);
            return;
        }

        var contextCaptureIndex = Array.FindIndex(args, value => value.Equals("--capture-context", StringComparison.OrdinalIgnoreCase));
        if (contextCaptureIndex >= 0 && contextCaptureIndex + 1 < args.Length)
        {
            DiagnosticRunner.CaptureVisibleOverlay(args[contextCaptureIndex + 1], includeContext: true);
            return;
        }

        var codexCaptureIndex = Array.FindIndex(args, value => value.Equals("--capture-codex", StringComparison.OrdinalIgnoreCase));
        if (codexCaptureIndex >= 0 && codexCaptureIndex + 1 < args.Length)
        {
            DiagnosticRunner.CaptureCodexWindow(args[codexCaptureIndex + 1]);
            return;
        }

        var detectIndex = Array.FindIndex(args, value => value.Equals("--detect-image", StringComparison.OrdinalIgnoreCase));
        if (detectIndex >= 0 && detectIndex + 2 < args.Length)
        {
            DiagnosticRunner.DetectComposerInImage(args[detectIndex + 1], args[detectIndex + 2]);
            return;
        }

        var windowsIndex = Array.FindIndex(args, value => value.Equals("--windows-report", StringComparison.OrdinalIgnoreCase));
        if (windowsIndex >= 0 && windowsIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteCodexWindowsReport(args[windowsIndex + 1]);
            return;
        }

        var compatibilityIndex = Array.FindIndex(args, value => value.Equals("--compat-report", StringComparison.OrdinalIgnoreCase));
        if (compatibilityIndex >= 0 && compatibilityIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteCompatibilityReport(args[compatibilityIndex + 1]);
            return;
        }

        var trackerIndex = Array.FindIndex(args, value => value.Equals("--tracker-report", StringComparison.OrdinalIgnoreCase));
        if (trackerIndex >= 0 && trackerIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteTrackerReport(args[trackerIndex + 1]);
            return;
        }

        var petsIndex = Array.FindIndex(args, value => value.Equals("--pets-report", StringComparison.OrdinalIgnoreCase));
        if (petsIndex >= 0 && petsIndex + 1 < args.Length)
        {
            DiagnosticRunner.WritePetsReport(args[petsIndex + 1]);
            return;
        }

        var uiaIndex = Array.FindIndex(args, value => value.Equals("--uia-report", StringComparison.OrdinalIgnoreCase));
        if (uiaIndex >= 0 && uiaIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteUiaReport(args[uiaIndex + 1]);
            return;
        }

        var benchmarkUiaIndex = Array.FindIndex(args, value => value.Equals("--benchmark-uia", StringComparison.OrdinalIgnoreCase));
        if (benchmarkUiaIndex >= 0 && benchmarkUiaIndex + 2 < args.Length)
        {
            DiagnosticRunner.BenchmarkUiaLocator(
                args[benchmarkUiaIndex + 1],
                int.Parse(args[benchmarkUiaIndex + 2], System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        var fallbackIndex = Array.FindIndex(args, value => value.Equals("--fallback-report", StringComparison.OrdinalIgnoreCase));
        if (fallbackIndex >= 0 && fallbackIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteScreenshotFallbackReport(args[fallbackIndex + 1]);
            return;
        }

        var benchmarkIndex = Array.FindIndex(args, value => value.Equals("--benchmark-detect", StringComparison.OrdinalIgnoreCase));
        if (benchmarkIndex >= 0 && benchmarkIndex + 3 < args.Length)
        {
            DiagnosticRunner.BenchmarkComposerDetector(
                args[benchmarkIndex + 1],
                args[benchmarkIndex + 2],
                int.Parse(args[benchmarkIndex + 3], System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        var verifyLayoutIndex = Array.FindIndex(args, value => value.Equals("--verify-layout", StringComparison.OrdinalIgnoreCase));
        if (verifyLayoutIndex >= 0 && verifyLayoutIndex + 1 < args.Length)
        {
            DiagnosticRunner.WriteOverlayPlacementReport(args[verifyLayoutIndex + 1]);
            return;
        }

        if (args.Length == 0 || args.Contains("--watch-codex", StringComparer.OrdinalIgnoreCase))
        {
            CodexWatcher.Run();
            return;
        }

        if (!args.Contains("--overlay", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        using var singleInstance = new Mutex(true, InstanceNames.OverlayMutex, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        Application.Run(new QuotaOverlayForm());
    }
}
