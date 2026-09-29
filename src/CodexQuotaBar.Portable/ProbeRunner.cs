using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexQuotaBar;

internal static class ProbeRunner
{
    public static async Task WriteReportAsync(string outputPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var completion = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastStatus = string.Empty;
        await using var client = new CodexQuotaClient();
        client.QuotaChanged += value => completion.TrySetResult(value);
        client.StatusChanged += value => lastStatus = value;
        client.Start();

        object result;
        try
        {
            var snapshot = await completion.Task.WaitAsync(timeout.Token);
            result = new
            {
                found = true,
                remaining = snapshot.RemainingPercent,
                windowMinutes = snapshot.WindowDurationMinutes,
                windowLabel = snapshot.WindowLabel,
                resetsAt = snapshot.ResetsAt,
                label = snapshot.CompactLabel
            };
        }
        catch (OperationCanceledException)
        {
            result = new { found = false, status = lastStatus, error = "probe_timeout" };
            Environment.ExitCode = 1;
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunAsync()
    {
        AttachToParentConsole();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var completion = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new CodexQuotaClient();
        client.QuotaChanged += value => completion.TrySetResult(value);
        client.StatusChanged += value => Console.Error.WriteLine(value);
        client.Start();

        try
        {
            var snapshot = await completion.Task.WaitAsync(timeout.Token);
            Console.WriteLine($"remaining={snapshot.RemainingPercent:0.##}");
            Console.WriteLine($"windowMinutes={snapshot.WindowDurationMinutes}");
            Console.WriteLine($"windowLabel={snapshot.WindowLabel}");
            Console.WriteLine($"resetsAt={snapshot.ResetsAt:O}");
            Console.WriteLine($"label={snapshot.CompactLabel}");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("probe_timeout");
            Environment.ExitCode = 1;
        }
    }

    private static void AttachToParentConsole()
    {
        if (!NativeMethods.AttachConsole(uint.MaxValue))
        {
            return;
        }

        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(output);
        Console.SetError(error);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);
    }
}
