namespace CodexQuotaBar;

internal static class OverlayPlacement
{
    internal const int Height = 28;
    internal const int ComposerGap = 6;

    internal static bool TryFind(
        Rectangle windowBounds,
        bool windowIsMaximized,
        ComposerAnchor anchor,
        Rectangle workingArea,
        Func<Rectangle, Rectangle?> resolveClearAbove,
        out Rectangle overlayBounds)
    {
        if (!anchor.IsHomePage)
        {
            return TryFind(windowBounds, windowIsMaximized, anchor.ComposerBounds,
                workingArea, resolveClearAbove, out overlayBounds);
        }

        overlayBounds = Rectangle.Empty;
        var composerBounds = anchor.ComposerBounds;
        var headingBounds = anchor.HomeHeadingBounds;
        var gap = composerBounds.Top - headingBounds.Bottom;
        if (!HomePageRecognition.IsHeadingPositionValid(windowBounds, composerBounds, headingBounds)
            || gap < Height + 2)
        {
            return false;
        }

        var candidate = new Rectangle(
            composerBounds.Left + 8,
            headingBounds.Bottom + (gap - Height) / 2,
            Math.Max(320, composerBounds.Width - 16),
            Height);
        // A home-page bar must stay in the title/composer gap, even when maximized.
        if (!Contains(Rectangle.Intersect(windowBounds, workingArea), candidate)
            || resolveClearAbove(candidate) != candidate)
        {
            return false;
        }

        overlayBounds = candidate;
        return true;
    }

    internal static bool TryFind(
        Rectangle windowBounds,
        bool windowIsMaximized,
        Rectangle composerBounds,
        Rectangle workingArea,
        Func<Rectangle, bool> isClearAbove,
        out Rectangle overlayBounds) =>
        TryFind(
            windowBounds,
            windowIsMaximized,
            composerBounds,
            workingArea,
            candidate => isClearAbove(candidate) ? candidate : null,
            out overlayBounds);

    internal static bool TryFind(
        Rectangle windowBounds,
        bool windowIsMaximized,
        Rectangle composerBounds,
        Rectangle workingArea,
        Func<Rectangle, Rectangle?> resolveClearAbove,
        out Rectangle overlayBounds)
    {
        overlayBounds = Rectangle.Empty;
        var usableArea = Rectangle.Intersect(windowBounds, workingArea);
        if (usableArea.IsEmpty || composerBounds.IsEmpty)
        {
            return false;
        }

        var width = Math.Max(320, composerBounds.Width - 16);
        var x = composerBounds.Left + 8;

        var above = new Rectangle(x, composerBounds.Top - Height - ComposerGap, width, Height);
        var clearAbove = Contains(usableArea, above)
            ? resolveClearAbove(above)
            : null;
        if (clearAbove is { } resolvedAbove
            && resolvedAbove.X == above.X
            && resolvedAbove.Width == above.Width
            && resolvedAbove.Height == above.Height
            && resolvedAbove.Top <= above.Top
            && Contains(usableArea, resolvedAbove))
        {
            overlayBounds = resolvedAbove;
            return true;
        }

        if (!windowIsMaximized)
        {
            return false;
        }

        var below = new Rectangle(x, composerBounds.Bottom + ComposerGap, width, Height);
        if (!Contains(usableArea, below))
        {
            return false;
        }

        overlayBounds = below;
        return true;
    }

    internal static bool IsBlockedByAuxiliary(
        Rectangle overlayBounds,
        Rectangle auxiliaryBounds)
    {
        if (overlayBounds.IsEmpty || auxiliaryBounds.IsEmpty)
        {
            return false;
        }

        var overlap = Rectangle.Intersect(overlayBounds, auxiliaryBounds);
        return overlap.Width > 0 && overlap.Height > 0;
    }

    internal static bool IsBlockedByAnyAuxiliary(
        Rectangle overlayBounds,
        IReadOnlyList<Rectangle> auxiliaryBounds) =>
        auxiliaryBounds.Any(bounds => IsBlockedByAuxiliary(overlayBounds, bounds));

    internal static bool TryReuseDuringAuxiliaryInteraction(
        bool foregroundIsAuxiliary,
        Rectangle lastSafeOverlayBounds,
        IReadOnlyList<Rectangle> auxiliaryBounds,
        out Rectangle reusableOverlayBounds)
    {
        reusableOverlayBounds = Rectangle.Empty;
        if (!foregroundIsAuxiliary || lastSafeOverlayBounds.IsEmpty)
        {
            return false;
        }

        if (!IsBlockedByAnyAuxiliary(lastSafeOverlayBounds, auxiliaryBounds))
        {
            reusableOverlayBounds = lastSafeOverlayBounds;
        }

        return true;
    }

    private static bool Contains(Rectangle outer, Rectangle inner)
    {
        return inner.Left >= outer.Left
            && inner.Top >= outer.Top
            && inner.Right <= outer.Right
            && inner.Bottom <= outer.Bottom;
    }

}
