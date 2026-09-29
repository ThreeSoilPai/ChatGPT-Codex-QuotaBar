using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CodexQuotaBar;

internal sealed class CodexQuotaClient : IAsyncDisposable
{
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstQuotaTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim writerLock = new(1, 1);
    private Process? process;
    private Task? lifetimeTask;
    private int requestId = 6;

    public event Action<QuotaSnapshot>? QuotaChanged;
    public event Action<string>? StatusChanged;

    public void Start()
    {
        lifetimeTask ??= Task.Run(() => MaintainConnectionAsync(cancellation.Token));
    }

    private async Task MaintainConnectionAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var candidates = FindCodexExecutableCandidates();
            if (candidates.Count == 0)
            {
                StatusChanged?.Invoke("额度暂不可用 · 未找到 Codex App Server");
                if (!await DelayBeforeRetryAsync(token))
                {
                    return;
                }

                continue;
            }

            CandidateConnectionException? lastFailure = null;
            var rediscoverImmediately = false;
            foreach (var executable in candidates)
            {
                try
                {
                    await RunConnectionAsync(executable, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (CandidateConnectionException ex)
                {
                    lastFailure = ex;
                    if (ex.WasReady)
                    {
                        // A previously healthy runtime probably disappeared during an app update.
                        // Re-enumerate immediately so the newly installed hashed runtime wins.
                        rediscoverImmediately = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastFailure = new CandidateConnectionException(ex.Message, false, ex);
                }
            }

            if (lastFailure is not null)
            {
                StatusChanged?.Invoke($"额度暂不可用 · {ShortMessage(lastFailure.Message)}");
            }

            if (rediscoverImmediately)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                continue;
            }

            if (!await DelayBeforeRetryAsync(token))
            {
                return;
            }
        }
    }

