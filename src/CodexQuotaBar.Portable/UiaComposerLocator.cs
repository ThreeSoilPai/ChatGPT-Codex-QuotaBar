using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsRect = System.Windows.Rect;

namespace CodexQuotaBar;

internal sealed class UiaComposerLocator : IDisposable
{
    private static readonly TimeSpan FullRediscoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CollisionRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CollisionEventDelay = TimeSpan.FromMilliseconds(500);

    private readonly object gate = new();
    private readonly StructureChangedEventHandler structureChangedHandler;
    private readonly AutomationPropertyChangedEventHandler composerPropertyChangedHandler;
    private AutomationElement? root;
    private AutomationElement? composer;
    private AutomationElement? plusButton;
    private AutomationElement? homeHeading;
    private IntPtr rootHandle;
    private DateTime lastDiscoveryAt = DateTime.MinValue;
    private DateTime lastCollisionRefreshAt = DateTime.MinValue;
    private Rectangle lastCollisionCandidate = Rectangle.Empty;
    private IReadOnlyList<UiaNodeDescriptor> collisionNodes = Array.Empty<UiaNodeDescriptor>();
    private int collisionChanged = 1;
    private bool hasCollisionSnapshot;
    private bool disposed;

    internal UiaComposerLocator()
    {
        structureChangedHandler = (_, _) =>
        {
            Interlocked.Exchange(ref collisionChanged, 1);
        };
        composerPropertyChangedHandler = (_, _) =>
        {
            Interlocked.Exchange(ref collisionChanged, 1);
        };
    }

    internal bool TryGetComposer(IntPtr windowHandle, Rectangle windowBounds, out Rectangle composerBounds)
    {
        var found = TryGetComposerAnchor(windowHandle, windowBounds, out var anchor);
        composerBounds = found ? anchor.ComposerBounds : Rectangle.Empty;
        return found;
    }

    internal bool TryRediscoverComposer(IntPtr windowHandle, Rectangle windowBounds, out Rectangle composerBounds)
    {
        var found = TryRediscoverComposerAnchor(windowHandle, windowBounds, out var anchor);
        composerBounds = found ? anchor.ComposerBounds : Rectangle.Empty;
        return found;
    }

    internal bool TryGetComposerAnchor(
        IntPtr windowHandle,
        Rectangle windowBounds,
        out ComposerAnchor anchor) =>
        TryGetComposerAnchorCore(windowHandle, windowBounds, forceRediscovery: false, out anchor);

    internal bool TryRediscoverComposerAnchor(
        IntPtr windowHandle,
        Rectangle windowBounds,
        out ComposerAnchor anchor) =>
        TryGetComposerAnchorCore(windowHandle, windowBounds, forceRediscovery: true, out anchor);

    private bool TryGetComposerAnchorCore(
        IntPtr windowHandle,
        Rectangle windowBounds,
        bool forceRediscovery,
        out ComposerAnchor anchor)
    {
        anchor = default;
        if (windowHandle == IntPtr.Zero
            || windowBounds.IsEmpty
            || Environment.GetEnvironmentVariable("CODEX_QUOTA_DISABLE_UIA") == "1")
        {
            return false;
        }

        lock (gate)
        {
            if (disposed || !EnsureRoot(windowHandle))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            var cachedIsValid = TryReadAnchor(windowBounds, out var cachedAnchor);
            var shouldRediscover = forceRediscovery
                || !cachedIsValid
                || now - lastDiscoveryAt >= FullRediscoveryInterval;

            if (shouldRediscover)
            {
                if (TryDiscoverComposerAnchor(
                        windowBounds,
                        out var discoveredComposer,
                        out var discoveredPlusButton,
                        out var discoveredHomeHeading,
                        out var discoveredAnchor))
                {
                    SetAnchor(discoveredComposer, discoveredPlusButton, discoveredHomeHeading);
                    lastDiscoveryAt = now;
                    anchor = discoveredAnchor;
                    return true;
                }

                lastDiscoveryAt = now;
                if (!cachedIsValid)
                {
                    ClearComposer();
                    return false;
                }
            }

            anchor = cachedAnchor;
            return cachedIsValid;
        }
    }

