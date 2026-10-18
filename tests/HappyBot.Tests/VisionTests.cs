using System.Drawing;
using System.Text.Json;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

static partial class Program
{
    private static void FullFrameScreenCoordinatesPreserveRoiDetection()
    {
        var frame = new ScreenFrame { Width = 4, Height = 3, Stride = 16, OriginX = 100, OriginY = 200, Buffer = new byte[48] };
        int pixel = 1 * frame.Stride + 2 * 4;
        frame.Buffer[pixel] = 41;
        frame.Buffer[pixel + 1] = 49;
        frame.Buffer[pixel + 2] = 255;

        bool found = frame.ScreenPixelSearch(100, 200, 103, 202, 255, 49, 41, 0, out int x, out int y);
        ColorProbe probe = frame.ProbeColor(2, 1, 2, 1, 255, 49, 41, 0);
        Require(found && x == 102 && y == 201, "full-frame ROI search must return screen coordinates");
        Require(probe.MatchCount == 1, "ROI telemetry probe must remain scoped to the combat region");
    }

    private static void CroppedSearchDoesNotRepeatEdgePixels()
    {
        var frame = new ScreenFrame
        {
            Width = 4,
            Height = 3,
            Stride = 16,
            OriginX = 100,
            OriginY = 200,
            Buffer = new byte[48]
        };
        int edge = 0;
        frame.Buffer[edge] = 41;
        frame.Buffer[edge + 1] = 49;
        frame.Buffer[edge + 2] = 255;

        bool outside = frame.ScreenPixelSearch(90, 200, 99, 202, 255, 49, 41, 0, out _, out _);
        bool partial = frame.ScreenPixelSearch(99, 200, 100, 200, 255, 49, 41, 0, out int x, out int y);
        Require(!outside, "a screen query wholly outside a crop must not clamp to its edge pixel");
        Require(partial && x == 100 && y == 200, "a partially overlapping query should still scan the valid crop intersection");
    }

    private static void CapturePlannerCoversBootstrapAndTrackedRegions()
    {
        Rectangle screen = new(0, 0, 1920, 1200);
        Rectangle marker = new(860, 80, 215, 345);
        Rectangle box = new(670, 300, 150, 210);
        Rectangle possible = CaptureRegionPlanner.PossibleCombatBounds(marker, 1920.0 / 1920.0, 1200.0 / 1080.0);
        CapturePlan bootstrap = CaptureRegionPlanner.Build(screen, marker, box, possible,
            Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, false);
        Require(bootstrap.Mode == CaptureMode.Bootstrap && !bootstrap.IsFullScreen &&
            bootstrap.Region.Contains(new Point(600, 500)),
            "bootstrap capture must include the padded combat area");

        Rectangle active = new(500, 400, 650, 500);
        Rectangle confirmation = new(600, 800, 350, 250);
        CapturePlan tracked = CaptureRegionPlanner.Build(screen, marker, box, possible,
            active, Rectangle.Empty, confirmation, true);
        Require(tracked.Mode == CaptureMode.Tracked && tracked.Region.Contains(new Point(1100, 850)) &&
            tracked.Region.Contains(new Point(700, 900)),
            "tracked capture must include active combat and confirmation regions");
    }

    private static void ReactionPolicySelectionCoversWardenPriority()
    {
        Settings wardenSettings = new() { Autoblock = true, Parry = true, YourHero = true };
        wardenSettings.Chars["Warden"] = true;
        ReactionSelection wardenSelection = ReactionPolicy.ResolveCommand(
            Observation(20, CombatDirection.Top), wardenSettings);
        Require(wardenSelection.Kind == ReactionCommandKind.Crushing && wardenSelection.Hold == "F",
            "Warden top F should retain its crushing priority");

        wardenSettings.Chars["Warden"] = false;
        ReactionSelection normalSelection = ReactionPolicy.ResolveCommand(
            Observation(21, CombatDirection.Top), wardenSettings);
        Require(normalSelection.Kind == ReactionCommandKind.Parry && normalSelection.Hold == "F",
            "a non-Warden top F should remain a normal parry");

        Settings orangeSettings = new() { Unblockables = true };
        CombatObservation orange = Observation(30, CombatDirection.Right) with { OrangeIndicator = true };
        Require(ReactionPolicy.OrangeHasPriority(orange, orangeSettings, false),
            "an orange indicator must win before reaction selection");
        Require(ReactionPolicy.OrangeHasPriority(orange with { OrangeIndicator = false }, orangeSettings, true),
            "an active action must remain a priority gate even without orange");
    }

