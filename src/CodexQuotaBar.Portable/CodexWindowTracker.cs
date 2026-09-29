using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexQuotaBar;

internal readonly record struct CodexWindowState(
    IntPtr Handle,
    Rectangle Bounds,
    bool IsMaximized,
    bool ForegroundIsAuxiliary,
    Rectangle[] VisibleAuxiliaryBounds)
{
    internal bool IsFollowingAuxiliaryWindow => ForegroundIsAuxiliary;

    internal IReadOnlyList<Rectangle> AuxiliaryBounds => VisibleAuxiliaryBounds ?? [];
}

internal sealed class CodexWindowTracker
{
    private const int DwmExtendedFrameBounds = 9;
    private const int SystemMetricSwapButton = 23;
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;
    private readonly PetWindowLocator petWindowLocator = new();

    public bool TryGetActiveCodexBounds(out Rectangle bounds)
    {
        var found = TryGetActiveCodexWindow(out var state);
        bounds = found ? state.Bounds : Rectangle.Empty;
        return found;
    }

    public bool TryGetActiveCodexBounds(out Rectangle bounds, out bool isMaximized)
    {
        var found = TryGetActiveCodexWindow(out var state);
        bounds = found ? state.Bounds : Rectangle.Empty;
        isMaximized = found && state.IsMaximized;
        return found;
    }

    public bool TryGetActiveCodexWindow(out CodexWindowState state)
    {
        state = default;
        IntPtr candidateWindow;
        uint foregroundProcessId = 0;
        if (Environment.GetEnvironmentVariable("CODEX_QUOTA_ALWAYS_VISIBLE") == "1")
        {
            candidateWindow = CodexWindowCatalog.FindLargestVisibleWindow()?.Handle ?? IntPtr.Zero;
        }
        else
        {
            candidateWindow = NativeMethods.GetForegroundWindow();
            if (candidateWindow == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(candidateWindow, out foregroundProcessId);
            Process? foregroundProcess;
            try
            {
                foregroundProcess = Process.GetProcessById((int)foregroundProcessId);
            }
            catch
            {
                return false;
            }

            using (foregroundProcess)
            {
                if (!CodexWindowCatalog.IsCodexDesktopProcess(foregroundProcess))
                {
                    return false;
                }
            }
        }

        var root = NativeMethods.GetAncestor(candidateWindow, NativeMethods.GetRoot);
        if (root == IntPtr.Zero || NativeMethods.IsIconic(root))
        {
            return false;
        }

        var foregroundIsAuxiliary = CodexWindowCatalog.IsAuxiliaryWindow(root);
        var foregroundIsPet = false;
        if (foregroundIsAuxiliary)
        {
            // Pets is a separate Electron tool window. Keep following the main
            // Codex window while the user drags it.
            if (TryGetVisibleBounds(root, out var foregroundAuxiliaryBounds))
            {
                foregroundIsPet = TryResolvePetBounds(
                    root,
                    foregroundAuxiliaryBounds,
                    out _);
            }

            var mainWindow = CodexWindowCatalog.FindLargestVisibleWindowForProcess(
                checked((int)foregroundProcessId));
            if (mainWindow is null)
            {
                return false;
            }

            root = mainWindow.Handle;
        }

        var isMaximized = NativeMethods.IsZoomed(root);
        if (!TryGetVisibleBounds(root, out var bounds))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(root, out var trackedProcessId);
        var auxiliaryBounds = CodexWindowCatalog
            .ListVisibleAuxiliaryWindowsForProcess(checked((int)trackedProcessId))
            .Select(window => TryResolvePetBounds(
                window.Handle,
                TryGetVisibleBounds(window.Handle, out var visibleBounds)
                    ? visibleBounds
                    : window.Bounds,
                out var petBounds)
                    ? petBounds
                    : Rectangle.Empty)
            .Where(bounds => !bounds.IsEmpty)
            .ToArray();

        state = new CodexWindowState(
            root,
            bounds,
            isMaximized,
            foregroundIsPet,
            auxiliaryBounds);
        return true;
    }

    internal bool IsPointerOverVisiblePet()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var point = new Point(cursor.X, cursor.Y);
        foreach (var window in CodexWindowCatalog.ListVisibleWindows())
        {
            if (!CodexWindowCatalog.IsAuxiliaryWindow(window.Handle)
                || !TryResolvePetBounds(window.Handle, window.Bounds, out var petBounds))
            {
                continue;
            }

            if (petBounds.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolvePetBounds(
        IntPtr windowHandle,
        Rectangle windowBounds,
        out Rectangle petBounds)
    {
        if (petWindowLocator.TryGetPetBounds(windowHandle, windowBounds, out petBounds))
        {
            return true;
        }

        if (PetWindowLocator.ShouldUseNativeBoundsAsLegacyFallback(windowBounds))
        {
            petBounds = windowBounds;
            return true;
        }

        petBounds = Rectangle.Empty;
        return false;
    }

    internal static bool IsPrimaryMouseButtonDown()
    {
        var virtualKey = NativeMethods.GetSystemMetrics(SystemMetricSwapButton) == 0
            ? VirtualKeyLeftButton
            : VirtualKeyRightButton;
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    internal static bool IsForegroundCodexAuxiliaryWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GetRoot);
        return root != IntPtr.Zero
            && !NativeMethods.IsIconic(root)
            && CodexWindowCatalog.IsCodexProcessWindow(root)
            && CodexWindowCatalog.IsAuxiliaryWindow(root);
    }

    private static bool TryGetVisibleBounds(IntPtr window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (NativeMethods.DwmGetWindowAttribute(
                window,
                DwmExtendedFrameBounds,
                out var rect,
                Marshal.SizeOf<NativeMethods.Rect>()) != 0
            && !NativeMethods.GetWindowRect(window, out rect))
        {
            return false;
        }

        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return false;
        }

        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

    private static class NativeMethods
    {
        internal const uint GetRoot = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            IntPtr window,
            int attribute,
            out Rect value,
            int valueSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }
    }
}
