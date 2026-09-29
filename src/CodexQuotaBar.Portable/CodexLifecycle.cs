namespace CodexQuotaBar;

internal static class CodexLifecycle
{
    internal static bool IsRunning()
        => TryGetSessionKey(out _);

    internal static bool TryGetSessionKey(out string sessionKey)
    {
        sessionKey = string.Empty;
        var testEventName = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_LIFECYCLE_EVENT");
        if (!string.IsNullOrWhiteSpace(testEventName))
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(testEventName, out var testEvent))
                {
                    return false;
                }

                using (testEvent)
                {
                    if (!testEvent.WaitOne(0))
                    {
                        return false;
                    }

                    sessionKey = $"test:{testEventName}";
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        var processIds = CodexWindowCatalog.GetApplicationProcessIds();
        if (processIds.Count == 0)
        {
            return false;
        }

        sessionKey = string.Join(',', processIds);
        return true;
    }
}
