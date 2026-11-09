using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.SequenceStatus;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class SequenceStatusTests
{
    private static readonly RemoteQueueDestination Destination = new()
    {
        ServerUrl = "https://psf.example/",
        CatalogId = "review",
        CredentialReference = "PSFGuard.Nina.Plugin/test",
    };

    [Fact]
    public async Task ClientPostsTheContractShapeWithSyncHeaders()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(async request =>
            {
                captured = request;
                body = await request.Content!.ReadAsStringAsync();
                return Json("""{"success":true,"data":{"accepted":true,"received_at_ms":1800000000500},"error":null}""");
            })),
            new Uri("https://psf.example/base"),
            "secret");

        var response = await client.SendSequenceStatusAsync(
            new SequenceStatusReport
            {
                SessionId = "0f0c6f1e-6a43-4f43-9f0e-3b8c3b8f2a11",
                ReportedAtMs = 1_800_000_000_000,
                Running = true,
                Operation = "M 31 › Imaging › Smart Exposure",
                Activity = new SequenceActivityReport
                {
                    Kind = SequenceActivityKinds.Exposing,
                    Name = "Smart Exposure",
                    StartedMs = 1_799_999_880_000,
                    TargetName = "M 31",
                    Filter = "L",
                    ExposureSeconds = 300,
                    Completed = 12,
                    Total = 30,
                },
                Recent = new SequenceRecentEvents
                {
                    ImageSavedMs = 1_799_999_870_000,
                    DitherMs = 1_799_999_600_000,
                    AutofocusMs = 1_799_990_000_000,
                },
            },
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(1_800_000_000_500, response.ReceivedAtMs);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://psf.example/base/api/sync/v1/sequence-status", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer secret", captured.Headers.Authorization!.ToString());
        Assert.True(captured.Headers.Contains(PsfGuardSyncClient.PluginVersionHeader));
        Assert.True(captured.Headers.Contains(PsfGuardSyncClient.PluginCapabilitiesHeader));

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal(
            ["session_id", "reported_at_ms", "running", "fresh_for_ms", "operation", "activity", "recent"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(45_000, root.GetProperty("fresh_for_ms").GetInt32());
        var activity = root.GetProperty("activity");
        Assert.Equal(
            ["kind", "name", "started_ms", "until_ms", "target_name", "filter", "exposure_seconds", "completed", "total", "target_temperature_c"],
            activity.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("exposing", activity.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, activity.GetProperty("until_ms").ValueKind);
        Assert.Equal(300, activity.GetProperty("exposure_seconds").GetDouble());
        var recent = root.GetProperty("recent");
        Assert.Equal(
            ["image_saved_ms", "dither_ms", "autofocus_ms", "meridian_flip_ms"],
            recent.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, recent.GetProperty("meridian_flip_ms").ValueKind);
    }

    [Fact]
    public async Task ClientReadsAcceptedFalseAndRefusesAnAnswerWithoutIt()
    {
        var answer = """{"success":true,"data":{"accepted":false,"received_at_ms":1},"error":null}""";
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(Json(answer)))),
            new Uri("https://psf.example/"),
            "secret");
        var report = new SequenceStatusReport { SessionId = "s", ReportedAtMs = 1, Running = false };

        Assert.False((await client.SendSequenceStatusAsync(report, CancellationToken.None)).Accepted);

        answer = """{"success":true,"data":{"received_at_ms":1},"error":null}""";
        await Assert.ThrowsAsync<JsonException>(() => client.SendSequenceStatusAsync(report, CancellationToken.None));
    }

    [Theory]
    [InlineData("""["merge","sequence_status_v1"]""", true)]
    [InlineData("""["merge","star_detection_profile_v1"]""", false)]
    public async Task ClientChecksTheSequenceStatusCapability(string capabilities, bool expected)
    {
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(request =>
            {
                Assert.Equal("/api/sync/v1/capabilities", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Json(
                    $$"""{"protocol_version":1,"product":"psf-guard","product_version":"0.13.0","capabilities":{{capabilities}},"catalogs":[]}"""));
            })),
            new Uri("https://psf.example/"),
            "secret");

        Assert.Equal(expected, await client.SupportsSequenceStatusAsync(CancellationToken.None));
    }

    [Fact]
    public void NamesLoseControlCharactersAndStopAtEightyCharacters()
    {
        Assert.Equal("Smart Exposure", SequenceText.Clean("  Smart Exp\u0007osure\r\n "));
        Assert.Null(SequenceText.Clean(" \r\n "));
        Assert.Null(SequenceText.Clean(null));
        Assert.Equal(new string('a', 80), SequenceText.Clean(new string('a', 120)));
    }

    [Fact]
    public void OperationMatchesTheDirectorPlugin()
    {
        Assert.Equal(
            "Start Sequence › Cool Camera",
            SequenceText.Path(["Cool Camera", "Start Sequence"], "CoolCamera"));
        Assert.Equal(
            "B › C › D › E",
            SequenceText.Path(["E", "", "D", "C", "B", "A"], "Leaf"));
        Assert.Equal("WaitForTimeSpan", SequenceText.Path([" ", null], "WaitForTimeSpan"));

        Assert.Equal(
            "Start Sequence › Connect Equipment · Start Sequence › Cool Camera",
            SequenceText.Operation(["Start Sequence › Cool Camera", "Start Sequence › Connect Equipment", "Start Sequence › Cool Camera"]));
        Assert.Equal("a · b · c · d · More instructions running", SequenceText.Operation(["e", "d", "c", "b", "a"]));
        Assert.Null(SequenceText.Operation([]));
    }

    [Theory]
    [InlineData("exposing", "TakeExposure")]
    [InlineData("exposing", "SmartExposure")]
    [InlineData("exposing", "TakeManyExposures")]
    [InlineData("exposing", "PlanTakeExposure", "TakeExposure", "SequenceItem")]
    [InlineData("autofocus", "RunAutofocus")]
    [InlineData("dithering", "Dither")]
    [InlineData("guiding", "StartGuiding")]
    [InlineData("mount_slew", "SlewScopeToRaDec")]
    [InlineData("mount_center", "CenterAndRotate")]
    [InlineData("plate_solve", "SolveAndSync")]
    [InlineData("camera_cooling", "CoolCamera")]
    [InlineData("camera_warming", "WarmCamera")]
    [InlineData("time_wait", "WaitForTime")]
    [InlineData("time_wait", "WaitForTimeSpan")]
    [InlineData("astronomical_wait", "WaitForAltitude")]
    [InlineData("astronomical_wait", "WaitUntilAboveHorizon")]
    [InlineData("safety_wait", "WaitUntilSafe")]
    [InlineData("condition_wait", "WaitUntil")]
    [InlineData("manual_wait", "WaitIndefinitely")]
    [InlineData("manual_wait", "Break")]
    [InlineData("other", "SwitchFilter", "SequenceItem")]
    public void InstructionsMapToKinds(string kind, params string[] typeNames) =>
        Assert.Equal(kind, SequenceActivityKinds.ForInstruction(typeNames));

    [Theory]
    [InlineData("meridian_flip", "MeridianFlipTrigger")]
    [InlineData("autofocus", "AutofocusAfterExposures")]
    [InlineData("autofocus", "AutofocusAfterHFRIncreaseTrigger")]
    [InlineData("dithering", "DitherAfterExposures")]
    [InlineData("mount_center", "CenterAfterDriftTrigger")]
    [InlineData("guiding", "RestoreGuiding")]
    [InlineData("other", "SomePluginTrigger", "SequenceTrigger")]
    public void TriggersMapToKinds(string kind, params string[] typeNames) =>
        Assert.Equal(kind, SequenceActivityKinds.ForTrigger(typeNames));

    [Fact]
    public void EventLogKeepsTheLatestTimeAndOnlyThisSessionsEvents()
    {
        var log = new SequenceEventLog();
        log.ImageSaved(DateTimeOffset.FromUnixTimeMilliseconds(2_000));
        log.ImageSaved(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        log.Dithered(DateTimeOffset.FromUnixTimeMilliseconds(500));
        log.MeridianFlipFinished(DateTimeOffset.FromUnixTimeMilliseconds(1_500));

        Assert.Equal(
            new SequenceRecentEvents { ImageSavedMs = 2_000, MeridianFlipMs = 1_500 },
            log.Since(1_000));
    }

    [Fact]
    public async Task NothingIsSentWithoutADestinationOrASequence()
    {
        var harness = new Harness();

        Assert.Equal(SequenceStatusTickResult.Idle, await harness.TickAsync(null, Running(Exposure())));
        Assert.Equal(SequenceStatusTickResult.Idle, await harness.TickAsync(Destination, Stopped()));
        Assert.Equal(SequenceStatusTickResult.Idle, await harness.TickAsync(Destination, null));

        Assert.Equal(0, harness.CapabilityChecks);
        Assert.Empty(harness.Sent);
    }

    [Fact]
    public async Task ASequenceRunSendsChangesHeartbeatsAndOneFinalReport()
    {
        var harness = new Harness();
        var exposure = Exposure(completed: 1);

        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(exposure)));
        var first = Assert.Single(harness.Sent);
        Assert.True(Guid.TryParseExact(first.SessionId, "D", out _));
        Assert.True(first.Running);
        Assert.Equal("Imaging › Smart Exposure", first.Operation);
        Assert.Equal(harness.NowMs, first.ReportedAtMs);
        Assert.Equal(harness.NowMs, first.Activity!.StartedMs);

        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Unchanged, await harness.TickAsync(Destination, Running(exposure)));

        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(exposure with { Completed = 2 })));
        Assert.Equal(2, harness.Sent[^1].Activity!.Completed);
        Assert.Equal(first.Activity.StartedMs, harness.Sent[^1].Activity!.StartedMs);

        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(exposure with { Completed = 2 })));
        Assert.Equal(3, harness.Sent.Count);

        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Stopped()));
        var last = harness.Sent[^1];
        Assert.False(last.Running);
        Assert.Null(last.Operation);
        Assert.Null(last.Activity);
        Assert.Equal(first.SessionId, last.SessionId);
        Assert.Null(harness.Reporter.SessionId);

        harness.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(SequenceStatusTickResult.Idle, await harness.TickAsync(Destination, Stopped()));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.NotEqual(first.SessionId, harness.Sent[^1].SessionId);
        Assert.Equal(1, harness.CapabilityChecks);
    }

    [Fact]
    public async Task ANewInstructionRestartsItsStartTime()
    {
        var harness = new Harness();
        var startedAt = harness.NowMs;
        await harness.TickAsync(Destination, Running(Exposure()));
        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.TickAsync(Destination, Running(Exposure(completed: 3)));
        Assert.Equal(startedAt, harness.Sent[^1].Activity!.StartedMs);

        harness.Advance(TimeSpan.FromSeconds(5));
        await harness.TickAsync(Destination, Running(new SequenceActivityObservation(
            new object(), SequenceActivityKinds.Dithering, "Dither")));
        Assert.Equal(harness.NowMs, harness.Sent[^1].Activity!.StartedMs);
    }

    [Fact]
    public async Task WaitsReportAStableExpectedEnd()
    {
        var harness = new Harness();
        var wait = new object();
        var startedAt = harness.NowMs;
        await harness.TickAsync(Destination, Running(new SequenceActivityObservation(
            wait, SequenceActivityKinds.TimeWait, "Wait for Time Span", RunsFor: TimeSpan.FromMinutes(10))));
        Assert.Equal(startedAt + 600_000, harness.Sent[^1].Activity!.UntilMs);

        var clock = new object();
        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.TickAsync(Destination, Running(new SequenceActivityObservation(
            clock, SequenceActivityKinds.TimeWait, "Wait for Time", UntilMs: 5_000_000)));
        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Unchanged, await harness.TickAsync(Destination, Running(new SequenceActivityObservation(
            clock, SequenceActivityKinds.TimeWait, "Wait for Time", UntilMs: 5_000_900))));
        Assert.Equal(5_000_000, harness.Sent[^1].Activity!.UntilMs);

        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.TickAsync(Destination, Running(new SequenceActivityObservation(
            clock, SequenceActivityKinds.TimeWait, "Wait for Time", UntilMs: 5_060_000)));
        Assert.Equal(5_060_000, harness.Sent[^1].Activity!.UntilMs);
    }

    [Fact]
    public async Task OnlyFieldsForTheKindAreSentAndNamesAreCleaned()
    {
        var harness = new Harness();
        await harness.TickAsync(Destination, Running(new SequenceActivityObservation(
            new object(),
            SequenceActivityKinds.CameraCooling,
            "Cool\nCamera",
            UntilMs: 99,
            TargetName: " M 31 ",
            Filter: "Ha",
            ExposureSeconds: 300,
            Completed: 1,
            Total: 2,
            TargetTemperatureC: -10)));

        Assert.Equal(
            new SequenceActivityReport
            {
                Kind = SequenceActivityKinds.CameraCooling,
                Name = "CoolCamera",
                StartedMs = harness.NowMs,
                TargetName = "M 31",
                TargetTemperatureC = -10,
            },
            harness.Sent[^1].Activity);
    }

    [Fact]
    public async Task RecentEventsStartWithTheSession()
    {
        var harness = new Harness();
        harness.Events.ImageSaved(harness.Clock.GetUtcNow().AddMinutes(-5));
        await harness.TickAsync(Destination, Running(Exposure()));
        Assert.Equal(new SequenceRecentEvents(), harness.Sent[^1].Recent);

        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Events.Dithered(harness.Clock.GetUtcNow());
        await harness.TickAsync(Destination, Running(Exposure()));
        Assert.Equal(new SequenceRecentEvents { DitherMs = harness.NowMs }, harness.Sent[^1].Recent);
    }

    [Fact]
    public async Task AServerWithoutTheCapabilityIsAskedAgainOnlyWhenASequenceStarts()
    {
        var harness = new Harness { Supported = false };

        Assert.Equal(SequenceStatusTickResult.Unsupported, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(SequenceStatusTickResult.Unsupported, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(SequenceStatusTickResult.Idle, await harness.TickAsync(Destination, Stopped()));
        Assert.Equal(1, harness.CapabilityChecks);
        Assert.Empty(harness.Sent);
        Assert.Single(harness.Log);

        harness.Supported = true;
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(2, harness.CapabilityChecks);
    }

    [Fact]
    public async Task ANewDestinationChecksCapabilitiesAndStartsANewSession()
    {
        var harness = new Harness();
        await harness.TickAsync(Destination, Running(Exposure()));
        var other = Destination with { CatalogId = "other" };

        harness.Advance(TimeSpan.FromSeconds(1));
        await harness.TickAsync(other, Running(Exposure()));

        Assert.Equal(2, harness.CapabilityChecks);
        Assert.Equal([Destination, other], harness.Destinations);
        Assert.NotEqual(harness.Sent[0].SessionId, harness.Sent[1].SessionId);
    }

    [Fact]
    public async Task AFailureWaitsFifteenSecondsAndIsLoggedOnce()
    {
        var harness = new Harness { SendStatus = HttpStatusCode.ServiceUnavailable };

        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(SequenceStatusTickResult.Waiting, await harness.TickAsync(Destination, Running(Exposure(completed: 2))));
        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure(completed: 2))));
        Assert.Equal(2, harness.Attempts);
        var line = Assert.Single(harness.Log);
        Assert.Contains("HTTP 503", line);

        harness.SendStatus = HttpStatusCode.OK;
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure(completed: 2))));
        harness.SendStatus = HttpStatusCode.BadGateway;
        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure(completed: 3))));
        Assert.Single(harness.Log);
    }

    [Fact]
    public async Task AnUnsentFinalReportIsRetriedWithTheSameSession()
    {
        var harness = new Harness();
        await harness.TickAsync(Destination, Running(Exposure()));
        var session = harness.Sent[^1].SessionId;

        harness.SendStatus = HttpStatusCode.BadGateway;
        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Stopped()));

        harness.SendStatus = HttpStatusCode.OK;
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Stopped()));
        Assert.False(harness.Sent[^1].Running);
        Assert.Equal(session, harness.Sent[^1].SessionId);
    }

    public static TheoryData<string> UnusableCapabilityAnswers() => ["unreadable", "no list", "404", "405"];

    [Theory]
    [MemberData(nameof(UnusableCapabilityAnswers))]
    public async Task AnUnusableCapabilityAnswerTurnsReportingOffUntilPairingChanges(string failure)
    {
        var harness = new Harness
        {
            CapabilityFailure = failure switch
            {
                "unreadable" => _ => throw new JsonException("not JSON"),
                "no list" => _ => throw new InvalidDataException("no capabilities list"),
                "404" => _ => throw new HttpRequestException("gone", null, HttpStatusCode.NotFound),
                _ => _ => throw new HttpRequestException("gone", null, HttpStatusCode.MethodNotAllowed),
            },
        };

        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure())));
        for (var tick = 0; tick < 5; tick++)
        {
            harness.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure(completed: tick))));
        }

        // A new sequence does not ask again either.
        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Stopped()));
        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(1, harness.CapabilityChecks);
        Assert.Equal(0, harness.Attempts);
        Assert.Contains("are off", Assert.Single(harness.Log));

        harness.CapabilityFailure = null;
        harness.Reporter.RequestRecheck();
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(2, harness.CapabilityChecks);
    }

    public static TheoryData<string> PassingCapabilityFailures() =>
        ["timeout", "no status", "503", "401", "403", "credential"];

    [Theory]
    [MemberData(nameof(PassingCapabilityFailures))]
    public async Task APassingCapabilityFailureIsRetriedAfterTheWait(string failure)
    {
        var harness = new Harness(requestTimeout: TimeSpan.FromMilliseconds(50))
        {
            CapabilityFailure = failure switch
            {
                "timeout" => _ => new TaskCompletionSource().Task,
                "no status" => _ => throw new HttpRequestException("connection refused"),
                "503" => _ => throw new HttpRequestException("restarting", null, HttpStatusCode.ServiceUnavailable),
                "401" => _ => throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
                "403" => _ => throw new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden),
                _ => _ => throw new InvalidOperationException("credential missing"),
            },
        };

        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(SequenceStatusTickResult.Waiting, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(3, harness.CapabilityChecks);
        Assert.Equal(0, harness.Attempts);
        var line = Assert.Single(harness.Log);
        Assert.DoesNotContain("are off", line);
        if (failure is "401" or "403" or "503")
        {
            Assert.Contains($"HTTP {failure}", line);
        }

        // The link comes back: the next check succeeds and reports flow.
        harness.CapabilityFailure = null;
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(4, harness.CapabilityChecks);
        harness.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure(completed: 2))));
        Assert.Equal(4, harness.CapabilityChecks);
        Assert.Single(harness.Log);
    }

    [Fact]
    public async Task AnUnsuccessfulCapabilitiesEnvelopeIsAPassingFailure()
    {
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(Json(
                """{"success":false,"data":null,"error":"scheduler sync is off"}""")))),
            new Uri("https://psf.example/"),
            "secret");

        var exception = await Record.ExceptionAsync(() => client.SupportsSequenceStatusAsync(CancellationToken.None));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(SequenceStatusReporter.IsRouteUnavailable(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task APassingSendFailureIsRetriedAfterTheWait(HttpStatusCode status)
    {
        var harness = new Harness { SendStatus = status };

        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        harness.SendStatus = HttpStatusCode.OK;
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.Equal(1, harness.CapabilityChecks);
        Assert.Single(harness.Log);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task ARouteThatAnswers404Or405TurnsReportingOffForThatDestination(HttpStatusCode status)
    {
        var harness = new Harness { SendStatus = status };

        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure(completed: 2))));
        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Stopped()));
        Assert.Equal(1, harness.Attempts);
        Assert.Contains($"HTTP {(int)status}", Assert.Single(harness.Log));

        // Another server or catalog starts fresh.
        harness.SendStatus = HttpStatusCode.OK;
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination with { CatalogId = "other" }, Running(Exposure())));
    }

    [Fact]
    public async Task AnUnreadableAnswerTurnsReportingOff()
    {
        var harness = new Harness { SendFailure = _ => throw new JsonException("missing accepted") };

        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure())));
        harness.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(SequenceStatusTickResult.Disabled, await harness.TickAsync(Destination, Running(Exposure(completed: 2))));
        Assert.Equal(1, harness.Attempts);
    }

    [Fact]
    public async Task ASlowReportTimesOutAndIsRetriedLater()
    {
        var harness = new Harness(requestTimeout: TimeSpan.FromMilliseconds(50))
        {
            // Ignores its token, so only the reporter's own bound ends the wait.
            SendFailure = _ => new TaskCompletionSource().Task,
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(SequenceStatusTickResult.Failed, await harness.TickAsync(Destination, Running(Exposure())));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));

        harness.SendFailure = null;
        harness.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SequenceStatusTickResult.Sent, await harness.TickAsync(Destination, Running(Exposure())));
    }

    [Fact]
    public async Task OnlyTheCallersCancellationEscapes()
    {
        var harness = new Harness { SendFailure = token => Task.Delay(Timeout.Infinite, token) };
        using var stop = new CancellationTokenSource();
        var tick = harness.Reporter.TickAsync(Destination, Running(Exposure()), harness.Clock.GetUtcNow(), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
    }

    [Theory]
    [InlineData("""{"capabilities":["sequence_status_v1"]}""", true)]
    [InlineData("""{"success":true,"data":{"capabilities":[1,null,{"x":1},"sequence_status_v1"],"future_field":{"a":[1]}}}""", true)]
    [InlineData("""{"protocol_version":1,"capabilities":["merge"],"catalogs":"not a list"}""", false)]
    public async Task TheCapabilityCheckReadsOnlyTheCapabilitiesList(string body, bool expected)
    {
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(Json(body)))),
            new Uri("https://psf.example/"),
            "secret");

        Assert.Equal(expected, await client.SupportsSequenceStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"protocol_version":1,"catalogs":[]}""", typeof(InvalidDataException))]
    [InlineData("""{"capabilities":"sequence_status_v1"}""", typeof(InvalidDataException))]
    [InlineData("""<html>Not Found</html>""", typeof(JsonException))]
    public async Task ACapabilitiesAnswerWithoutAListIsUnreadable(string body, Type expected)
    {
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(Json(body)))),
            new Uri("https://psf.example/"),
            "secret");

        var exception = await Record.ExceptionAsync(() => client.SupportsSequenceStatusAsync(CancellationToken.None));
        Assert.IsAssignableFrom(expected, exception);
        Assert.True(SequenceStatusReporter.IsRouteUnavailable(exception!));
    }

    private static SequenceActivityObservation Exposure(int completed = 1) => new(
        ExposureIdentity,
        SequenceActivityKinds.Exposing,
        "Smart Exposure",
        TargetName: "M 31",
        Filter: "L",
        ExposureSeconds: 300,
        Completed: completed,
        Total: 30);

    private static readonly object ExposureIdentity = new();

    private static SequenceObservation Running(SequenceActivityObservation activity) =>
        new(true, "Imaging › Smart Exposure", activity);

    private static SequenceObservation Stopped() => new(false, null, null);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class Harness
    {
        public Harness(TimeSpan? requestTimeout = null)
        {
            Reporter = new SequenceStatusReporter(
                async (destination, token) =>
                {
                    CapabilityChecks++;
                    if (CapabilityFailure is { } failure)
                    {
                        await failure(token);
                    }

                    return Supported;
                },
                async (destination, report, token) =>
                {
                    Attempts++;
                    if (SendFailure is { } failure)
                    {
                        await failure(token);
                    }

                    if (SendStatus != HttpStatusCode.OK)
                    {
                        throw new HttpRequestException("refused", null, SendStatus);
                    }

                    Sent.Add(report);
                    Destinations.Add(destination);
                    return new SequenceStatusResponse { Accepted = true };
                },
                Events,
                Log.Add,
                Clock,
                requestTimeout ?? SequenceStatusReporter.RequestTimeout);
        }

        /// <summary>Runs before the capability answer; throw or hang to simulate a failure.</summary>
        public Func<CancellationToken, Task>? CapabilityFailure { get; set; }

        /// <summary>Runs before the send; throw or hang to simulate a failure.</summary>
        public Func<CancellationToken, Task>? SendFailure { get; set; }

        public FakeClock Clock { get; } = new();

        public SequenceEventLog Events { get; } = new();

        public SequenceStatusReporter Reporter { get; }

        public bool Supported { get; set; } = true;

        public HttpStatusCode SendStatus { get; set; } = HttpStatusCode.OK;

        public int CapabilityChecks { get; private set; }

        public int Attempts { get; private set; }

        public List<SequenceStatusReport> Sent { get; } = [];

        public List<RemoteQueueDestination> Destinations { get; } = [];

        public List<string> Log { get; } = [];

        public long NowMs => Clock.GetUtcNow().ToUnixTimeMilliseconds();

        public void Advance(TimeSpan by) => Clock.Advance(by);

        public Task<SequenceStatusTickResult> TickAsync(
            RemoteQueueDestination? destination,
            SequenceObservation? observation) =>
            Reporter.TickAsync(destination, observation, Clock.GetUtcNow(), CancellationToken.None);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 8, 4, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
