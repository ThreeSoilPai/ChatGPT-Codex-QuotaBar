using System.Drawing.Imaging;

namespace CodexQuotaBar;

internal sealed class ComposerDetector : IDisposable
{
    private const int EdgeDifferenceThreshold = 12;
    private readonly object gate = new();
    private Bitmap? plusCapture;
    private Bitmap? verticalCapture;
    private Bitmap? horizontalCapture;
    private bool disposed;

    internal string LastFailureReason { get; private set; } = string.Empty;

    internal long SurfaceBytes
    {
        get
        {
            lock (gate)
            {
                return SurfaceBytesFor(plusCapture)
                    + SurfaceBytesFor(verticalCapture)
                    + SurfaceBytesFor(horizontalCapture);
            }
        }
    }

    internal bool TryVerifyPlusOnScreen(Rectangle plusButtonBounds)
    {
        if (plusButtonBounds.IsEmpty)
        {
            return false;
        }

        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            var padding = Math.Clamp(
                Math.Max(6, Math.Max(plusButtonBounds.Width, plusButtonBounds.Height) / 4),
                6,
                18);
            var captureBounds = Rectangle.Inflate(plusButtonBounds, padding, padding);
            if (!TryCapture(ref plusCapture, captureBounds))
            {
                return false;
            }

            var expectedCenter = new Point(
                plusButtonBounds.Left + plusButtonBounds.Width / 2 - captureBounds.Left,
                plusButtonBounds.Top + plusButtonBounds.Height / 2 - captureBounds.Top);
            var tolerance = Math.Clamp(
                Math.Max(5, Math.Max(plusButtonBounds.Width, plusButtonBounds.Height) / 5),
                5,
                12);
            return TryFindPlus(plusCapture!, expectedCenter, tolerance, out _);
        }
    }

    internal bool TryVerifyOnScreen(
        Rectangle windowBounds,
        ComposerAnchor expectedAnchor,
        out ComposerAnchor verifiedAnchor)
    {
        verifiedAnchor = default;
        LastFailureReason = string.Empty;
        if (windowBounds.IsEmpty
            || expectedAnchor.IsEmpty
            || !ComposerAnchorGeometry.IsPlausible(
                windowBounds,
                expectedAnchor.ComposerBounds,
                expectedAnchor.PlusBounds,
                expectedAnchor.HomeHeadingBounds))
        {
            LastFailureReason = "invalid_anchor";
            return false;
        }

        lock (gate)
        {
            if (disposed)
            {
                LastFailureReason = "disposed";
                return false;
            }

            var buttonSize = Math.Max(
                expectedAnchor.PlusBounds.Width,
                expectedAnchor.PlusBounds.Height);
            var tolerance = Math.Clamp(Math.Max(8, buttonSize / 4), 8, 18);
            var plusPadding = Math.Clamp(Math.Max(6, buttonSize / 4), 6, 18);
            var plusCaptureBounds = Rectangle.Intersect(
                windowBounds,
                Rectangle.Inflate(expectedAnchor.PlusBounds, plusPadding, plusPadding));
            if (plusCaptureBounds.IsEmpty
                || !TryCapture(ref plusCapture, plusCaptureBounds))
            {
                LastFailureReason = "plus_capture_failed";
                return false;
            }

            var expectedPlusCenter = new Point(
                expectedAnchor.PlusBounds.Left
                    + expectedAnchor.PlusBounds.Width / 2
                    - plusCaptureBounds.Left,
                expectedAnchor.PlusBounds.Top
                    + expectedAnchor.PlusBounds.Height / 2
                    - plusCaptureBounds.Top);
            if (!TryFindPlus(
                    plusCapture!,
                    expectedPlusCenter,
                    Math.Clamp(tolerance, 5, 12),
                    out var relativePlusGlyph))
            {
                LastFailureReason = "plus_not_found";
                return false;
            }

            var glyphCenter = new Point(
                plusCaptureBounds.Left + relativePlusGlyph.Left + relativePlusGlyph.Width / 2,
                plusCaptureBounds.Top + relativePlusGlyph.Top + relativePlusGlyph.Height / 2);
            var expectedCenter = new Point(
                expectedAnchor.PlusBounds.Left + expectedAnchor.PlusBounds.Width / 2,
                expectedAnchor.PlusBounds.Top + expectedAnchor.PlusBounds.Height / 2);
            var centerDelta = new Size(
                glyphCenter.X - expectedCenter.X,
                glyphCenter.Y - expectedCenter.Y);
            if (Math.Abs(centerDelta.Width) > tolerance
                || Math.Abs(centerDelta.Height) > tolerance)
            {
                LastFailureReason = "plus_moved";
                return false;
            }

            var expected = expectedAnchor.ComposerBounds;
            var radius = Math.Clamp((int)Math.Round(expected.Width * 0.025d), 18, 42);
            if (!TryLocateVerticalBorder(
                    windowBounds,
                    expected.Left,
                    expected.Top + radius / 2,
                    expected.Bottom - radius / 2,
                    tolerance,
                    out var left))
            {
                LastFailureReason = "left_border_not_found";
                return false;
            }

            if (!TryLocateVerticalBorder(
                    windowBounds,
                    expected.Right,
                    expected.Top + radius / 2,
                    expected.Bottom - radius / 2,
                    tolerance,
                    out var right))
            {
                LastFailureReason = "right_border_not_found";
                return false;
            }

            if (!TryLocateHorizontalBorder(
                    windowBounds,
                    expected.Top,
                    left + radius / 2,
                    right - radius / 2,
                    tolerance,
                    out var top))
            {
                LastFailureReason = "top_border_not_found";
                return false;
            }

            if (!TryLocateHorizontalBorder(
                    windowBounds,
                    expected.Bottom,
                    left + radius / 2,
                    right - radius / 2,
                    tolerance,
                    out var bottom))
            {
                LastFailureReason = "bottom_border_not_found";
                return false;
            }

            var composerBounds = Rectangle.FromLTRB(left, top, right, bottom);
            var plusBounds = expectedAnchor.PlusBounds;
            plusBounds.Offset(centerDelta.Width, centerDelta.Height);
            if (!ComposerAnchorGeometry.BoundsAreClose(expected, composerBounds, tolerance)
                || !ComposerAnchorGeometry.IsPlausible(windowBounds, composerBounds, plusBounds,
                    expectedAnchor.HomeHeadingBounds))
            {
                LastFailureReason = "verified_bounds_mismatch";
                return false;
            }

            verifiedAnchor = new ComposerAnchor(
                composerBounds,
                plusBounds,
                expectedAnchor.Identity,
                expectedAnchor.HomeHeadingBounds);
            return true;
        }
    }

    internal static bool TryFind(Bitmap source, out Rectangle bounds)
    {
        return TryFind(source, out bounds, out _);
    }

    internal static bool TryFind(
        Bitmap source,
        out Rectangle bounds,
        out Rectangle plusGlyphBounds)
    {
        bounds = Rectangle.Empty;
        plusGlyphBounds = Rectangle.Empty;
        if (source.Width < 320 || source.Height < 64)
        {
            return false;
        }

        Bitmap? converted = null;
        var bitmap = source;
        if (!IsDirectlyReadable(source.PixelFormat))
        {
            converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(converted);
            graphics.DrawImageUnscaled(source, 0, 0);
            bitmap = converted;
        }

        var detectedBounds = Rectangle.Empty;
        var detectedPlusGlyphBounds = Rectangle.Empty;
        try
        {
            var found = WithPixels(bitmap, reader =>
            {
                var candidates = FindPlusCandidates(reader, expectedCenter: null, tolerance: 0);
                var bestScore = double.MinValue;
                foreach (var candidate in candidates)
                {
                    if (!TryFindComposerForPlus(
                            reader,
                            candidate,
                            out var candidateBounds,
                            out var borderScore))
                    {
                        continue;
                    }

                    var totalScore = candidate.Score + borderScore;
                    if (totalScore <= bestScore)
                    {
                        continue;
                    }

                    bestScore = totalScore;
                    detectedBounds = candidateBounds;
                    detectedPlusGlyphBounds = candidate.GlyphBounds;
                }

                return !detectedBounds.IsEmpty;
            });
            bounds = detectedBounds;
            plusGlyphBounds = detectedPlusGlyphBounds;
            return found;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    internal static long EstimateLegacyPeakBytes(int width, int height)
    {
        var captureHeight = Math.Min(Math.Max(0, height), 900);
        return Math.Max(0L, (long)width * captureHeight * 4L * 3L);
    }

    private bool TryLocateVerticalBorder(
        Rectangle windowBounds,
        int expectedX,
        int top,
        int bottom,
        int tolerance,
        out int borderX)
    {
        borderX = 0;
        var captureBounds = Rectangle.Intersect(
            windowBounds,
            Rectangle.FromLTRB(
                expectedX - tolerance,
                top,
                expectedX + tolerance + 1,
                bottom));
        if (captureBounds.Width < 3
            || captureBounds.Height < 12
            || !TryCapture(ref verticalCapture, captureBounds))
        {
            return false;
        }

        var relativeX = -1;
        var bestCount = 0;
        WithPixels(verticalCapture!, reader =>
        {
            for (var x = 1; x < reader.Width; x++)
            {
                var count = CountVerticalEdges(reader, x, 1, reader.Height - 1);
                if (count > bestCount)
                {
                    bestCount = count;
                    relativeX = x;
                }
            }

            return true;
        });

        var minimumCount = Math.Max(8, captureBounds.Height / 8);
        if (relativeX < 0 || bestCount < minimumCount)
        {
            return false;
        }

        borderX = captureBounds.Left + relativeX;
        return Math.Abs(borderX - expectedX) <= tolerance;
    }

    private bool TryLocateHorizontalBorder(
        Rectangle windowBounds,
        int expectedY,
        int left,
        int right,
        int tolerance,
        out int borderY)
    {
        borderY = 0;
        var captureBounds = Rectangle.Intersect(
            windowBounds,
            Rectangle.FromLTRB(
                left,
                expectedY - tolerance,
                right,
                expectedY + tolerance + 1));
        if (captureBounds.Width < 32
            || captureBounds.Height < 3
            || !TryCapture(ref horizontalCapture, captureBounds))
        {
            return false;
        }

        var relativeY = -1;
        var bestCount = 0;
        WithPixels(horizontalCapture!, reader =>
        {
            for (var y = 1; y < reader.Height; y++)
            {
                var count = CountHorizontalEdges(reader, y, 1, reader.Width - 1);
                if (count > bestCount)
                {
                    bestCount = count;
                    relativeY = y;
                }
            }

            return true;
        });

        var minimumCount = Math.Max(12, captureBounds.Width / 5);
        if (relativeY < 0 || bestCount < minimumCount)
        {
            return false;
        }

        borderY = captureBounds.Top + relativeY;
        return Math.Abs(borderY - expectedY) <= tolerance;
    }

    private bool TryCapture(ref Bitmap? surface, Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            return false;
        }

        if (surface is null
            || surface.Width != screenBounds.Width
            || surface.Height != screenBounds.Height)
        {
            surface?.Dispose();
            surface = new Bitmap(
                screenBounds.Width,
                screenBounds.Height,
                PixelFormat.Format32bppArgb);
        }

        try
        {
            using var graphics = Graphics.FromImage(surface);
            graphics.CopyFromScreen(
                screenBounds.Left,
                screenBounds.Top,
                0,
                0,
                screenBounds.Size,
                CopyPixelOperation.SourceCopy);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindPlus(
        Bitmap bitmap,
        Point expectedCenter,
        int tolerance,
        out Rectangle glyphBounds)
    {
        glyphBounds = Rectangle.Empty;
        var detectedGlyphBounds = Rectangle.Empty;
        var found = WithPixels(bitmap, reader =>
        {
            var candidates = FindPlusCandidates(reader, expectedCenter, tolerance);
            if (candidates.Count == 0)
            {
                return false;
            }

            var best = candidates
                .OrderBy(candidate => DistanceSquared(candidate.Center, expectedCenter))
                .ThenByDescending(candidate => candidate.Score)
                .First();
            detectedGlyphBounds = best.GlyphBounds;
            return true;
        });
        glyphBounds = detectedGlyphBounds;
        return found;
    }

    private static List<PlusCandidate> FindPlusCandidates(
        PixelReader reader,
        Point? expectedCenter,
        int tolerance)
    {
        var candidates = new List<PlusCandidate>(8);
        var xStart = 14;
        var xEnd = reader.Width - 14;
        var yStart = reader.Height >= 220
            ? Math.Max(14, reader.Height - 280)
            : Math.Max(14, (int)Math.Round(reader.Height * 0.45d));
        var yEnd = reader.Height - 14;
        if (expectedCenter is { } expected)
        {
            xStart = Math.Max(14, expected.X - tolerance);
            xEnd = Math.Min(reader.Width - 14, expected.X + tolerance + 1);
            yStart = Math.Max(14, expected.Y - tolerance);
            yEnd = Math.Min(reader.Height - 14, expected.Y + tolerance + 1);
        }

        for (var y = yStart; y < yEnd; y++)
        {
            for (var x = xStart; x < xEnd; x++)
            {
                if (!TryScorePlus(reader, x, y, out var score, out var radius, out var darkInk))
                {
                    continue;
                }

                var center = new Point(x, y);
                var nearbyIndex = -1;
                for (var index = 0; index < candidates.Count; index++)
                {
                    if (DistanceSquared(candidates[index].Center, center) <= 16)
                    {
                        nearbyIndex = index;
                        break;
                    }
                }
                var glyphBounds = FindGlyphBounds(reader, center, radius, darkInk);
                var candidate = new PlusCandidate(center, glyphBounds, score);
                if (nearbyIndex >= 0)
                {
                    if (candidate.Score > candidates[nearbyIndex].Score)
                    {
                        candidates[nearbyIndex] = candidate;
                    }
                    continue;
                }

                candidates.Add(candidate);
                candidates.Sort(static (left, right) => right.Score.CompareTo(left.Score));
                if (candidates.Count > 24)
                {
                    candidates.RemoveAt(candidates.Count - 1);
                }
            }
        }

        return candidates;
    }

    private static bool TryScorePlus(
        PixelReader reader,
        int centerX,
        int centerY,
        out double score,
        out int selectedRadius,
        out bool darkInk)
    {
        score = double.MinValue;
        selectedRadius = 0;
        darkInk = true;
        if (centerX - 11 < 0
            || centerY - 11 < 0
            || centerX + 11 >= reader.Width
            || centerY + 11 >= reader.Height)
        {
            return false;
        }

        var prefilterBackground = EstimateBackground(reader, centerX, centerY, 11);
        if (Math.Abs(reader.Luminance(centerX, centerY) - prefilterBackground) < 32)
        {
            return false;
        }

        ReadOnlySpan<int> radii = [7, 9, 11, 13];
        foreach (var radius in radii)
        {
            if (centerX - radius - 2 < 0
                || centerY - radius - 2 < 0
                || centerX + radius + 2 >= reader.Width
                || centerY + radius + 2 >= reader.Height)
            {
                continue;
            }

            var background = EstimateBackground(reader, centerX, centerY, radius + 2);
            var centerLuminance = reader.Luminance(centerX, centerY);
            var isDark = centerLuminance < background;
            if (Math.Abs(centerLuminance - background) < 32)
            {
                continue;
            }

            var horizontalHits = 0;
            var verticalHits = 0;
            for (var offset = -radius; offset <= radius; offset++)
            {
                var horizontalHit = false;
                var verticalHit = false;
                for (var delta = -1; delta <= 1; delta++)
                {
                    horizontalHit |= IsInk(
                        reader.Luminance(centerX + offset, centerY + delta),
                        background,
                        isDark);
                    verticalHit |= IsInk(
                        reader.Luminance(centerX + delta, centerY + offset),
                        background,
                        isDark);
                }

                horizontalHits += horizontalHit ? 1 : 0;
                verticalHits += verticalHit ? 1 : 0;
            }

            var minimumLineHits = (int)Math.Ceiling((radius * 2 + 1) * 0.72d);
            if (horizontalHits < minimumLineHits || verticalHits < minimumLineHits)
            {
                continue;
            }

            var cornerHits = 0;
            var totalInk = 0;
            for (var y = -radius; y <= radius; y++)
            {
                for (var x = -radius; x <= radius; x++)
                {
                    if (!IsInk(
                            reader.Luminance(centerX + x, centerY + y),
                            background,
                            isDark))
                    {
                        continue;
                    }

                    totalInk++;
                    if (Math.Abs(x) >= radius / 2 && Math.Abs(y) >= radius / 2)
                    {
                        cornerHits++;
                    }
                }
            }

            var area = (radius * 2 + 1) * (radius * 2 + 1);
            if (cornerHits > Math.Max(8, radius)
                || totalInk < minimumLineHits * 2 - 8
                || totalInk > area * 0.35d)
            {
                continue;
            }

            var candidateScore = horizontalHits
                + verticalHits
                - cornerHits * 0.5d
                + Math.Abs(centerLuminance - background) / 32d;
            if (candidateScore > score)
            {
                score = candidateScore;
                selectedRadius = radius;
                darkInk = isDark;
            }
        }

        return selectedRadius > 0;
    }

    private static Rectangle FindGlyphBounds(
        PixelReader reader,
        Point center,
        int radius,
        bool darkInk)
    {
        var background = EstimateBackground(reader, center.X, center.Y, radius + 2);
        var left = center.X;
        var top = center.Y;
        var right = center.X;
        var bottom = center.Y;
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                if (!IsInk(reader.Luminance(x, y), background, darkInk))
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static bool TryFindComposerForPlus(
        PixelReader reader,
        PlusCandidate plus,
        out Rectangle bounds,
        out double score)
    {
        bounds = Rectangle.Empty;
        score = double.MinValue;
        var glyphSize = Math.Max(plus.GlyphBounds.Width, plus.GlyphBounds.Height);
        var scale = Math.Clamp(glyphSize / 16d, 0.8d, 2d);
        var minimumLeftGap = (int)Math.Round(20d * scale);
        var maximumLeftGap = (int)Math.Round(48d * scale);
        var yStart = Math.Max(1, plus.Center.Y - (int)Math.Round(650d * scale));
        var yEnd = Math.Min(reader.Height - 1, plus.Center.Y + (int)Math.Round(60d * scale));

        var bestLeft = -1;
        var bestLeftCount = 0;
        var leftStart = Math.Max(1, plus.Center.X - maximumLeftGap);
        var leftEnd = Math.Min(reader.Width - 2, plus.Center.X - minimumLeftGap);
        for (var x = leftStart; x <= leftEnd; x++)
        {
            var count = CountVerticalEdges(reader, x, yStart, yEnd);
            if (count > bestLeftCount)
            {
                bestLeftCount = count;
                bestLeft = x;
            }
        }

        var minimumEdgeCount = Math.Max(8, (yEnd - yStart) / 14);
        if (bestLeft < 0 || bestLeftCount < minimumEdgeCount)
        {
            return false;
        }

        var minimumWidth = Math.Max(280, (int)Math.Round(280d * scale));
        var bestRight = -1;
        var bestRunStart = -1;
        var bestRunEnd = -1;
        var bestBorderScore = double.MinValue;
        for (var right = bestLeft + minimumWidth; right < reader.Width - 1; right++)
        {
            var rightCount = CountVerticalEdges(reader, right, yStart, yEnd);
            if (rightCount < minimumEdgeCount)
            {
                continue;
            }

            var similarity = Math.Min(bestLeftCount, rightCount)
                / (double)Math.Max(bestLeftCount, rightCount);
            if (similarity < 0.50d
                || !TryGetLongestSharedRun(
                    reader,
                    bestLeft,
                    right,
                    yStart,
                    yEnd,
                    out var runStart,
                    out var runEnd))
            {
                continue;
            }

            var runLength = runEnd - runStart + 1;
            var minimumRun = Math.Max(12, (int)Math.Round(glyphSize * 0.75d));
            var maximumRun = Math.Min(reader.Height, (int)Math.Round(650d * scale));
            if (runLength < minimumRun || runLength > maximumRun)
            {
                continue;
            }

            var candidateScore = runLength * 3d
                + Math.Min(bestLeftCount, rightCount)
                + similarity * 50d;
            if (candidateScore <= bestBorderScore)
            {
                continue;
            }

            bestBorderScore = candidateScore;
            bestRight = right;
            bestRunStart = runStart;
            bestRunEnd = runEnd;
        }

        if (bestRight < 0)
        {
            return false;
        }

        var radius = Math.Clamp(
            (int)Math.Round((bestRight - bestLeft) * 0.025d),
            Math.Max(14, (int)Math.Round(18d * scale)),
            Math.Max(28, (int)Math.Round(42d * scale)));
        var top = Math.Max(0, bestRunStart - radius);
        var bottom = Math.Min(reader.Height, bestRunEnd + radius + 1);
        var leftGap = plus.Center.X - bestLeft;
        var bottomGap = bottom - plus.Center.Y;
        if (leftGap < 16d * scale
            || leftGap > 60d * scale
            || bottomGap < 16d * scale
            || bottomGap > 64d * scale
            || bottom < reader.Height - Math.Max(24, (int)(reader.Height * 0.10d)))
        {
            return false;
        }

        bounds = Rectangle.FromLTRB(bestLeft, top, bestRight + 1, bottom);
        score = bestBorderScore;
        return bounds.Width >= 280 && bounds.Height >= 48;
    }

    private static int CountVerticalEdges(PixelReader reader, int x, int yStart, int yEnd)
    {
        var count = 0;
        for (var y = Math.Max(0, yStart); y < Math.Min(reader.Height, yEnd); y++)
        {
            if (reader.ColorDifference(x, y, x - 1, y) >= EdgeDifferenceThreshold)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountHorizontalEdges(PixelReader reader, int y, int xStart, int xEnd)
    {
        var count = 0;
        for (var x = Math.Max(0, xStart); x < Math.Min(reader.Width, xEnd); x++)
        {
            if (reader.ColorDifference(x, y, x, y - 1) >= EdgeDifferenceThreshold)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryGetLongestSharedRun(
        PixelReader reader,
        int left,
        int right,
        int yStart,
        int yEnd,
        out int longestStart,
        out int longestEnd)
    {
        longestStart = -1;
        longestEnd = -1;
        var currentStart = -1;
        var lastGood = -1;
        for (var y = yStart; y < yEnd; y++)
        {
            var good = reader.ColorDifference(left, y, left - 1, y) >= EdgeDifferenceThreshold
                && reader.ColorDifference(right, y, right - 1, y) >= EdgeDifferenceThreshold;
            if (!good)
            {
                continue;
            }

            if (currentStart < 0 || y - lastGood > 3)
            {
                SaveLongestRun(currentStart, lastGood, ref longestStart, ref longestEnd);
                currentStart = y;
            }

            lastGood = y;
        }

        SaveLongestRun(currentStart, lastGood, ref longestStart, ref longestEnd);
        return longestStart >= 0;
    }

    private static void SaveLongestRun(
        int currentStart,
        int currentEnd,
        ref int longestStart,
        ref int longestEnd)
    {
        if (currentStart >= 0 && currentEnd - currentStart > longestEnd - longestStart)
        {
            longestStart = currentStart;
            longestEnd = currentEnd;
        }
    }

    private static int EstimateBackground(PixelReader reader, int x, int y, int radius)
    {
        var sum = reader.Luminance(x - radius, y - radius)
            + reader.Luminance(x, y - radius)
            + reader.Luminance(x + radius, y - radius)
            + reader.Luminance(x - radius, y)
            + reader.Luminance(x + radius, y)
            + reader.Luminance(x - radius, y + radius)
            + reader.Luminance(x, y + radius)
            + reader.Luminance(x + radius, y + radius);
        return sum / 8;
    }

    private static bool IsInk(int luminance, int background, bool darkInk) =>
        darkInk
            ? luminance <= background - 28
            : luminance >= background + 28;

    private static int DistanceSquared(Point first, Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return x * x + y * y;
    }

    private static bool IsDirectlyReadable(PixelFormat pixelFormat) =>
        pixelFormat is PixelFormat.Format32bppArgb
            or PixelFormat.Format32bppPArgb
            or PixelFormat.Format32bppRgb;

    private static unsafe bool WithPixels(Bitmap bitmap, Func<PixelReader, bool> action)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            bitmap.PixelFormat);
        try
        {
            return action(new PixelReader(
                (byte*)bitmapData.Scan0,
                bitmapData.Stride,
                bitmap.Width,
                bitmap.Height));
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static long SurfaceBytesFor(Bitmap? bitmap) =>
        bitmap is null ? 0L : (long)bitmap.Width * bitmap.Height * 4L;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            plusCapture?.Dispose();
            verticalCapture?.Dispose();
            horizontalCapture?.Dispose();
            plusCapture = null;
            verticalCapture = null;
            horizontalCapture = null;
        }
    }

    private readonly record struct PlusCandidate(
        Point Center,
        Rectangle GlyphBounds,
        double Score);

    private readonly unsafe struct PixelReader(
        byte* scan0,
        int stride,
        int width,
        int height)
    {
        internal int Width { get; } = width;
        internal int Height { get; } = height;

        internal int Luminance(int x, int y)
        {
            var pixel = Pixel(x, y);
            return (pixel[2] * 77 + pixel[1] * 150 + pixel[0] * 29) >> 8;
        }

        internal int ColorDifference(int x1, int y1, int x2, int y2)
        {
            var first = Pixel(x1, y1);
            var second = Pixel(x2, y2);
            return Math.Abs(first[0] - second[0])
                + Math.Abs(first[1] - second[1])
                + Math.Abs(first[2] - second[2]);
        }

        private byte* Pixel(int x, int y)
        {
            var row = stride >= 0
                ? scan0 + y * stride
                : scan0 + (Height - 1 - y) * -stride;
            return row + x * 4;
        }
    }
}
