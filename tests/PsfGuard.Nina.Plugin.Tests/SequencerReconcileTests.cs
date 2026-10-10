using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using PsfGuard.Nina.Plugin.Sequence;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.TargetScheduler;
using PsfGuard.Nina.Sync.Tests;

namespace PsfGuard.Nina.Plugin.Tests;

public sealed class SequencerReconcileTests
{
    private const string TargetGuid = "8a31b8fe-c8bc-414c-af47-5d1638b16b94";

    [Theory]
    [InlineData("catalog", false)]
    [InlineData("catalog", true)]
    [InlineData("target", false)]
    [InlineData("target", true)]
    [InlineData("trigger", false)]
    [InlineData("trigger", true)]
    public async Task ExplicitReconcileAppliesRegardlessOfGlobalSetting(string kind, bool autoApply)
    {
        await using var fixture = new Fixture();
        fixture.Settings.AutoApplyPushes = autoApply;

        await fixture.ExecuteAsync(kind);

        var preview = Assert.Single(fixture.Requests, request => request.Path == "/api/sync/v1/previews");
        using var body = JsonDocument.Parse(preview.Body);
        Assert.Equal("catalog-test", body.RootElement.GetProperty("catalog_id").GetString());
        Assert.Single(fixture.Requests, request => request.Path == "/api/sync/v1/previews/preview-test/apply");
        Assert.Contains(fixture.Statuses, status => status.Contains("Applying PSF Guard preview", StringComparison.Ordinal));
        Assert.Contains(fixture.Statuses, status => status.StartsWith("PSF Guard preview preview-test applied.", StringComparison.Ordinal));
        Assert.Equal(autoApply, fixture.Settings.AutoApplyPushes);
    }

    [Theory]
    [InlineData("catalog", false)]
    [InlineData("target", false)]
    [InlineData("trigger", false)]
    [InlineData("catalog", true)]
    public async Task FailedApplyFailsTheInstructionWithoutPullingBack(string kind, bool roundTrip)
    {
        await using var fixture = new Fixture();
        fixture.Settings.RoundTripReconcile = roundTrip;
        fixture.ApplyState = "ready";

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ExecuteAsync(kind));

