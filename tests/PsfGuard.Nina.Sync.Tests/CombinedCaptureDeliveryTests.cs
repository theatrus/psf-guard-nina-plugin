using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class CombinedCaptureDeliveryTests
{
    [Fact]
    public async Task CombinedCaptureAppliesSchedulerBeforeUploadingImage()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory, createImage: true);
        var requests = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource<PushReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            destination => Client(
                destination,
                (request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Enqueue(path);
                    return Task.FromResult(ResponseFor(path));
                }));
        queue.Pushed += (_, receipt) => completed.TrySetResult(receipt);

        await queue.EnqueueCaptureAsync(
            Destination(),
            database.Path,
            "5.9.6.0",
            imagePath,
            default,
            includeThumbnail: false,
            autoApply: true,
            uploadImageAfterApply: true,
            CancellationToken.None);
        queue.Start();

        var receipt = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(receipt.Applied);
        Assert.Equal(
            [PreviewPath, ApplyPath, UploadPath],
            requests.ToArray());
    }

    [Fact]
    public async Task CombinedCaptureDoesNotUploadWhileApplyIsPendingOrAfterItFails()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory, createImage: true);
        var requests = new ConcurrentQueue<string>();
        var applyStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            destination => Client(
                destination,
                async (request, cancellationToken) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Enqueue(path);
                    if (path == ApplyPath)
                    {
                        applyStarted.TrySetResult();
                        await releaseApply.Task.WaitAsync(cancellationToken);
                        return Json("""{"error":"apply failed"}""", HttpStatusCode.BadRequest);
                    }

                    return ResponseFor(path);
                }));

        var jobId = await queue.EnqueueCaptureAsync(
            Destination(),
            database.Path,
            "5.9.6.0",
            imagePath,
            default,
            includeThumbnail: false,
            autoApply: true,
            uploadImageAfterApply: true,
            CancellationToken.None);
        queue.Start();

        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(UploadPath, requests);

        releaseApply.TrySetResult();
        var jobPath = Path.Combine(directory.Child("queue"), $"{jobId:N}.json");
        await WaitUntilAsync(
            () => File.Exists(jobPath)
                && File.ReadAllText(jobPath).Contains("\"blocked\":true", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        Assert.Equal([PreviewPath, ApplyPath], requests.Take(2).ToArray());
        Assert.DoesNotContain(UploadPath, requests);
    }

    [Fact]
    public async Task DependentUploadGetsFreshRetryBudgetAndResumesAloneAfterRestart()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory, createImage: true);
        var reader = new TargetSchedulerCatalogReader(database.Path, "5.9.6.0");
        var bundle = await reader.BuildCaptureBundleAsync(
            acquiredImageId: 5,
            includeThumbnail: false,
            CancellationToken.None);
        var queuePath = Directory.CreateDirectory(directory.Child("queue")).FullName;
        var job = new QueuedBundleJob
        {
            JobId = Guid.NewGuid(),
            Destination = Destination(),
            AutoApply = true,
            Bundle = bundle,
            Capture = new QueuedCaptureSource
            {
                DatabasePath = database.Path,
                ProductVersion = "5.9.6.0",
                ImagePath = imagePath,
                ExposureStart = default,
                IncludeThumbnail = false,
                UploadImageAfterApply = true,
            },
            Attempts = QueueFailurePolicy.MaximumAttempts - 1,
            NextAttemptUtc = DateTimeOffset.UtcNow,
        };
        var jobPath = Path.Combine(queuePath, $"{job.JobId:N}.json");
        await WriteJobAsync(jobPath, job);
        var firstRequests = new ConcurrentQueue<string>();
        var statuses = new ConcurrentQueue<string>();

        await using (var firstQueue = new DurablePushQueue(
            queuePath,
            destination => Client(
                destination,
                (request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    firstRequests.Enqueue(path);
                    return Task.FromResult(path == UploadPath
                        ? Json("""{"error":"try again"}""", HttpStatusCode.ServiceUnavailable)
                        : ResponseFor(path));
                }),
            statuses.Enqueue))
        {
            firstQueue.Start();
            await WaitUntilAsync(
                () => statuses.Any(status => status.StartsWith(
                    "Sync job failed; retrying",
                    StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3));
        }

        var pending = await ReadJobAsync<QueuedBundleJob>(jobPath);
        Assert.True(pending.SchedulerApplied);
        Assert.NotNull(pending.SchedulerReceipt);
        Assert.Null(pending.Bundle);
        Assert.False(pending.Blocked);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(
            [PreviewPath, ApplyPath, UploadPath],
            firstRequests.ToArray());

        pending.NextAttemptUtc = DateTimeOffset.UtcNow;
        await WriteJobAsync(jobPath, pending);
        var resumedRequests = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource<PushReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var resumedQueue = new DurablePushQueue(
            queuePath,
            destination => Client(
                destination,
                (request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    resumedRequests.Enqueue(path);
                    return Task.FromResult(ResponseFor(path));
                }));
        resumedQueue.Pushed += (_, receipt) => completed.TrySetResult(receipt);
        resumedQueue.Start();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal([UploadPath], resumedRequests.ToArray());
        Assert.False(File.Exists(jobPath));
    }

    [Fact]
    public async Task LostApplyResponseRenewsTheBundleBeforeUploadingAfterRestart()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory, createImage: true);
        var reader = new TargetSchedulerCatalogReader(database.Path, "5.9.6.0");
        var bundle = await reader.BuildCaptureBundleAsync(
            acquiredImageId: 5,
            includeThumbnail: false,
            CancellationToken.None);
        var queuePath = Directory.CreateDirectory(directory.Child("queue")).FullName;
        var job = new QueuedBundleJob
        {
            JobId = Guid.NewGuid(),
            Destination = Destination(),
            AutoApply = true,
            Bundle = bundle,
            Capture = new QueuedCaptureSource
            {
                DatabasePath = database.Path,
                ProductVersion = "5.9.6.0",
                ImagePath = imagePath,
                ExposureStart = default,
                IncludeThumbnail = false,
                UploadImageAfterApply = true,
            },
            NextAttemptUtc = DateTimeOffset.UtcNow,
        };
        var jobPath = Path.Combine(queuePath, $"{job.JobId:N}.json");
        await WriteJobAsync(jobPath, job);
        var requests = new ConcurrentQueue<RequestObservation>();
        var firstStatuses = new ConcurrentQueue<string>();

        await using (var firstQueue = new DurablePushQueue(
            queuePath,
            destination => Client(
                destination,
                (request, _) =>
                {
                    requests.Enqueue(Observe(request));
                    var path = request.RequestUri!.AbsolutePath;
                    return Task.FromResult(path switch
                    {
                        PreviewPath => Json(
                            """{"preview_id":"preview-1","state":"ready"}"""),
                        ApplyPath => Json(
                            """{"error":"response lost"}""",
                            HttpStatusCode.ServiceUnavailable),
                        _ => throw new InvalidOperationException(
                            $"Unexpected first-attempt path {path}."),
                    });
                }),
            firstStatuses.Enqueue))
        {
            firstQueue.Start();
            await WaitUntilAsync(
                () => firstStatuses.Any(status => status.StartsWith(
                    "Sync job failed; retrying",
                    StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3));
        }

        var uncertain = await ReadJobAsync<QueuedBundleJob>(jobPath);
        Assert.NotNull(uncertain.Bundle);
        Assert.Equal(bundle.BundleId, uncertain.Bundle.BundleId);
        Assert.False(uncertain.SchedulerApplied);
        uncertain.NextAttemptUtc = DateTimeOffset.UtcNow;
        await WriteJobAsync(jobPath, uncertain);
        var renewalStatuses = new ConcurrentQueue<string>();

        await using (var renewalQueue = new DurablePushQueue(
            queuePath,
            destination => Client(
                destination,
                (request, _) =>
                {
                    requests.Enqueue(Observe(request));
                    var path = request.RequestUri!.AbsolutePath;
                    return Task.FromResult(path switch
                    {
                        PreviewPath => Json(
                            """{"preview_id":"preview-1","state":"ready"}"""),
                        ApplyPath => Json(
                            """{"error":"preview was already consumed"}""",
                            HttpStatusCode.NotFound),
                        _ => throw new InvalidOperationException(
                            $"Unexpected renewal path {path}."),
                    });
                }),
            renewalStatuses.Enqueue))
        {
            renewalQueue.Start();
            await WaitUntilAsync(
                () => renewalStatuses.Any(status => status.Contains(
                    "retrying with a fresh preview",
                    StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3));
        }

        var renewed = await ReadJobAsync<QueuedBundleJob>(jobPath);
        Assert.NotNull(renewed.Bundle);
        Assert.NotEqual(bundle.BundleId, renewed.Bundle.BundleId);
        Assert.True(renewed.Bundle.VerifyDigest());
        Assert.False(renewed.SchedulerApplied);
        renewed.NextAttemptUtc = DateTimeOffset.UtcNow;
        await WriteJobAsync(jobPath, renewed);
        var completed = new TaskCompletionSource<PushReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var finalQueue = new DurablePushQueue(
            queuePath,
            destination => Client(
                destination,
                (request, _) =>
                {
                    requests.Enqueue(Observe(request));
                    var path = request.RequestUri!.AbsolutePath;
                    return Task.FromResult(path switch
                    {
                        PreviewPath => Json(
                            """{"preview_id":"preview-2","state":"ready"}"""),
                        SecondApplyPath => Json("""{"state":"applied"}"""),
                        UploadPath => Json("""{"success":true,"data":{}}"""),
                        _ => throw new InvalidOperationException(
                            $"Unexpected final-attempt path {path}."),
                    });
                }));
        finalQueue.Pushed += (_, receipt) => completed.TrySetResult(receipt);
        finalQueue.Start();

        var receipt = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var observations = requests.ToArray();

        Assert.Equal(
            [
                PreviewPath,
                ApplyPath,
                PreviewPath,
                ApplyPath,
                PreviewPath,
                SecondApplyPath,
                UploadPath,
            ],
            observations.Select(observation => observation.Path).ToArray());
        var idempotencyKeys = observations
            .Where(observation => observation.Path == PreviewPath)
            .Select(observation => observation.IdempotencyKey)
            .ToArray();
        Assert.Equal(bundle.BundleId.ToString("D"), idempotencyKeys[0]);
        Assert.Equal(idempotencyKeys[0], idempotencyKeys[1]);
        Assert.Equal(renewed.Bundle.BundleId.ToString("D"), idempotencyKeys[2]);
        Assert.NotEqual(idempotencyKeys[0], idempotencyKeys[2]);
        Assert.Equal(renewed.Bundle.BundleId, receipt.BundleId);
        Assert.False(File.Exists(jobPath));
    }

    [Fact]
    public async Task StalePreviewIsRefreshedBeforeApplyAndUpload()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory, createImage: true);
        var requests = new ConcurrentQueue<string>();
        var applyAttempts = 0;
        var completed = new TaskCompletionSource<PushReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            destination => Client(
                destination,
                (request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Enqueue(path);
                    return Task.FromResult(path switch
                    {
                        PreviewPath => Json(
                            """{"preview_id":"preview-1","state":"ready"}"""),
                        ApplyPath when Interlocked.Increment(ref applyAttempts) == 1 => Json(
                            """{"error":"stale preview"}""",
                            HttpStatusCode.Conflict),
                        ApplyPath => Json("""{"state":"applied"}"""),
                        RefreshPath => Json(
                            """{"preview_id":"preview-1","state":"ready"}"""),
                        UploadPath => Json("""{"success":true,"data":{}}"""),
                        _ => throw new InvalidOperationException(
                            $"Unexpected request path {path}."),
                    });
                }));
        queue.Pushed += (_, receipt) => completed.TrySetResult(receipt);

        await queue.EnqueueCaptureAsync(
            Destination(),
            database.Path,
            "5.9.6.0",
            imagePath,
            default,
            includeThumbnail: false,
            autoApply: true,
            uploadImageAfterApply: true,
            CancellationToken.None);
        queue.Start();

        var receipt = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(receipt.Applied);
        Assert.Equal(
            [PreviewPath, ApplyPath, RefreshPath, ApplyPath, UploadPath],
            requests.ToArray());
    }

    [Fact]
    public async Task ImageUploadConflictRemainsQueuedForRetry()
    {
        using var directory = new TestDirectory();
        var imagePath = directory.Write("capture.fit", "test image");
        var queuePath = directory.Child("queue");
        var statuses = new ConcurrentQueue<string>();
        var requests = 0;
        Guid jobId;

        await using (var firstQueue = new DurableImageUploadQueue(
            queuePath,
            destination => Client(
                destination,
                (_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(Json(
                        """{"error":"destination is busy"}""",
                        HttpStatusCode.Conflict));
                }),
            statuses.Enqueue))
        {
            jobId = await firstQueue.EnqueueAsync(
                Destination(),
                imagePath,
                CancellationToken.None);
            firstQueue.Start();
            await WaitUntilAsync(
                () => statuses.Any(status => status.StartsWith(
                    "Image upload failed; retrying",
                    StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3));
        }

        var jobPath = Path.Combine(queuePath, $"{jobId:N}.json");
        var pending = await ReadJobAsync<QueuedImageUploadJob>(jobPath);
        Assert.False(pending.Blocked);
        Assert.Equal(1, pending.Attempts);
        pending.NextAttemptUtc = DateTimeOffset.UtcNow;
        await WriteJobAsync(jobPath, pending);

        await using var resumedQueue = new DurableImageUploadQueue(
            queuePath,
            destination => Client(
                destination,
                (_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(Json("""{"success":true,"data":{}}"""));
                }));
        resumedQueue.Start();

        await WaitUntilAsync(
            () => !File.Exists(jobPath),
            TimeSpan.FromSeconds(3));
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task DependentUploadRequiresAutomaticApplyWithoutWritingAJob()
    {
        using var directory = new TestDirectory();
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            _ => throw new InvalidOperationException("The worker is not started."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.EnqueueCaptureAsync(
                Destination(),
                directory.Child("scheduler.sqlite"),
                "5.9.6.0",
                directory.Child("capture.fit"),
                default,
                includeThumbnail: false,
                autoApply: false,
                uploadImageAfterApply: true,
                CancellationToken.None));

        Assert.Contains("automatic preview apply", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory.Child("queue")));
    }

    [Fact]
    public async Task SchedulerOnlyCaptureDoesNotRequireOrUploadTheImageFile()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory, createImage: false);
        var requests = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource<PushReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            destination => Client(
                destination,
                (request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Enqueue(path);
                    return Task.FromResult(ResponseFor(path));
                }));
        queue.Pushed += (_, receipt) => completed.TrySetResult(receipt);

        await queue.EnqueueCaptureAsync(
            Destination(),
            database.Path,
            "5.9.6.0",
            imagePath,
            default,
            includeThumbnail: false,
            autoApply: true,
            CancellationToken.None);
        queue.Start();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(File.Exists(imagePath));
        Assert.Equal([PreviewPath, ApplyPath], requests.ToArray());
    }

    [Fact]
    public void LegacyCaptureJobDefaultsToSchedulerOnlyDelivery()
    {
        var jobId = Guid.NewGuid();
        var job = ProtocolJson.Deserialize<QueuedBundleJob>(
            $$"""
            {
              "job_id":"{{jobId:D}}",
              "destination":{
                "server_url":"https://original.example/",
                "catalog_id":"ultra-cat",
                "credential_reference":"credential-profile-a"
              },
              "auto_apply":true,
              "capture":{
                "database_path":"scheduler.sqlite",
                "product_version":"5.9.6.0",
                "image_path":"capture.fit",
                "exposure_start":"0001-01-01T00:00:00",
                "include_thumbnail":false
              },
              "attempts":0,
              "next_attempt_utc":"2026-08-21T00:00:00Z"
            }
            """);

        Assert.False(job.SchedulerApplied);
        Assert.Null(job.SchedulerReceipt);
        Assert.NotNull(job.Capture);
        Assert.False(job.Capture.UploadImageAfterApply);
    }

    private const string PreviewPath = "/api/sync/v1/previews";
    private const string ApplyPath = "/api/sync/v1/previews/preview-1/apply";
    private const string SecondApplyPath = "/api/sync/v1/previews/preview-2/apply";
    private const string RefreshPath = "/api/sync/v1/previews/preview-1/refresh";
    private const string UploadPath = "/api/db/ultra-cat/images/upload";

    private static RemoteQueueDestination Destination() => new()
    {
        ServerUrl = "https://original.example/",
        CatalogId = "ultra-cat",
        CredentialReference = "credential-profile-a",
    };

    private static string SeedCapture(
        TestDatabase database,
        TestDirectory directory,
        bool createImage)
    {
        database.Seed(0);
        var imagePath = directory.Child("capture.fit");
        if (createImage)
        {
            File.WriteAllText(imagePath, "test image");
        }

        using var connection = database.Open();
        connection.Execute(
            "UPDATE acquiredimage SET metadata = @metadata WHERE Id = 5",
            new Dictionary<string, object?>
            {
                ["@metadata"] = JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["FileName"] = imagePath }),
            });
        return imagePath;
    }

    private static PsfGuardSyncClient Client(
        RemoteQueueDestination destination,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(
            new HttpClient(new StubHandler(send)),
            new Uri(destination.ServerUrl),
            "test-token");

    private static RequestObservation Observe(HttpRequestMessage request)
    {
        var idempotencyKey = request.Headers.TryGetValues(
            "Idempotency-Key",
            out var values)
                ? values.Single()
                : null;
        return new RequestObservation(request.RequestUri!.AbsolutePath, idempotencyKey);
    }

    private static HttpResponseMessage ResponseFor(string path) => path switch
    {
        PreviewPath => Json("""{"preview_id":"preview-1","state":"ready"}"""),
        ApplyPath => Json("""{"state":"applied"}"""),
        UploadPath => Json("""{"success":true,"data":{}}"""),
        _ => throw new InvalidOperationException($"Unexpected request path {path}."),
    };

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static async Task<T> ReadJobAsync<T>(string path)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(input, ProtocolJson.Options)
            ?? throw new InvalidDataException("Queued test job was empty.");
    }

    private static async Task WriteJobAsync<T>(string path, T job)
    {
        await using var output = File.Create(path);
        await JsonSerializer.SerializeAsync(output, job, ProtocolJson.Options);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the queue test condition.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string path = Directory.CreateTempSubdirectory(
            "psf-guard-combined-capture-test-").FullName;

        public string Child(string name) => Path.Combine(path, name);

        public string Write(string name, string value)
        {
            var file = Child(name);
            File.WriteAllText(file, value);
            return file;
        }

        public void Dispose() => Directory.Delete(path, recursive: true);
    }

    private sealed record RequestObservation(string Path, string? IdempotencyKey);
}
