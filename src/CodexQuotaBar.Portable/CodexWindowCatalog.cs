using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaBar;

internal static class CodexWindowCatalog
{
    private static readonly string[] DesktopProcessNames = ["ChatGPT", "Codex"];
    private static readonly object ProcessCacheGate = new();
    private static readonly TimeSpan ProcessCacheDuration = TimeSpan.FromSeconds(1);
    private static DateTime processCacheUpdatedAt = DateTime.MinValue;
    private static DesktopProcessInfo[] processCache = [];

    internal sealed record DesktopProcessInfo(
        int ProcessId,
        string ProcessName,
        string? ExecutablePath,
        string? ProductName,
        string? FileDescription,
        string? FileVersion);

    internal sealed record WindowInfo(IntPtr Handle, int ProcessId, string Title, string ClassName, Rectangle Bounds)
    {
        internal long Area => (long)Bounds.Width * Bounds.Height;
    }

    internal static IReadOnlyList<WindowInfo> ListVisibleWindows()
    {
        var processIds = GetCodexProcessIds();
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (!processIds.Contains(processId) || !NativeMethods.GetWindowRect(window, out var rect))
            {
                return true;
            }

            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width < 280 || bounds.Height < 180)
            {
                return true;
            }

            windows.Add(new WindowInfo(window, processId, ReadWindowText(window), ReadClassName(window), bounds));
            return true;
        }, IntPtr.Zero);

        return windows.OrderByDescending(window => window.Area).ToArray();
    }

    internal static WindowInfo? FindLargestVisibleWindow() =>
        ListVisibleWindows().FirstOrDefault(IsTrackableMainWindow);

    internal static WindowInfo? FindLargestVisibleWindowForProcess(int processId) =>
        ListVisibleWindows().FirstOrDefault(window =>
            window.ProcessId == processId && IsTrackableMainWindow(window));

    internal static IReadOnlyList<WindowInfo> ListVisibleAuxiliaryWindowsForProcess(int processId)
    {
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var candidateProcessId);
            if (candidateProcessId != processId
                || !NativeMethods.IsWindowVisible(window)
                || NativeMethods.IsIconic(window)
                || !IsAuxiliaryWindow(window)
                || !NativeMethods.GetWindowRect(window, out var rect))
            {
                return true;
            }

            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                windows.Add(new WindowInfo(
                    window,
                    candidateProcessId,
                    ReadWindowText(window),
                    ReadClassName(window),
                    bounds));
            }

            return true;
        }, IntPtr.Zero);
        return windows.OrderByDescending(window => window.Area).ToArray();
    }

    internal static bool IsCodexProcessWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return IsCodexDesktopProcess(process);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsApplicationWindow(IntPtr window)
    {
        if (!IsCodexProcessWindow(window))
        {
            return false;
        }

        return IsVisibleApplicationWindow(window);
    }

    internal static bool IsAuxiliaryWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.NativeWindowInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NativeWindowInfo>()
        };
        return NativeMethods.GetWindowInfo(window, ref info)
            && IsAuxiliaryWindowStyle(info.ExtendedStyle);
    }

    internal static bool IsAuxiliaryWindowStyle(uint extendedStyle) =>
        (extendedStyle & NativeMethods.ExtendedStyleToolWindow) != 0;

    internal static bool HasApplicationWindow()
        => GetApplicationProcessIds().Count > 0;

    internal static IReadOnlyList<int> GetApplicationProcessIds()
    {
        var processIds = GetCodexProcessIds();
        if (processIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        var applicationProcessIds = new HashSet<int>();
        NativeMethods.EnumWindows((window, _) =>
        {
            if (IsApplicationWindow(window, processIds))
            {
                NativeMethods.GetWindowThreadProcessId(window, out var processId);
                applicationProcessIds.Add(processId);
            }

            return true;
        }, IntPtr.Zero);
        return applicationProcessIds.Order().ToArray();
    }

    private static bool IsApplicationWindow(IntPtr window, HashSet<int> processIds)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processIds.Contains(processId) && IsVisibleApplicationWindow(window);
    }

    private static bool IsVisibleApplicationWindow(IntPtr window)
    {
        if (!NativeMethods.IsWindowVisible(window) || IsAuxiliaryWindow(window))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            return true;
        }

        return NativeMethods.GetWindowRect(window, out var rect)
            && rect.Right - rect.Left >= 280
            && rect.Bottom - rect.Top >= 180;
    }

    private static bool IsTrackableMainWindow(WindowInfo window) =>
        !IsAuxiliaryWindow(window.Handle) && !NativeMethods.IsIconic(window.Handle);

    internal static IReadOnlyList<DesktopProcessInfo> ListDetectedDesktopProcesses(
        bool forceRefresh = false)
    {
        lock (ProcessCacheGate)
        {
            if (!forceRefresh
                && processCache.Length > 0
                && DateTime.UtcNow - processCacheUpdatedAt < ProcessCacheDuration)
            {
                return processCache;
            }

            processCache = DiscoverDesktopProcesses();
            processCacheUpdatedAt = DateTime.UtcNow;
            return processCache;
        }
    }

    private static DesktopProcessInfo[] DiscoverDesktopProcesses()
    {
        var processIds = new HashSet<int>();
        foreach (var processName in DesktopProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    processIds.Add(process.Id);
                }
            }
        }

        NativeMethods.EnumWindows((window, _) =>
        {
            if (NativeMethods.IsWindowVisible(window))
            {
                NativeMethods.GetWindowThreadProcessId(window, out var processId);
                if (processId != 0)
                {
                    processIds.Add(processId);
                }
            }

            return true;
        }, IntPtr.Zero);

        var detected = new List<DesktopProcessInfo>();
        foreach (var processId in processIds)
        {
            if (processId == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (TryDescribeDesktopProcess(process, out var info))
                {
                    detected.Add(info);
                }
            }
            catch
            {
            }
        }

        return detected.OrderBy(info => info.ProcessId).ToArray();
    }

    private static HashSet<int> GetCodexProcessIds() =>
        ListDetectedDesktopProcesses().Select(info => info.ProcessId).ToHashSet();

    internal static bool IsCodexDesktopProcess(Process process)
    {
        if (process.Id == Environment.ProcessId)
        {
            return false;
        }

        if (ListDetectedDesktopProcesses().Any(info => info.ProcessId == process.Id))
        {
            return true;
        }

        return TryDescribeDesktopProcess(process, out _);
    }

    private static bool TryDescribeDesktopProcess(Process process, out DesktopProcessInfo info)
    {
        info = null!;
        string processName;
        try
        {
            processName = process.ProcessName;
        }
        catch
        {
            return false;
        }

        string? executablePath = null;
        string? productName = null;
        string? fileDescription = null;
        string? fileVersion = null;
        try
        {
            executablePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var version = FileVersionInfo.GetVersionInfo(executablePath);
                productName = version.ProductName;
                fileDescription = version.FileDescription;
                fileVersion = version.FileVersion;
            }
        }
        catch
        {
        }

        if (!LooksLikeCodexDesktopProcess(
                processName,
                executablePath,
                productName,
                fileDescription))
        {
            return false;
        }

        info = new DesktopProcessInfo(
            process.Id,
            processName,
            executablePath,
            productName,
            fileDescription,
            fileVersion);
        return true;
    }

    internal static bool LooksLikeCodexDesktopProcess(
        string processName,
        string? executablePath,
        string? productName,
        string? fileDescription)
    {
        if (IsCodexDesktopProcessName(processName))
        {
            return true;
        }

        var identity = $"{processName} {productName} {fileDescription}";
        if (identity.Contains("QuotaBar", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Quota Bar", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("剩余额度", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var normalizedPath = executablePath.Replace('/', '\\');
            if (normalizedPath.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("\\Programs\\Codex\\", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("\\Programs\\OpenAI Codex\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return identity.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Codex Desktop", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(productName)
                && productName.Contains("Codex", StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(fileDescription)
                && fileDescription.Contains("Codex", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsCodexDesktopProcessName(string processName) =>
        DesktopProcessNames.Any(name => name.Equals(processName, StringComparison.OrdinalIgnoreCase));

    private static string ReadWindowText(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string ReadClassName(IntPtr window)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static class NativeMethods
    {
        internal const uint ExtendedStyleToolWindow = 0x00000080;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        internal static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowInfo(IntPtr window, ref NativeWindowInfo info);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeWindowInfo
        {
            internal uint Size;
            internal Rect Window;
            internal Rect Client;
            internal uint Style;
            internal uint ExtendedStyle;
            internal uint WindowStatus;
            internal uint WindowBordersWidth;
            internal uint WindowBordersHeight;
            internal ushort WindowType;
            internal ushort CreatorVersion;
        }

        internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    }
}
