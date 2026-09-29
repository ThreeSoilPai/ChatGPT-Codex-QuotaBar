using System.Windows.Automation;

namespace CodexQuotaBar;

internal static class HomePageRecognition
{
    internal const int Level1 = 80051;
    internal static readonly string[] HeadingNames =
    [
        "我们要做什么？", "我们要做什么?",
        "我們要做什麼？", "我們要做什麼?",
        "What should we do?", "What should we work on?"
    ];

    internal static bool IsHeading(UiaNodeDescriptor node) =>
        !node.IsOffscreen
        && (node.ControlTypeId == ControlType.Text.Id || node.ControlTypeId == ControlType.Group.Id)
        && node.InHomeHero
        && !UiaCollisionRules.IsVisuallyHidden(node.ClassName)
        && HeadingNames.Contains(node.Name.Trim(), StringComparer.OrdinalIgnoreCase);

    internal static bool IsHeadingPositionValid(
        Rectangle windowBounds,
        Rectangle composerBounds,
        Rectangle headingBounds)
    {
        if (windowBounds.IsEmpty || composerBounds.IsEmpty || headingBounds.IsEmpty
            || !ComposerGeometry.ContainsWithTolerance(windowBounds, composerBounds, 4)
            || !ComposerGeometry.ContainsWithTolerance(windowBounds, headingBounds, 0)
            || headingBounds.Width < 80
            || headingBounds.Width > composerBounds.Width + 8
            || headingBounds.Height < 20
            || headingBounds.Height > Math.Max(100, windowBounds.Height * 0.12d))
        {
            return false;
        }

        var gap = composerBounds.Top - headingBounds.Bottom;
        var centerDifference = Math.Abs(
            headingBounds.Left + headingBounds.Width / 2d
            - composerBounds.Left - composerBounds.Width / 2d);
        return gap >= 0
            && gap <= Math.Max(180, windowBounds.Height * 0.18d)
            && centerDifference <= Math.Max(24, composerBounds.Width * 0.08d);
    }
}
