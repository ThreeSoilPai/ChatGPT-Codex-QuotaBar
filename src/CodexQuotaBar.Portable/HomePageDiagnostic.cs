using System.Text.Json;
using System.Windows.Automation;

namespace CodexQuotaBar;

internal static class HomePageDiagnostic
{
    internal static void WriteReport(string outputPath)
    {
        var window = CodexWindowCatalog.FindLargestVisibleWindow();
        if (window is null)
        {
            Save(new { passed = false, reason = "window_not_found" });
            Environment.ExitCode = 1;
            return;
        }

        var root = AutomationElement.FromHandle(window.Handle);
        var condition = new OrCondition(HomePageRecognition.HeadingNames
            .Concat(["添加文件等内容", "添加文件等", "Add files and more"])
            .Select(name => (Condition)new PropertyCondition(AutomationElement.NameProperty, name))
            .ToArray());
        var nodes = new List<object>();
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
        {
            var current = element;
            var parents = new List<object>();
            for (var depth = 0; depth < 7 && current is not null; depth++)
            {
                try
                {
                    var state = current.Current;
                    var heading = current.GetCurrentPropertyValue(AutomationElement.HeadingLevelProperty, true);
                    parents.Add(new
                    {
                        depth,
                        type = state.ControlType.ProgrammaticName,
                        localizedType = state.LocalizedControlType,
                        className = state.ClassName,
                        automationId = state.AutomationId,
                        headingLevel = heading is IConvertible value ? value.ToInt32(null) : -1,
                        offscreen = state.IsOffscreen,
                        bounds = new { state.BoundingRectangle.X, state.BoundingRectangle.Y,
                            state.BoundingRectangle.Width, state.BoundingRectangle.Height }
                    });
                    current = TreeWalker.RawViewWalker.GetParent(current);
                }
                catch { break; }
            }

            nodes.Add(new { name = element.Current.Name, parents });
        }

        using var locator = new UiaComposerLocator();
        using var detector = new ComposerDetector();
        var found = locator.TryGetComposerAnchor(window.Handle, window.Bounds, out var anchor);
        var plusVisible = found && detector.TryVerifyPlusOnScreen(anchor.PlusBounds);
        var workingArea = Screen.FromRectangle(window.Bounds).WorkingArea;
        var placed = found && OverlayPlacement.TryFind(window.Bounds, false, anchor, workingArea,
            candidate => locator.ResolveClearRegionAbove(window.Handle, window.Bounds,
                Rectangle.Intersect(window.Bounds, workingArea), candidate, anchor), out _);
        var passed = found && anchor.IsHomePage && plusVisible && placed;
        Save(new
        {
            passed,
            window = window.Bounds,
            found, isHomePage = anchor.IsHomePage, plusVisible, placed,
            composer = anchor.ComposerBounds, heading = anchor.HomeHeadingBounds,
            gap = anchor.IsHomePage ? anchor.ComposerBounds.Top - anchor.HomeHeadingBounds.Bottom : 0,
            nodes
        });
        Environment.ExitCode = passed ? 0 : 1;

        void Save<T>(T report)
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
