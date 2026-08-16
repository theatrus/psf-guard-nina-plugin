using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PsfGuard.Nina.Sync.Client;
using PsfGuard.Nina.Sync.Protocol;
using PsfGuard.Nina.Sync.Queue;
using PsfGuard.Nina.Sync.TargetScheduler;

namespace PsfGuard.Nina.Sync.Tests;

public sealed class DurableQueueTests
{
    [Fact]
    public async Task ConcurrentImageEnqueuesDoNotLoseWakeSignals()
    {
        using var directory = new TestDirectory();
        var imagePath = directory.Write("capture.fit", "test image");
        await using var queue = new DurableImageUploadQueue(
            directory.Child("queue"),
            _ => throw new InvalidOperationException("The worker is not started."));

        var jobs = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => queue.EnqueueAsync(
                    Destination(),
                    imagePath,
                    CancellationToken.None)));

        Assert.Equal(32, jobs.Distinct().Count());
        Assert.Equal(32, Directory.GetFiles(directory.Child("queue"), "*.json").Length);
    }

    [Fact]
    public async Task ImageWorkerSurvivesAQueueDirectoryFailure()
    {
        using var directory = new TestDirectory();
        var queuePath = directory.Write("queue", "temporarily not a directory");
        var statuses = new ConcurrentQueue<string>();
        var requests = 0;
        await using var queue = new DurableImageUploadQueue(
            queuePath,
            destination => Client(
                destination,
                _ =>
                {
                    Interlocked.Increment(ref requests);
                    return Json("""{"success":true,"data":{}}""");
                }),
            statuses.Enqueue);
        queue.Start();

        await WaitUntilAsync(
            () => statuses.Any(status => status.Contains("worker error", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        File.Delete(queuePath);
        Directory.CreateDirectory(queuePath);
        var imagePath = directory.Write("capture.fit", "test image");
        await queue.EnqueueAsync(Destination(), imagePath, CancellationToken.None);

        await WaitUntilAsync(
            () => Volatile.Read(ref requests) == 1
                && Directory.GetFiles(queuePath, "*.json").Length == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StatusSinkFailureDoesNotStopImageDelivery()
    {
        using var directory = new TestDirectory();
        var requests = 0;
        await using var queue = new DurableImageUploadQueue(
            directory.Child("queue"),
            destination => Client(
                destination,
                _ =>
                {
                    Interlocked.Increment(ref requests);
                    return Json("""{"success":true,"data":{}}""");
                }),
            _ => throw new InvalidOperationException("status failed"));
        queue.Start();

        await queue.EnqueueAsync(
            Destination(),
            directory.Write("capture.fit", "test image"),
            CancellationToken.None);

        await WaitUntilAsync(
            () => Volatile.Read(ref requests) == 1
                && Directory.GetFiles(directory.Child("queue"), "*.json").Length == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PermanentImageFailureBlocksUntilExplicitRetry()
    {
        using var directory = new TestDirectory();
        var imagePath = directory.Write("capture.fit", "test image");
        var statuses = new ConcurrentQueue<string>();
        var destinations = new ConcurrentQueue<RemoteQueueDestination>();
        var accept = 0;
        await using var queue = new DurableImageUploadQueue(
            directory.Child("queue"),
            destination =>
            {
                destinations.Enqueue(destination);
                return Client(
                    destination,
                    _ => Volatile.Read(ref accept) == 0
                        ? Json("""{"error":"upload disabled"}""", HttpStatusCode.Forbidden)
                        : Json("""{"success":true,"data":{}}"""));
            },
            statuses.Enqueue);
        queue.Start();
        var destination = Destination();

        await queue.EnqueueAsync(destination, imagePath, CancellationToken.None);
        await WaitUntilAsync(
            () => statuses.Any(status => status.StartsWith("Image upload blocked", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        Assert.Single(Directory.GetFiles(directory.Child("queue"), "*.json"));
        Assert.All(destinations, actual => Assert.Equal(destination, actual));

        Volatile.Write(ref accept, 1);
        Assert.Equal(
            1,
            await queue.RetryBlockedAsync(destination, CancellationToken.None));
        await WaitUntilAsync(
            () => Directory.GetFiles(directory.Child("queue"), "*.json").Length == 0,
            TimeSpan.FromSeconds(2));
        Assert.All(destinations, actual => Assert.Equal(destination, actual));
    }

    [Fact]
    public async Task CompletionListenerFailureDoesNotRepeatAPush()
    {
        using var directory = new TestDirectory();
        var requests = 0;
        var statuses = new ConcurrentQueue<string>();
        await using var queue = new DurablePushQueue(
            directory.Child("queue"),
            destination => Client(
                destination,
                _ =>
                {
                    Interlocked.Increment(ref requests);
                    return Json("""{"preview_id":"preview-1","state":"ready"}""");
                }),
            statuses.Enqueue);
        queue.Pushed += (_, _) => throw new InvalidOperationException("listener failed");
        queue.Start();

        await queue.EnqueueAsync(
            Destination(),
            Bundle(),
            autoApply: false,
            CancellationToken.None);

        await WaitUntilAsync(
            () => statuses.Any(status => status.Contains("listener failed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.Empty(Directory.GetFiles(directory.Child("queue"), "*.json"));
    }

    [Fact]
    public async Task SchedulerCaptureIsDurableBeforeItsDatabaseRowExists()
    {
        using var directory = new TestDirectory();
        var queuePath = directory.Child("queue");
        var databasePath = directory.Child("scheduler.sqlite");
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
            directory.Child("future.fit"),
            new DateTime(2026, 8, 16, 1, 2, 3, DateTimeKind.Utc),
            CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(queuePath, "*.json"));
        var json = await File.ReadAllTextAsync(file);
        Assert.Contains("https://original.example/", json, StringComparison.Ordinal);
        Assert.Contains("credential-profile-a", json, StringComparison.Ordinal);
        Assert.Contains("scheduler.sqlite", json, StringComparison.Ordinal);
        Assert.Contains("future.fit", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyJobIsBlockedInsteadOfUsingTheCurrentDestination()
    {
        using var directory = new TestDirectory();
        var queuePath = Directory.CreateDirectory(directory.Child("queue")).FullName;
        var jobId = Guid.NewGuid();
        var legacyJob = Path.Combine(queuePath, $"{jobId:N}.json");
        await File.WriteAllTextAsync(
            legacyJob,
            $$"""
            {
              "job_id":"{{jobId:D}}",
              "destination_catalog_id":"ultra-cat",
              "image_path":"capture.fit",
              "attempts":0,
              "next_attempt_utc":"2026-08-16T01:02:03Z"
            }
            """);
        var clients = 0;
        await using var queue = new DurableImageUploadQueue(
            queuePath,
            destination =>
            {
                Interlocked.Increment(ref clients);
                return Client(destination, _ => Json("{}"));
            });
        queue.Start();

        await WaitUntilAsync(
            () => File.ReadAllText(legacyJob).Contains("\"blocked\":true", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref clients));
    }

    private static RemoteQueueDestination Destination() => new()
    {
        ServerUrl = "https://original.example/",
        CatalogId = "ultra-cat",
        CredentialReference = "credential-profile-a",
    };

    private static PsfGuardSyncClient Client(
        RemoteQueueDestination destination,
        Func<HttpRequestMessage, HttpResponseMessage> send) => new(
            new HttpClient(new StubHandler(send)),
            new Uri(destination.ServerUrl),
            "test-token");

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static CatalogBundle Bundle()
    {
        var bundle = new CatalogBundle
        {
            BundleId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Operation = SyncOperation.PushGrades,
            Source = new CatalogIdentity
            {
                Id = "source",
                Product = "Target Scheduler",
                ProductVersion = "5.9.6.0",
                SchemaVersion = 23,
            },
            Tables = new SortedDictionary<string, BundleTable>(),
        };
        bundle.Seal();
        return bundle;
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string path = Directory.CreateTempSubdirectory(
            "psf-guard-queue-test-").FullName;

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