    private static void NuxiaTopDeflectIsDisabledForYourHero()
    {
        Settings nuxia = new() { Autoblock = true, Deflect = true, YourHero = true };
        nuxia.Chars["Nuxia"] = true;
        ReactionSelection top = ReactionPolicy.ResolveCommand(Observation(11, CombatDirection.Top), nuxia);
        ReactionSelection side = ReactionPolicy.ResolveCommand(Observation(12, CombatDirection.Left), nuxia);
        Require(top.Kind == ReactionCommandKind.None,
            "Your Hero Nuxia must not select a top deflect");
        Require(side.Kind == ReactionCommandKind.Deflect,
            "Your Hero Nuxia must retain side deflects");

        nuxia.Nohero = true;
        ReactionSelection disabledHero = ReactionPolicy.ResolveCommand(Observation(13, CombatDirection.Top), nuxia);
        Require(disabledHero.Kind == ReactionCommandKind.Deflect,
            "Nuxia top deflect should return when Your Hero is disabled");
    }

    private static void VisionAnalyzerUsesExplicitBoundsAndPreservesMarkerLoss()
    {
        var analyzer = new VisionAnalyzer();
        Rectangle combatRoi = new(120, 220, 80, 80);
        Rectangle screenBounds = new(100, 200, 70, 70);

        VisionScanRequest request = new(
            100,
            true,
            new Point(110, 210),
            2,
            combatRoi,
            0,
            240,
            0,
            280,
            150,
            240,
            130,
            240,
            screenBounds,
            true,
            true,
            false,
            false,
            true);

        ScreenFrame rightFrame = SyntheticIndicatorFrame(160, 260);
        VisionAnalysisResult right = analyzer.Scan(rightFrame, request);
        Require(right.Observation.CombatRoi == new Rectangle(120, 220, 50, 50),
            "vision ROI should be clipped to the supplied screen bounds");
        Require(right.Observation.HasIndicator && right.Observation.Direction == CombatDirection.Right,
            "a red indicator in the right half-plane should classify as right");
        Require(right.Observation.Indicator == new Point(160, 260) && right.RedProbe.MatchCount == 49,
            "vision should preserve screen coordinates and scoped red telemetry");

        VisionAnalysisResult left = analyzer.Scan(SyntheticIndicatorFrame(125, 260), request);
        Require(left.Observation.Direction == CombatDirection.Left,
            "a red indicator in the left half-plane should classify as left");

        VisionAnalysisResult top = analyzer.Scan(SyntheticIndicatorFrame(145, 245), request);
        Require(top.Observation.Direction == CombatDirection.Top,
            "a red indicator between the vertical thresholds should classify as top");

        VisionAnalysisResult markerLoss = analyzer.Scan(rightFrame, request with { MarkerFound = false });
        Require(!markerLoss.Observation.HasIndicator && markerLoss.Observation.Direction == CombatDirection.None,
            "marker loss must suppress indicator and direction output");
        Require(markerLoss.Observation.CombatRoi == combatRoi,
            "marker loss should retain the configured combat ROI for diagnostics");
    }

    private static void VisionAnalyzerUsesOriginalRoiAndStrictFlashPixel()
    {
        Rectangle raw = new(100, 220, 360, 456);
        Require(raw == new Rectangle(100, 220, 360, 456),
            "combat ROI should retain the original marker-relative size");

        var analyzer = new VisionAnalyzer();
        Rectangle combatRoi = new(100, 220, 80, 80);
        VisionScanRequest request = new(
            100,
            true,
            new Point(140, 240),
            2,
            combatRoi,
            0,
            250,
            0,
            280,
            150,
            250,
            130,
            250,
            new Rectangle(0, 0, 400, 500),
            true,
            true,
            false,
            false,
            true);

        ScreenFrame sideFlash = SyntheticClusterFrame(400, 500,
            new[] { (120, 260), (121, 260), (120, 261), (121, 261) }, 255, 154, 141);
        PaintIndicator(sideFlash, 110, 260);
        VisionAnalysisResult result = analyzer.Scan(sideFlash, request);
        Require(result.Observation.LightFlash && result.Observation.FlashClusterMatches >= 4 &&
            result.Observation.Direction == CombatDirection.Left && result.Observation.Indicator.X == 110,
            "a left indicator inside the original ROI should classify correctly");

        ScreenFrame rightSide = SyntheticClusterFrame(400, 500,
            new[] { (150, 260), (151, 260), (150, 261), (151, 261) }, 255, 154, 141);
        PaintIndicator(rightSide, 170, 260);
        VisionAnalysisResult rightResult = analyzer.Scan(rightSide, request);
        Require(rightResult.Observation.Direction == CombatDirection.Right && rightResult.Observation.Indicator.X == 170,
            "a right indicator inside the original ROI should classify correctly");

        ScreenFrame noise = SyntheticClusterFrame(400, 500,
            new[] { (120, 260), (121, 260), (120, 261) }, 255, 160, 150);
        VisionAnalysisResult belowMinimum = analyzer.Scan(noise, request);
        Require(!belowMinimum.Observation.LightFlash && belowMinimum.Observation.FlashClusterMatches == 3,
            "a near-color flash cluster must remain noise under the strict parry pixel rule");
    }

