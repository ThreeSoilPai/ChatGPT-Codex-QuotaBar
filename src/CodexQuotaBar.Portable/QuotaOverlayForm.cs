using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexQuotaBar;

internal sealed class QuotaOverlayForm : Form
{
    private const int ExTransparent = 0x00000020;
    private const int ExToolWindow = 0x00000080;
    private const int ExLayered = 0x00080000;
    private const int ExNoActivate = 0x08000000;
    private const int OverlayHeight = OverlayPlacement.Height;
    private const int MissingCodexChecksBeforeExit = 1;
    private const float RailLeft = 10f;
    private const float RailTextGap = 12f;
    private const float OverlayRightPadding = 10f;
    private const float MinimumRailWidth = 80f;
    private static readonly TimeSpan ScreenshotFallbackLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PlusVisualVerificationInterval = TimeSpan.FromSeconds(1);
    private static readonly Color TransparentColor = Color.FromArgb(255, 1, 2, 3);
    private static readonly Color RailColor = Color.FromArgb(255, 229, 229, 231);
    private static readonly Color InkColor = Color.FromArgb(255, 57, 57, 61);
    private static readonly Color MutedColor = Color.FromArgb(255, 104, 104, 110);
    private static readonly Color HealthyColor = Color.FromArgb(255, 152, 150, 226);
    private static readonly Color CautionColor = Color.FromArgb(255, 214, 132, 43);
    private static readonly Color CriticalColor = Color.FromArgb(255, 207, 60, 76);