    private static async Task<bool> DelayBeforeRetryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunConnectionAsync(string executable, CancellationToken token)
    {
        var receivedQuota = false;
        Process? activeProcess = null;
        Task<string>? stderrDrain = null;
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? refreshTask = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            activeProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process = activeProcess;
            if (!activeProcess.Start())
            {
                throw new InvalidOperationException("无法启动 Codex App Server");
            }

            StatusChanged?.Invoke("额度读取中…");
            stderrDrain = activeProcess.StandardError.ReadToEndAsync(token);
            await SendAsync(new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "quota_bar_local",
                        title = "Codex Quota Bar",
                        version = typeof(CodexQuotaClient).Assembly.GetName().Version?.ToString(3)
                            ?? "unknown"
                    }
                }
            }, token);

            var initialized = false;
            var responseDeadline = DateTime.UtcNow + InitializationTimeout;
            while (!token.IsCancellationRequested && !activeProcess.HasExited)
            {
                var line = initialized && receivedQuota
                    ? await activeProcess.StandardOutput.ReadLineAsync(token)
                    : await ReadLineBeforeDeadlineAsync(
                        activeProcess.StandardOutput,
                        responseDeadline,
                        initialized ? "额度接口响应超时" : "App Server 初始化超时",
                        token);
                if (line is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt32(out var id))
                {
                    if (id == 0)
                    {
                        if (root.TryGetProperty("result", out _))
                        {
                            initialized = true;
                            responseDeadline = DateTime.UtcNow + FirstQuotaTimeout;
                            await SendAsync(new { method = "initialized", @params = new { } }, token);
                            await RequestQuotaAsync(token);
                            refreshTask = RefreshLoopAsync(refreshCancellation.Token);
                            continue;
                        }

                        if (root.TryGetProperty("error", out var initializationError))
                        {
                            throw new InvalidOperationException(ReadError(initializationError));
                        }
                    }

                    if (root.TryGetProperty("result", out var result))
                    {
                        if (PublishFromPayload(result))
                        {
                            receivedQuota = true;
                        }
                        else if (!receivedQuota)
                        {
                            throw new InvalidOperationException("额度响应结构不受支持");
                        }
                    }
                    else if (root.TryGetProperty("error", out var error))
                    {
                        if (!receivedQuota)
                        {
                            throw new InvalidOperationException(ReadError(error));
                        }

                        StatusChanged?.Invoke($"额度暂不可用 · {ReadError(error)}");
                    }
                }
                else if (root.TryGetProperty("method", out var methodElement)
                    && methodElement.ValueKind == JsonValueKind.String
                    && methodElement.GetString() == "account/rateLimits/updated"
                    && root.TryGetProperty("params", out var parameters))
                {
                    receivedQuota |= PublishFromPayload(parameters);
                }
            }

            if (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException("Codex App Server 已断开");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CandidateConnectionException(ex.Message, receivedQuota, ex);
        }
        finally
        {
            refreshCancellation.Cancel();
            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }

            if (activeProcess is not null)
            {
                try
                {
                    if (!activeProcess.HasExited)
                    {
                        activeProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            }

            if (stderrDrain is not null)
            {
                try
                {
                    await stderrDrain;
                }
                catch
                {
                }
            }

            if (ReferenceEquals(process, activeProcess))
            {
                process = null;
            }

            activeProcess?.Dispose();
        }
    }

    private static async Task<string?> ReadLineBeforeDeadlineAsync(
        StreamReader reader,
        DateTime deadline,
        string timeoutMessage,
        CancellationToken token)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        var readTask = reader.ReadLineAsync(token).AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(remaining, token));
        if (!ReferenceEquals(completed, readTask))
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException(timeoutMessage);
        }

        return await readTask;
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), token);
            if (process is { HasExited: false })
            {
                await RequestQuotaAsync(token);
            }
        }
    }

    private Task RequestQuotaAsync(CancellationToken token)
    {
        var id = Interlocked.Increment(ref requestId);
        return SendAsync(new { method = "account/rateLimits/read", id }, token);
    }

    private async Task SendAsync<T>(T message, CancellationToken token)
    {
        var activeProcess = process ?? throw new InvalidOperationException("App Server 尚未启动");
        var line = JsonSerializer.Serialize(message);

        await writerLock.WaitAsync(token);
        try
        {
            await activeProcess.StandardInput.WriteLineAsync(line.AsMemory(), token);
            await activeProcess.StandardInput.FlushAsync(token);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private bool PublishFromPayload(JsonElement payload)
    {
        if (!TryCreateShortestQuotaSnapshot(payload, out var snapshot))
        {
            StatusChanged?.Invoke("额度暂不可用");
            return false;
        }

        QuotaChanged?.Invoke(snapshot);
        return true;
    }

    internal static bool TryCreateShortestQuotaSnapshot(
        JsonElement payload,
        out QuotaSnapshot snapshot)
    {
        snapshot = null!;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var exactCodexRoots = new List<JsonElement>();
        var codexFamilyRoots = new List<JsonElement>();
        var multiBucketRoots = new List<JsonElement>();
        CollectNamedBucketRoots(payload, 0, exactCodexRoots, codexFamilyRoots, multiBucketRoots);

        IReadOnlyList<JsonElement> selectedRoots;
        if (exactCodexRoots.Count > 0)
        {
            selectedRoots = exactCodexRoots;
        }
        else if (codexFamilyRoots.Count > 0)
        {
            selectedRoots = codexFamilyRoots;
        }
        else if (TryGetPropertyAny(payload, out var backwardCompatibleLimits, "rateLimits", "rate_limits")
            && backwardCompatibleLimits.ValueKind == JsonValueKind.Object)
        {
            selectedRoots = [backwardCompatibleLimits];
        }
        else if (multiBucketRoots.Count > 0)
        {
            selectedRoots = multiBucketRoots;
        }
        else
        {
            selectedRoots = [payload];
        }

        var windows = new List<QuotaSnapshot>();
        foreach (var root in selectedRoots)
        {
            CollectQuotaWindows(root, null, 0, windows);
        }

        if (windows.Count == 0)
        {
            return false;
        }

        snapshot = windows.OrderBy(window => window.WindowDurationMinutes).First();
        return true;
    }

    internal static bool TrySelectCanonicalRateLimits(JsonElement result, out JsonElement rateLimits)
    {
        rateLimits = default;
        if (result.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetPropertyAny(result, out var buckets, "rateLimitsByLimitId", "rate_limits_by_limit_id")
            && buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject())
            {
                if (bucket.Name.Equals("codex", StringComparison.OrdinalIgnoreCase)
                    && bucket.Value.ValueKind == JsonValueKind.Object)
                {
                    rateLimits = bucket.Value;
                    return true;
                }
            }
        }

        return TryGetPropertyAny(result, out rateLimits, "rateLimits", "rate_limits")
            && rateLimits.ValueKind == JsonValueKind.Object;
    }

    internal static bool TryCreateShortestWindowSnapshot(
        JsonElement rateLimits,
        out QuotaSnapshot snapshot)
    {
        snapshot = null!;
        var windows = new List<QuotaSnapshot>();
        CollectQuotaWindows(rateLimits, null, 0, windows);
        if (windows.Count == 0)
        {
            return false;
        }

        snapshot = windows.OrderBy(window => window.WindowDurationMinutes).First();
        return true;
    }

    private static void CollectNamedBucketRoots(
        JsonElement node,
        int depth,
        List<JsonElement> exactCodexRoots,
        List<JsonElement> codexFamilyRoots,
        List<JsonElement> multiBucketRoots)
    {
        if (depth > 8)
        {
            return;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyAny(node, out var limitIdElement, "limitId", "limit_id")
                && limitIdElement.ValueKind == JsonValueKind.String)
            {
                AddBucketById(node, limitIdElement.GetString(), exactCodexRoots, codexFamilyRoots);
            }

            foreach (var property in node.EnumerateObject())
            {
                if (IsMultiBucketContainerName(property.Name)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var bucket in property.Value.EnumerateObject())
                    {
                        if (bucket.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        multiBucketRoots.Add(bucket.Value);
                        var bucketId = TryGetPropertyAny(bucket.Value, out var nestedId, "limitId", "limit_id")
                            && nestedId.ValueKind == JsonValueKind.String
                                ? nestedId.GetString()
                                : bucket.Name;
                        AddBucketById(bucket.Value, bucketId, exactCodexRoots, codexFamilyRoots);
                    }
                }

                CollectNamedBucketRoots(
                    property.Value,
                    depth + 1,
                    exactCodexRoots,
                    codexFamilyRoots,
                    multiBucketRoots);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                CollectNamedBucketRoots(
                    item,
                    depth + 1,
                    exactCodexRoots,
                    codexFamilyRoots,
                    multiBucketRoots);
            }
        }
    }

    private static void AddBucketById(
        JsonElement bucket,
        string? bucketId,
        List<JsonElement> exactCodexRoots,
        List<JsonElement> codexFamilyRoots)
    {
        if (string.IsNullOrWhiteSpace(bucketId))
        {
            return;
        }

        if (bucketId.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            exactCodexRoots.Add(bucket);
        }
        else if (bucketId.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            codexFamilyRoots.Add(bucket);
        }
    }

    private static bool IsMultiBucketContainerName(string name) =>
        name.Equals("rateLimitsByLimitId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("rate_limits_by_limit_id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("rateLimitBuckets", StringComparison.OrdinalIgnoreCase)
        || name.Equals("rate_limit_buckets", StringComparison.OrdinalIgnoreCase)
        || name.Equals("limitsById", StringComparison.OrdinalIgnoreCase)
        || name.Equals("limits_by_id", StringComparison.OrdinalIgnoreCase);

    private static void CollectQuotaWindows(
        JsonElement node,
        string? inheritedPlanType,
        int depth,
        List<QuotaSnapshot> windows)
    {
        if (depth > 10)
        {
            return;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            var planType = TryGetPropertyAny(node, out var planElement, "planType", "plan_type")
                && planElement.ValueKind == JsonValueKind.String
                    ? planElement.GetString()
                    : inheritedPlanType;

            if (TryReadFiniteDouble(node, out var usedPercent,
                    "usedPercent", "used_percent", "usagePercent", "usage_percent")
                && TryReadPositiveInt(node, out var duration,
                    "windowDurationMins", "window_duration_mins", "windowMinutes", "window_minutes",
                    "durationMins", "duration_mins", "durationMinutes", "duration_minutes"))
            {
                windows.Add(new QuotaSnapshot(
                    usedPercent,
                    duration,
                    ReadResetTime(node),
                    planType));
            }

            foreach (var property in node.EnumerateObject())
            {
                CollectQuotaWindows(property.Value, planType, depth + 1, windows);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                CollectQuotaWindows(item, inheritedPlanType, depth + 1, windows);
            }
        }
    }

    private static DateTimeOffset? ReadResetTime(JsonElement node)
    {
        if (!TryGetPropertyAny(node, out var resetElement,
                "resetsAt", "resets_at", "resetAt", "reset_at"))
        {
            return null;
        }

        long value;
        if (resetElement.ValueKind == JsonValueKind.Number)
        {
            if (!resetElement.TryGetInt64(out value))
            {
                return null;
            }
        }
        else if (resetElement.ValueKind == JsonValueKind.String
            && long.TryParse(resetElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
        }
        else
        {
            return null;
        }

        try
        {
            return value > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadFiniteDouble(
        JsonElement node,
        out double value,
        params string[] propertyNames)
    {
        value = 0d;
        if (!TryGetPropertyAny(node, out var element, propertyNames))
        {
            return false;
        }

        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => double.NaN
        };
        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadPositiveInt(
        JsonElement node,
        out int value,
        params string[] propertyNames)
    {
        value = 0;
        if (!TryGetPropertyAny(node, out var element, propertyNames))
        {
            return false;
        }

        var parsed = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => 0
        };
        if (parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetPropertyAny(
        JsonElement element,
        out JsonElement value,
        params string[] propertyNames)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    internal static string? FindCodexExecutable() => FindCodexExecutableCandidates().FirstOrDefault();

    internal static IReadOnlyList<string> FindCodexExecutableCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
                if (File.Exists(fullPath) && seen.Add(fullPath))
                {
                    candidates.Add(fullPath);
                }
            }
            catch
            {
            }
        }

        AddCandidate(Environment.GetEnvironmentVariable("CODEX_QUOTA_CODEX_EXE"));
        AddRunningCodexCandidates(AddCandidate);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddLocalCodexRuntimeCandidates(localAppData, AddCandidate);
        AddDesktopInstallCandidates(AddCandidate);
        AddCandidate(Path.Combine(AppContext.BaseDirectory, "codex.exe"));
        AddWindowsAppCandidates(AddCandidate);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var path in new[]
        {
            Path.Combine(home, ".codex", "plugins", ".plugin-appserver", "codex.exe"),
            Path.Combine(home, ".codex", ".sandbox-bin", "codex.exe"),
            Path.Combine(home, ".codex", "bin", "codex.exe"),
            Path.Combine(home, ".local", "bin", "codex.exe"),
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "codex.exe"),
            Path.Combine(localAppData, "Programs", "Codex", "resources", "codex.exe"),
            Path.Combine(localAppData, "Programs", "OpenAI Codex", "resources", "codex.exe"),
            Path.Combine(localAppData, "pnpm", "codex.exe")
        })
        {
            AddCandidate(path);
        }

        AddNpmCandidates(AddCandidate);

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddCandidate(Path.Combine(directory.Trim('"'), "codex.exe"));
            }
        }

        return candidates;
    }

    private static void AddRunningCodexCandidates(Action<string?> addCandidate)
    {
        foreach (var runningProcess in Process.GetProcessesByName("codex"))
        {
            using (runningProcess)
            {
                try
                {
                    addCandidate(runningProcess.MainModule?.FileName);
                }
                catch
                {
                }
            }
        }
    }

    private static void AddDesktopInstallCandidates(Action<string?> addCandidate)
    {
        foreach (var desktopProcess in CodexWindowCatalog.ListDetectedDesktopProcesses())
        {
            var appDirectory = Path.GetDirectoryName(desktopProcess.ExecutablePath);
            if (string.IsNullOrWhiteSpace(appDirectory))
            {
                continue;
            }

            addCandidate(Path.Combine(appDirectory, "resources", "codex.exe"));
            addCandidate(Path.Combine(appDirectory, "app", "resources", "codex.exe"));
            addCandidate(Path.Combine(appDirectory, "codex.exe"));
        }
    }

    private static void AddLocalCodexRuntimeCandidates(
        string localAppData,
        Action<string?> addCandidate)
    {
        var runtimeRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            foreach (var runtimeDirectory in Directory
                .EnumerateDirectories(runtimeRoot, "*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                addCandidate(Path.Combine(runtimeDirectory, "codex.exe"));
            }
        }
        catch
        {
        }
    }

    private static void AddWindowsAppCandidates(Action<string?> addCandidate)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        try
        {
            foreach (var packageDirectory in Directory
                .EnumerateDirectories(windowsApps, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                addCandidate(Path.Combine(packageDirectory, "app", "resources", "codex.exe"));
                addCandidate(Path.Combine(packageDirectory, "resources", "codex.exe"));
            }
        }
        catch
        {
        }
    }

    private static void AddNpmCandidates(Action<string?> addCandidate)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var packageRoot = Path.Combine(appData, "npm", "node_modules", "@openai", "codex");
        if (!Directory.Exists(packageRoot))
        {
            return;
        }

        try
        {
            foreach (var executable in Directory.EnumerateFiles(
                packageRoot,
                "codex.exe",
                SearchOption.AllDirectories))
            {
                addCandidate(executable);
            }
        }
        catch
        {
        }
    }

    private static string ReadError(JsonElement error)
    {
        return error.TryGetProperty("message", out var message)
            ? ShortMessage(message.GetString() ?? "未知错误")
            : "未知错误";
    }

    private static string ShortMessage(string value)
    {
        var firstLine = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "未知错误" : firstLine.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        if (lifetimeTask is not null)
        {
            try
            {
                await lifetimeTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
        writerLock.Dispose();
    }

    private sealed class CandidateConnectionException(
        string message,
        bool wasReady,
        Exception innerException)
        : Exception(message, innerException)
    {
        internal bool WasReady { get; } = wasReady;
    }
}
