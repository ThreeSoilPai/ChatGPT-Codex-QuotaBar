using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace CodexQuotaBar;

internal static class DiagnosticRunner
{
    public static void DetectComposerInImage(string inputPath, string outputPath)
    {
        using var image = new Bitmap(inputPath);
        var found = ComposerDetector.TryFind(image, out var bounds, out var plusBounds);
        var result = new
        {
            found,
            x = found ? bounds.X : 0,
            y = found ? bounds.Y : 0,
            width = found ? bounds.Width : 0,
            height = found ? bounds.Height : 0,
            plus = found
                ? new
                {
                    plusBounds.X,
                    plusBounds.Y,
                    plusBounds.Width,
                    plusBounds.Height
                }
                : null
        };
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (!found)
        {
            Environment.ExitCode = 1;
        }
    }

    public static void WriteCodexWindowsReport(string outputPath)
    {
        var rows = CodexWindowCatalog.ListVisibleWindows().Select(window => new
        {
            handle = window.Handle.ToInt64(),
            window.ProcessId,
            window.Title,
            window.ClassName,
            auxiliary = CodexWindowCatalog.IsAuxiliaryWindow(window.Handle),
            x = window.Bounds.X,
            y = window.Bounds.Y,
            width = window.Bounds.Width,
            height = window.Bounds.Height,
            area = window.Area
        });
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void WriteCompatibilityReport(string outputPath)
    {
        var processes = CodexWindowCatalog.ListDetectedDesktopProcesses(forceRefresh: true)
            .Select(process => new
            {
                process.ProcessId,
                process.ProcessName,
                process.ExecutablePath,
                process.ProductName,
                process.FileDescription,
                process.FileVersion
            })
            .ToArray();
        var appServers = CodexQuotaClient.FindCodexExecutableCandidates()
            .Select((path, index) =>
            {
                string? version = null;
                try
                {
                    version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                }
                catch
                {
                }

                return new { priority = index + 1, path, version };
            })
            .ToArray();

        WriteJson(outputPath, new
        {
            generatedAt = DateTimeOffset.Now,
            adaptiveStrategies = new[]
            {
                "desktop_process_name_path_and_version_metadata",
                "app_server_candidate_rotation_with_timeouts",
                "canonical_then_alias_and_recursive_quota_schema",
                "shortest_positive_quota_window_only",
                "semantic_plus_anchor_with_restricted_screenshot_verification",
                "semantic_pet_body_detection_with_legacy_fallback"
            },
            desktopProcesses = processes,
            appServerCandidates = appServers,
            visibleCodexWindowCount = CodexWindowCatalog.ListVisibleWindows().Count,
            preferredAppServer = appServers.FirstOrDefault()
        });
    }

    public static void WriteStartupReport(string outputPath)
    {
        var status = AutoStartManager.GetStatus();
        var log = WatcherLog.LogPath;
        WriteJson(outputPath, new
        {
            healthy = status.IsHealthy,
            status.Executable,
            registry = new
            {
                matches = status.RunEntryMatches,
                expected = status.ExpectedRunCommand,
                actual = status.ActualRunCommand
            },
            scheduledTask = new
            {
                name = AutoStartManager.ScheduledTaskName,
                registered = status.ScheduledTaskRegistered,
                matches = status.ScheduledTaskMatches,
                recoveryTriggerPresent = status.RecoveryTriggerPresent,
                recoveryInterval = AutoStartManager.RecoveryIntervalXml,
                queryExitCode = status.ScheduledTaskQueryExitCode,
                error = status.ScheduledTaskError
            },
            watcherLog = new
            {
                path = log,
                exists = File.Exists(log),
                size = File.Exists(log) ? new FileInfo(log).Length : 0
            }
        });

        if (!status.IsHealthy)
        {
            Environment.ExitCode = 1;
        }
    }

    public static void WriteTrackerReport(string outputPath)
    {
        var tracker = new CodexWindowTracker();
        var active = tracker.TryGetActiveCodexWindow(out var state);
        var result = new
        {
            active,
            handle = active ? state.Handle.ToInt64() : 0,
            maximized = active && state.IsMaximized,
            followingAuxiliary = active && state.IsFollowingAuxiliaryWindow,
            x = active ? state.Bounds.X : 0,
            y = active ? state.Bounds.Y : 0,
            width = active ? state.Bounds.Width : 0,
            height = active ? state.Bounds.Height : 0,
            auxiliaries = state.AuxiliaryBounds.Select(bounds => new
            {
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height
            })
        };
        WriteJson(outputPath, result);
    }

    public static void WritePetsReport(string outputPath)
    {
        var locator = new PetWindowLocator();
        var rows = CodexWindowCatalog.ListVisibleWindows()
            .Where(window => CodexWindowCatalog.IsAuxiliaryWindow(window.Handle))
            .Select(window =>
            {
                var uiaFound = locator.TryGetPetBounds(
                    window.Handle,
                    window.Bounds,
                    out var petBounds);
                var usedLegacyFallback = !uiaFound
                    && PetWindowLocator.ShouldUseNativeBoundsAsLegacyFallback(window.Bounds);
                var effectiveBounds = uiaFound || usedLegacyFallback
                    ? uiaFound ? petBounds : window.Bounds
                    : Rectangle.Empty;
                return new
                {
                    handle = window.Handle.ToInt64(),
                    window.ProcessId,
                    window.Title,
                    window.ClassName,
                    host = new
                    {
                        window.Bounds.X,
                        window.Bounds.Y,
                        window.Bounds.Width,
                        window.Bounds.Height
                    },
                    uiaFound,
                    usedLegacyFallback,
                    pet = effectiveBounds.IsEmpty
                        ? null
                        : new
                        {
                            effectiveBounds.X,
                            effectiveBounds.Y,
                            effectiveBounds.Width,
                            effectiveBounds.Height
                        }
                };
            })
            .ToArray();
        WriteJson(outputPath, new
        {
            found = rows.Any(row => row.uiaFound || row.usedLegacyFallback),
            windows = rows
        });
    }

    public static void WriteUiaReport(string outputPath)
    {
        var target = CodexWindowCatalog.FindLargestVisibleWindow();
        if (target is null)
        {
            WriteJson(outputPath, new { found = false, reason = "codex_window_not_found" });
            Environment.ExitCode = 1;
            return;
        }

        using var locator = new UiaComposerLocator();
        var stopwatch = Stopwatch.StartNew();
        var found = locator.TryGetComposerAnchor(target.Handle, target.Bounds, out var anchor);
        var bounds = found ? anchor.ComposerBounds : Rectangle.Empty;
        stopwatch.Stop();
        bool? aboveIsClear = null;
        UiaNodeDescriptor? blocker = null;
        if (found)
        {
            var workingArea = Screen.FromRectangle(target.Bounds).WorkingArea;
            OverlayPlacement.TryFind(
                target.Bounds,
                false,
                anchor,
                workingArea,
                candidate =>
                {
                    var clearBounds = locator.ResolveClearRegionAbove(
                        target.Handle,
                        target.Bounds,
                        Rectangle.Intersect(target.Bounds, workingArea),
                        candidate,
                        anchor);
                    aboveIsClear = clearBounds is not null;
                    blocker = clearBounds is null
                        ? locator.GetLastCollisionBlocker(target.Bounds, candidate)
                        : null;
                    return clearBounds;
                },
                out _);
        }

        WriteJson(outputPath, new
        {
            source = "UI Automation Raw View",
            found,
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            windowHandle = target.Handle.ToInt64(),
            window = new
            {
                target.Bounds.X,
                target.Bounds.Y,
                target.Bounds.Width,
                target.Bounds.Height
            },
            composer = found ? new { bounds.X, bounds.Y, bounds.Width, bounds.Height } : null,
            plus = found
                ? new
                {
                    anchor.PlusBounds.X,
                    anchor.PlusBounds.Y,
                    anchor.PlusBounds.Width,
                    anchor.PlusBounds.Height
                }
                : null,
            isHomePage = found && anchor.IsHomePage,
            homeHeadingBounds = found && anchor.IsHomePage
                ? new
                {
                    anchor.HomeHeadingBounds.X,
                    anchor.HomeHeadingBounds.Y,
                    anchor.HomeHeadingBounds.Width,
                    anchor.HomeHeadingBounds.Height
                }
                : null,
            aboveIsClear,
            blocker = blocker is { } value
                ? new
                {
                    value.ControlTypeId,
                    value.ClassName,
                    value.Bounds.X,
                    value.Bounds.Y,
                    value.Bounds.Width,
                    value.Bounds.Height
                }
                : null
        });

        if (!found)
        {
            Environment.ExitCode = 1;
        }
    }

    public static void BenchmarkUiaLocator(string outputPath, int iterations)
    {
        iterations = Math.Clamp(iterations, 1, 1000);
        var target = CodexWindowCatalog.FindLargestVisibleWindow();
        if (target is null)
        {
            WriteJson(outputPath, new { found = false, reason = "codex_window_not_found" });
            Environment.ExitCode = 1;
            return;
        }

        using var locator = new UiaComposerLocator();
        var discoveryStopwatch = Stopwatch.StartNew();
        var found = locator.TryGetComposer(target.Handle, target.Bounds, out var bounds);
        discoveryStopwatch.Stop();
        if (!found)
        {
            WriteJson(outputPath, new
            {
                found = false,
                discoveryMilliseconds = discoveryStopwatch.Elapsed.TotalMilliseconds
            });
            Environment.ExitCode = 1;
            return;
        }

        var samples = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            found = locator.TryGetComposer(target.Handle, target.Bounds, out bounds);
            stopwatch.Stop();
            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
            if (!found)
            {
                break;
            }
        }

        Array.Sort(samples);
        var p95Index = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95d) - 1, 0, samples.Length - 1);
        var rediscoveryIterations = Math.Min(20, iterations);
        var rediscoverySamples = new double[rediscoveryIterations];
        for (var index = 0; index < rediscoveryIterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            found = locator.TryRediscoverComposer(target.Handle, target.Bounds, out bounds);
            stopwatch.Stop();
            rediscoverySamples[index] = stopwatch.Elapsed.TotalMilliseconds;
            if (!found)
            {
                break;
            }
        }

        Array.Sort(rediscoverySamples);
        var rediscoveryP95Index = Math.Clamp(
            (int)Math.Ceiling(rediscoverySamples.Length * 0.95d) - 1,
            0,
            rediscoverySamples.Length - 1);
        WriteJson(outputPath, new
        {
            found,
            iterations,
            discoveryMilliseconds = discoveryStopwatch.Elapsed.TotalMilliseconds,
            averageCachedMilliseconds = samples.Average(),
            medianCachedMilliseconds = samples[samples.Length / 2],
            p95CachedMilliseconds = samples[p95Index],
            maximumCachedMilliseconds = samples[^1],
            rediscoveryIterations,
            averageRediscoveryMilliseconds = rediscoverySamples.Average(),
            p95RediscoveryMilliseconds = rediscoverySamples[rediscoveryP95Index],
            maximumRediscoveryMilliseconds = rediscoverySamples[^1],
            composer = new { bounds.X, bounds.Y, bounds.Width, bounds.Height }
        });

        if (!found)
        {
            Environment.ExitCode = 1;
        }
    }

    public static void WriteScreenshotFallbackReport(string outputPath)
    {
        var target = CodexWindowCatalog.FindLargestVisibleWindow();
        if (target is null)
        {
            WriteJson(outputPath, new { found = false, reason = "codex_window_not_found" });
            Environment.ExitCode = 1;
            return;
        }

        using var locator = new UiaComposerLocator();
        using var detector = new ComposerDetector();
        var tracker = new CodexWindowTracker();
        var windowIsMaximized = tracker.TryGetActiveCodexWindow(out var windowState)
            && windowState.Handle == target.Handle
            && windowState.IsMaximized;
        var anchorFound = locator.TryGetComposerAnchor(target.Handle, target.Bounds, out var anchor);
        var verifiedAnchor = default(ComposerAnchor);
        var screenshotFound = anchorFound
            && detector.TryVerifyOnScreen(target.Bounds, anchor, out verifiedAnchor);
        var composerBounds = screenshotFound
            ? verifiedAnchor.ComposerBounds
            : Rectangle.Empty;
        var placementFound = false;
        var overlayBounds = Rectangle.Empty;
        if (screenshotFound)
        {
            var workingArea = Screen.FromRectangle(target.Bounds).WorkingArea;
            placementFound = OverlayPlacement.TryFind(
                target.Bounds,
                windowIsMaximized,
                verifiedAnchor,
                workingArea,
                candidate => locator.ResolveClearRegionAbove(
                    target.Handle,
                    target.Bounds,
                    Rectangle.Intersect(target.Bounds, workingArea),
                    candidate,
                    verifiedAnchor),
                out overlayBounds);
        }

        var legacyPeakBytes = ComposerDetector.EstimateLegacyPeakBytes(
            target.Bounds.Width,
            target.Bounds.Height);
        var memoryReductionPercent = legacyPeakBytes > 0
            ? (1d - detector.SurfaceBytes / (double)legacyPeakBytes) * 100d
            : 0d;
        WriteJson(outputPath, new
        {
            source = "restricted reusable screenshot verification",
            anchorFound,
            screenshotFound,
            failureReason = screenshotFound ? null : detector.LastFailureReason,
            surfaceBytes = detector.SurfaceBytes,
            legacyPeakBytes,
            memoryReductionPercent,
            windowIsMaximized,
            isHomePage = screenshotFound && verifiedAnchor.IsHomePage,
            composer = screenshotFound
                ? new { composerBounds.X, composerBounds.Y, composerBounds.Width, composerBounds.Height }
                : null,
            placementFound,
            overlay = placementFound
                ? new { overlayBounds.X, overlayBounds.Y, overlayBounds.Width, overlayBounds.Height }
                : null
        });

        if (!screenshotFound)
        {
            Environment.ExitCode = 1;
        }
    }

    public static void BenchmarkComposerDetector(string inputPath, string outputPath, int iterations)
    {
        iterations = Math.Clamp(iterations, 1, 100);
        using var image = new Bitmap(inputPath);
        ComposerDetector.TryFind(image, out _);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var samples = new double[iterations];
        var found = false;
        var bounds = Rectangle.Empty;

        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            found = ComposerDetector.TryFind(image, out bounds);
            stopwatch.Stop();
            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Array.Sort(samples);
        var result = new
        {
            iterations,
            found,
            bounds = found ? new { bounds.X, bounds.Y, bounds.Width, bounds.Height } : null,
            allocatedBytesPerIteration = allocatedBytes / (double)iterations,
            sourceSurfaceBytes = (long)image.Width * image.Height * 4L,
            legacyPeakBytes = ComposerDetector.EstimateLegacyPeakBytes(image.Width, image.Height),
            averageMilliseconds = samples.Average(),
            medianMilliseconds = samples[samples.Length / 2],
            maximumMilliseconds = samples[^1]
        };
        WriteJson(outputPath, result);
    }

    public static void WriteOverlayPlacementReport(string outputPath)
    {
        var workingArea = new Rectangle(0, 0, 1200, 900);
        var windowBounds = workingArea;
        var composerWithRoomBelow = new Rectangle(100, 650, 1000, 150);
        var composerAtBottom = new Rectangle(100, 730, 1000, 150);
        var expandedComposer = new Rectangle(100, 350, 1000, 500);
        var cases = new List<object>();
        var allPassed = true;

        AddCase("maximized_prefers_clear_above", true, composerWithRoomBelow, true, true, true);
        AddCase("maximized_falls_back_below_when_above_is_occupied", true, composerWithRoomBelow, false, true, false);
        AddCase("maximized_uses_clear_above_at_bottom", true, composerAtBottom, true, true, true);
        AddCase("maximized_hides_when_neither_side_is_available", true, composerAtBottom, false, false, false);
        AddCase("windowed_uses_clear_above", false, composerWithRoomBelow, true, true, true);
        AddCase("windowed_hides_when_above_is_occupied", false, composerWithRoomBelow, false, false, false);
        AddCase("expanded_windowed_composer_uses_above", false, expandedComposer, true, true, true);

        var homeCases = new List<object>();
        var homeComposer = new Rectangle(100, 340, 1000, 90);
        var homePlus = new Rectangle(112, 378, 40, 40);
        var homeHeading = new UiaNodeDescriptor(
            new Rectangle(400, 270, 400, 30),
            ControlType.Text.Id,
            string.Empty,
            false,
            "我们要做什么？",
            HeadingLevel: HomePageRecognition.Level1,
            InHomeHero: true);
        var homeAnchor = new ComposerAnchor(
            homeComposer,
            homePlus,
            "home-composer:1|plus:1",
            homeHeading.Bounds);
        var homeContainer = new UiaNodeDescriptor(
            new Rectangle(100, 270, 1000, 160),
            ControlType.Group.Id,
            "home-composer-group",
            false);
        AddHomeCase("visible_exact_home_heading_is_recognized", HomePageRecognition.IsHeading(homeHeading));
        AddHomeCase("supported_home_heading_variants_are_recognized",
            new[] { "我们要做什么?", "我們要做什麼？", "What should we do?", "What should we work on?" }
                .All(name => HomePageRecognition.IsHeading(homeHeading with { Name = name })));
        AddHomeCase("quoted_heading_in_longer_text_is_rejected", !HomePageRecognition.IsHeading(
            homeHeading with { Name = "用户提到：我们要做什么？" }));
        AddHomeCase("offscreen_home_heading_is_rejected", !HomePageRecognition.IsHeading(
            homeHeading with { IsOffscreen = true }));
        AddHomeCase("visually_hidden_home_heading_is_rejected", !HomePageRecognition.IsHeading(
            homeHeading with { ClassName = "sr-only" }));
        AddHomeCase("same_named_button_is_not_a_home_heading", !HomePageRecognition.IsHeading(
            homeHeading with { ControlTypeId = ControlType.Button.Id }));
        AddHomeCase("same_text_in_body_is_not_a_home_heading", !HomePageRecognition.IsHeading(
            homeHeading with { HeadingLevel = 0, InHomeHero = false }));
        AddHomeCase("level_one_title_without_home_context_is_rejected", !HomePageRecognition.IsHeading(
            homeHeading with { InHomeHero = false }));
        AddHomeCase("group_in_home_hero_is_a_home_heading", HomePageRecognition.IsHeading(
            homeHeading with { ControlTypeId = ControlType.Group.Id }));
        AddHomeCase("level_two_title_without_home_context_is_rejected", !HomePageRecognition.IsHeading(
            homeHeading with { HeadingLevel = 80052, InHomeHero = false }));
        AddHomeCase("centered_heading_above_composer_is_valid", HomePageRecognition.IsHeadingPositionValid(
            windowBounds, homeComposer, homeHeading.Bounds));
        AddHomeCase("heading_inside_input_is_rejected", !HomePageRecognition.IsHeadingPositionValid(
            windowBounds, homeComposer, new Rectangle(400, 350, 400, 30)));
        AddHomeCase("heading_in_sidebar_is_rejected", !HomePageRecognition.IsHeadingPositionValid(
            windowBounds, homeComposer, new Rectangle(20, 270, 200, 30)));
        AddHomeCase("distant_heading_is_rejected", !HomePageRecognition.IsHeadingPositionValid(
            windowBounds, homeComposer, new Rectangle(400, 20, 400, 30)));
        AddHomeCase("small_body_text_is_not_a_home_heading", !HomePageRecognition.IsHeadingPositionValid(
            windowBounds, homeComposer, new Rectangle(400, 280, 400, 16)));
        AddHomeCase("offwindow_heading_is_rejected", !HomePageRecognition.IsHeadingPositionValid(
            windowBounds, homeComposer, new Rectangle(400, -10, 400, 30)));
        AddHomeCase("centered_composer_requires_home_evidence",
            !ComposerGeometry.IsPlausible(windowBounds, homeComposer)
            && !ComposerAnchorGeometry.IsPlausible(windowBounds, homeComposer, homePlus)
            && ComposerGeometry.IsPlausible(windowBounds, homeComposer, homeHeading.Bounds)
            && ComposerAnchorGeometry.IsPlausible(windowBounds, homeComposer, homePlus, homeHeading.Bounds));
        AddHomeCase("home_evidence_does_not_accept_wrong_plus",
            !ComposerAnchorGeometry.IsPlausible(
                windowBounds, homeComposer, new Rectangle(1048, 378, 40, 40), homeHeading.Bounds));
        AddHomeCase("home_evidence_does_not_accept_narrow_composer",
            !ComposerGeometry.IsPlausible(
                windowBounds,
                new Rectangle(440, 340, 319, 90),
                new Rectangle(500, 270, 200, 30)));

        // Captured from the installed desktop's real Work home page on 2026-09-29.
        // Its title is a plain Text node with HeadingLevel=0 and a 30 px composer gap.
        var actualHomeWindow = new Rectangle(321, 153, 1920, 1224);
        var actualHomeComposer = new Rectangle(1021, 667, 961, 148);
        var actualHomeHeading = homeHeading with
        {
            Bounds = new Rectangle(1354, 580, 295, 57),
            HeadingLevel = 0,
            InHomeHero = true
        };
        var actualHomeAnchor = new ComposerAnchor(
            actualHomeComposer,
            new Rectangle(1033, 760, 43, 43),
            "actual-home-composer:1|plus:1",
            actualHomeHeading.Bounds);
        var actualHomeContainer = new UiaNodeDescriptor(
            new Rectangle(1021, 580, 961, 235),
            ControlType.Group.Id,
            "home-composer-anchor group/home-composer-layout",
            false);
        AddHomeCase("actual_home_plain_text_with_hero_context_is_recognized",
            HomePageRecognition.IsHeading(actualHomeHeading));
        AddHomeCase("actual_home_plain_text_without_hero_context_is_rejected",
            !HomePageRecognition.IsHeading(actualHomeHeading with { InHomeHero = false }));
        AddHomeCase("actual_home_geometry_and_plus_are_accepted",
            HomePageRecognition.IsHeadingPositionValid(
                actualHomeWindow, actualHomeComposer, actualHomeHeading.Bounds)
            && ComposerAnchorGeometry.IsPlausible(
                actualHomeWindow, actualHomeComposer, actualHomeAnchor.PlusBounds, actualHomeHeading.Bounds));
        foreach (var maximized in new[] { false, true })
        {
            var placed = OverlayPlacement.TryFind(
                actualHomeWindow,
                maximized,
                actualHomeAnchor,
                actualHomeWindow,
                candidate => UiaCollisionRules.IsClearOnHomePage(
                    candidate, actualHomeWindow, actualHomeAnchor, [actualHomeContainer, actualHomeHeading])
                        ? candidate
                        : null,
                out var actualHomeBar);
            AddHomeCase($"actual_{(maximized ? "maximized" : "windowed")}_home_30px_gap_displays_bar",
                placed
                && actualHomeBar == new Rectangle(1029, 638, 945, 28)
                && actualHomeBar.Top - actualHomeHeading.Bounds.Bottom == 1
                && actualHomeComposer.Top - actualHomeBar.Bottom == 1);
        }

        var homeOverlay = Rectangle.Empty;
        foreach (var maximized in new[] { false, true })
        {
            var placed = OverlayPlacement.TryFind(
                windowBounds,
                maximized,
                homeAnchor,
                workingArea,
                candidate => UiaCollisionRules.IsClearOnHomePage(
                    candidate, windowBounds, homeAnchor, [homeContainer, homeHeading])
                        ? candidate
                        : null,
                out var placedBounds);
            var gapAbove = placedBounds.Top - homeHeading.Bounds.Bottom;
            var gapBelow = homeComposer.Top - placedBounds.Bottom;
            AddHomeCase($"{(maximized ? "maximized" : "windowed")}_home_centers_bar_between_heading_and_input",
                placed
                && placedBounds.Height == OverlayPlacement.Height
                && placedBounds.Left >= homeComposer.Left
                && placedBounds.Right <= homeComposer.Right
                && gapAbove >= 2
                && gapBelow >= 2
                && Math.Abs(gapAbove - gapBelow) <= 1);
            if (placed)
            {
                homeOverlay = placedBounds;
            }

            AddHomeCase($"{(maximized ? "maximized" : "windowed")}_home_does_not_fall_back_below_occupied_gap",
                !OverlayPlacement.TryFind(
                    windowBounds, maximized, homeAnchor, workingArea, _ => null, out _));
        }

        var tightHomeAnchor = homeAnchor with
        {
            HomeHeadingBounds = new Rectangle(400, 281, 400, 30)
        };
        AddHomeCase("home_hides_when_heading_gap_is_too_small",
            !OverlayPlacement.TryFind(
                windowBounds, true, tightHomeAnchor, workingArea, candidate => candidate, out _));
        var minimumGapHomeAnchor = homeAnchor with
        {
            HomeHeadingBounds = new Rectangle(400, 280, 400, 30)
        };
        AddHomeCase("home_uses_minimum_gap_without_covering_heading",
            OverlayPlacement.TryFind(
                windowBounds, true, minimumGapHomeAnchor, workingArea, candidate => candidate, out var minimumGapBounds)
            && minimumGapBounds.Top - minimumGapHomeAnchor.HomeHeadingBounds.Bottom == 1
            && homeComposer.Top - minimumGapBounds.Bottom == 1);
        AddHomeCase("home_rejects_resolved_position_above_heading",
            !OverlayPlacement.TryFind(
                windowBounds,
                true,
                homeAnchor,
                workingArea,
                candidate => new Rectangle(candidate.X, homeHeading.Bounds.Top - 40, candidate.Width, candidate.Height),
                out _));
        AddHomeCase("home_container_exception_is_specific",
            !homeOverlay.IsEmpty
            && !UiaCollisionRules.IsClear(homeOverlay, windowBounds, [homeContainer, homeHeading])
            && UiaCollisionRules.IsClearOnHomePage(
                homeOverlay, windowBounds, homeAnchor, [homeContainer, homeHeading]));
        AddHomeCase("home_collision_requires_still_visible_heading",
            !UiaCollisionRules.IsClearOnHomePage(homeOverlay, windowBounds, homeAnchor, [homeContainer])
            && !UiaCollisionRules.IsClearOnHomePage(
                homeOverlay, windowBounds, homeAnchor, [homeContainer, homeHeading with { IsOffscreen = true }]));
        AddHomeCase("home_collision_rejects_stale_heading_bounds",
            !UiaCollisionRules.IsClearOnHomePage(
                homeOverlay,
                windowBounds,
                homeAnchor,
                [homeContainer, homeHeading with { Bounds = new Rectangle(400, 240, 400, 30) }]));
        foreach (var controlType in new[] { ControlType.Text, ControlType.Button, ControlType.Group })
        {
            var obstruction = new UiaNodeDescriptor(
                homeOverlay,
                controlType.Id,
                "independent-panel",
                false,
                "其他内容");
            AddHomeCase($"home_gap_still_rejects_{controlType.ProgrammaticName}",
                !UiaCollisionRules.IsClearOnHomePage(
                    homeOverlay, windowBounds, homeAnchor, [homeContainer, homeHeading, obstruction]));
        }

        AddHomeCase("home_does_not_ignore_semantic_container",
            !UiaCollisionRules.IsClearOnHomePage(
                homeOverlay,
                windowBounds,
                homeAnchor,
                [homeHeading, homeContainer with { ControlTypeId = ControlType.Text.Id, Name = "其他内容" }]));
        var ordinaryAnchor = new ComposerAnchor(
            composerWithRoomBelow,
            new Rectangle(112, 748, 40, 40),
            "ordinary-composer:1|plus:1");
        AddHomeCase("ordinary_anchor_keeps_maximized_bottom_fallback",
            !ordinaryAnchor.IsHomePage
            && OverlayPlacement.TryFind(
                windowBounds, true, ordinaryAnchor, workingArea, _ => null, out var ordinaryFallbackBounds)
            && ordinaryFallbackBounds.Top == composerWithRoomBelow.Bottom + OverlayPlacement.ComposerGap);

        var desktopWindow = new Rectangle(-11, -11, 2582, 1550);
        var desktopComposer = new Rectangle(713, 1357, 1105, 147);
        var desktopComposerPlus = new Rectangle(725, 1450, 43, 42);
        var desktopComposerPlusAccepted = ComposerAnchorGeometry.IsPlausible(
            desktopWindow,
            desktopComposer,
            desktopComposerPlus);
        var rightSideActionRejected = !ComposerAnchorGeometry.IsPlausible(
            desktopWindow,
            desktopComposer,
            new Rectangle(1709, 1450, 43, 42));
        var upperLeftActionRejected = !ComposerAnchorGeometry.IsPlausible(
            desktopWindow,
            desktopComposer,
            new Rectangle(725, 1369, 43, 42));
        var topSidebarPlusRejected = !ComposerAnchorGeometry.IsPlausible(
            desktopWindow,
            new Rectangle(1800, 40, 500, 100),
            new Rectangle(1812, 90, 43, 42));
        var changedRuntimeIdentityRejected = !ComposerAnchorGeometry.IsSameAnchor(
            new ComposerAnchor(desktopComposer, desktopComposerPlus, "composer:1|plus:1"),
            new ComposerAnchor(desktopComposer, desktopComposerPlus, "composer:2|plus:2"));
        var nearbySameIdentityAccepted = ComposerAnchorGeometry.IsSameAnchor(
            new ComposerAnchor(desktopComposer, desktopComposerPlus, "composer:1|plus:1"),
            new ComposerAnchor(
                new Rectangle(
                    desktopComposer.X + 3,
                    desktopComposer.Y - 2,
                    desktopComposer.Width,
                    desktopComposer.Height),
                new Rectangle(
                    desktopComposerPlus.X + 2,
                    desktopComposerPlus.Y - 1,
                    desktopComposerPlus.Width,
                    desktopComposerPlus.Height),
                "composer:1|plus:1"));
        var largeJumpRejectedAsSameAnchor = !ComposerAnchorGeometry.IsSameAnchor(
            new ComposerAnchor(desktopComposer, desktopComposerPlus, "composer:1|plus:1"),
            new ComposerAnchor(
                new Rectangle(
                    desktopComposer.X + 120,
                    desktopComposer.Y - 80,
                    desktopComposer.Width,
                    desktopComposer.Height),
                new Rectangle(
                    desktopComposerPlus.X + 120,
                    desktopComposerPlus.Y - 80,
                    desktopComposerPlus.Width,
                    desktopComposerPlus.Height),
                "composer:1|plus:1"));
        allPassed &= desktopComposerPlusAccepted
            && rightSideActionRejected
            && upperLeftActionRejected
            && topSidebarPlusRejected
            && changedRuntimeIdentityRejected
            && nearbySameIdentityAccepted
            && largeJumpRejectedAsSameAnchor;

        var maximizedSplitWindow = new Rectangle(0, 0, 2560, 1536);
        var splitPaneComposer = new Rectangle(492, 1357, 435, 147);
        var splitPaneEdit = new Rectangle(512, 1385, 395, 64);
        var topSidebarEdit = new Rectangle(1980, 80, 360, 42);
        var tooNarrowComposer = new Rectangle(492, 1357, 319, 147);
        var splitPaneComposerAccepted = ComposerGeometry.IsPlausible(maximizedSplitWindow, splitPaneComposer);
        var splitPaneEditAccepted = ComposerGeometry.IsPlausibleEdit(maximizedSplitWindow, splitPaneEdit);
        var topSidebarEditRejected = !ComposerGeometry.IsPlausibleEdit(maximizedSplitWindow, topSidebarEdit);
        var tooNarrowComposerRejected = !ComposerGeometry.IsPlausible(maximizedSplitWindow, tooNarrowComposer);
        var toolWindowClassifiedAuxiliary = CodexWindowCatalog.IsAuxiliaryWindowStyle(0x00000080);
        var ordinaryWindowNotAuxiliary = !CodexWindowCatalog.IsAuxiliaryWindowStyle(0x00000000);
        var overlaySample = new Rectangle(108, 616, 984, OverlayPlacement.Height);
        var clearPetsWindow = new Rectangle(900, 300, 240, 180);
        var overlappingPetsWindow = new Rectangle(600, 610, 240, 180);
        var draggingPetsClearKeepsVisible = !OverlayPlacement.IsBlockedByAuxiliary(
            overlaySample,
            clearPetsWindow);
        var draggingPetsOverlapHides = OverlayPlacement.IsBlockedByAuxiliary(
            overlaySample,
            overlappingPetsWindow);
        var sustainedPetsDragKeepsCachedPlacement = Enumerable.Range(0, 24).All(_ =>
            OverlayPlacement.TryReuseDuringAuxiliaryInteraction(
                true,
                overlaySample,
                [clearPetsWindow],
                out var reusedBounds)
            && reusedBounds == overlaySample);
        var petsOverlapStillHidesAfterMainClick = OverlayPlacement.IsBlockedByAnyAuxiliary(
            overlaySample,
            [clearPetsWindow, overlappingPetsWindow]);
        var petsOverlapSuppressesCachedPlacement = OverlayPlacement.TryReuseDuringAuxiliaryInteraction(
                true,
                overlaySample,
                [overlappingPetsWindow],
                out var blockedCachedBounds)
            && blockedCachedBounds.IsEmpty;
        var petsDragFreezeStartsOnPress = PetsDragFreezePolicy.ShouldFreeze(
            false,
            true,
            true);
        var petsDragFreezePreservesStateFor240Ticks = Enumerable.Range(0, 240).All(_ =>
            PetsDragFreezePolicy.ShouldFreeze(
                true,
                true,
                false));
        var petsDragFreezeStopsOnRelease = !PetsDragFreezePolicy.ShouldFreeze(
            true,
            false,
            false);
        var otherWindowPressDoesNotFreeze = !PetsDragFreezePolicy.ShouldFreeze(
            false,
            true,
            false);
        var visualUpdatesDeferredDuringFreeze = PetsDragFreezePolicy.ShouldDeferVisualUpdate(true);
        var visualUpdatesResumeAfterRelease = !PetsDragFreezePolicy.ShouldDeferVisualUpdate(false);
        var transparentPetsHost = new Rectangle(1758, 0, 803, 1601);
        var visiblePetSprite = new Rectangle(2256, 688, 120, 131);
        var activityToast = new Rectangle(2103, 588, 428, 83);
        var petSpriteSelectedFromTransparentHost = PetWindowLocator.TrySelectPetBounds(
                transparentPetsHost,
                [
                    new PetUiaNodeDescriptor(
                        activityToast,
                        "running activity",
                        "group relative z-10",
                        ControlType.ListItem.Id,
                        false),
                    new PetUiaNodeDescriptor(
                        visiblePetSprite,
                        "Icebun Noir 宠物",
                        "codex-avatar-button relative flex cursor-interaction",
                        ControlType.Image.Id,
                        false)
                ],
                out var selectedPetBounds)
            && selectedPetBounds == visiblePetSprite;
        var transparentPetsHostDoesNotUseNativeFallback =
            !PetWindowLocator.ShouldUseNativeBoundsAsLegacyFallback(transparentPetsHost);
        var legacyPetsHostKeepsNativeFallback = PetWindowLocator.ShouldUseNativeBoundsAsLegacyFallback(
            new Rectangle(1931, 887, 613, 601));
        var wideOverlay = new Rectangle(600, 700, 1300, OverlayPlacement.Height);
        var transparentHostWouldHaveBlocked = OverlayPlacement.IsBlockedByAuxiliary(
            wideOverlay,
            transparentPetsHost);
        var visiblePetSpriteDoesNotFalseBlock = !OverlayPlacement.IsBlockedByAuxiliary(
            wideOverlay,
            selectedPetBounds);
        allPassed &= splitPaneComposerAccepted
            && splitPaneEditAccepted
            && topSidebarEditRejected
            && tooNarrowComposerRejected
            && toolWindowClassifiedAuxiliary
            && ordinaryWindowNotAuxiliary
            && draggingPetsClearKeepsVisible
            && draggingPetsOverlapHides
            && sustainedPetsDragKeepsCachedPlacement
            && petsOverlapStillHidesAfterMainClick
            && petsOverlapSuppressesCachedPlacement
            && petsDragFreezeStartsOnPress
            && petsDragFreezePreservesStateFor240Ticks
            && petsDragFreezeStopsOnRelease
            && otherWindowPressDoesNotFreeze
            && visualUpdatesDeferredDuringFreeze
            && visualUpdatesResumeAfterRelease
            && petSpriteSelectedFromTransparentHost
            && transparentPetsHostDoesNotUseNativeFallback
            && legacyPetsHostKeepsNativeFallback
            && transparentHostWouldHaveBlocked
            && visiblePetSpriteDoesNotFalseBlock;

        var collisionCandidate = new Rectangle(108, 616, 984, OverlayPlacement.Height);
        var broadStructure = new UiaNodeDescriptor(
            new Rectangle(80, 100, 1040, 700),
            ControlType.Group.Id,
            "thread-scroll-container",
            false);
        var visibleText = new UiaNodeDescriptor(
            new Rectangle(500, 620, 180, 20),
            ControlType.Text.Id,
            string.Empty,
            false);
        var localPane = new UiaNodeDescriptor(
            new Rectangle(108, 610, 984, 52),
            ControlType.Group.Id,
            "inline-pane",
            false);
        var hiddenText = visibleText with { IsOffscreen = true };
        var broadStructureIgnored = UiaCollisionRules.IsClear(
            collisionCandidate,
            windowBounds,
            [broadStructure]);
        var visibleTextRejected = !UiaCollisionRules.IsClear(
            collisionCandidate,
            windowBounds,
            [broadStructure, visibleText]);
        var localPaneRejected = !UiaCollisionRules.IsClear(
            collisionCandidate,
            windowBounds,
            [broadStructure, localPane]);
        var hiddenTextIgnored = UiaCollisionRules.IsClear(
            collisionCandidate,
            windowBounds,
            [broadStructure, hiddenText]);
        var activeGoalPanel = new UiaNodeDescriptor(
            new Rectangle(108, 574, 984, 66),
            ControlType.Group.Id,
            "rounded-xl goal-surface",
            false);
        var activeGoalLabel = new UiaNodeDescriptor(
            new Rectangle(130, 590, 720, 24),
            ControlType.Text.Id,
            string.Empty,
            false,
            "进行中的目标 完全按照这个计划进行",
            string.Empty);
        var activeGoalMovesQuotaAbove = UiaCollisionRules.TryResolveClearRegionAboveActiveGoal(
                collisionCandidate,
                workingArea,
                windowBounds,
                [broadStructure, activeGoalPanel, activeGoalLabel],
                out var activeGoalOverlayBounds)
            && activeGoalOverlayBounds.Y
                == activeGoalPanel.Bounds.Top
                    - OverlayPlacement.Height
                    - OverlayPlacement.ComposerGap;
        var ordinaryPanelDoesNotMoveQuota = !UiaCollisionRules.TryResolveClearRegionAboveActiveGoal(
            collisionCandidate,
            workingArea,
            windowBounds,
            [broadStructure, localPane, visibleText],
            out _);
        var contentAboveGoalKeepsQuotaHidden = !UiaCollisionRules.TryResolveClearRegionAboveActiveGoal(
            collisionCandidate,
            workingArea,
            windowBounds,
            [
                broadStructure,
                activeGoalPanel,
                activeGoalLabel,
                new UiaNodeDescriptor(
                    activeGoalOverlayBounds,
                    ControlType.Text.Id,
                    string.Empty,
                    false,
                    "已有内容")
            ],
            out _);
        allPassed &= broadStructureIgnored
            && visibleTextRejected
            && localPaneRejected
            && hiddenTextIgnored
            && activeGoalMovesQuotaAbove
            && ordinaryPanelDoesNotMoveQuota
            && contentAboveGoalKeepsQuotaHidden;

        using var quotaDocument = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitId": "legacy",
                "primary": { "usedPercent": 2, "windowDurationMins": 60 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "planType": "plus",
                  "primary": { "usedPercent": 12, "windowDurationMins": 10080, "resetsAt": 1788408654 },
                  "secondary": { "usedPercent": 80, "windowDurationMins": 300, "resetsAt": 1787821854 }
                }
              }
            }
            """);
        var canonicalBucketSelected = CodexQuotaClient.TrySelectCanonicalRateLimits(
            quotaDocument.RootElement,
            out var canonicalBucket)
            && canonicalBucket.GetProperty("limitId").GetString() == "codex";
        var shortestQuotaWindowSelected = canonicalBucketSelected
            && CodexQuotaClient.TryCreateShortestWindowSnapshot(canonicalBucket, out var shortestSnapshot)
            && shortestSnapshot.WindowDurationMinutes == 300
            && Math.Abs(shortestSnapshot.RemainingPercent - 20d) < 0.001d
            && shortestSnapshot.WindowLabel == "5 小时额度";
        var minuteWindowLabelSupported = new QuotaSnapshot(25d, 15, null, null).WindowLabel == "15 分钟额度";
        using var futureQuotaDocument = JsonDocument.Parse(
            """
            {
              "rate_limit_buckets": {
                "codex_workspace": {
                  "limit_id": "codex_workspace",
                  "plan_type": "plus",
                  "windows": {
                    "weekly": { "used_percent": "10", "window_duration_mins": "10080" },
                    "rolling": { "usage_percent": 82, "window_minutes": 300, "resets_at": "1787821854000" }
                  }
                }
              }
            }
            """);
        var futureQuotaAliasesSupported = CodexQuotaClient.TryCreateShortestQuotaSnapshot(
                futureQuotaDocument.RootElement,
                out var futureSnapshot)
            && futureSnapshot.WindowDurationMinutes == 300
            && Math.Abs(futureSnapshot.RemainingPercent - 18d) < 0.001d
            && futureSnapshot.ResetsAt is not null;
        var renamedPackagedProcessDetected = CodexWindowCatalog.LooksLikeCodexDesktopProcess(
            "OpenAIWorkbench",
            @"C:\Program Files\WindowsApps\OpenAI.Codex_27.1.0_x64__test\app\OpenAIWorkbench.exe",
            null,
            null);
        var renamedMetadataProcessDetected = CodexWindowCatalog.LooksLikeCodexDesktopProcess(
            "OpenAIWorkbench",
            @"C:\Apps\OpenAIWorkbench.exe",
            "OpenAI Codex Desktop",
            "OpenAI Workbench");
        var unrelatedProcessRejected = !CodexWindowCatalog.LooksLikeCodexDesktopProcess(
            "Notes",
            @"C:\Apps\Notes.exe",
            "Notes",
            "Notes");
        allPassed &= canonicalBucketSelected
            && shortestQuotaWindowSelected
            && minuteWindowLabelSupported
            && futureQuotaAliasesSupported
            && renamedPackagedProcessDetected
            && renamedMetadataProcessDetected
            && unrelatedProcessRejected;

        WriteJson(outputPath, new
        {
            passed = allPassed,
            desktopComposerPlusAccepted,
            rightSideActionRejected,
            upperLeftActionRejected,
            topSidebarPlusRejected,
            changedRuntimeIdentityRejected,
            nearbySameIdentityAccepted,
            largeJumpRejectedAsSameAnchor,
            splitPaneComposerAccepted,
            splitPaneEditAccepted,
            topSidebarEditRejected,
            tooNarrowComposerRejected,
            toolWindowClassifiedAuxiliary,
            ordinaryWindowNotAuxiliary,
            draggingPetsClearKeepsVisible,
            draggingPetsOverlapHides,
            sustainedPetsDragKeepsCachedPlacement,
            petsOverlapStillHidesAfterMainClick,
            petsOverlapSuppressesCachedPlacement,
            petsDragFreezeStartsOnPress,
            petsDragFreezePreservesStateFor240Ticks,
            petsDragFreezeStopsOnRelease,
            otherWindowPressDoesNotFreeze,
            visualUpdatesDeferredDuringFreeze,
            visualUpdatesResumeAfterRelease,
            petSpriteSelectedFromTransparentHost,
            transparentPetsHostDoesNotUseNativeFallback,
            legacyPetsHostKeepsNativeFallback,
            transparentHostWouldHaveBlocked,
            visiblePetSpriteDoesNotFalseBlock,
            broadStructureIgnored,
            visibleTextRejected,
            localPaneRejected,
            hiddenTextIgnored,
            activeGoalMovesQuotaAbove,
            ordinaryPanelDoesNotMoveQuota,
            contentAboveGoalKeepsQuotaHidden,
            canonicalBucketSelected,
            shortestQuotaWindowSelected,
            minuteWindowLabelSupported,
            futureQuotaAliasesSupported,
            renamedPackagedProcessDetected,
            renamedMetadataProcessDetected,
            unrelatedProcessRejected,
            homeCases,
            cases
        });

        if (!allPassed)
        {
            Environment.ExitCode = 1;
        }

        void AddHomeCase(string name, bool passed)
        {
            allPassed &= passed;
            homeCases.Add(new { name, passed });
        }

        void AddCase(
            string name,
            bool maximized,
            Rectangle composerBounds,
            bool aboveIsClear,
            bool expectedVisible,
            bool expectedAbove)
        {
            var visible = OverlayPlacement.TryFind(
                windowBounds,
                maximized,
                composerBounds,
                workingArea,
                _ => aboveIsClear,
                out var bounds);
            var expectedY = expectedAbove
                ? composerBounds.Top - OverlayPlacement.Height - OverlayPlacement.ComposerGap
                : composerBounds.Bottom + OverlayPlacement.ComposerGap;
            var passed = visible == expectedVisible
                && (!visible || bounds.Y == expectedY);
            allPassed &= passed;
            cases.Add(new
            {
                name,
                expectedVisible,
                expectedAbove,
                visible,
                passed,
                bounds = visible ? new { bounds.X, bounds.Y, bounds.Width, bounds.Height } : null
            });
        }
    }

    private static void WriteJson<T>(string outputPath, T value)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void CaptureCodexWindow(string outputPath)
    {
        AttachToParentConsole();
        var target = CodexWindowCatalog.FindLargestVisibleWindow();

        if (target is null)
        {
            Console.Error.WriteLine("codex_window_not_found");
            Environment.ExitCode = 1;
            return;
        }

        if (!NativeMethods.GetWindowRect(target.Handle, out var rect))
        {
            Console.Error.WriteLine("codex_bounds_unavailable");
            Environment.ExitCode = 1;
            return;
        }

        SaveScreenRegion(outputPath, rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static void CaptureVisibleOverlay(string outputPath, bool includeContext)
    {
        AttachToParentConsole();
        var currentProcessId = Environment.ProcessId;
        var processNames = new[] { "CodexQuotaBar", "Codex剩余额度条" };
        var targets = processNames
            .SelectMany(Process.GetProcessesByName)
            .Where(process => process.Id != currentProcessId)
            .ToArray();
        Process? target = null;
        var window = IntPtr.Zero;
        foreach (var candidate in targets)
        {
            var candidateWindow = FindOverlayWindow(candidate.Id);
            if (candidateWindow != IntPtr.Zero)
            {
                target = candidate;
                window = candidateWindow;
                break;
            }
        }

        foreach (var candidate in targets)
        {
            if (!ReferenceEquals(candidate, target))
            {
                candidate.Dispose();
            }
        }

        if (target is null)
        {
            Console.Error.WriteLine(targets.Length == 0
                ? "overlay_process_not_found"
                : "visible_overlay_not_found");
            Environment.ExitCode = 1;
            return;
        }

        using (target)
        {
            if (!NativeMethods.GetWindowRect(window, out var rect))
            {
                Console.Error.WriteLine("overlay_bounds_unavailable");
                Environment.ExitCode = 1;
                return;
            }

            var captureLeft = includeContext ? rect.Left - 20 : rect.Left;
            var captureTop = includeContext ? rect.Top - 100 : rect.Top;
            var captureRight = includeContext ? rect.Right + 20 : rect.Right;
            var captureBottom = includeContext ? rect.Bottom + 10 : rect.Bottom;
            var virtualScreen = SystemInformation.VirtualScreen;
            captureLeft = Math.Max(captureLeft, virtualScreen.Left);
            captureTop = Math.Max(captureTop, virtualScreen.Top);
            captureRight = Math.Min(captureRight, virtualScreen.Right);
            captureBottom = Math.Min(captureBottom, virtualScreen.Bottom);

            var width = captureRight - captureLeft;
            var height = captureBottom - captureTop;
            if (width <= 0 || height <= 0)
            {
                Console.Error.WriteLine("overlay_bounds_invalid");
                Environment.ExitCode = 1;
                return;
            }

            var fullPath = Path.GetFullPath(outputPath);
            SaveScreenRegion(fullPath, captureLeft, captureTop, captureRight, captureBottom);
            Console.WriteLine($"capture={fullPath}");
            Console.WriteLine($"overlayBounds={rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}");
            Console.WriteLine($"captureBounds={captureLeft},{captureTop},{width},{height}");
        }
    }

    private static void SaveScreenRegion(string outputPath, int left, int top, int right, int bottom)
    {
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("截图区域无效");
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        bitmap.Save(fullPath, ImageFormat.Png);
    }

    private static IntPtr FindOverlayWindow(int processId)
    {
        var result = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId && NativeMethods.IsWindowVisible(window))
            {
                result = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void AttachToParentConsole()
    {
        if (!NativeMethods.AttachConsole(uint.MaxValue))
        {
            return;
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    }
}
