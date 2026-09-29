namespace CodexQuotaBar;

internal readonly record struct ComposerAnchor(
    Rectangle ComposerBounds,
    Rectangle PlusBounds,
    string Identity,
    Rectangle HomeHeadingBounds = default)
{
    internal bool IsEmpty => ComposerBounds.IsEmpty || PlusBounds.IsEmpty;
    internal bool IsHomePage => !HomeHeadingBounds.IsEmpty;
}

internal static class ComposerAnchorGeometry
{
    internal static bool IsPlausible(
        Rectangle windowBounds,
        Rectangle composerBounds,
        Rectangle plusBounds,
        Rectangle homeHeadingBounds = default)
    {
        if (!ComposerGeometry.IsPlausible(windowBounds, composerBounds, homeHeadingBounds)
            || plusBounds.IsEmpty
            || !ComposerGeometry.ContainsWithTolerance(composerBounds, plusBounds, 4))
        {
            return false;
        }

        var minimumButtonSize = Math.Max(16, (int)Math.Round(windowBounds.Height * 0.01d));
        var maximumButtonSize = Math.Clamp((int)Math.Round(windowBounds.Height * 0.07d), 56, 96);
        if (plusBounds.Width < minimumButtonSize
            || plusBounds.Height < minimumButtonSize
            || plusBounds.Width > maximumButtonSize
            || plusBounds.Height > maximumButtonSize)
        {
            return false;
        }

        var aspectRatio = plusBounds.Width / (double)Math.Max(1, plusBounds.Height);
        if (aspectRatio is < 0.65d or > 1.35d)
        {
            return false;
        }

        var plusCenterX = plusBounds.Left + plusBounds.Width / 2d;
        var plusCenterY = plusBounds.Top + plusBounds.Height / 2d;
        var leftGap = plusCenterX - composerBounds.Left;
        var bottomGap = composerBounds.Bottom - plusCenterY;
        var buttonScale = Math.Max(plusBounds.Width, plusBounds.Height);
        return leftGap >= buttonScale * 0.45d
            && leftGap <= buttonScale * 1.45d
            && bottomGap >= buttonScale * 0.45d
            && bottomGap <= buttonScale * 1.45d
            && plusCenterY >= composerBounds.Top + composerBounds.Height * 0.50d;
    }

    internal static bool IsSameAnchor(ComposerAnchor first, ComposerAnchor second)
    {
        if (first.IsEmpty || second.IsEmpty || first.IsHomePage != second.IsHomePage)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(first.Identity)
            && !string.IsNullOrEmpty(second.Identity)
            && !string.Equals(first.Identity, second.Identity, StringComparison.Ordinal))
        {
            return false;
        }

        return CentersAreClose(first.PlusBounds, second.PlusBounds, 6)
            && BoundsAreClose(first.ComposerBounds, second.ComposerBounds, 12);
    }

    internal static bool BoundsAreClose(Rectangle first, Rectangle second, int tolerance) =>
        Math.Abs(first.Left - second.Left) <= tolerance
        && Math.Abs(first.Top - second.Top) <= tolerance
        && Math.Abs(first.Right - second.Right) <= tolerance
        && Math.Abs(first.Bottom - second.Bottom) <= tolerance;

    internal static bool CentersAreClose(Rectangle first, Rectangle second, int tolerance)
    {
        var firstCenterX = first.Left + first.Width / 2;
        var firstCenterY = first.Top + first.Height / 2;
        var secondCenterX = second.Left + second.Width / 2;
        var secondCenterY = second.Top + second.Height / 2;
        return Math.Abs(firstCenterX - secondCenterX) <= tolerance
            && Math.Abs(firstCenterY - secondCenterY) <= tolerance;
    }
}
