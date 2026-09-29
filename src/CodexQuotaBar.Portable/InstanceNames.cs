namespace CodexQuotaBar;

internal static class InstanceNames
{
    internal const int OverlayUserExitCode = 20;

    internal static string WatcherMutex => $"Local\\CodexQuotaBar.Watcher{Suffix}";

    internal static string OverlayMutex => $"Local\\CodexQuotaBar.Overlay{Suffix}";

    private static string Suffix
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("CODEX_QUOTA_INSTANCE_SUFFIX");
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var safe = new string(value.Where(char.IsLetterOrDigit).Take(32).ToArray());
            return safe.Length == 0 ? string.Empty : $".{safe}";
        }
    }
}
