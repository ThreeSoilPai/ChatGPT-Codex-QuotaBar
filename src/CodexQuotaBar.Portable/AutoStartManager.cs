using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CodexQuotaBar;

internal static class AutoStartManager
{
    internal const string ValueName = "CodexQuotaBarWatcher";
    internal const string ScheduledTaskName = "CodexQuotaBarWatcherTask";
    internal const string RecoveryIntervalXml = "PT1M";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static void EnsureInstalled()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_QUOTA_INSTANCE_SUFFIX")))
        {
            return;
        }

        var executable = Path.GetFullPath(Environment.ProcessPath ?? Application.ExecutablePath);
        if (IsPortableVariant() && HasPreferredFormalRegistration(executable))
        {
            return;
        }

        EnsureRunEntry(executable);
        if (!EnsureScheduledTask(executable))
        {
            WatcherLog.Write("startup", "任务计划注册失败；保留注册表启动兜底。");
        }
    }

    internal static StartupRegistrationStatus GetStatus()
    {
        var executable = Path.GetFullPath(Environment.ProcessPath ?? Application.ExecutablePath);
        var expectedCommand = BuildRunCommand(executable);
        string? runCommand = null;
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            runCommand = runKey?.GetValue(ValueName) as string;
        }
        catch
        {
        }

        var query = RunTaskScheduler("/Query", "/TN", ScheduledTaskName, "/XML");
        var taskXml = query.StandardOutput;
        var taskRegistered = query.ExitCode == 0;
        var recoveryTriggerPresent = taskRegistered && HasRecoveryTrigger(taskXml);
        var taskMatchesCurrentExecutable = taskRegistered && TaskMatches(taskXml, executable);

        return new StartupRegistrationStatus(
            executable,
            expectedCommand,
            runCommand,
            string.Equals(runCommand, expectedCommand, StringComparison.Ordinal),
            taskRegistered,
            taskMatchesCurrentExecutable,
            recoveryTriggerPresent,
            query.ExitCode,
            ShortMessage(query.StandardError));
    }

    private static void EnsureRunEntry(string executable)
    {
        var command = BuildRunCommand(executable);
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!string.Equals(runKey?.GetValue(ValueName) as string, command, StringComparison.Ordinal))
            {
                runKey?.SetValue(ValueName, command, RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            WatcherLog.Write("startup_registry", ex);
        }
    }

    private static bool EnsureScheduledTask(string executable)
    {
        var query = RunTaskScheduler("/Query", "/TN", ScheduledTaskName, "/XML");
        if (query.ExitCode == 0 && TaskMatches(query.StandardOutput, executable))
        {
            return true;
        }

        try
        {
            var workingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
                $action = New-ScheduledTaskAction -Execute {{QuotePowerShell(executable)}} -Argument '--watch-codex' -WorkingDirectory {{QuotePowerShell(workingDirectory)}}
                $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
                $recoveryTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
                $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
                Register-ScheduledTask -TaskName {{QuotePowerShell(ScheduledTaskName)}} -Action $action -Trigger @($logonTrigger, $recoveryTrigger) -Settings $settings -User $identity -RunLevel Limited -Force | Out-Null
                """;
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var create = RunProcess(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle",
                "Hidden",
                "-EncodedCommand",
                encodedScript);
            if (create.ExitCode != 0)
            {
                WatcherLog.Write(
                    "startup_task",
                    $"PowerShell exit={create.ExitCode}: {ShortMessage(create.StandardError)}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            WatcherLog.Write("startup_task", ex);
            return false;
        }
    }

    private static TaskSchedulerResult RunTaskScheduler(params string[] arguments)
    {
        return RunProcess(
            Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            arguments);
    }

    private static TaskSchedulerResult RunProcess(string executable, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new TaskSchedulerResult(-1, string.Empty, "无法启动 schtasks.exe");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new TaskSchedulerResult(-1, string.Empty, "schtasks.exe 超时");
            }

            Task.WaitAll([standardOutput, standardError], 1000);
            return new TaskSchedulerResult(
                process.ExitCode,
                standardOutput.IsCompletedSuccessfully ? standardOutput.Result : string.Empty,
                standardError.IsCompletedSuccessfully ? standardError.Result : string.Empty);
        }
        catch (Exception ex)
        {
            return new TaskSchedulerResult(-1, string.Empty, ex.Message);
        }
    }

    private static string BuildRunCommand(string executable) => $"\"{executable}\" --watch-codex";

    private static bool TaskMatches(string taskXml, string executable) =>
        taskXml.Contains(executable, StringComparison.OrdinalIgnoreCase)
        && taskXml.Contains("--watch-codex", StringComparison.OrdinalIgnoreCase)
        && taskXml.Contains("<LogonTrigger>", StringComparison.OrdinalIgnoreCase)
        && taskXml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase)
        && taskXml.Contains("<RestartOnFailure>", StringComparison.OrdinalIgnoreCase)
        && HasRecoveryTrigger(taskXml);

    private static bool HasRecoveryTrigger(string taskXml) =>
        taskXml.Contains("<TimeTrigger>", StringComparison.OrdinalIgnoreCase)
        && taskXml.Contains("<Repetition>", StringComparison.OrdinalIgnoreCase)
        && taskXml.Contains($"<Interval>{RecoveryIntervalXml}</Interval>", StringComparison.OrdinalIgnoreCase);

    private static bool IsPortableVariant() =>
        !string.Equals(
            typeof(AutoStartManager).Assembly.GetName().Name,
            "CodexQuotaBar",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasPreferredFormalRegistration(string currentExecutable)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (IsPreferredFormalExecutable(
                    ExtractExecutablePath(runKey?.GetValue(ValueName) as string),
                    currentExecutable))
            {
                return true;
            }
        }
        catch
        {
        }

        var query = RunTaskScheduler("/Query", "/TN", ScheduledTaskName, "/XML");
        if (query.ExitCode != 0)
        {
            return false;
        }

        try
        {
            var command = XDocument.Parse(query.StandardOutput)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Command")
                ?.Value;
            return IsPreferredFormalExecutable(command, currentExecutable);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var separator = trimmed.IndexOf(' ');
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static bool IsPreferredFormalExecutable(string? candidate, string currentExecutable)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return false;
        }

        return !string.Equals(fullPath, currentExecutable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(fullPath), "CodexQuotaBar.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(fullPath);
    }

    private static string QuotePowerShell(string value) => $"'{value.Replace("'", "''")}'";

    private static string? ShortMessage(string? value)
    {
        var firstLine = value?.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
    }

    private sealed record TaskSchedulerResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

internal sealed record StartupRegistrationStatus(
    string Executable,
    string ExpectedRunCommand,
    string? ActualRunCommand,
    bool RunEntryMatches,
    bool ScheduledTaskRegistered,
    bool ScheduledTaskMatches,
    bool RecoveryTriggerPresent,
    int ScheduledTaskQueryExitCode,
    string? ScheduledTaskError)
{
    internal bool IsHealthy => RunEntryMatches && ScheduledTaskMatches;
}
