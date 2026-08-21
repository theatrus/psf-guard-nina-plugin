using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class DeferredUploadQueueTests
{
    [Fact]
    public async Task CombinedCaptureAppliesSchedulerThenWaitsForRelease()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory);
        var queuePath = directory.Child("queue");
        var destination = Destination();
        var requests = new ConcurrentQueue<string>();
        var deferredPersisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DurablePushQueue(
            queuePath,
            actualDestination => Client(
                actualDestination,
                (request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Enqueue(path);
                    return Task.FromResult(ResponseFor(path));
                }),
            status =>
            {
                if (status.Contains("image upload is deferred", StringComparison.Ordinal))
                {
                    deferredPersisted.TrySetResult();
                }
            });

        var jobId = await queue.EnqueueCaptureAsync(
            destination,
            database.Path,
            "5.9.6.0",
            imagePath,
            default,
            includeThumbnail: false,
            autoApply: true,
            uploadImageAfterApply: true,
            deferImageUpload: true,
            CancellationToken.None);
        queue.Start();

        await deferredPersisted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var jobPath = Path.Combine(queuePath, $"{jobId:N}.json");
        var held = await ReadJobAsync<QueuedBundleJob>(jobPath);

        Assert.True(held.SchedulerApplied);
        Assert.True(held.ImageUploadDeferred);
        Assert.NotNull(held.SchedulerReceipt);
        Assert.Null(held.Bundle);
        Assert.Equal(0, held.PrerequisiteAttempts);
        Assert.Equal([PreviewPath, ApplyPath], requests.ToArray());

        Assert.Equal(
            1,
            await queue.ReleaseDeferredAsync(destination, CancellationToken.None));
        await WaitUntilAsync(
            () => !File.Exists(jobPath),
            TimeSpan.FromSeconds(3));

        Assert.Equal([PreviewPath, ApplyPath, UploadPath], requests.ToArray());
    }

    [Fact]
    public async Task ReleaseDuringApplyReturnsBeforeApplyAndWorkerPreservesIt()
    {
        using var directory = new TestDirectory();
        using var database = new TestDatabase();
        var imagePath = SeedCapture(database, directory);
        var destination = Destination();
        var requests = new ConcurrentQueue<string>();
        var applyStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishApply = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            actualDestination => Client(
                actualDestination,
                async (request, cancellationToken) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Enqueue(path);
                    if (path == ApplyPath)
                    {
                        applyStarted.TrySetResult();
                        await finishApply.Task.WaitAsync(cancellationToken);
                    }

                    return ResponseFor(path);
                }));

        var jobId = await queue.EnqueueCaptureAsync(
            destination,
            database.Path,
            "5.9.6.0",
            imagePath,
            default,
            includeThumbnail: false,
            autoApply: true,
            uploadImageAfterApply: true,
            deferImageUpload: true,
            CancellationToken.None);
        queue.Start();

        await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var release = queue.ReleaseDeferredAsync(destination, CancellationToken.None);
        try
        {
            Assert.Equal(1, await release.WaitAsync(TimeSpan.FromSeconds(3)));
            var released = await ReadJobAsync<QueuedBundleJob>(
                Path.Combine(directory.Child("queue"), $"{jobId:N}.json"));
            Assert.False(released.ImageUploadDeferred);
            Assert.False(released.SchedulerApplied);
        }
        finally
        {
            finishApply.TrySetResult();
        }

        await WaitUntilAsync(
            () => !File.Exists(Path.Combine(directory.Child("queue"), $"{jobId:N}.json")),
            TimeSpan.FromSeconds(3));
        Assert.Equal([PreviewPath, ApplyPath, UploadPath], requests.ToArray());
    }

    [Fact]
    public async Task DirectReleaseReturnsWhileAnUnrelatedUploadIsRunning()
    {
        using var directory = new TestDirectory();
        var queuePath = directory.Child("queue");
        var destination = Destination();
        var uploadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstUpload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var queue = new DurableImageUploadQueue(
            queuePath,
            actualDestination => Client(
                actualDestination,
                async (_, cancellationToken) =>
                {
                    var requestNumber = Interlocked.Increment(ref requests);
                    if (requestNumber == 1)
                    {
                        uploadStarted.TrySetResult();
                        await finishFirstUpload.Task.WaitAsync(cancellationToken);
                    }

                    return Json("""{"success":true,"data":{}}""");
                }));

        var immediateId = await queue.EnqueueAsync(
            destination,
            directory.Write("immediate.fit", "first image"),
            deferImageUpload: false,
            CancellationToken.None);
        queue.Start();
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var deferredId = await queue.EnqueueAsync(
            destination,
            directory.Write("deferred.fit", "second image"),
            deferImageUpload: true,
            CancellationToken.None);

        try
        {
            Assert.Equal(
                1,
                await queue.ReleaseDeferredAsync(destination, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3)));
            var released = await ReadJobAsync<QueuedImageUploadJob>(
                Path.Combine(queuePath, $"{deferredId:N}.json"));
            Assert.False(released.ImageUploadDeferred);
            Assert.Equal(1, Volatile.Read(ref requests));
        }
        finally
        {
            finishFirstUpload.TrySetResult();
        }

        await WaitUntilAsync(
            () => Volatile.Read(ref requests) == 2
                && !File.Exists(Path.Combine(queuePath, $"{immediateId:N}.json"))
                && !File.Exists(Path.Combine(queuePath, $"{deferredId:N}.json")),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DeferredImageUploadSurvivesRestartUntilReleased()
    {
        using var directory = new TestDirectory();
        var queuePath = directory.Child("queue");
        var imagePath = directory.Write("capture.fit", "test image");
        var destination = Destination();
        var requests = 0;
        Guid jobId;

        await using (var firstQueue = new DurableImageUploadQueue(
            queuePath,
            actualDestination => Client(
                actualDestination,
                (_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(Json("""{"success":true,"data":{}}"""));
                })))
        {
            jobId = await firstQueue.EnqueueAsync(
                destination,
                imagePath,
                deferImageUpload: true,
                CancellationToken.None);
            firstQueue.Start();
            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref requests));

            var persisted = await ReadJobAsync<QueuedImageUploadJob>(
                Path.Combine(queuePath, $"{jobId:N}.json"));
            Assert.True(persisted.ImageUploadDeferred);
        }

        await using var resumedQueue = new DurableImageUploadQueue(
            queuePath,
            actualDestination => Client(
                actualDestination,
                (_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(Json("""{"success":true,"data":{}}"""));
                }));
        resumedQueue.Start();
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref requests));

        Assert.Equal(
            1,
            await resumedQueue.ReleaseDeferredAsync(destination, CancellationToken.None));
        await WaitUntilAsync(
            () => Volatile.Read(ref requests) == 1
                && !File.Exists(Path.Combine(queuePath, $"{jobId:N}.json")),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReleaseIsDestinationExactAndPreservesFailureState()
    {
        using var directory = new TestDirectory();
        var queuePath = directory.Child("queue");
        var firstDestination = Destination();
        var secondDestination = Destination() with { CatalogId = "other-cat" };
        await using var queue = new DurableImageUploadQueue(
            queuePath,
            _ => throw new InvalidOperationException("The worker is not started."));
        var firstId = await queue.EnqueueAsync(
            firstDestination,
            directory.Child("first.fit"),
            deferImageUpload: true,
            CancellationToken.None);
        var secondId = await queue.EnqueueAsync(
            secondDestination,
            directory.Child("second.fit"),
            deferImageUpload: true,
            CancellationToken.None);
        var firstPath = Path.Combine(queuePath, $"{firstId:N}.json");
        var first = await ReadJobAsync<QueuedImageUploadJob>(firstPath);
        first.Attempts = 4;
        first.PrerequisiteAttempts = 3;
        first.Blocked = true;
        first.LastError = "preserve me";
        first.NextAttemptUtc = DateTimeOffset.UtcNow.AddDays(1);
        await WriteJobAsync(firstPath, first);
        var releaseStarted = DateTimeOffset.UtcNow;

        Assert.Equal(
            1,
            await queue.ReleaseDeferredAsync(firstDestination, CancellationToken.None));

        var released = await ReadJobAsync<QueuedImageUploadJob>(firstPath);
        var stillHeld = await ReadJobAsync<QueuedImageUploadJob>(
            Path.Combine(queuePath, $"{secondId:N}.json"));
        Assert.False(released.ImageUploadDeferred);
        Assert.True(released.Blocked);
        Assert.Equal(4, released.Attempts);
        Assert.Equal(3, released.PrerequisiteAttempts);
        Assert.Equal("preserve me", released.LastError);
        Assert.True(released.NextAttemptUtc >= releaseStarted);
        Assert.True(stillHeld.ImageUploadDeferred);
        Assert.Equal(0, await queue.ReleaseDeferredAsync(firstDestination, CancellationToken.None));
    }

    [Fact]
    public async Task DeferredUploadRequiresDependentCaptureUpload()
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
                autoApply: true,
                uploadImageAfterApply: false,
                deferImageUpload: true,
                CancellationToken.None));

        Assert.Contains("dependent image upload", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory.Child("queue")));
    }

    [Fact]
    public async Task OrchestratorPersistsDeferredCaptureIntent()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.Child("scheduler.sqlite");
        var queuePath = directory.Child("queue");
        var destination = Destination();
        await using var queue = new DurablePushQueue(
            queuePath,
            _ => throw new InvalidOperationException("The worker is not started."));
        var orchestrator = new SyncOrchestrator(
            destination.CatalogId,
            autoApplyPushes: true,
            includeThumbnails: false,
            () => throw new InvalidOperationException("No immediate client is required."),
            new TargetSchedulerCatalogReader(databasePath, "5.9.6.0"),
            new TargetSchedulerCatalogWriter(databasePath),
            queue,
            destination);

        await orchestrator.QueueCapturedImageAsync(
            directory.Child("capture.fit"),
            default,
            uploadImageAfterApply: true,
            deferImageUpload: true,
            CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(queuePath, "*.json"));
        var job = await ReadJobAsync<QueuedBundleJob>(file);
        Assert.True(job.ImageUploadDeferred);
        Assert.True(job.Capture!.UploadImageAfterApply);
    }

    [Fact]
    public void LegacyJobsDefaultToImmediateImageDelivery()
    {
        var jobId = Guid.NewGuid();
        var imageJob = ProtocolJson.Deserialize<QueuedImageUploadJob>(
            $$"""
            {
              "job_id":"{{jobId:D}}",
              "destination":{
                "server_url":"https://original.example/",
                "catalog_id":"ultra-cat",
                "credential_reference":"credential-profile-a"
              },
              "image_path":"capture.fit",
              "attempts":0,
              "next_attempt_utc":"2026-08-21T00:00:00Z"
            }
            """);
        var captureJob = ProtocolJson.Deserialize<QueuedBundleJob>(
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
                "include_thumbnail":false,
                "upload_image_after_apply":true
              },
              "attempts":0,
              "next_attempt_utc":"2026-08-21T00:00:00Z"
            }
            """);

        Assert.False(imageJob.ImageUploadDeferred);
        Assert.False(captureJob.ImageUploadDeferred);
    }

    private const string PreviewPath = "/api/sync/v1/previews";
    private const string ApplyPath = "/api/sync/v1/previews/preview-1/apply";
    private const string UploadPath = "/api/db/ultra-cat/images/upload";

    private static RemoteQueueDestination Destination() => new()
    {
        ServerUrl = "https://original.example/",
        CatalogId = "ultra-cat",
        CredentialReference = "credential-profile-a",
    };

    private static string SeedCapture(TestDatabase database, TestDirectory directory)
    {
        database.Seed(0);
        var imagePath = directory.Write("capture.fit", "test image");
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
            "psf-guard-deferred-upload-test-").FullName;

        public string Child(string name) => Path.Combine(path, name);

        public string Write(string name, string value)
        {
            var file = Child(name);
            File.WriteAllText(file, value);
            return file;
        }

        public void Dispose() => Directory.Delete(path, recursive: true);
    }
}
