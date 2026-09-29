using System.Windows.Automation;
using WindowsRect = System.Windows.Rect;

namespace CodexQuotaBar;

internal sealed class PetWindowLocator
{
    private readonly Dictionary<IntPtr, AutomationElement> cachedPets = [];

    internal bool TryGetPetBounds(
        IntPtr windowHandle,
        Rectangle windowBounds,
        out Rectangle petBounds)
    {
        petBounds = Rectangle.Empty;
        if (windowHandle == IntPtr.Zero || windowBounds.IsEmpty)
        {
            return false;
        }

        if (cachedPets.TryGetValue(windowHandle, out var cachedPet)
            && TryReadElement(cachedPet, out var cachedNode)
            && TrySelectPetBounds(windowBounds, [cachedNode], out petBounds))
        {
            return true;
        }

        cachedPets.Remove(windowHandle);
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root is null)
            {
                return false;
            }

            var request = new CacheRequest { TreeScope = TreeScope.Element };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.IsOffscreenProperty);

            AutomationElementCollection elements;
            using (request.Activate())
            {
                elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }

            AutomationElement? bestElement = null;
            var bestBounds = Rectangle.Empty;
            var bestScore = int.MinValue;
            foreach (AutomationElement element in elements)
            {
                if (!TryReadCachedElement(element, out var node)
                    || !TryScorePetNode(windowBounds, node, out var score)
                    || score <= bestScore)
                {
                    continue;
                }

                bestElement = element;
                bestBounds = node.Bounds;
                bestScore = score;
            }

            if (bestElement is null)
            {
                return false;
            }

            cachedPets[windowHandle] = bestElement;
            petBounds = bestBounds;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySelectPetBounds(
        Rectangle windowBounds,
        IReadOnlyList<PetUiaNodeDescriptor> nodes,
        out Rectangle petBounds)
    {
        petBounds = Rectangle.Empty;
        var bestScore = int.MinValue;
        foreach (var node in nodes)
        {
            if (!TryScorePetNode(windowBounds, node, out var score) || score <= bestScore)
            {
                continue;
            }

            petBounds = node.Bounds;
            bestScore = score;
        }

        return !petBounds.IsEmpty;
    }

    internal static bool ShouldUseNativeBoundsAsLegacyFallback(Rectangle windowBounds) =>
        windowBounds.Width is >= 80 and <= 800
        && windowBounds.Height is >= 80 and <= 800;

    private static bool TryScorePetNode(
        Rectangle windowBounds,
        PetUiaNodeDescriptor node,
        out int score)
    {
        score = int.MinValue;
        if (node.IsOffscreen
            || node.Bounds.IsEmpty
            || node.Bounds.Width < 24
            || node.Bounds.Height < 24
            || node.Bounds.Width > 640
            || node.Bounds.Height > 640
            || !windowBounds.IntersectsWith(node.Bounds))
        {
            return false;
        }

        var classLooksLikeCodexPet = node.ClassName.Contains(
            "codex-avatar",
            StringComparison.OrdinalIgnoreCase);
        var semanticIdentity = $"{node.ClassName} {node.AutomationId}";
        var classLooksLikePet = semanticIdentity.Contains("mascot", StringComparison.OrdinalIgnoreCase)
            || semanticIdentity.Contains("avatar", StringComparison.OrdinalIgnoreCase)
            || semanticIdentity.Contains("pet-", StringComparison.OrdinalIgnoreCase)
            || semanticIdentity.Contains("pet_", StringComparison.OrdinalIgnoreCase)
            || semanticIdentity.Contains("-pet", StringComparison.OrdinalIgnoreCase)
            || semanticIdentity.Contains("_pet", StringComparison.OrdinalIgnoreCase)
            || semanticIdentity.Contains("pets", StringComparison.OrdinalIgnoreCase);
        var nameLooksLikePet = node.Name.Contains("宠物", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains(" pet", StringComparison.OrdinalIgnoreCase)
            || node.Name.EndsWith("pet", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("mascot", StringComparison.OrdinalIgnoreCase);
        if (!classLooksLikeCodexPet && !classLooksLikePet && !nameLooksLikePet)
        {
            return false;
        }

        score = classLooksLikeCodexPet ? 1200 : classLooksLikePet ? 750 : 500;
        if (node.ControlTypeId == ControlType.Image.Id)
        {
            score += 200;
        }

        if (node.ClassName.Contains("button", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return true;
    }

    private static bool TryReadCachedElement(
        AutomationElement element,
        out PetUiaNodeDescriptor node)
    {
        node = default;
        try
        {
            node = new PetUiaNodeDescriptor(
                ToRectangle(element.Cached.BoundingRectangle),
                element.Cached.Name ?? string.Empty,
                element.Cached.ClassName ?? string.Empty,
                element.Cached.ControlType.Id,
                element.Cached.IsOffscreen,
                element.Cached.AutomationId ?? string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadElement(
        AutomationElement element,
        out PetUiaNodeDescriptor node)
    {
        node = default;
        try
        {
            var current = element.Current;
            node = new PetUiaNodeDescriptor(
                ToRectangle(current.BoundingRectangle),
                current.Name ?? string.Empty,
                current.ClassName ?? string.Empty,
                current.ControlType.Id,
                current.IsOffscreen,
                current.AutomationId ?? string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Rectangle ToRectangle(WindowsRect value)
    {
        if (value.IsEmpty
            || double.IsNaN(value.X)
            || double.IsNaN(value.Y)
            || double.IsNaN(value.Width)
            || double.IsNaN(value.Height)
            || double.IsInfinity(value.X)
            || double.IsInfinity(value.Y)
            || double.IsInfinity(value.Width)
            || double.IsInfinity(value.Height))
        {
            return Rectangle.Empty;
        }

        return Rectangle.Round(new RectangleF(
            (float)value.X,
            (float)value.Y,
            (float)value.Width,
            (float)value.Height));
    }
}

internal readonly record struct PetUiaNodeDescriptor(
    Rectangle Bounds,
    string Name,
    string ClassName,
    int ControlTypeId,
    bool IsOffscreen,
    string AutomationId = "");