    internal bool IsRegionClear(IntPtr windowHandle, Rectangle windowBounds, Rectangle candidateBounds)
    {
        if (windowHandle == IntPtr.Zero || windowBounds.IsEmpty || candidateBounds.IsEmpty)
        {
            return false;
        }

        lock (gate)
        {
            if (disposed
                || !EnsureRoot(windowHandle)
                || !EnsureCollisionSnapshot(candidateBounds))
            {
                return false;
            }

            return UiaCollisionRules.IsClear(candidateBounds, windowBounds, collisionNodes);
        }
    }

    internal Rectangle? ResolveClearRegionAbove(
        IntPtr windowHandle,
        Rectangle windowBounds,
        Rectangle usableArea,
        Rectangle candidateBounds,
        ComposerAnchor anchor = default)
    {
        if (windowHandle == IntPtr.Zero
            || windowBounds.IsEmpty
            || usableArea.IsEmpty
            || candidateBounds.IsEmpty)
        {
            return null;
        }

        lock (gate)
        {
            if (disposed
                || !EnsureRoot(windowHandle)
                || !EnsureCollisionSnapshot(candidateBounds))
            {
                return null;
            }

            if (anchor.IsHomePage)
            {
                return UiaCollisionRules.IsClearOnHomePage(candidateBounds, windowBounds, anchor, collisionNodes)
                    ? candidateBounds
                    : null;
            }

            return UiaCollisionRules.TryResolveClearRegionAboveActiveGoal(
                candidateBounds,
                usableArea,
                windowBounds,
                collisionNodes,
                out var clearBounds)
                    ? clearBounds
                    : null;
        }
    }

    internal UiaNodeDescriptor? GetLastCollisionBlocker(Rectangle windowBounds, Rectangle candidateBounds)
    {
        lock (gate)
        {
            return UiaCollisionRules.TryFindBlocker(
                candidateBounds,
                windowBounds,
                collisionNodes,
                out var blocker)
                    ? blocker
                    : null;
        }
    }

    private bool EnsureCollisionSnapshot(Rectangle candidateBounds)
    {
        var now = DateTime.UtcNow;
        var candidateChanged = candidateBounds != lastCollisionCandidate;
        var eventChanged = Volatile.Read(ref collisionChanged) != 0;
        var shouldRefresh = !hasCollisionSnapshot
            || candidateChanged
            || now - lastCollisionRefreshAt >= CollisionRefreshInterval
            || (eventChanged && now - lastCollisionRefreshAt >= CollisionEventDelay);
        if (!shouldRefresh)
        {
            return true;
        }

        if (!TryRefreshCollisionSnapshot())
        {
            return false;
        }

        lastCollisionRefreshAt = now;
        lastCollisionCandidate = candidateBounds;
        hasCollisionSnapshot = true;
        Interlocked.Exchange(ref collisionChanged, 0);
        return true;
    }