        Assert.Contains("expected 'applied'", exception.Message);
        Assert.Single(fixture.Requests, request => request.Path.EndsWith("/apply", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Requests, request => request.Path == "/api/sync/v1/exports");
        Assert.DoesNotContain(fixture.Statuses, status => status.StartsWith("PSF Guard preview preview-test applied.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerApplyErrorFailsTheInstructionWithoutPullingBack()
    {
        await using var fixture = new Fixture();
        fixture.Settings.RoundTripReconcile = true;
        fixture.FailApplyRequest = true;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.ExecuteAsync("catalog"));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("Apply failed", exception.Message);
        Assert.DoesNotContain(fixture.Requests, request => request.Path == "/api/sync/v1/exports");
        Assert.DoesNotContain(fixture.Statuses, status => status.StartsWith("PSF Guard preview preview-test applied.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoundTripAppliesThenPullsMergedCatalogWithGlobalApplyOff()
    {
        await using var fixture = new Fixture();
        fixture.Settings.RoundTripReconcile = true;
        using var source = new TestDatabase();
        source.Seed(100, grade: 2, rejectReason: "Clouds");
        using (var sourceConnection = source.Open())
        {
            sourceConnection.Execute($"UPDATE target SET guid = '{TargetGuid}'");
        }
        fixture.ExportBundle = await new TargetSchedulerCatalogReader(source.Path, "test")
            .BuildFullMergeBundleAsync(includeThumbnails: false, CancellationToken.None);
        var instruction = new ReconcilePsfGuardCatalog(fixture.Service);
        Assert.True(instruction.Validate(), string.Join("; ", instruction.Issues));

        await fixture.ExecuteAsync("catalog");

        var applyIndex = fixture.Requests.FindIndex(request => request.Path.EndsWith("/apply", StringComparison.Ordinal));
        var exportIndex = fixture.Requests.FindIndex(request => request.Path == "/api/sync/v1/exports");
        Assert.True(applyIndex >= 0 && exportIndex > applyIndex);
        using var body = JsonDocument.Parse(fixture.Requests[exportIndex].Body);
        Assert.False(body.RootElement.GetProperty("include_thumbnails").GetBoolean());
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT gradingStatus FROM acquiredimage WHERE guid = 'image-guid'";
        Assert.Equal(2L, command.ExecuteScalar());
        Assert.False(fixture.Settings.AutoApplyPushes);
    }

    [Fact]
    public async Task AsynchronousPreviewIsAppliedAfterItBecomesReady()
    {
        await using var fixture = new Fixture();
        fixture.AsyncPreview = true;

        await fixture.ExecuteAsync("catalog");

        var jobIndex = fixture.Requests.FindIndex(request => request.Path == "/api/sync/v1/jobs/job-test");
        var applyIndex = fixture.Requests.FindIndex(request => request.Path.EndsWith("/apply", StringComparison.Ordinal));
        Assert.True(jobIndex >= 0 && applyIndex > jobIndex);
    }

    private sealed record Request(string Path, string Body);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpListener server = new();
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly Task serving;
        private readonly string credentialReference;
        public TestDatabase Database { get; } = new();
        public IProfileService Service { get; }
        public PluginSettings Settings { get; }
        public List<Request> Requests { get; } = [];
        public List<string> Statuses { get; } = [];
        public string ApplyState { get; set; } = "applied";
        public bool FailApplyRequest { get; set; }
        public bool AsyncPreview { get; set; }
        public CatalogBundle? ExportBundle { get; set; }

        public Fixture()
        {
            Database.Seed(0);
            using (var connection = Database.Open())
            {
                connection.Execute($"UPDATE target SET guid = '{TargetGuid}'");
            }
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var url = $"http://127.0.0.1:{port}/";
            server.Prefixes.Add(url);
            server.Start();

            var profile = Stub<IProfile>(new()
            {
                ["get_Id"] = Guid.NewGuid(),
                ["get_Name"] = "Disposable reconcile test",
                ["get_PluginSettings"] = new NINA.Profile.PluginSettings(),
            });
            Service = Stub<IProfileService>(new() { ["get_ActiveProfile"] = profile });
            Settings = new PluginSettings(Service)
            {
                Enabled = true,
                ServerUrl = url,
                TargetSchedulerDatabase = Database.Path,
                AutoApplyPushes = false,
                IncludeThumbnails = false,
            };
            // Use the public MEF constructors and a credential owned only by this random profile.
            credentialReference = PluginSettings.CredentialReferenceFor(profile.Id, url, "catalog-test");
            Assert.Null(WindowsCredentialStore.Read(credentialReference));
            Settings.StorePairing(Settings.CapturePairingTarget(), "catalog-test", "disposable-test-token");
            Assert.Equal(credentialReference, Settings.CredentialReference);
            serving = ServeAsync();
        }

        public Task ExecuteAsync(string kind)
        {
            var progress = new RecordingProgress(status => Statuses.Add(status.Status));
            var target = Stub<IDeepSkyObjectContainer>(new()
            {
                ["get_Target"] = new InputTarget(Angle.ByDegree(0), Angle.ByDegree(0), null)
                {
                    TargetName = "M 31",
                },
                ["get_Parent"] = null,
            });
            var targetInstruction = new ReconcilePsfGuardTarget(Service);
            targetInstruction.AttachNewParent(target);
            return kind switch
            {
                "catalog" => new ReconcilePsfGuardCatalog(Service).Execute(progress, lifetime.Token),
                "target" => targetInstruction.Execute(progress, lifetime.Token),
                "trigger" => new ReconcilePsfGuardTargetAfterExposures(Service).Execute(target, progress, lifetime.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        private async Task ServeAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                var context = await server.GetContextAsync().WaitAsync(lifetime.Token);
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync(lifetime.Token);
                var path = context.Request.Url!.AbsolutePath;
                Requests.Add(new Request(path, body));
                var statusCode = 200;
                var payload = path switch
                {
                    "/api/sync/v1/previews" when AsyncPreview =>
                        """{"job_id":"job-test","state":"queued"}""",
                    "/api/sync/v1/previews" =>
                        """{"preview_id":"preview-test","state":"ready"}""",
                    "/api/sync/v1/jobs/job-test" =>
                        """{"job_id":"job-test","state":"ready","preview":{"preview_id":"preview-test","state":"ready"}}""",
                    "/api/sync/v1/previews/preview-test/apply" =>
                        $$"""{"preview_id":"preview-test","state":"{{ApplyState}}"}""",
                    "/api/sync/v1/capabilities" =>
                        """{"protocol_version":1,"product":"PSF Guard","product_version":"test","capabilities":["exports"],"catalogs":[{"id":"catalog-test","name":"Test","readable":true,"writable":true}]}""",
                    "/api/sync/v1/exports" when ExportBundle is not null =>
                        $$"""{"export_id":"export-test","state":"ready","bundle":{{ProtocolJson.Serialize(ExportBundle)}}}""",
                    _ => """{"error":"Unexpected test request"}""",
                };
                if (path == "/api/sync/v1/previews" && AsyncPreview)
                {
                    statusCode = 202;
                }
                var response = $$"""{"success":true,"data":{{payload}}}""";
                if (path.EndsWith("/apply", StringComparison.Ordinal) && FailApplyRequest)
                {
                    statusCode = 500;
                    response = """{"success":false,"error":"Apply failed"}""";
                }
                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, lifetime.Token);
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            server.Close();
            try
            {
                await serving;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                WindowsCredentialStore.Write(credentialReference, null);
                Database.Dispose();
                lifetime.Dispose();
            }
        }
    }

    private sealed class RecordingProgress(Action<ApplicationStatus> record) : IProgress<ApplicationStatus>
    {
        public void Report(ApplicationStatus value) => record(value);
    }

    private static T Stub<T>(Dictionary<string, object?> values) where T : class
    {
        var stub = DispatchProxy.Create<T, GetterProxy>();
        ((GetterProxy)(object)stub).Values = values;
        return stub;
    }

    public class GetterProxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Values.TryGetValue(targetMethod!.Name, out var value)
                ? value
                : throw new NotSupportedException(targetMethod.Name);
    }
}
