namespace CodexQuotaBar;

internal static class ComposerGeometry
{
    internal static bool IsPlausible(
        Rectangle windowBounds,
        Rectangle composerBounds,
        Rectangle homeHeadingBounds = default)
    {
        if (windowBounds.IsEmpty || composerBounds.IsEmpty)
        {
            return false;
        }

        var minimumWidth = Math.Max(320, (int)(windowBounds.Width * 0.15d));
        var maximumHeight = Math.Clamp((int)(windowBounds.Height * 0.65d), 280, 650);
        var lowerWindowBoundary = windowBounds.Top + (int)(windowBounds.Height * 0.55d);
        var isHomePage = !homeHeadingBounds.IsEmpty;
        if (isHomePage
            && !HomePageRecognition.IsHeadingPositionValid(windowBounds, composerBounds, homeHeadingBounds))
        {
            return false;
        }

        return composerBounds.Width >= minimumWidth
            && composerBounds.Width <= windowBounds.Width + 4
            && composerBounds.Height >= 64
            && composerBounds.Height <= maximumHeight
            && composerBounds.Left >= windowBounds.Left - 4
            && composerBounds.Right <= windowBounds.Right + 4
            && (isHomePage || composerBounds.Bottom >= lowerWindowBoundary)
            && composerBounds.Bottom <= windowBounds.Bottom + 4;
    }

    internal static bool IsPlausibleEdit(Rectangle windowBounds, Rectangle editBounds)
    {
        if (windowBounds.IsEmpty || editBounds.IsEmpty)
        {
            return false;
        }

        var minimumWidth = Math.Max(280, (int)(windowBounds.Width * 0.14d));
        var lowerWindowBoundary = windowBounds.Top + (int)(windowBounds.Height * 0.50d);
        return editBounds.Width >= minimumWidth
            && editBounds.Height >= 24
            && editBounds.Height <= Math.Max(320, (int)(windowBounds.Height * 0.45d))
            && editBounds.Left >= windowBounds.Left - 4
            && editBounds.Right <= windowBounds.Right + 4
            && editBounds.Bottom >= lowerWindowBoundary
            && editBounds.Bottom <= windowBounds.Bottom + 4;
    }

    internal static bool ContainsWithTolerance(Rectangle outer, Rectangle inner, int tolerance = 6)
    {
        return inner.Left >= outer.Left - tolerance
            && inner.Top >= outer.Top - tolerance
            && inner.Right <= outer.Right + tolerance
            && inner.Bottom <= outer.Bottom + tolerance;
    }
}