    private bool EnsureRoot(IntPtr windowHandle)
    {
        if (root is not null && rootHandle == windowHandle)
        {
            return true;
        }

        ClearAutomationElements();
        try
        {
            root = AutomationElement.FromHandle(windowHandle);
            rootHandle = windowHandle;
            Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, structureChangedHandler);
            Interlocked.Exchange(ref collisionChanged, 1);
            return true;
        }
        catch
        {
            root = null;
            rootHandle = IntPtr.Zero;
            return false;
        }
    }

    private bool TryDiscoverComposerAnchor(
        Rectangle windowBounds,
        out AutomationElement discoveredComposer,
        out AutomationElement discoveredPlusButton,
        out AutomationElement? discoveredHomeHeading,
        out ComposerAnchor discoveredAnchor)
    {
        discoveredComposer = null!;
        discoveredPlusButton = null!;
        discoveredHomeHeading = null;
        discoveredAnchor = default;
        if (root is null)
        {
            return false;
        }

        try
        {
            var request = new CacheRequest { TreeScope = TreeScope.Element };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.IsEnabledProperty);
            request.Add(AutomationElement.ControlTypeProperty);

            AutomationElementCollection buttons;
            using (request.Activate())
            {
                var buttonCondition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button);
                var semanticPlusCondition = new OrCondition(
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "composer-plus-btn"),
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        "添加文件等内容"),
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        "添加文件等"),
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        "Add files and more"),
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        "Add files"),
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        "Attach files"));
                buttons = root.FindAll(
                    TreeScope.Descendants,
                    new AndCondition(buttonCondition, semanticPlusCondition));
                if (buttons.Count == 0)
                {
                    buttons = root.FindAll(TreeScope.Descendants, buttonCondition);
                }
            }

            var headings = root.FindAll(
                TreeScope.Descendants,
                new AndCondition(
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)),
                    new OrCondition(HomePageRecognition.HeadingNames
                        .Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name))
                        .ToArray())));
            var candidates = new List<(AutomationElement Composer, AutomationElement Plus,
                AutomationElement? Heading, ComposerAnchor Anchor)>();
            foreach (AutomationElement button in buttons)
            {
                if (!TryReadCachedButton(button, out var buttonBounds, out _, out _, out _)
                    || !TryFindComposerAncestor(
                        button,
                        windowBounds,
                        buttonBounds,
                        headings,
                        out var composerElement,
                        out var composerBounds,
                        out var headingElement,
                        out var headingBounds))
                {
                    continue;
                }

                var anchor = new ComposerAnchor(
                    composerBounds,
                    buttonBounds,
                    CreateIdentity(composerElement, button),
                    headingBounds);
                candidates.Add((composerElement, button, headingElement, anchor));
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            if (candidates.Count > 1 && plusButton is not null && composer is not null)
            {
                var currentIdentity = CreateIdentity(composer, plusButton);
                candidates = candidates
                    .Where(candidate => string.Equals(
                        candidate.Anchor.Identity,
                        currentIdentity,
                        StringComparison.Ordinal))
                    .ToList();
            }

            if (candidates.Count != 1)
            {
                return false;
            }

            var selected = candidates[0];
            discoveredComposer = selected.Composer;
            discoveredPlusButton = selected.Plus;
            discoveredHomeHeading = selected.Heading;
            discoveredAnchor = selected.Anchor;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindComposerAncestor(
        AutomationElement button,
        Rectangle windowBounds,
        Rectangle buttonBounds,
        AutomationElementCollection headings,
        out AutomationElement composerElement,
        out Rectangle composerBounds,
        out AutomationElement? headingElement,
        out Rectangle headingBounds)
    {
        composerElement = null!;
        composerBounds = Rectangle.Empty;
        headingElement = null;
        headingBounds = Rectangle.Empty;
        var walker = TreeWalker.RawViewWalker;
        var current = walker.GetParent(button);
        for (var depth = 1; current is not null && depth <= 6; depth++)
        {
            if (TryReadElement(current, out var bounds, out var className, out _, out _)
                && IsKnownComposerContainerClass(className))
            {
                AutomationElement? matchingHeading = null;
                var matchingBounds = Rectangle.Empty;
                var headingCount = 0;
                foreach (AutomationElement heading in headings)
                {
                    if (TryReadHomeHeading(heading, windowBounds, bounds, out var titleBounds))
                    {
                        matchingHeading = heading;
                        matchingBounds = titleBounds;
                        headingCount++;
                    }
                }

                if (headingCount > 1)
                {
                    return false;
                }

                if (ComposerAnchorGeometry.IsPlausible(windowBounds, bounds, buttonBounds, matchingBounds))
                {
                    composerElement = current;
                    composerBounds = bounds;
                    headingElement = matchingHeading;
                    headingBounds = matchingBounds;
                    return true;
                }
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    private static bool IsKnownComposerContainerClass(string className) =>
        className.Contains("ComposerLayoutBody", StringComparison.OrdinalIgnoreCase)
        || className.Contains("ComposerLayoutRoot", StringComparison.OrdinalIgnoreCase);

    private bool TryRefreshCollisionSnapshot()
    {
        if (root is null)
        {
            return false;
        }

        try
        {
            var request = new CacheRequest { TreeScope = TreeScope.Element };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.HeadingLevelProperty);

            AutomationElementCollection elements;
            using (request.Activate())
            {
                elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }

            var nodes = new List<UiaNodeDescriptor>(elements.Count);
            foreach (AutomationElement element in elements)
            {
                try
                {
                    if (!TryConvert(element.Cached.BoundingRectangle, out var bounds) || bounds.IsEmpty)
                    {
                        continue;
                    }

                    nodes.Add(new UiaNodeDescriptor(
                        bounds,
                        element.Cached.ControlType.Id,
                        element.Cached.ClassName ?? string.Empty,
                        element.Cached.IsOffscreen,
                        element.Cached.Name ?? string.Empty,
                        element.Cached.AutomationId ?? string.Empty,
                        ReadHeadingLevel(element.GetCachedPropertyValue(AutomationElement.HeadingLevelProperty, true)),
                        HomePageRecognition.HeadingNames.Contains(element.Cached.Name?.Trim(), StringComparer.OrdinalIgnoreCase)
                            && IsInHomeHero(element)));
                }
                catch
                {
                }
            }

            collisionNodes = nodes;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadAnchor(Rectangle windowBounds, out ComposerAnchor anchor)
    {
        anchor = default;
        if (!TryReadElement(composer, out var composerBounds, out var composerClass, out _, out _)
            || !TryReadElement(plusButton, out var buttonBounds, out _, out _, out var buttonType)
            || !IsKnownComposerContainerClass(composerClass)
            || buttonType != ControlType.Button)
        {
            return false;
        }

        var headingBounds = Rectangle.Empty;
        if (homeHeading is not null
            && !TryReadHomeHeading(homeHeading, windowBounds, composerBounds, out headingBounds))
        {
            return false;
        }

        if (!ComposerAnchorGeometry.IsPlausible(windowBounds, composerBounds, buttonBounds, headingBounds))
        {
            return false;
        }

        anchor = new ComposerAnchor(
            composerBounds,
            buttonBounds,
            CreateIdentity(composer!, plusButton!),
            headingBounds);
        return true;
    }

    private static bool TryReadHomeHeading(
        AutomationElement element,
        Rectangle windowBounds,
        Rectangle composerBounds,
        out Rectangle headingBounds)
    {
        headingBounds = Rectangle.Empty;
        try
        {
            var value = element.Current;
            if (!TryConvert(value.BoundingRectangle, out var bounds)
                || !HomePageRecognition.IsHeading(new UiaNodeDescriptor(bounds, value.ControlType.Id,
                    value.ClassName ?? string.Empty, value.IsOffscreen, value.Name ?? string.Empty,
                    HeadingLevel: ReadHeadingLevel(element.GetCurrentPropertyValue(AutomationElement.HeadingLevelProperty, true)),
                    InHomeHero: IsInHomeHero(element)))
                || !HomePageRecognition.IsHeadingPositionValid(windowBounds, composerBounds, bounds))
            {
                return false;
            }

            headingBounds = bounds;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetAnchor(AutomationElement composerValue, AutomationElement plusButtonValue,
        AutomationElement? homeHeadingValue)
    {
        ClearComposer();
        composer = composerValue;
        plusButton = plusButtonValue;
        homeHeading = homeHeadingValue;
        try
        {
            Automation.AddAutomationPropertyChangedEventHandler(
                composer,
                TreeScope.Element,
                composerPropertyChangedHandler,
                AutomationElement.BoundingRectangleProperty,
                AutomationElement.IsOffscreenProperty);
            Automation.AddAutomationPropertyChangedEventHandler(
                plusButton,
                TreeScope.Element,
                composerPropertyChangedHandler,
                AutomationElement.BoundingRectangleProperty,
                AutomationElement.IsOffscreenProperty,
                AutomationElement.IsEnabledProperty);
        }
        catch
        {
        }

        Interlocked.Exchange(ref collisionChanged, 1);
    }

    private static int ReadHeadingLevel(object value) =>
        value is IConvertible convertible ? convertible.ToInt32(null) : 80050;

    private static bool IsInHomeHero(AutomationElement element)
    {
        // The Work home title is plain text, not an accessibility heading. Require
        // both stable home layout markers so an identical chat sentence cannot qualify.
        var current = TreeWalker.RawViewWalker.GetParent(element);
        var hasHero = false;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            var state = current.Current;
            if (state.IsOffscreen)
            {
                return false;
            }

            var tokens = (state.ClassName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            hasHero |= tokens.Contains("home-composer-anchor", StringComparer.Ordinal);
            if (tokens.Contains("group/home-composer-layout", StringComparer.Ordinal))
            {
                return hasHero;
            }

            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        return false;
    }

    private void ClearComposer()
    {
        if (composer is not null)
        {
            try
            {
                Automation.RemoveAutomationPropertyChangedEventHandler(composer, composerPropertyChangedHandler);
            }
            catch
            {
            }
        }

        if (plusButton is not null)
        {
            try
            {
                Automation.RemoveAutomationPropertyChangedEventHandler(
                    plusButton,
                    composerPropertyChangedHandler);
            }
            catch
            {
            }
        }

        composer = null;
        plusButton = null;
        homeHeading = null;
    }

    private void ClearAutomationElements()
    {
        ClearComposer();
        if (root is not null)
        {
            try
            {
                Automation.RemoveStructureChangedEventHandler(root, structureChangedHandler);
            }
            catch
            {
            }
        }

        root = null;
        rootHandle = IntPtr.Zero;
        lastDiscoveryAt = DateTime.MinValue;
        lastCollisionRefreshAt = DateTime.MinValue;
        lastCollisionCandidate = Rectangle.Empty;
        collisionNodes = Array.Empty<UiaNodeDescriptor>();
        hasCollisionSnapshot = false;
    }

    private static bool TryReadCachedButton(
        AutomationElement element,
        out Rectangle bounds,
        out string className,
        out string name,
        out string automationId)
    {
        bounds = Rectangle.Empty;
        className = string.Empty;
        name = string.Empty;
        automationId = string.Empty;
        try
        {
            if (element.Cached.IsOffscreen
                || !element.Cached.IsEnabled
                || element.Cached.ControlType != ControlType.Button)
            {
                return false;
            }

            className = element.Cached.ClassName ?? string.Empty;
            name = element.Cached.Name ?? string.Empty;
            automationId = element.Cached.AutomationId ?? string.Empty;
            return TryConvert(element.Cached.BoundingRectangle, out bounds);
        }
        catch
        {
            return false;
        }
    }

    private static string CreateIdentity(
        AutomationElement composerElement,
        AutomationElement buttonElement)
    {
        try
        {
            return string.Join('.', composerElement.GetRuntimeId())
                + '|'
                + string.Join('.', buttonElement.GetRuntimeId());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryReadElement(
        AutomationElement? element,
        out Rectangle bounds,
        out string className,
        out bool focusable,
        out ControlType? controlType)
    {
        bounds = Rectangle.Empty;
        className = string.Empty;
        focusable = false;
        controlType = null;
        if (element is null)
        {
            return false;
        }

        try
        {
            var current = element.Current;
            if (current.IsOffscreen || !current.IsEnabled)
            {
                return false;
            }

            className = current.ClassName ?? string.Empty;
            focusable = current.IsKeyboardFocusable;
            controlType = current.ControlType;
            return TryConvert(current.BoundingRectangle, out bounds);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryConvert(WindowsRect source, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (source.IsEmpty
            || double.IsNaN(source.Left)
            || double.IsNaN(source.Top)
            || double.IsNaN(source.Right)
            || double.IsNaN(source.Bottom)
            || double.IsInfinity(source.Left)
            || double.IsInfinity(source.Top)
            || double.IsInfinity(source.Right)
            || double.IsInfinity(source.Bottom))
        {
            return false;
        }

        var left = (int)Math.Floor(source.Left);
        var top = (int)Math.Floor(source.Top);
        var right = (int)Math.Ceiling(source.Right);
        var bottom = (int)Math.Ceiling(source.Bottom);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        bounds = Rectangle.FromLTRB(left, top, right, bottom);
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ClearAutomationElements();
        }
    }
}

internal readonly record struct UiaNodeDescriptor(
    Rectangle Bounds,
    int ControlTypeId,
    string ClassName,
    bool IsOffscreen,
    string Name = "",
    string AutomationId = "",
    int HeadingLevel = 80050,
    bool InHomeHero = false);

internal static class UiaCollisionRules
{
    private static readonly HashSet<int> SemanticControlTypes =
    [
        ControlType.Text.Id,
        ControlType.Button.Id,
        ControlType.Edit.Id,
        ControlType.Image.Id,
        ControlType.ListItem.Id,
        ControlType.DataItem.Id,
        ControlType.MenuItem.Id,
        ControlType.CheckBox.Id,
        ControlType.RadioButton.Id,
        ControlType.ComboBox.Id,
        ControlType.Hyperlink.Id,
        ControlType.TabItem.Id,
        ControlType.TreeItem.Id,
        ControlType.ToolBar.Id,
        ControlType.StatusBar.Id,
        ControlType.Separator.Id
    ];

    private static readonly HashSet<int> StructuralControlTypes =
    [
        ControlType.Group.Id,
        ControlType.Pane.Id,
        ControlType.Document.Id,
        ControlType.Custom.Id,
        ControlType.Window.Id
    ];

    internal static bool IsClear(
        Rectangle candidateBounds,
        Rectangle windowBounds,
        IReadOnlyList<UiaNodeDescriptor> nodes)
    {
        return !TryFindBlocker(candidateBounds, windowBounds, nodes, out _);
    }

    internal static bool IsClearOnHomePage(
        Rectangle candidateBounds,
        Rectangle windowBounds,
        ComposerAnchor anchor,
        IReadOnlyList<UiaNodeDescriptor> nodes)
    {
        if (!anchor.IsHomePage
            || !HomePageRecognition.IsHeadingPositionValid(windowBounds, anchor.ComposerBounds, anchor.HomeHeadingBounds)
            || !nodes.Any(node => HomePageRecognition.IsHeading(node)
                && ComposerAnchorGeometry.BoundsAreClose(node.Bounds, anchor.HomeHeadingBounds, 4)))
        {
            return false;
        }

        // Ignore only the shared home-page layout wrapper, never text or local controls.
        var contentNodes = nodes.Where(node => !(StructuralControlTypes.Contains(node.ControlTypeId)
            && ComposerGeometry.ContainsWithTolerance(node.Bounds, anchor.HomeHeadingBounds, 0)
            && ComposerGeometry.ContainsWithTolerance(node.Bounds, anchor.ComposerBounds, 0))).ToArray();
        return IsClear(candidateBounds, windowBounds, contentNodes);
    }

    internal static bool TryFindBlocker(
        Rectangle candidateBounds,
        Rectangle windowBounds,
        IReadOnlyList<UiaNodeDescriptor> nodes,
        out UiaNodeDescriptor blocker)
    {
        foreach (var node in nodes)
        {
            if (node.IsOffscreen || IsVisuallyHidden(node.ClassName))
            {
                continue;
            }

            var intersection = Rectangle.Intersect(candidateBounds, node.Bounds);
            if (intersection.Width < 2 || intersection.Height < 2)
            {
                continue;
            }

            if (SemanticControlTypes.Contains(node.ControlTypeId))
            {
                blocker = node;
                return true;
            }

            if (!StructuralControlTypes.Contains(node.ControlTypeId)
                || IsBroadStructuralContainer(node.Bounds, candidateBounds, windowBounds))
            {
                continue;
            }

            if (IsLocalizedStructuralContainer(node.Bounds, candidateBounds))
            {
                blocker = node;
                return true;
            }
        }

        blocker = default;
        return false;
    }

    internal static bool TryResolveClearRegionAboveActiveGoal(
        Rectangle candidateBounds,
        Rectangle usableArea,
        Rectangle windowBounds,
        IReadOnlyList<UiaNodeDescriptor> nodes,
        out Rectangle clearBounds)
    {
        clearBounds = Rectangle.Empty;
        if (IsClear(candidateBounds, windowBounds, nodes))
        {
            clearBounds = candidateBounds;
            return true;
        }

        if (!TryFindActiveGoalPanel(candidateBounds, windowBounds, nodes, out var goalPanelBounds))
        {
            return false;
        }

        var movedBounds = new Rectangle(
            candidateBounds.X,
            goalPanelBounds.Top - candidateBounds.Height - OverlayPlacement.ComposerGap,
            candidateBounds.Width,
            candidateBounds.Height);
        if (movedBounds.Top >= candidateBounds.Top
            || !Contains(usableArea, movedBounds)
            || !IsClear(movedBounds, windowBounds, nodes))
        {
            return false;
        }

        clearBounds = movedBounds;
        return true;
    }

    private static bool TryFindActiveGoalPanel(
        Rectangle candidateBounds,
        Rectangle windowBounds,
        IReadOnlyList<UiaNodeDescriptor> nodes,
        out Rectangle panelBounds)
    {
        panelBounds = Rectangle.Empty;
        var label = nodes
            .Where(node => !node.IsOffscreen
                && IsActiveGoalLabel(node)
                && HorizontalOverlap(node.Bounds, candidateBounds) >= 2
                && node.Bounds.Bottom >= candidateBounds.Top - 140
                && node.Bounds.Top < candidateBounds.Bottom)
            .OrderByDescending(node => node.Bounds.Bottom)
            .FirstOrDefault();
        if (label.Bounds.IsEmpty)
        {
            return false;
        }

        var maximumPanelHeight = Math.Max(160, candidateBounds.Height * 5);
        var containingPanel = nodes
            .Where(node => !node.IsOffscreen
                && node.Bounds.Height >= label.Bounds.Height
                && node.Bounds.Height <= maximumPanelHeight
                && node.Bounds.Width <= windowBounds.Width * 0.98d
                && node.Bounds.Width >= candidateBounds.Width * 0.35d
                && ComposerGeometry.ContainsWithTolerance(node.Bounds, label.Bounds, 4)
                && Rectangle.Intersect(node.Bounds, candidateBounds).Height >= 2)
            .OrderByDescending(node => (long)node.Bounds.Width * node.Bounds.Height)
            .FirstOrDefault();

        panelBounds = containingPanel.Bounds.IsEmpty
            ? Rectangle.FromLTRB(
                label.Bounds.Left,
                Math.Max(windowBounds.Top, label.Bounds.Top - 18),
                label.Bounds.Right,
                label.Bounds.Bottom)
            : containingPanel.Bounds;
        return true;
    }

    private static bool IsActiveGoalLabel(UiaNodeDescriptor node)
    {
        if (node.Name.Contains("进行中的目标", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("進行中的目標", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("Goal in progress", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("Active goal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return node.AutomationId.Contains("active-goal", StringComparison.OrdinalIgnoreCase)
            || node.AutomationId.Contains("goal-progress", StringComparison.OrdinalIgnoreCase)
            || node.ClassName.Contains("active-goal", StringComparison.OrdinalIgnoreCase)
            || node.ClassName.Contains("goal-progress", StringComparison.OrdinalIgnoreCase);
    }

    private static int HorizontalOverlap(Rectangle first, Rectangle second) =>
        Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));

    private static bool Contains(Rectangle outer, Rectangle inner) =>
        inner.Left >= outer.Left
        && inner.Top >= outer.Top
        && inner.Right <= outer.Right
        && inner.Bottom <= outer.Bottom;

    private static bool IsBroadStructuralContainer(
        Rectangle bounds,
        Rectangle candidateBounds,
        Rectangle windowBounds)
    {
        var candidateArea = Math.Max(1L, (long)candidateBounds.Width * candidateBounds.Height);
        var area = Math.Max(1L, (long)bounds.Width * bounds.Height);
        var coversCandidate = ComposerGeometry.ContainsWithTolerance(bounds, candidateBounds, 2);
        return bounds.Width >= windowBounds.Width * 0.75d
                && bounds.Height >= windowBounds.Height * 0.25d
            || coversCandidate
                && area >= candidateArea * 12L
                && bounds.Height > candidateBounds.Height * 6;
    }

    private static bool IsLocalizedStructuralContainer(Rectangle bounds, Rectangle candidateBounds)
    {
        var candidateArea = Math.Max(1L, (long)candidateBounds.Width * candidateBounds.Height);
        var area = Math.Max(1L, (long)bounds.Width * bounds.Height);
        return area <= candidateArea * 12L
            && (bounds.Height <= candidateBounds.Height * 6
                || bounds.Width <= candidateBounds.Width * 0.65d);
    }

    internal static bool IsVisuallyHidden(string className)
    {
        return className.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => token.Equals("sr-only", StringComparison.OrdinalIgnoreCase)
                || token.Equals("invisible", StringComparison.OrdinalIgnoreCase)
                || token.Equals("visually-hidden", StringComparison.OrdinalIgnoreCase)
                || token.Equals("empty:hidden", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("opacity-0", StringComparison.OrdinalIgnoreCase));
    }
}
