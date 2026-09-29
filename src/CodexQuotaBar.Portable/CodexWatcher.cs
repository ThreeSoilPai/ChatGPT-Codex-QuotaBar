using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexQuotaBar;

internal static class CodexWatcher
{
    internal static void Run()
    {
        using var singleInstance = new Mutex(true, InstanceNames.WatcherMutex, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        AutoStartManager.EnsureInstalled();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
            WatcherLog.Write("watcher_ui", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                WatcherLog.Write("watcher_unhandled", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            WatcherLog.Write("watcher_task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
        using var context = new WatcherApplicationContext();
        Application.Run(context);
    }

    private sealed class WatcherApplicationContext : ApplicationContext
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectShow = 0x8002;
        private const uint EventObjectHide = 0x8003;
        private const uint EventObjectUncloaked = 0x8018;
        private const uint WinEventOutOfContext = 0x0000;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const int ObjectIdWindow = 0;
        private const int ChildIdSelf = 0;
        private static readonly TimeSpan StableRunThreshold = TimeSpan.FromSeconds(30);

        private readonly object stateGate = new();
        private readonly CancellationTokenSource shutdown = new();
        private readonly System.Windows.Forms.Timer watchdogTimer = new() { Interval = 2000 };
        private readonly System.Windows.Forms.Timer eventRecheckTimer = new() { Interval = 100 };
        private readonly NativeMethods.WinEventDelegate winEventCallback;
        private readonly List<IntPtr> winEventHooks = [];
        private Process? overlayProcess;
        private DateTime overlayStartedAt = DateTime.MinValue;
        private DateTime nextLaunchAllowedAt = DateTime.MinValue;
        private int consecutiveFailures;
        private bool launchSuppressedForCurrentSession;
        private string? activeSessionKey;
        private bool disposed;

        internal WatcherApplicationContext()
        {
            winEventCallback = OnWinEvent;
            watchdogTimer.Tick += (_, _) => EvaluateStateSafely("watchdog");
            eventRecheckTimer.Tick += (_, _) =>
            {
                eventRecheckTimer.Stop();
                EvaluateStateSafely("window_recheck");
            };

            InstallWinEventHook(EventSystemForeground);
            InstallWinEventHook(EventObjectDestroy);
            InstallWinEventHook(EventObjectShow);
            InstallWinEventHook(EventObjectHide);
            InstallWinEventHook(EventObjectUncloaked);

            EvaluateStateSafely("startup");
            watchdogTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Process? processToDispose;
                lock (stateGate)
                {
                    if (disposed)
                    {
                        base.Dispose(disposing);
                        return;
                    }

                    disposed = true;
                    processToDispose = overlayProcess;
                    overlayProcess = null;
                }

                shutdown.Cancel();
                watchdogTimer.Stop();
                watchdogTimer.Dispose();
                eventRecheckTimer.Stop();
                eventRecheckTimer.Dispose();

                foreach (var hook in winEventHooks)
                {
                    NativeMethods.UnhookWinEvent(hook);
                }

                winEventHooks.Clear();
                StopOverlay(processToDispose);
                shutdown.Dispose();
            }

            base.Dispose(disposing);
        }

        private void InstallWinEventHook(uint eventType)
        {
            var hook = NativeMethods.SetWinEventHook(
                eventType,
                eventType,
                IntPtr.Zero,
                winEventCallback,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (hook != IntPtr.Zero)
            {
                winEventHooks.Add(hook);
            }
        }

        private void OnWinEvent(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            try
            {
                OnWinEventCore(eventType, window, objectId, childId);
            }
            catch (Exception ex)
            {
                WatcherLog.Write("window_event", ex);
            }
        }

        private void OnWinEventCore(
            uint eventType,
            IntPtr window,
            int objectId,
            int childId)
        {
            if (disposed || window == IntPtr.Zero)
            {
                return;
            }

            if (eventType != EventSystemForeground
                && (objectId != ObjectIdWindow || childId != ChildIdSelf))
            {
                return;
            }

            if (eventType is EventObjectDestroy or EventObjectHide)
            {
                ScheduleWindowRecheck();
                return;
            }

            if (!CodexWindowCatalog.IsCodexProcessWindow(window))
            {
                return;
            }

            if (CodexWindowCatalog.IsApplicationWindow(window))
            {
                eventRecheckTimer.Stop();
                EvaluateStateSafely("window_event");
                return;
            }

            // Electron can announce a window before its final size and visibility settle.
            ScheduleWindowRecheck();
        }

        private void ScheduleWindowRecheck()
        {
            eventRecheckTimer.Stop();
            eventRecheckTimer.Start();
        }

        private void EvaluateStateSafely(string source)
        {
            try
            {
                EvaluateState();
            }
            catch (Exception ex)
            {
                WatcherLog.Write(source, ex);
            }
        }

        private void EvaluateState()
        {
            if (disposed)
            {
                return;
            }

            var codexRunning = CodexLifecycle.TryGetSessionKey(out var sessionKey);
            Process? processToStop = null;
            Process? processToDispose = null;
            var startRequested = false;
            var retryScheduled = false;

            lock (stateGate)
            {
                if (disposed)
                {
                    return;
                }

                if (!codexRunning)
                {
                    activeSessionKey = null;
                    consecutiveFailures = 0;
                    nextLaunchAllowedAt = DateTime.MinValue;
                    launchSuppressedForCurrentSession = false;
                    processToStop = overlayProcess;
                    overlayProcess = null;
                }
                else
                {
                    var sessionChanged = !string.Equals(
                        activeSessionKey,
                        sessionKey,
                        StringComparison.Ordinal);
                    if (sessionChanged)
                    {
                        activeSessionKey = sessionKey;
                        consecutiveFailures = 0;
                        nextLaunchAllowedAt = DateTime.MinValue;
                        launchSuppressedForCurrentSession = false;
                        processToStop = overlayProcess;
                        overlayProcess = null;
                    }

                    if (overlayProcess is not null)
                    {
                        try
                        {
                            if (!overlayProcess.HasExited)
                            {
                                return;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                        }

                        processToDispose = overlayProcess;
                        overlayProcess = null;
                        var stoppedAt = DateTime.UtcNow;
                        if (WasUserRequestedExit(processToDispose))
                        {
                            consecutiveFailures = 0;
                            nextLaunchAllowedAt = DateTime.MinValue;
                            launchSuppressedForCurrentSession = true;
                        }
                        else
                        {
                            if (stoppedAt - overlayStartedAt >= StableRunThreshold)
                            {
                                consecutiveFailures = 0;
                            }

                            ScheduleRetryLocked(stoppedAt);
                        }

                        retryScheduled = true;
                    }

                    var now = DateTime.UtcNow;
                    if (!launchSuppressedForCurrentSession
                        && !retryScheduled
                        && now >= nextLaunchAllowedAt)
                    {
                        startRequested = true;
                    }
                }
            }

            StopOverlay(processToStop);
            processToDispose?.Dispose();
            if (startRequested)
            {
                StartOverlayForSession(sessionKey);
            }
        }

        private void StartOverlayForSession(string sessionKey)
        {
            if (!CodexLifecycle.TryGetSessionKey(out var currentSessionKey)
                || !string.Equals(currentSessionKey, sessionKey, StringComparison.Ordinal))
            {
                return;
            }

            Process process;
            try
            {
                process = StartOverlay();
            }
            catch
            {
                lock (stateGate)
                {
                    if (!disposed
                        && string.Equals(activeSessionKey, sessionKey, StringComparison.Ordinal))
                    {
                        ScheduleRetryLocked(DateTime.UtcNow);
                    }
                }

                return;
            }

            var accepted = false;
            lock (stateGate)
            {
                if (!disposed
                    && overlayProcess is null
                    && string.Equals(activeSessionKey, sessionKey, StringComparison.Ordinal))
                {
                    overlayProcess = process;
                    overlayStartedAt = DateTime.UtcNow;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                StopOverlay(process);
                return;
            }

            _ = MonitorOverlayExitAsync(process, shutdown.Token);
        }

        private async Task MonitorOverlayExitAsync(Process process, CancellationToken token)
        {
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }

            lock (stateGate)
            {
                if (disposed || !ReferenceEquals(overlayProcess, process))
                {
                    process.Dispose();
                    return;
                }

                var now = DateTime.UtcNow;
                if (WasUserRequestedExit(process))
                {
                    consecutiveFailures = 0;
                    nextLaunchAllowedAt = DateTime.MinValue;
                    launchSuppressedForCurrentSession = true;
                }
                else
                {
                    if (now - overlayStartedAt >= StableRunThreshold)
                    {
                        consecutiveFailures = 0;
                    }

                    ScheduleRetryLocked(now);
                }

                overlayProcess = null;
            }

            process.Dispose();
        }

        private static bool WasUserRequestedExit(Process process)
        {
            try
            {
                return process.ExitCode == InstanceNames.OverlayUserExitCode;
            }
            catch
            {
                return false;
            }
        }

        private void ScheduleRetryLocked(DateTime now)
        {
            consecutiveFailures = Math.Min(consecutiveFailures + 1, 6);
            var seconds = consecutiveFailures switch
            {
                1 => 1,
                2 => 2,
                3 => 4,
                4 => 8,
                5 => 16,
                _ => 30
            };
            var delay = TimeSpan.FromSeconds(seconds);
            nextLaunchAllowedAt = now + delay;
            _ = RetryAfterDelayAsync(delay, shutdown.Token);
        }

        private async Task RetryAfterDelayAsync(TimeSpan delay, CancellationToken token)
        {
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            EvaluateStateSafely("retry");
        }

        private static void StopOverlay(Process? process)
        {
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static Process StartOverlay()
    {
        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--overlay",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动额度条");
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr eventHookModule,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(IntPtr eventHook);

        internal delegate void WinEventDelegate(
            IntPtr eventHook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);
    }
}
