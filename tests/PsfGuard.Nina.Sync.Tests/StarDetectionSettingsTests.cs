using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.StarDetection;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class StarDetectionSettingsTests
{
    private const string CredentialReference = "PSFGuard.Nina.Plugin/redacted-test-reference";

    private static readonly RemoteQueueDestination Destination = new()
    {
        ServerUrl = "https://psf.example/",
        CatalogId = "review",
        CredentialReference = CredentialReference,
    };

    // The Hocus Focus block of a real C925 profile, with the save paths'
    // user names replaced.
    private static Dictionary<string, object?> HocusFocusBlock() => new()
    {
        ["IntermediateSavePath"] = @"C:\Users\observer\AppData\Local\NINA\HocusFocusIntermediate",
        ["NoiseReductionRadius"] = 4,
        ["AutoFocusTimeoutSeconds"] = 1800,
        ["Simple_FocusRange"] = "WideRange",
        ["StructureLayers"] = 6,
        ["BrightnessSensitivity"] = 1.2000000000000002,
        ["Simple_PixelScale"] = "LongFocalLength",
        ["MinStarBoundingBoxSize"] = 6,
        ["Save"] = false,
        ["SavePath"] = @"C:\Users\observer\Documents\N.I.N.A\HocusFocus",
        ["KeepFramesForReview"] = true,
        ["UseAdvanced"] = true,
        ["LastInFocusHfrPixels"] = 5.214123060870589,
        ["LastInFocusHfrMeasuredAtUtc"] = 639228231542427906L,
        ["CurvatureSignMeasurementMigrated"] = true,
        ["MaxOutlierRejections"] = 2,
        ["ValidateHfrImprovement"] = false,
    };

    [Fact]
    public void HocusFocusSelectionKeepsOnlyAllowlistedKeys()
    {
        var block = HocusFocusBlock();

        var selected = HocusFocusSettings.Select(block.TryGetValue);

        Assert.Equal(
            new[]
            {
                "BrightnessSensitivity",
                "MinStarBoundingBoxSize",
                "NoiseReductionRadius",
                "Simple_FocusRange",
                "Simple_PixelScale",
                "StructureLayers",
                "UseAdvanced",
            },
            selected.Keys);
        Assert.Equal(4L, selected["NoiseReductionRadius"]);
        Assert.Equal(1.2000000000000002, selected["BrightnessSensitivity"]);
        Assert.Equal("LongFocalLength", selected["Simple_PixelScale"]);
        Assert.Equal(true, selected["UseAdvanced"]);
    }

    [Fact]
    public void HocusFocusSelectionKeepsEveryAllowlistedKeyItFinds()
    {
        var block = HocusFocusSettings.Keys.ToDictionary(key => key, object? (_) => 1);

        var selected = HocusFocusSettings.Select(block.TryGetValue);

        Assert.Equal(HocusFocusSettings.Keys.Order(StringComparer.Ordinal), selected.Keys);
        Assert.Contains("OptimizedSettingsJson", selected.Keys);
        Assert.Contains("DetectionBinning", selected.Keys);
    }

    [Fact]
    public void HocusFocusValuesKeepTheirKindAndDropNonFiniteNumbers()
    {
        Assert.Equal(3L, HocusFocusSettings.Normalize((short)3));
        Assert.Equal(0.5, HocusFocusSettings.Normalize(0.5f));
        Assert.Null(HocusFocusSettings.Normalize(double.NaN));
        Assert.Null(HocusFocusSettings.Normalize(double.PositiveInfinity));
        Assert.Equal("Friday", HocusFocusSettings.Normalize(DayOfWeek.Friday));
        Assert.Equal("text", HocusFocusSettings.Normalize("text"));
        Assert.Equal(false, HocusFocusSettings.Normalize(false));
    }

    [Fact]
    public void HocusFocusSelectionLeavesOutValuesTheServerWouldRefuse()
    {
        var block = new Dictionary<string, object?>
        {
            ["MinHFR"] = double.NaN,
            ["UseAdvanced"] = null,
            ["OptimizedSettingsJson"] = new string('x', HocusFocusSettings.MaxValueChars + 1),
            ["StructureLayers"] = 4,
        };

        var selected = HocusFocusSettings.Select(block.TryGetValue);

        Assert.Equal(["StructureLayers"], selected.Keys);
    }

    [Theory]
    [InlineData(null, "nina")]
    [InlineData("", "nina")]
    [InlineData(StarDetectionSettings.HocusFocusStarDetection, "hocus_focus")]
    [InlineData("Other.Plugin.StarDetection", "Other.Plugin.StarDetection")]
    public void DetectorNameMatchesTheServersSelection(string? selectedType, string expected)
    {
        Assert.Equal(expected, StarDetectionSettings.DetectorName(selectedType));
    }

    [Fact]
    public void ReportSerializesInSnakeCaseAndKeepsHocusFocusKeyNames()
    {
        var json = ProtocolJson.Serialize(Report());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            root.GetProperty("profile_id").GetString());
        Assert.Equal("C925", root.GetProperty("profile_name").GetString());
        Assert.Equal("2026-11-02T21:00:00+00:00", root.GetProperty("captured_at").GetString());
        Assert.Equal("3.2.0.9001", root.GetProperty("nina_version").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("hocus_focus_version").ValueKind);
        var settings = root.GetProperty("settings");
        Assert.Equal(
            new[]
            {
                "star_detector",
                "star_sensitivity",
                "noise_reduction",
                "auto_stretch_factor",
                "black_clipping",
                "autofocus_inner_crop_ratio",
                "autofocus_outer_crop_ratio",
                "autofocus_brightest_stars",
                "focal_length_mm",
                "hocus_focus",
            },
            settings.EnumerateObject().Select(property => property.Name));
        Assert.Equal("hocus_focus", settings.GetProperty("star_detector").GetString());
        Assert.Equal("high", settings.GetProperty("star_sensitivity").GetString());
        Assert.Equal("none", settings.GetProperty("noise_reduction").GetString());
        Assert.Equal(30, settings.GetProperty("autofocus_brightest_stars").GetInt32());
        Assert.Equal(2350, settings.GetProperty("focal_length_mm").GetDouble());
        var hocusFocus = settings.GetProperty("hocus_focus");
        Assert.Equal(4, hocusFocus.GetProperty("NoiseReductionRadius").GetInt64());
        Assert.True(hocusFocus.GetProperty("UseAdvanced").GetBoolean());
    }

    [Fact]
    public void ReportCarriesNoPathsHistoryOrCredentialReference()
    {
        // A profile's plugin store, keyed by plugin: PSF Guard Sync's own
        // block holds the pairing credential reference.
        var store = new Dictionary<Guid, Dictionary<string, object?>>
        {
            [HocusFocusSettings.PluginId] = HocusFocusBlock(),
            [Guid.Parse("6fd90294-1335-41d2-988c-c1d4bb749588")] = new()
            {
                ["PairingMetadataV1"] =
                    $$"""{"ServerUrl":"https://psf.example/","CatalogId":"review","CredentialReference":"{{CredentialReference}}"}""",
                ["NoiseReductionRadius"] = 99,
            },
        };
        var hocusFocus = HocusFocusSettings.Select(
            (string key, out object? value) =>
            {
                value = null;
                return store[HocusFocusSettings.PluginId].TryGetValue(key, out value);
            });

        var json = ProtocolJson.Serialize(Report(hocusFocus));

        Assert.DoesNotContain(CredentialReference, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PairingMetadataV1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("observer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LastInFocusHfr", json, StringComparison.Ordinal);
        Assert.DoesNotContain("99", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintIgnoresTheReadTimeOnly()
    {
        var report = Report();

        Assert.Equal(
            StarDetectionSettingsReporter.Fingerprint(report),
            StarDetectionSettingsReporter.Fingerprint(
                report with { CapturedAt = report.CapturedAt.AddHours(1) }));
        Assert.NotEqual(
            StarDetectionSettingsReporter.Fingerprint(report),
            StarDetectionSettingsReporter.Fingerprint(
                report with
                {
                    Settings = report.Settings with
                    {
                        StarSensitivity = "highest",
                    },
                }));
    }

    [Fact]
    public async Task ClientPostsTheReportToThePairedCatalog()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(async request =>
            {
                captured = request;
                body = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            })),
            new Uri("https://psf.example/base/"),
            "secret");

        await client.SendStarDetectionSettingsAsync("review", Report(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(
            "https://psf.example/base/api/db/review/nina/star-detection-settings",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal("secret", captured.Headers.Authorization!.Parameter);
        Assert.Equal("review", captured.Headers.GetValues("X-PSF-Guard-Database-ID").Single());
        Assert.Contains("\"profile_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"", body);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientReportsTheStatusOfARefusal()
    {
        using var client = new PsfGuardSyncClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(
                Json("""{"success":false,"error":"no such route"}""", HttpStatusCode.NotFound)))),
            new Uri("https://psf.example/"),
            "secret");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendStarDetectionSettingsAsync("review", Report(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task ReporterSendsOnlyWhenTheSettingsChange()
    {
        var fixture = new ReporterFixture();
        var report = Report();
        fixture.Current = Capture(Destination, report);

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        fixture.Current = fixture.Current with
        {
            Report = report with { CapturedAt = report.CapturedAt.AddMinutes(1) },
        };
        Assert.Equal(StarDetectionSendResult.Unchanged, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(1, fixture.Requests);

        fixture.Current = fixture.Current with
        {
            Report = report with
            {
                Settings = report.Settings with
                {
                    HocusFocus = new SortedDictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["NoiseReductionRadius"] = 3L,
                    },
                },
            },
        };
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task ReporterSendsTheSameSettingsToANewCatalog()
    {
        var fixture = new ReporterFixture();
        fixture.Current = Capture(Destination, Report());
        await fixture.Reporter.SendIfChangedAsync(default);

        fixture.Current = Capture(Destination with { CatalogId = "other" }, Report());

        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal("other", fixture.LastCatalogHeader);
    }

    [Fact]
    public async Task ReporterDoesNothingWithoutAPairing()
    {
        var fixture = new ReporterFixture();

        Assert.Equal(StarDetectionSendResult.NotPaired, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task ReporterRetriesAfterAFailedSend()
    {
        var fixture = new ReporterFixture { Status = HttpStatusCode.ServiceUnavailable };
        fixture.Current = Capture(Destination, Report());

        Assert.Equal(StarDetectionSendResult.Failed, await fixture.Reporter.SendIfChangedAsync(default));
        fixture.Status = HttpStatusCode.OK;
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task ReporterTreatsAReadFailureAsAFailedSend()
    {
        var fixture = new ReporterFixture
        {
            Capture = () => throw new InvalidOperationException("profile changed"),
        };

        Assert.Equal(StarDetectionSendResult.Failed, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Contains(fixture.Log, line => line.Contains("profile changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReporterWaitsBeforeAskingAServerWithoutTheRouteAgain()
    {
        var fixture = new ReporterFixture { Status = HttpStatusCode.NotFound };
        fixture.Current = Capture(Destination, Report());

        Assert.Equal(StarDetectionSendResult.Unsupported, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(StarDetectionSendResult.Unsupported, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(1, fixture.Requests);

        fixture.Clock.Now += TimeSpan.FromHours(7);
        fixture.Status = HttpStatusCode.OK;
        Assert.Equal(StarDetectionSendResult.Sent, await fixture.Reporter.SendIfChangedAsync(default));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public void RetryDelayDoublesUpToTheLimit()
    {
        var first = TimeSpan.FromSeconds(30);
        var max = TimeSpan.FromMinutes(30);

        Assert.Equal(first, StarDetectionSettingsReporter.RetryDelay(1, first, max));
        Assert.Equal(TimeSpan.FromMinutes(1), StarDetectionSettingsReporter.RetryDelay(2, first, max));
        Assert.Equal(TimeSpan.FromMinutes(16), StarDetectionSettingsReporter.RetryDelay(6, first, max));
        Assert.Equal(max, StarDetectionSettingsReporter.RetryDelay(7, first, max));
        Assert.Equal(max, StarDetectionSettingsReporter.RetryDelay(500, first, max));
    }

    [Fact]
    public async Task BackgroundWorkerSendsAtStartAndRetriesAFailure()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReporterFixture(
            new StarDetectionReporterTiming(
                PollInterval: TimeSpan.FromMinutes(10),
                SettleDelay: TimeSpan.FromMilliseconds(10),
                FirstRetryDelay: TimeSpan.FromMilliseconds(50),
                MaxRetryDelay: TimeSpan.FromMilliseconds(50),
                UnsupportedHold: TimeSpan.FromHours(6),
                RequestTimeout: TimeSpan.FromSeconds(10)),
            TimeProvider.System)
        {
            Status = HttpStatusCode.ServiceUnavailable,
        };
        fixture.Current = Capture(Destination, Report());
        fixture.OnRequest = count =>
        {
            if (count == 1)
            {
                fixture.Status = HttpStatusCode.OK;
            }
            else
            {
                sent.TrySetResult();
            }
        };

        fixture.Reporter.Start();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Reporter.DisposeAsync();

        Assert.Equal(2, fixture.Requests);
        Assert.Contains(fixture.Log, line => line.StartsWith("Sent star-detection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackgroundWorkerChecksAgainOnRequest()
    {
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReporterFixture(
            StarDetectionReporterTiming.Default with { SettleDelay = TimeSpan.FromMilliseconds(10) },
            TimeProvider.System);
        var report = Report();
        fixture.Current = Capture(Destination, report);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnRequest = count => (count == 1 ? first : second).TrySetResult();

        fixture.Reporter.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Current = fixture.Current with
        {
            Report = report with { ProfileName = "C925 renamed" },
        };
        fixture.Reporter.RequestSend();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Reporter.DisposeAsync();

        Assert.Equal(2, fixture.Requests);
    }

    private static StarDetectionSettingsCapture Capture(
        RemoteQueueDestination destination,
        StarDetectionSettingsReport report) =>
        new($"{destination.ServerUrl}\n{destination.CatalogId}", report, destination);

    private static StarDetectionSettingsReport Report(
        IReadOnlyDictionary<string, object>? hocusFocus = null) => new()
        {
            ProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ProfileName = "C925",
            CapturedAt = new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero),
            NinaVersion = "3.2.0.9001",
            HocusFocusVersion = null,
            Settings = new StarDetectionSettings
            {
                StarDetector = "hocus_focus",
                StarSensitivity = "high",
                NoiseReduction = "none",
                AutoStretchFactor = 0.2,
                BlackClipping = -2.8,
                AutofocusInnerCropRatio = 1,
                AutofocusOuterCropRatio = 1,
                AutofocusBrightestStars = 30,
                FocalLengthMm = 2350,
                PixelSizeUm = null,
                HocusFocus = hocusFocus ?? HocusFocusSettings.Select(HocusFocusBlock().TryGetValue),
            },
        };

    private static HttpResponseMessage Json(string body, HttpStatusCode statusCode) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class ReporterFixture
    {
        public ReporterFixture()
            : this(StarDetectionReporterTiming.Default, null)
        {
        }

        public ReporterFixture(StarDetectionReporterTiming timing, TimeProvider? time)
        {
            Capture = () => Current;
            Reporter = new StarDetectionSettingsReporter(
                () => Capture(),
                async (capture, cancellationToken) =>
                {
                    var destination = (RemoteQueueDestination)capture.Destination!;
                    using var client = new PsfGuardSyncClient(
                    new HttpClient(new StubHandler(request =>
                    {
                        var count = Interlocked.Increment(ref requests);
                        LastCatalogHeader = request.Headers.GetValues("X-PSF-Guard-Database-ID").Single();
                        var status = Status;
                        OnRequest?.Invoke(count);
                        return Task.FromResult(Json(
                            status == HttpStatusCode.OK
                                ? """{"success":true,"data":{}}"""
                                : """{"success":false,"error":"refused"}""",
                            status));
                    })),
                    new Uri(destination.ServerUrl),
                    "secret");
                    await client.SendStarDetectionSettingsAsync(
                        destination.CatalogId,
                        capture.Report,
                        cancellationToken);
                },
                PsfGuardSyncClient.IsMissingStarDetectionRoute,
                line =>
                {
                    lock (Log)
                    {
                        Log.Add(line);
                    }
                },
                timing,
                time ?? Clock);
        }

        private int requests;

        public StarDetectionSettingsReporter Reporter { get; }

        public FakeClock Clock { get; } = new();

        public StarDetectionSettingsCapture? Current { get; set; }

        public Func<StarDetectionSettingsCapture?> Capture { get; set; }

        public volatile HttpStatusCode Status = HttpStatusCode.OK;

        public Action<int>? OnRequest { get; set; }

        public string? LastCatalogHeader { get; private set; }

        public List<string> Log { get; } = [];

        public int Requests => Volatile.Read(ref requests);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 11, 2, 21, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