    private static void VisionAnalyzerToleratesNearFlashColour()
    {
        var analyzer = new VisionAnalyzer();
        VisionScanRequest request = new(
            100, true, new Point(140, 240), 2, new Rectangle(100, 220, 80, 80),
            0, 250, 0, 280, 150, 250, 130, 250,
            new Rectangle(0, 0, 400, 500), true, true, false, false, false);

        // Off-by-one/two shades seen in the Oct 17 training frames, none of them exact.
        ScreenFrame nearFlash = SyntheticClusterFrame(400, 500, Array.Empty<(int x, int y)>(), 0, 0, 0);
        SetPixel(nearFlash, 120, 260, 255, 153, 141);
        SetPixel(nearFlash, 121, 260, 254, 156, 142);
        SetPixel(nearFlash, 120, 261, 253, 154, 139);
        PaintIndicator(nearFlash, 110, 260);
        CombatObservation near = analyzer.Scan(nearFlash, request).Observation;
        Require(near.LightFlash && near.StrictFlashPoint == new Point(120, 260),
            "three flash pixels within 2 shades must open the live flash gate without an exact match");

        ScreenFrame twoPixels = SyntheticClusterFrame(400, 500, new[] { (120, 260), (121, 260) }, 255, 154, 141);
        CombatObservation sparse = analyzer.Scan(twoPixels, request).Observation;
        Require(!sparse.LightFlash && sparse.StrictFlashPoint == new Point(-1, -1),
            "fewer than three flash pixels must stay closed and report no flash point");

        ScreenFrame threeShadesOff = SyntheticClusterFrame(400, 500,
            new[] { (120, 260), (121, 260), (120, 261) }, 255, 157, 141);
        Require(!analyzer.Scan(threeShadesOff, request).Observation.LightFlash,
            "pixels 3 shades off the flash colour are outside the live tolerance");

        // The indicator's dark-red phase can carry flash-like pixels; the gate still blocks it.
        SetPixel(nearFlash, 130, 270, 255, 41, 34);
        CombatObservation gated = analyzer.Scan(nearFlash, request).Observation;
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(gated with { TimestampMs = 90, LightFlash = false }, ReactionCommandKind.Deflect, "F");
        CoordinatorTick tick = coordinator.Tick(gated, ReactionCommandKind.Deflect, "F");
        Require(gated.LightFlash && gated.DarkRedGate && tick.Command == null,
            "a tolerant flash beside a dark-red pixel must not be accepted");
        CoordinatorTick clean = coordinator.Tick(near with { TimestampMs = 110 }, ReactionCommandKind.Deflect, "F");
        Require(clean.Command is { Kind: ReactionCommandKind.Deflect, Direction: CombatDirection.Left },
            "the same flash without dark red must be accepted for the armed candidate");
    }

    private static void VisionAnalyzerProfilesStrictFlashAtArmedIndicator()
    {
        var analyzer = new VisionAnalyzer();
        Rectangle roi = new(0, 0, 300, 300);
        FlashTemporalBaseline baseline = VisionAnalyzer.CaptureTemporalBaseline(
            SyntheticClusterFrame(300, 300, Array.Empty<(int x, int y)>(), 0, 0, 0), roi, new Point(100, 100));
        ScreenFrame frame = SyntheticClusterFrame(300, 300,
            new[] { (110, 110), (111, 110), (110, 111), (111, 111) }, 255, 154, 141);
        VisionAnalysisResult result = analyzer.Scan(frame, new VisionScanRequest(
            100, true, new Point(150, 150), 2, roi,
            0, 100, 0, 150, 200, 100, 100, 100,
            new Rectangle(0, 0, 300, 300), true, true, false, false, true,
            TemporalBaseline: baseline));

        Require(result.Observation.LightFlash && result.Observation.StrictFlashPoint == new Point(110, 110),
            "strict flash telemetry must preserve the actual exact-pixel location");
        Require(result.Observation.IndicatorFlashClusterMatches == 4 &&
            result.Observation.IndicatorFlashClusterBounds == new Rectangle(110, 110, 2, 2),
            "calibration must profile tolerant matches only around the armed indicator");

        VisionAnalysisResult live = analyzer.Scan(frame, new VisionScanRequest(
            101, true, new Point(150, 150), 2, roi,
            0, 100, 0, 150, 200, 100, 100, 100,
            new Rectangle(0, 0, 300, 300), true, true, false, false, false));
        Require(live.Observation.LightFlash && live.Observation.FlashClusterMatches == 0 &&
            live.Observation.IndicatorFlashClusterMatches == 0,
            "normal play must retain strict flash detection without running calibration scans");
    }