    private readonly CodexWindowTracker windowTracker = new();
    private UiaComposerLocator uiaComposerLocator = new();
    private readonly ComposerDetector composerDetector = new();
    private readonly CodexQuotaClient quotaClient = new();
    private readonly System.Windows.Forms.Timer positionTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer lifecycleTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource detectionCancellation = new();
    private readonly Font labelFont = new("Segoe UI Variable Text", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font strongLabelFont = new("Segoe UI Variable Text", 9f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly NotifyIcon trayIcon = new();
    private readonly ToolStripMenuItem trayStatusItem = new("额度读取中…") { Enabled = false };
    private QuotaSnapshot? snapshot;
    private QuotaSnapshot? deferredSnapshot;
    private string? deferredStatusText;
    private string statusText = "额度读取中…";
    private IntPtr lastWindowHandle;
    private Rectangle lastWindowBounds = Rectangle.Empty;
    private Rectangle lastComposerBounds = Rectangle.Empty;
    private Rectangle lastSafeOverlayBounds = Rectangle.Empty;
    private ComposerAnchor confirmedAnchor;
    private ComposerAnchor pendingAnchor;
    private IReadOnlyList<Rectangle> lastVisibleAuxiliaryBounds = Array.Empty<Rectangle>();
    private Task<PositionDetectionResult>? detectionTask;
    private DateTime nextPlusVisualVerificationAt = DateTime.MinValue;
    private DateTime lastUiaConfirmedAt = DateTime.MinValue;
    private bool lastWindowMaximized;
    private bool lastForegroundIsAuxiliary;
    private bool petsDragFreezeActive;
    private int pendingAnchorConfirmations;
    private int missingCodexChecks;

    public QuotaOverlayForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = TransparentColor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(900, OverlayHeight);
        Text = "Codex Quota Bar";
        AccessibleRole = AccessibleRole.StatusBar;
        AccessibleName = "Codex 剩余额度";
        AccessibleDescription = statusText;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);

        quotaClient.QuotaChanged += OnQuotaChanged;
        quotaClient.StatusChanged += OnStatusChanged;
        positionTimer.Tick += (_, _) => UpdatePositionAndVisibility();
        lifecycleTimer.Tick += (_, _) => CheckCodexLifecycle();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(trayStatusItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出额度条", null, (_, _) =>
        {
            Environment.ExitCode = InstanceNames.OverlayUserExitCode;
            Application.Exit();
        });
        trayIcon.Icon = SystemIcons.Information;
        trayIcon.Text = "Codex 剩余额度";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        Shown += (_, _) =>
        {
            Hide();
            quotaClient.Start();
            UpdatePositionAndVisibility();
            positionTimer.Start();
            lifecycleTimer.Start();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ExTransparent | ExToolWindow | ExLayered | ExNoActivate;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawOverlay(e.Graphics);
    }

    private void DrawOverlay(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var windowLabel = snapshot?.WindowLabel;
        var percentLabel = snapshot is null
            ? null
            : $"剩余 {snapshot.RemainingPercent:0}%";
        var windowLabelWidth = windowLabel is null
            ? 0f
            : graphics.MeasureString(windowLabel, labelFont).Width;
        var separatorWidth = windowLabel is null
            ? 0f
            : graphics.MeasureString(" · ", labelFont).Width;
        var textWidth = windowLabel is null
            ? graphics.MeasureString(statusText, labelFont).Width
            : windowLabelWidth
                + separatorWidth
                + graphics.MeasureString(percentLabel!, strongLabelFont).Width
                - 5f;
        var railWidth = Math.Max(
            MinimumRailWidth,
            ClientSize.Width - RailLeft - RailTextGap - textWidth - OverlayRightPadding);
        var railBounds = new RectangleF(RailLeft, 11f, railWidth, 6f);
        using var railPath = RoundedRectangle(railBounds, 3f);
        var railColor = SystemInformation.HighContrast ? SystemColors.ControlDark : RailColor;
        using var railBrush = new SolidBrush(railColor);
        graphics.FillPath(railBrush, railPath);

        if (snapshot is not null)
        {
            var remaining = snapshot.RemainingPercent;
            var fillWidth = (float)(railBounds.Width * remaining / 100d);
            if (fillWidth > 0f)
            {
                var fillBounds = new RectangleF(railBounds.X, railBounds.Y, Math.Max(6f, fillWidth), railBounds.Height);
                using var fillPath = RoundedRectangle(fillBounds, 3f);
                var fillColor = SystemInformation.HighContrast ? SystemColors.Highlight : StateColor(remaining);
                using var fillBrush = new SolidBrush(fillColor);
                graphics.FillPath(fillBrush, fillPath);
            }

            var textX = railBounds.Right + RailTextGap;
            using var mutedBrush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.GrayText : MutedColor);
            using var inkBrush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : InkColor);
            graphics.DrawString(windowLabel!, labelFont, mutedBrush, textX, 4f);
            graphics.DrawString(" · ", labelFont, mutedBrush, textX + windowLabelWidth - 2f, 4f);
            graphics.DrawString(percentLabel!, strongLabelFont, inkBrush, textX + windowLabelWidth + separatorWidth - 5f, 4f);
        }
        else
        {
            var textX = railBounds.Right + RailTextGap;
            using var textBrush = new SolidBrush(MutedColor);
            graphics.DrawString(statusText, labelFont, textBrush, textX, 4f);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Transparent);
    }

    private void RenderLayeredWindow()
    {
        if (!IsHandleCreated || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            DrawOverlay(graphics);
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        var previousBitmap = NativeMethods.SelectObject(memoryDc, bitmapHandle);
        try
        {
            var destination = new NativeMethods.NativePoint(Left, Top);
            var size = new NativeMethods.NativeSize(bitmap.Width, bitmap.Height);
            var source = new NativeMethods.NativePoint(0, 0);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.SourceOver,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.SourceAlpha
            };

            NativeMethods.UpdateLayeredWindow(
                Handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                NativeMethods.LayeredWindowUseAlpha);
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, previousBitmap);
            NativeMethods.DeleteObject(bitmapHandle);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void UpdatePositionAndVisibility()
    {
        var primaryMouseButtonDown = CodexWindowTracker.IsPrimaryMouseButtonDown();
        var interactionStartedOnPets = !petsDragFreezeActive
            && primaryMouseButtonDown
            && (windowTracker.IsPointerOverVisiblePet()
                || CodexWindowTracker.IsForegroundCodexAuxiliaryWindow());
        if (PetsDragFreezePolicy.ShouldFreeze(
                petsDragFreezeActive,
                primaryMouseButtonDown,
                interactionStartedOnPets))
        {
            if (!petsDragFreezeActive)
            {
                petsDragFreezeActive = true;
                AbandonInFlightDetection();
            }

            return;
        }

        var freezeWasActive = petsDragFreezeActive;
        petsDragFreezeActive = false;
        if (freezeWasActive)
        {
            ApplyDeferredVisualUpdate();
        }

        if (!windowTracker.TryGetActiveCodexWindow(out var windowState))
        {
            AbandonInFlightDetection();
            lastWindowHandle = IntPtr.Zero;
            lastWindowBounds = Rectangle.Empty;
            lastComposerBounds = Rectangle.Empty;
            lastSafeOverlayBounds = Rectangle.Empty;
            confirmedAnchor = default;
            pendingAnchor = default;
            lastVisibleAuxiliaryBounds = Array.Empty<Rectangle>();
            lastWindowMaximized = false;
            lastForegroundIsAuxiliary = false;
            nextPlusVisualVerificationAt = DateTime.MinValue;
            lastUiaConfirmedAt = DateTime.MinValue;
            pendingAnchorConfirmations = 0;
            HideOverlay();
            return;
        }

        var pureWindowMove = windowState.Handle == lastWindowHandle
            && windowState.Handle != IntPtr.Zero
            && windowState.Bounds.Size == lastWindowBounds.Size
            && windowState.Bounds.Location != lastWindowBounds.Location
            && windowState.IsMaximized == lastWindowMaximized;
        if (pureWindowMove)
        {
            AbandonInFlightDetection();
            var deltaX = windowState.Bounds.Left - lastWindowBounds.Left;
            var deltaY = windowState.Bounds.Top - lastWindowBounds.Top;
            lastWindowBounds = windowState.Bounds;
            lastComposerBounds.Offset(deltaX, deltaY);
            lastSafeOverlayBounds.Offset(deltaX, deltaY);
            confirmedAnchor = OffsetAnchor(confirmedAnchor, deltaX, deltaY);
            pendingAnchor = OffsetAnchor(pendingAnchor, deltaX, deltaY);
            if (!lastSafeOverlayBounds.IsEmpty)
            {
                ShowOverlayAt(lastSafeOverlayBounds);
            }
        }
        else if (windowState.Handle != lastWindowHandle
            || windowState.Bounds != lastWindowBounds
            || windowState.IsMaximized != lastWindowMaximized)
        {
            AbandonInFlightDetection();
            lastWindowHandle = windowState.Handle;
            lastWindowBounds = windowState.Bounds;
            lastWindowMaximized = windowState.IsMaximized;
            lastComposerBounds = Rectangle.Empty;
            lastSafeOverlayBounds = Rectangle.Empty;
            confirmedAnchor = default;
            pendingAnchor = default;
            lastVisibleAuxiliaryBounds = Array.Empty<Rectangle>();
            lastForegroundIsAuxiliary = false;
            pendingAnchorConfirmations = 0;
            nextPlusVisualVerificationAt = DateTime.MinValue;
            lastUiaConfirmedAt = DateTime.MinValue;
            HideOverlay();
        }

        lastForegroundIsAuxiliary = windowState.ForegroundIsAuxiliary;
        lastVisibleAuxiliaryBounds = windowState.AuxiliaryBounds;
        if (OverlayPlacement.TryReuseDuringAuxiliaryInteraction(
                lastForegroundIsAuxiliary,
                lastSafeOverlayBounds,
                lastVisibleAuxiliaryBounds,
                out var reusableOverlayBounds))
        {
            if (reusableOverlayBounds.IsEmpty)
            {
                HideOverlay();
            }
            else
            {
                ShowOverlayAt(reusableOverlayBounds);
            }

            return;
        }

        if (Visible && OverlayPlacement.IsBlockedByAnyAuxiliary(
                Bounds,
                lastVisibleAuxiliaryBounds))
        {
            HideOverlay();
        }

        if (detectionTask is not { IsCompleted: false })
        {
            var workingArea = Screen.FromRectangle(windowState.Bounds).WorkingArea;
            StartComposerDetection(windowState, workingArea);
        }
    }

    private void StartComposerDetection(CodexWindowState windowState, Rectangle workingArea)
    {
        if (detectionTask is { IsCompleted: false })
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expectedAnchor = confirmedAnchor;
        var useScreenshotFallback = !expectedAnchor.IsEmpty
            && !expectedAnchor.IsHomePage
            && now - lastUiaConfirmedAt <= ScreenshotFallbackLifetime;
        var verifyUiaPlus = now >= nextPlusVisualVerificationAt;
        if (verifyUiaPlus)
        {
            nextPlusVisualVerificationAt = now + PlusVisualVerificationInterval;
        }

        var currentComposerBounds = lastComposerBounds;
        var token = detectionCancellation.Token;
        var locator = uiaComposerLocator;
        var task = Task.Run(() =>
        {
            try
            {
                var uiaFound = locator.TryGetComposerAnchor(
                    windowState.Handle,
                    windowState.Bounds,
                    out var anchor);
                var plusVerificationAttempted = uiaFound && verifyUiaPlus;
                var plusVerificationSucceeded = !plusVerificationAttempted
                    || composerDetector.TryVerifyPlusOnScreen(anchor.PlusBounds);
                if (uiaFound && !plusVerificationSucceeded)
                {
                    uiaFound = false;
                    anchor = default;
                }

                var fallbackAttempted = false;
                var fallbackSucceeded = false;
                if (!uiaFound && useScreenshotFallback)
                {
                    fallbackAttempted = true;
                    fallbackSucceeded = composerDetector.TryVerifyOnScreen(
                        windowState.Bounds,
                        expectedAnchor,
                        out anchor);
                }

                var composerFound = !anchor.IsEmpty
                    && ComposerAnchorGeometry.IsPlausible(
                        windowState.Bounds,
                        anchor.ComposerBounds,
                        anchor.PlusBounds,
                        anchor.HomeHeadingBounds);
                var placementFound = false;
                var overlayBounds = Rectangle.Empty;
                if (composerFound)
                {
                    var stabilizedBounds = Stabilize(
                        currentComposerBounds,
                        anchor.ComposerBounds);
                    anchor = anchor with { ComposerBounds = stabilizedBounds };
                    placementFound = OverlayPlacement.TryFind(
                        windowState.Bounds,
                        windowState.IsMaximized,
                        anchor,
                        workingArea,
                        candidate => locator.ResolveClearRegionAbove(
                            windowState.Handle,
                            windowState.Bounds,
                            Rectangle.Intersect(windowState.Bounds, workingArea),
                            candidate,
                            anchor),
                        out overlayBounds);
                }

                return new PositionDetectionResult(
                    windowState,
                    uiaFound,
                    plusVerificationAttempted,
                    plusVerificationSucceeded,
                    fallbackAttempted,
                    fallbackSucceeded,
                    composerFound,
                    anchor,
                    placementFound,
                    overlayBounds);
            }
            catch
            {
                return new PositionDetectionResult(
                    windowState,
                    false,
                    verifyUiaPlus,
                    false,
                    useScreenshotFallback,
                    false,
                    false,
                    default,
                    false,
                    Rectangle.Empty);
            }
        }, token);
        detectionTask = task;

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsCanceled || completed.IsFaulted || token.IsCancellationRequested)
                {
                    return;
                }

                PostToUi(() => ApplyPositionDetection(completed, completed.Result));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ApplyPositionDetection(
        Task<PositionDetectionResult> sourceTask,
        PositionDetectionResult result)
    {
        if (!ReferenceEquals(detectionTask, sourceTask))
        {
            return;
        }

        detectionTask = null;
        if (petsDragFreezeActive)
        {
            return;
        }

        if (result.WindowState.Handle != lastWindowHandle
            || result.WindowState.Bounds != lastWindowBounds
            || result.WindowState.IsMaximized != lastWindowMaximized)
        {
            return;
        }

        if (OverlayPlacement.TryReuseDuringAuxiliaryInteraction(
                lastForegroundIsAuxiliary,
                lastSafeOverlayBounds,
                lastVisibleAuxiliaryBounds,
                out var reusableOverlayBounds))
        {
            if (reusableOverlayBounds.IsEmpty)
            {
                HideOverlay();
            }
            else
            {
                ShowOverlayAt(reusableOverlayBounds);
            }

            return;
        }

        if (result.UiaFound)
        {
            lastUiaConfirmedAt = DateTime.UtcNow;

            if (HasSameIdentity(confirmedAnchor, result.Anchor)
                && ComposerAnchorGeometry.IsSameAnchor(confirmedAnchor, result.Anchor))
            {
                confirmedAnchor = result.Anchor;
                pendingAnchor = default;
                pendingAnchorConfirmations = 0;
            }
            else
            {
                if (ComposerAnchorGeometry.IsSameAnchor(pendingAnchor, result.Anchor))
                {
                    pendingAnchorConfirmations++;
                    pendingAnchor = result.Anchor;
                }
                else
                {
                    pendingAnchor = result.Anchor;
                    pendingAnchorConfirmations = 1;
                }

                if (pendingAnchorConfirmations < 2)
                {
                    lastSafeOverlayBounds = Rectangle.Empty;
                    HideOverlay();
                    return;
                }

                confirmedAnchor = result.Anchor;
                pendingAnchor = default;
                pendingAnchorConfirmations = 0;
            }
        }
        else if (result.FallbackSucceeded
            && !confirmedAnchor.IsEmpty
            && HasSameIdentity(confirmedAnchor, result.Anchor))
        {
            confirmedAnchor = result.Anchor;
        }
        else
        {
            lastComposerBounds = Rectangle.Empty;
            lastSafeOverlayBounds = Rectangle.Empty;
            HideOverlay();
            return;
        }

        if (!result.ComposerFound)
        {
            lastSafeOverlayBounds = Rectangle.Empty;
            HideOverlay();
            return;
        }

        lastComposerBounds = result.Anchor.ComposerBounds;
        if (!result.PlacementFound)
        {
            lastSafeOverlayBounds = Rectangle.Empty;
            HideOverlay();
            return;
        }

        lastSafeOverlayBounds = result.OverlayBounds;
        if (OverlayPlacement.IsBlockedByAnyAuxiliary(
                result.OverlayBounds,
                lastVisibleAuxiliaryBounds))
        {
            HideOverlay();
            return;
        }

        ShowOverlayAt(result.OverlayBounds);
    }

    private void ShowOverlayAt(Rectangle overlayBounds)
    {
        var needsRender = !Visible;
        if (Bounds != overlayBounds)
        {
            SetBounds(
                overlayBounds.X,
                overlayBounds.Y,
                overlayBounds.Width,
                overlayBounds.Height,
                BoundsSpecified.All);
            needsRender = true;
        }

        if (needsRender)
        {
            RenderLayeredWindow();
        }

        if (!Visible)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.ShowNoActivate);
        }
    }

    private void CheckCodexLifecycle()
    {
        if (CodexLifecycle.IsRunning())
        {
            missingCodexChecks = 0;
            return;
        }

        missingCodexChecks++;
        if (missingCodexChecks >= MissingCodexChecksBeforeExit)
        {
            Application.Exit();
        }
    }

    private void HideOverlay()
    {
        if (Visible)
        {
            Hide();
        }
    }

    private void AbandonInFlightDetection()
    {
        var abandonedTask = detectionTask;
        detectionTask = null;
        if (abandonedTask is not { IsCompleted: false })
        {
            return;
        }

        var abandonedLocator = uiaComposerLocator;
        uiaComposerLocator = new UiaComposerLocator();
        _ = abandonedTask.ContinueWith(
            _ => abandonedLocator.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool HasSameIdentity(ComposerAnchor first, ComposerAnchor second)
    {
        if (first.IsEmpty || second.IsEmpty)
        {
            return false;
        }

        return !string.IsNullOrEmpty(first.Identity)
            && string.Equals(first.Identity, second.Identity, StringComparison.Ordinal);
    }

    private static ComposerAnchor OffsetAnchor(ComposerAnchor anchor, int deltaX, int deltaY)
    {
        if (anchor.IsEmpty)
        {
            return anchor;
        }

        var composerBounds = anchor.ComposerBounds;
        var plusBounds = anchor.PlusBounds;
        var headingBounds = anchor.HomeHeadingBounds;
        composerBounds.Offset(deltaX, deltaY);
        plusBounds.Offset(deltaX, deltaY);
        if (!headingBounds.IsEmpty)
        {
            headingBounds.Offset(deltaX, deltaY);
        }
        return anchor with
        {
            ComposerBounds = composerBounds,
            PlusBounds = plusBounds,
            HomeHeadingBounds = headingBounds
        };
    }

    private static Rectangle Stabilize(Rectangle current, Rectangle candidate)
    {
        if (current.IsEmpty)
        {
            return candidate;
        }

        const int tolerance = 4;
        return Math.Abs(current.Left - candidate.Left) <= tolerance
            && Math.Abs(current.Top - candidate.Top) <= tolerance
            && Math.Abs(current.Right - candidate.Right) <= tolerance
            && Math.Abs(current.Bottom - candidate.Bottom) <= tolerance
                ? current
                : candidate;
    }

    private void OnQuotaChanged(QuotaSnapshot value)
    {
        if (IsDisposed)
        {
            return;
        }

        PostToUi(() =>
        {
            if (PetsDragFreezePolicy.ShouldDeferVisualUpdate(petsDragFreezeActive))
            {
                deferredSnapshot = value;
                return;
            }

            ApplyQuotaSnapshot(value);
        });
    }

    private void OnStatusChanged(string value)
    {
        if (IsDisposed)
        {
            return;
        }

        PostToUi(() =>
        {
            if (PetsDragFreezePolicy.ShouldDeferVisualUpdate(petsDragFreezeActive))
            {
                deferredStatusText = value;
                return;
            }

            ApplyStatusText(value);
        });
    }

    private void ApplyDeferredVisualUpdate()
    {
        if (deferredSnapshot is { } pendingSnapshot)
        {
            deferredSnapshot = null;
            deferredStatusText = null;
            ApplyQuotaSnapshot(pendingSnapshot);
            return;
        }

        if (deferredStatusText is { } pendingStatus)
        {
            deferredStatusText = null;
            ApplyStatusText(pendingStatus);
        }
    }

    private void ApplyQuotaSnapshot(QuotaSnapshot value)
    {
        if (snapshot == value)
        {
            return;
        }

        snapshot = value;
        statusText = value.CompactLabel;
        AccessibleDescription = value.AccessibleDescription;
        trayStatusItem.Text = value.AccessibleDescription;
        RenderLayeredWindow();
    }

    private void ApplyStatusText(string value)
    {
        if (snapshot is not null || string.Equals(statusText, value, StringComparison.Ordinal))
        {
            return;
        }

        statusText = value;
        AccessibleDescription = value;
        trayStatusItem.Text = value;
        RenderLayeredWindow();
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Color StateColor(double remainingPercent) => remainingPercent switch
    {
        <= 15d => CriticalColor,
        <= 35d => CautionColor,
        _ => HealthyColor
    };

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        positionTimer.Stop();
        positionTimer.Dispose();
        lifecycleTimer.Stop();
        lifecycleTimer.Dispose();
        detectionCancellation.Cancel();
        detectionCancellation.Dispose();
        uiaComposerLocator.Dispose();
        composerDetector.Dispose();
        trayIcon.Visible = false;
        trayIcon.ContextMenuStrip?.Dispose();
        trayIcon.Dispose();
        quotaClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        labelFont.Dispose();
        strongLabelFont.Dispose();
        base.OnFormClosed(e);
    }

    private sealed record PositionDetectionResult(
        CodexWindowState WindowState,
        bool UiaFound,
        bool PlusVerificationAttempted,
        bool PlusVerificationSucceeded,
        bool FallbackAttempted,
        bool FallbackSucceeded,
        bool ComposerFound,
        ComposerAnchor Anchor,
        bool PlacementFound,
        Rectangle OverlayBounds);

    private static class NativeMethods
    {
        internal const int ShowNoActivate = 4;
        internal const byte SourceOver = 0;
        internal const byte SourceAlpha = 1;
        internal const int LayeredWindowUseAlpha = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint(int x, int y)
        {
            internal int X = x;
            internal int Y = y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSize(int width, int height)
        {
            internal int Width = width;
            internal int Height = height;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr window,
            IntPtr destinationDc,
            ref NativePoint destination,
            ref NativeSize size,
            IntPtr sourceDc,
            ref NativePoint source,
            int colorKey,
            ref BlendFunction blend,
            int flags);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr graphicsObject);
    }
}