    private static void PixelClustersMatchEightConnectivityAtCellSizeOne()
    {
        var pixels = new List<Point> { new(10, 10), new(11, 11), new(13, 11), new(13, 12) };
        List<PixelCluster> touching = PixelClusters.Group(pixels, 1);
        Require(touching.Count == 2 && touching.Any(c => c.PixelCount == 2 && c.First == new Point(10, 10)) &&
                touching.Any(c => c.PixelCount == 2 && c.First == new Point(13, 11)),
            "cell size 1 must join diagonal neighbours and split a one-pixel gap");

        List<PixelCluster> linked = PixelClusters.Group(pixels, 4);
        Require(linked.Count == 1 && linked[0].PixelCount == 4 &&
                linked[0].Bounds == Rectangle.FromLTRB(10, 10, 14, 13) && linked[0].Center == new Point(12, 11),
            "a gap smaller than the cell size must not split a cluster");

        List<PixelCluster> apart = PixelClusters.Group(new List<Point> { new(0, 0), new(20, 0) }, 8);
        Require(apart.Count == 2, "a gap of two cells or more must always split clusters");
    }

    private static void AnchorDetectorReportsRunnerUpMarker()
    {
        ScreenFrame frame = SyntheticClusterFrame(100, 100,
            new[] { (10, 10), (11, 10), (10, 11), (11, 11), (60, 60), (61, 60) }, 5, 131, 65);
        AnchorMarkerScanResult scan = new AnchorMarkerDetector().Scan(frame, new Rectangle(0, 0, 100, 100),
            false, default, "NONE");
        Require(scan.Found && scan.ChosenPixelCount == 4 && scan.RunnerUpPixelCount == 2 && scan.CandidateCount == 2,
            "the marker scan must report the largest losing component for contested-anchor telemetry");
    }

    private static void VisionAnalyzerOwnsOneRedIndicator()
    {
        var analyzer = new VisionAnalyzer();
        // Top band 50-150, right of x 250 / left of x 150 below y 150.
        VisionScanRequest request = new(
            100, true, new Point(200, 20), 2, new Rectangle(0, 0, 400, 400),
            0, 50, 0, 150, 250, 150, 150, 150,
            new Rectangle(0, 0, 400, 400), true, true, false, false, false);
        ScreenFrame Blank() => SyntheticClusterFrame(400, 400, Array.Empty<(int x, int y)>(), 0, 0, 0);

        ScreenFrame stray = Blank();
        SetPixel(stray, 100, 250, 255, 49, 41);
        CombatObservation strayObservation = analyzer.Scan(stray, request).Observation;
        Require(!strayObservation.HasIndicator && strayObservation.Direction == CombatDirection.None &&
                !strayObservation.RedAmbiguous,
            "a single red pixel from an effect must not count as an indicator");

        ScreenFrame cluttered = Blank();
        SetPixel(cluttered, 200, 60, 255, 49, 41);
        SetPixel(cluttered, 201, 60, 255, 49, 41);
        SetPixel(cluttered, 200, 61, 255, 49, 41);
        PaintIndicator(cluttered, 100, 250);
        CombatObservation owned = analyzer.Scan(cluttered, request).Observation;
        Require(owned.HasIndicator && owned.Direction == CombatDirection.Left &&
                owned.Indicator == new Point(100, 250) && !owned.RedAmbiguous && owned.RedClusters.Count == 1,
            "stray red above the indicator must not steal the direction from the real indicator");

        ScreenFrame fragmented = Blank();
        PaintIndicator(fragmented, 100, 250);
        PaintIndicator(fragmented, 112, 250, 2);
        CombatObservation joined = analyzer.Scan(fragmented, request).Observation;
        Require(joined.HasIndicator && !joined.RedAmbiguous && joined.RedClusters.Count == 1 &&
                joined.RedClusters[0].PixelCount == 74 && joined.Indicator == new Point(104, 250),
            "indicator pieces a few pixels apart must join into one cluster centered on both");

        ScreenFrame twoAttackers = Blank();
        PaintIndicator(twoAttackers, 100, 250);
        PaintIndicator(twoAttackers, 300, 250);
        CombatObservation ambiguous = analyzer.Scan(twoAttackers, request).Observation;
        Require(ambiguous.RedAmbiguous && !ambiguous.HasIndicator && ambiguous.Direction == CombatDirection.None &&
                ambiguous.RedClusters.Count == 2,
            "red indicators in two directions must stand down instead of picking one");
        var coordinator = new ReactionCoordinator();
        Require(coordinator.Tick(ambiguous, ReactionCommandKind.Deflect, "F").Candidate == null,
            "an ambiguous frame must not arm a candidate");

        ScreenFrame sameSide = Blank();
        PaintIndicator(sameSide, 300, 250);
        PaintIndicator(sameSide, 300, 350, 4);
        CombatObservation agreeing = analyzer.Scan(sameSide, request).Observation;
        Require(agreeing.HasIndicator && !agreeing.RedAmbiguous && agreeing.Direction == CombatDirection.Right &&
                agreeing.Indicator == new Point(300, 350),
            "two clusters in the same direction must keep the larger one");

        ScreenFrame small = Blank();
        PaintIndicator(small, 100, 250, 1);
        Require(!analyzer.Scan(small, request).Observation.HasIndicator &&
                analyzer.Scan(small, request with { ResolutionScale = 0.5 }).Observation.HasIndicator,
            "the minimum indicator size must shrink with the game resolution");
    }

    private static void VisionAnalyzerTiesFlashToOwnedIndicator()
    {
        var analyzer = new VisionAnalyzer();
        VisionScanRequest request = new(
            100, true, new Point(200, 20), 2, new Rectangle(0, 0, 400, 400),
            0, 50, 0, 150, 250, 150, 150, 150,
            new Rectangle(0, 0, 400, 400), true, true, false, false, false);
        (int x, int y)[] farFlash = { (350, 350), (351, 350), (350, 351) };
        (int x, int y)[] nearFlash = { (115, 240), (116, 240), (115, 241) };

        ScreenFrame far = SyntheticClusterFrame(400, 400, farFlash, 255, 154, 141);
        PaintIndicator(far, 100, 250);
        CombatObservation farObservation = analyzer.Scan(far, request).Observation;
        Require(farObservation.HasIndicator && !farObservation.LightFlash,
            "a flash far from the owned indicator must not open the gate");

        ScreenFrame near = SyntheticClusterFrame(400, 400, nearFlash, 255, 154, 141);
        PaintIndicator(near, 100, 250);
        CombatObservation nearObservation = analyzer.Scan(near, request).Observation;
        Require(nearObservation.LightFlash && nearObservation.StrictFlashPoint == new Point(115, 240),
            "a flash on the owned indicator must open the gate");

        // At flash time the red is gone, so the armed candidate's region decides.
        ScreenFrame flashOnly = SyntheticClusterFrame(400, 400, farFlash, 255, 154, 141);
        Require(analyzer.Scan(flashOnly, request).Observation.LightFlash,
            "with no indicator and no armed region the whole ROI still counts");
        Require(!analyzer.Scan(flashOnly, request with
                { FlashRegion = VisionAnalyzer.IndicatorRegion(new Point(100, 250), 1.0) }).Observation.LightFlash,
            "an armed candidate's region must reject a flash elsewhere");
        Require(analyzer.Scan(flashOnly, request with
                { FlashRegion = VisionAnalyzer.IndicatorRegion(new Point(340, 340), 1.0) }).Observation.LightFlash,
            "an armed candidate's region must accept its own flash");
    }

    private static void VisionAnalyzerGraceScanAcceptsFlashWithoutMarker()
    {
        var analyzer = new VisionAnalyzer();
        Rectangle cachedRoi = new(20, 20, 180, 160);
        VisionScanRequest request = new(
            500,
            false,
            new Point(100, 100),
            2,
            new Rectangle(50, 20, 100, 160),
            0,
            60,
            0,
            100,
            120,
            60,
            80,
            60,
            new Rectangle(0, 0, 240, 220),
            true,
            true,
            false,
            false,
            true,
            cachedRoi,
            100,
            CombatDirection.Left,
            true);

        ScreenFrame frame = SyntheticClusterFrame(240, 220,
            new[] { (30, 80), (31, 80), (30, 81), (31, 81) }, 255, 154, 141);
        VisionAnalysisResult grace = analyzer.Scan(frame, request);
        Require(grace.Observation.ScanMode == VisionScanMode.MarkerGrace &&
            !grace.Observation.MarkerFound && !grace.Observation.HasIndicator &&
            grace.Observation.Direction == CombatDirection.Left && grace.Observation.LightFlash &&
            grace.Observation.CombatRoi == cachedRoi && grace.Observation.MarkerLossAgeMs == 100,
            "marker-grace scan should use cached geometry for flash-only detection");

        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(400, CombatDirection.Left), ReactionCommandKind.None, "");
        CoordinatorTick graceCommand = coordinator.Tick(grace.Observation with
            { TimestampMs = 500, Direction = CombatDirection.Right },
            ReactionCommandKind.Parry, "F");
        Require(graceCommand.Command is { Kind: ReactionCommandKind.Parry, Direction: CombatDirection.Left },
            "a flash during marker grace should accept only the already armed candidate");

        VisionAnalysisResult expired = analyzer.Scan(frame, request with { MarkerLossAgeMs = 251 });
        Require(expired.Observation.ScanMode == VisionScanMode.Tracked &&
            !expired.Observation.LightFlash && expired.Observation.Direction == CombatDirection.None,
            "marker grace must stop after 250ms and cannot react to a late flash");
    }

    private static void AnchorGraceKeepsExistingCandidateFlashOnly()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(200, CombatDirection.Right), ReactionCommandKind.None, "");

        CombatObservation grace = Observation(300, CombatDirection.None, hasThreat: false) with
        {
            MarkerFound = false,
            ScanMode = VisionScanMode.AnchorGrace,
            TrackingGraceAgeMs = 50
        };
        CoordinatorTick held = coordinator.Tick(grace, ReactionCommandKind.None, "");
        Require(held.Candidate is { Direction: CombatDirection.Right },
            "anchor grace must preserve the frozen candidate through a tracking interruption");

        CoordinatorTick accepted = coordinator.Tick(grace with { TimestampMs = 310, LightFlash = true }, ReactionCommandKind.Parry, "F");
        Require(accepted.Command is { Kind: ReactionCommandKind.Parry, Direction: CombatDirection.Right },
            "anchor grace must preserve only the existing candidate for a flash");
    }

    private static void AnchorGraceDoesNotRefreshCandidateValidity()
    {
        var coordinator = new ReactionCoordinator();
        coordinator.Tick(Observation(1, CombatDirection.Right), ReactionCommandKind.None, "");

        // A missing/unconfirmed frame may keep the cached candidate visible,
        // but it must not move LastValidMs forward. The candidate therefore
        // expires at the original 250 ms boundary.
        coordinator.Tick(Observation(200, CombatDirection.None, hasThreat: false) with
        {
            MarkerFound = false,
            ScanMode = VisionScanMode.AnchorGrace,
            TrackingGraceAgeMs = 50
        }, ReactionCommandKind.None, "");
        CoordinatorTick expired = coordinator.Tick(Observation(252, CombatDirection.None, hasThreat: false) with
        {
            MarkerFound = false,
            ScanMode = VisionScanMode.AnchorGrace,
            TrackingGraceAgeMs = 102
        }, ReactionCommandKind.None, "");
        Require(expired.Candidate is null && expired.CancellationReason == "indicator-stale",
            "anchor grace must not refresh candidate validity");
    }

    private static void AnchorMarkerDetectorRanksNearbyCandidates()
    {
        var detector = new AnchorMarkerDetector();
        ScreenFrame initial = SyntheticClusterFrame(320, 220,
            new[] { (180, 100), (181, 100), (182, 100), (180, 101), (181, 101), (182, 101),
                (180, 102), (181, 102), (182, 102), (25, 25) }, 5, 131, 65);
        AnchorMarkerScanResult first = detector.Scan(initial, new Rectangle(0, 0, 320, 220),
            previousFound: false, previousAnchor: Point.Empty, previousKind: "NONE");
        Require(first.Found && first.X == 180 && first.Y == 100 &&
                first.CandidateCount == 2 && first.ChosenPixelCount == 9 &&
                first.SelectionReason == "initial-largest-component",
            "initial marker scan must choose the largest exact-color component");

        ScreenFrame nearby = SyntheticClusterFrame(320, 220,
            new[] { (188, 106), (189, 106), (190, 106), (188, 107), (189, 107), (190, 107),
                (188, 108), (189, 108), (190, 108),
                (35, 35) }, 5, 131, 65);
        AnchorMarkerScanResult local = detector.Scan(nearby, new Rectangle(0, 0, 320, 220),
            previousFound: true, previousAnchor: new Point(181, 101), previousKind: "GREEN");
        Require(local.Found && local.X == 188 && local.Y == 106 &&
                local.SelectionReason == "near-accepted-marker",
            "a nearby accepted marker must beat a one-pixel distant decorative component");

        ScreenFrame moved = SyntheticClusterFrame(320, 220,
            new[] { (181, 101), (270, 160), (271, 160), (270, 161), (271, 161) }, 255, 255, 10);
        AnchorMarkerScanResult reacquired = detector.Scan(moved, new Rectangle(0, 0, 320, 220),
            previousFound: true, previousAnchor: new Point(181, 101), previousKind: "GREEN");
        Require(reacquired.Found && reacquired.Kind == "YELLOW" &&
                reacquired.SelectionReason == "global-reacquisition",
            "a large marker movement must beat a nearby decorative match during global reacquisition");

        ScreenFrame ambiguous = SyntheticClusterFrame(320, 220,
            new[] { (181, 101), (182, 101), (181, 102),
                (270, 160), (271, 160), (270, 161), (271, 161), (270, 162) },
            255, 255, 10);
        AnchorMarkerScanResult protectedNearby = detector.Scan(ambiguous,
            new Rectangle(0, 0, 320, 220), previousFound: true,
            previousAnchor: new Point(181, 101), previousKind: "GREEN");
        Require(protectedNearby.Found && protectedNearby.X == 181 && protectedNearby.Y == 101 &&
                protectedNearby.SelectionReason == "near-accepted-marker",
            "a distant 5-pixel component must not replace an established 3-pixel marker");
    }

    private static void TemporalFlashCalibrationExcludesArmedIndicator()
    {
        var analyzer = new VisionAnalyzer();
        Rectangle roi = new(0, 0, 300, 300);
        ScreenFrame baselineFrame = SyntheticClusterFrame(300, 300, Array.Empty<(int x, int y)>(), 0, 0, 0);
        FlashTemporalBaseline baseline = VisionAnalyzer.CaptureTemporalBaseline(baselineFrame, roi, new Point(100, 100));

        ScreenFrame frame = SyntheticClusterFrame(300, 300,
            new[] { (100, 100), (101, 100), (100, 101), (101, 101), (250, 250), (251, 250), (250, 251), (251, 251) },
            255, 230, 180);
        VisionScanRequest request = new(
            100, true, new Point(150, 150), 2, roi,
            0, 100, 0, 150, 200, 100, 100, 100,
            new Rectangle(0, 0, 300, 300), true, true, false, false, true,
            TemporalBaseline: baseline);
        VisionAnalysisResult result = analyzer.Scan(frame, request);
        Require(result.Observation.TemporalFlashMatches == 4,
            "temporal calibration must ignore the armed red-indicator area and keep the external bloom");
        Require(!result.Observation.LightFlash,
            "temporal calibration remains diagnostic-only until explicitly activated");

        VisionAnalysisResult unchanged = analyzer.Scan(frame, request);
        Require(unchanged.Observation.TemporalFlashMatches == 0,
            "temporal calibration must compare against the immediately previous frame, not the arm frame");
    }

    private static void AnchorTrackerConfirmsNewMarkerAfterTwoSamples()
    {
        var geometry = new CombatGeometry();
        var tracker = new AnchorTracker();
        AnchorTracking first = tracker.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, geometry, 1000, () => false);
        Require(!tracker.Found && tracker.Kind == "NONE" && !first.BecameFound && !first.BecameLost,
            "a single new sample must stay pending without changing anchor state");

        AnchorTracking second = tracker.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, geometry, 1100, () => false);
        Require(tracker.Found && tracker.Kind == "GREEN" && second.BecameFound,
            "a repeated sample must confirm the new anchor");
        Require(geometry.Ax == 100 && geometry.Ay == 100 && geometry.Box == 2,
            "confirmation must apply the accepted anchor geometry");
    }

    private static void AnchorTrackerHoldsThroughSingleFrameHole()
    {
        var geometry = new CombatGeometry();
        var tracker = new AnchorTracker();
        tracker.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, geometry, 1000, () => false);
        tracker.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, geometry, 1100, () => false);
        Require(tracker.Found, "anchor must be found after confirmation");

        tracker.Observe(true, 100, 100, 2, false, 0, 0, "NONE", 2, geometry, 2000, () => false);
        Require(tracker.Found, "a one-frame hole must not drop the anchor");

        AnchorTracking lost = tracker.Observe(true, 100, 100, 2, false, 0, 0, "NONE", 2, geometry, 3000, () => false);
        Require(!tracker.Found && tracker.Kind == "NONE" && lost.BecameLost,
            "a sustained loss must drop the anchor with a lost edge");
    }

    private static void AnchorTrackerJumpArmsGraceOnlyWithLiveCandidate()
    {
        var liveGeometry = new CombatGeometry();
        var live = new AnchorTracker();
        live.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, liveGeometry, 1000, () => true);
        live.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, liveGeometry, 1100, () => true);
        live.Observe(true, 100, 100, 2, true, 200, 100, "GREEN", 2, liveGeometry, 1200, () => true);
        AnchorTracking jumped = live.Observe(true, 100, 100, 2, true, 200, 100, "GREEN", 2, liveGeometry, 1300, () => true);
        Require(jumped.Jumped && jumped.Distance == 100 && live.AnchorGraceActive,
            "a large anchor jump with a live candidate must arm the missing grace window");

        var idleGeometry = new CombatGeometry();
        var idle = new AnchorTracker();
        idle.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, idleGeometry, 1000, () => false);
        idle.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, idleGeometry, 1100, () => false);
        idle.Observe(true, 100, 100, 2, true, 200, 100, "GREEN", 2, idleGeometry, 1200, () => false);
        AnchorTracking idleJumped = idle.Observe(true, 100, 100, 2, true, 200, 100, "GREEN", 2, idleGeometry, 1300, () => false);
        Require(idleJumped.Jumped && !idle.AnchorGraceActive,
            "a large anchor jump without a live candidate must not arm the grace window");
    }

    private static void AnchorTrackerBoxFlipDetected()
    {
        var geometry = new CombatGeometry();
        var tracker = new AnchorTracker();
        tracker.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, geometry, 1000, () => false);
        tracker.Observe(false, 0, 0, 0, true, 100, 100, "GREEN", 2, geometry, 1100, () => false);
        tracker.Observe(true, 100, 100, 2, true, 100, 100, "GREEN", 1, geometry, 1200, () => false);
        AnchorTracking flipped = tracker.Observe(true, 100, 100, 2, true, 100, 100, "GREEN", 1, geometry, 1300, () => false);
        Require(flipped.BoxFlipped && flipped.OldBox == 2 && geometry.Box == 1,
            "a confirmed box change must report the flip from the previous box");
    }

    private static void AnchorJumpPayloadKeepsLegacyFieldNames()
    {
        string json = JsonSerializer.Serialize(BotCore.AnchorJumpPayload(1000, 500, 100, -20, 100, 2));
        Require(json.Contains("\"deltaX\":100") && json.Contains("\"deltaY\":-20") && json.Contains("\"distance\":100"),
            "the anchor-jump payload must keep the lowercase deltaX/deltaY/distance field names");
        Require(!json.Contains("DeltaX") && !json.Contains("DeltaY") && !json.Contains("Distance"),
            "the anchor-jump payload must not use capitalized shorthand property names");
    }

    private static void CombatGeometryResolutionAndRoi()
    {
        var geometry = new CombatGeometry();
        geometry.UpdateResolution(1920, 1080);
        Require(geometry.B55 == 1 && geometry.Y55 == 1 && geometry.X8 == 860 && geometry.Y9 == 425,
            "resolution update must derive scalers and scan regions");
        Require(geometry.AnchorScan() == new Rectangle(860, 80, 215, 345),
            "anchor scan must match the calibrated 1080p region");

        geometry.ApplyMarker(1000, 500, "GREEN", 1);
        Require(geometry.CombatRoi() == new Rectangle(786, 510, 405, 218),
            "accepted marker must derive the padded combat ROI");
    }

    private static void VisionTrackingSnapshotPublishesCoherentGeometry()
    {
        var geometry = new CombatGeometry();
        geometry.UpdateResolution(1920, 1080);
        geometry.ApplyMarker(1000, 500, "GREEN", 1);
        VisionTrackingSnapshot first = VisionTrackingSnapshot.From(geometry, true, "GREEN",
            timestampMs: 10, version: 1, AnchorMarkerScanResult.Empty());

        geometry.UpdateResolution(2560, 1440);
        geometry.ApplyMarker(1300, 650, "YELLOW", 2);
        VisionTrackingSnapshot second = VisionTrackingSnapshot.From(geometry, true, "YELLOW",
            timestampMs: 20, version: 2, AnchorMarkerScanResult.Empty());
        (RectangleF top, RectangleF right, RectangleF left) = geometry.Zones(geometry.CombatRoi());

        Require(first.Version == 1 && first.MarkerKind == "GREEN" && first.Anchor == new Point(1000, 500) &&
                first.CombatRoi == new Rectangle(786, 510, 405, 218),
            "the first tracking snapshot must retain one complete resolution/anchor state");
        Require(second.Version == 2 && second.MarkerKind == "YELLOW" && second.Anchor == new Point(1300, 650) &&
                second.CombatRoi == geometry.CombatRoi() && second.TopZone == top &&
                second.RightZone == right && second.LeftZone == left,
            "a resolution change must publish one coherent replacement snapshot");
    }

    private static ScreenFrame SyntheticIndicatorFrame(int screenX, int screenY)
    {
        var frame = new ScreenFrame
        {
            Width = 100,
            Height = 100,
            Stride = 400,
            OriginX = 100,
            OriginY = 200,
            Buffer = new byte[40000]
        };
        PaintIndicator(frame, screenX, screenY);
        return frame;
    }

    /// <summary>
    /// Paints a square of indicator red centered on a screen point. The
    /// default 7x7 (49 pixels) is above the analyzer's 24-pixel minimum.
    /// </summary>
    private static void PaintIndicator(ScreenFrame frame, int screenX, int screenY, int half = 3)
    {
        for (int y = screenY - half; y <= screenY + half; y++)
        {
            for (int x = screenX - half; x <= screenX + half; x++)
            {
                int localX = x - frame.OriginX;
                int localY = y - frame.OriginY;
                if (localX < 0 || localY < 0 || localX >= frame.Width || localY >= frame.Height) continue;
                int offset = localY * frame.Stride + localX * 4;
                frame.Buffer[offset] = 41;
                frame.Buffer[offset + 1] = 49;
                frame.Buffer[offset + 2] = 255;
            }
        }
    }

    private static ScreenFrame SyntheticClusterFrame(int width, int height, (int x, int y)[] pixels,
        int red, int green, int blue)
    {
        var frame = new ScreenFrame
        {
            Width = width,
            Height = height,
            Stride = width * 4,
            Buffer = new byte[width * height * 4]
        };
        foreach ((int x, int y) in pixels)
        {
            int offset = y * frame.Stride + x * 4;
            frame.Buffer[offset] = (byte)blue;
            frame.Buffer[offset + 1] = (byte)green;
            frame.Buffer[offset + 2] = (byte)red;
            frame.Buffer[offset + 3] = 255;
        }
        return frame;
    }

    private static ScreenFrame SyntheticImpactFrame(int width, int height, int brightPixels,
        int red, int green, int blue)
    {
        var frame = new ScreenFrame
        {
            Width = width,
            Height = height,
            Stride = width * 4,
            Buffer = new byte[width * height * 4]
        };
        Rectangle region = ParryConfirmationTracker.NormalizedRegion(width, height);
        int count = Math.Min(Math.Max(0, brightPixels), region.Width * region.Height);
        for (int i = 0; i < count; i++)
        {
            int x = region.Left + i % region.Width;
            int y = region.Top + i / region.Width;
            int offset = y * frame.Stride + x * 4;
            frame.Buffer[offset] = (byte)blue;
            frame.Buffer[offset + 1] = (byte)green;
            frame.Buffer[offset + 2] = (byte)red;
            frame.Buffer[offset + 3] = 255;
        }
        return frame;
    }
}
