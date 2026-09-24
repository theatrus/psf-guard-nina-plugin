using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace PsfGuard.Nina.Plugin.Tests;

public sealed class PairingRecoveryTests
{
    [Fact]
    public void DeletedCredentialCanBeReplacedWithoutChangingQueueIdentity()
    {
        var fixture = new Fixture();
        fixture.Pair("catalog-a", "old-token");
        var destination = fixture.Settings.CaptureSnapshot();
        fixture.Credentials.Clear();

        Assert.Equal(PairingAvailability.CredentialMissing,
            fixture.Settings.GetPairingAvailability(fixture.Settings.ServerUrl));
        Assert.False(fixture.Settings.HasPairingCredential);
        Assert.True(fixture.Settings.HasPairingMetadata);

        fixture.Pair("catalog-a", "new-token");

        Assert.True(fixture.Settings.HasPairingCredential);
        Assert.Equal(destination, fixture.Settings.CaptureSnapshot());
        Assert.Equal("new-token", fixture.Credentials[fixture.Settings.CredentialReference]);
    }

    internal static void VerifyMissingCredentialCommandStates()
    {
        var fixture = new Fixture();
        fixture.Pair("catalog-a", "old-token");
        var plugin = new PsfGuardPlugin(fixture.Service, Stub<IImageSaveMediator>([]), fixture.Settings);
        Assert.Equal("Paired", plugin.PairingStatus);
        fixture.Credentials.Clear();
        var changed = new List<string?>();
        plugin.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        plugin.RefreshPairingState();
        plugin.PairingCode = "fresh-code";

        Assert.Contains(nameof(plugin.PairingStatus), changed);
        Assert.Contains(nameof(plugin.HasStoredCredential), changed);
        Assert.Equal("Credential missing; pair with a new code", plugin.PairingStatus);
        Assert.False(plugin.HasStoredCredential);
        Assert.True(plugin.IsSettingsEditable);
        Assert.True(plugin.PairCommand.CanExecute(null));
        Assert.True(plugin.ResetPairingCommand.CanExecute(null));
        Assert.False(plugin.TestConnectionCommand.CanExecute(null));

        fixture.Pair("catalog-a", "new-token");
        plugin.RefreshPairingState();
        Assert.Equal("Paired", plugin.PairingStatus);
        Assert.True(plugin.TestConnectionCommand.CanExecute(null));
    }

    internal static void VerifyResetAndPairButtons(DataTemplate template)
    {
        foreach (var deleted in new[] { false, true })
        {
            var fixture = new Fixture();
            fixture.Settings.TargetSchedulerDatabase = string.Empty;
            fixture.Pair("catalog-a", "old-token");
            if (deleted)
            {
                fixture.Credentials.Clear();
            }
            var errors = new List<string>();
            PsfGuardPlugin? plugin = null;
            plugin = new PsfGuardPlugin(fixture.Service, Stub<IImageSaveMediator>([]), fixture.Settings,
                notifySuccess: _ => Assert.False(plugin!.PairCommand.CanExecute(null)),
                notifyError: message =>
                {
                    Assert.False(plugin!.PairCommand.CanExecute(null));
                    errors.Add(message);
                });
            var panel = (FrameworkElement)template.LoadContent();
            panel.DataContext = plugin;
            panel.Measure(new Size(760, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            panel.UpdateLayout();
            DrainBindings();

            var buttons = Descendants<Button>(panel).ToArray();
            var pair = buttons.Single(button => ReferenceEquals(button.Command, plugin.PairCommand));
            var reset = buttons.Single(button => ReferenceEquals(button.Command, plugin.ResetPairingCommand));
            var code = Descendants<TextBox>(panel).Single(box => box.Name == "PairingCodeBox");
            var url = Descendants<TextBox>(panel).Single(box => box.Name == "ServerUrlBox");
            Assert.True(reset.IsEnabled);
            Click(reset);
            DrainBindings();
            Assert.False(plugin.IsOperationRunning);
            Assert.Equal("Not paired", plugin.PairingStatus);
            Assert.Empty(fixture.Credentials);
            Assert.False(reset.IsEnabled);

            EnterText(code, "fresh-code");
            Assert.Equal("fresh-code", plugin.PairingCode);
            Assert.True(pair.IsEnabled);
            Assert.False(plugin.Enabled); // Manual pairing does not require automation.
            Assert.Empty(plugin.TargetSchedulerDatabase);

            foreach (var invalidUrl in new[] { "", "psf.example", "http://192.168.1.10:3000", "ftp://psf.example" })
            {
                EnterText(url, invalidUrl);
                EnterText(code, "fresh-code");
                Assert.True(pair.IsEnabled);
                Click(pair);
                DrainBindings();
                Assert.Contains("HTTPS", plugin.LastStatus);
                Assert.Equal(plugin.LastStatus, errors[^1]);
                Assert.DoesNotContain("fresh-code", plugin.LastStatus);
                Assert.False(plugin.IsOperationRunning);
                Assert.True(pair.IsEnabled);
                Assert.Empty(fixture.Credentials);
            }

            foreach (var validUrl in new[] { "https://psf.example", "http://127.0.0.1:3000", "http://[::1]:3000" })
            {
                EnterText(url, validUrl);
                Assert.Empty(code.Text);
                Assert.True(pair.IsEnabled);
                Click(pair);
                DrainBindings();
                Assert.Contains("Enter the pairing code", plugin.LastStatus);
                EnterText(code, "fresh-code");
                Assert.True(pair.IsEnabled);
                Assert.True(plugin.IsSettingsEditable);
            }

            panel.DataContext = null;
        }
    }

    private static void EnterText(TextBox box, string text)
    {
        box.SetCurrentValue(TextBox.TextProperty, text);
        DrainBindings();
        Assert.NotNull(BindingOperations.GetBindingExpression(box, TextBox.TextProperty));
    }

    internal static void VerifyResetThenPairExchange(DataTemplate template, bool nativeCredentials = false)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();

        using var fixture = new Fixture(nativeCredentials);
        fixture.Settings.ServerUrl = $"http://127.0.0.1:{port}/";
        fixture.Pair("catalog-a", "old-token");
        var originalDestination = fixture.Settings.CaptureSnapshot();
        var errors = new List<string>();
        var plugin = new PsfGuardPlugin(fixture.Service, Stub<IImageSaveMediator>([]), fixture.Settings,
            notifySuccess: _ => { }, notifyError: errors.Add);
        var panel = (FrameworkElement)template.LoadContent();
        panel.DataContext = plugin;
        panel.Measure(new Size(760, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        panel.UpdateLayout();
        DrainBindings();

        var buttons = Descendants<Button>(panel).ToArray();
        var pair = buttons.Single(button => ReferenceEquals(button.Command, plugin.PairCommand));
        var reset = buttons.Single(button => ReferenceEquals(button.Command, plugin.ResetPairingCommand));
        var code = Descendants<TextBox>(panel).Single(box => box.Name == "PairingCodeBox");
        Assert.Equal("Paired", plugin.PairingStatus);
        // Delete outside the plugin while its controls are still bound and loaded.
        fixture.DeleteCredentialExternally();
        Assert.Equal("Credential missing; pair with a new code", plugin.PairingStatus);
        Assert.False(plugin.TestConnectionCommand.CanExecute(null));
        Click(reset);
        DrainBindings();
        Assert.Equal("Not paired", plugin.PairingStatus);
        Assert.False(plugin.IsOperationRunning);
        Assert.Equal(originalDestination.ServerUrl, plugin.ServerUrl);

        foreach (var succeed in new[] { false, true })
        {
            var pairingCode = succeed ? "fresh-code" : "expired-code";
            var requestTask = server.GetContextAsync();
            EnterText(code, pairingCode);
            Assert.True(pair.IsEnabled);
            Click(pair);
            PumpUntil(() => requestTask.IsCompleted);
            var context = requestTask.GetAwaiter().GetResult();
            Assert.True(plugin.IsOperationRunning);
            Assert.False(pair.IsEnabled);
            Assert.False(code.IsEnabled);
            Assert.False(reset.IsEnabled);
            Assert.Equal("POST", context.Request.HttpMethod);
            Assert.Equal("/api/sync/v1/pair", context.Request.Url!.AbsolutePath);
            Assert.Null(context.Request.Headers["Authorization"]);
            using (var reader = new StreamReader(context.Request.InputStream))
            using (var body = JsonDocument.Parse(reader.ReadToEnd()))
            {
                Assert.Equal(pairingCode, body.RootElement.GetProperty("pairing_token").GetString());
            }

            var response = succeed
                ? """
                  {"success":true,"data":{"catalog_id":"catalog-a","catalog_name":"Test catalog",
                  "client_uuid":"client-1","token":"new-token","product":"PSF Guard","product_version":"0.10.1"}}
                  """
                : """{"success":false,"error":"Pairing code expired"}""";
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = succeed ? 200 : 401;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
            PumpUntil(() => !plugin.IsOperationRunning && code.IsEnabled && (succeed || pair.IsEnabled));
            if (!succeed)
            {
                Assert.Single(errors);
                Assert.Contains("Pairing code expired", plugin.LastStatus);
                Assert.False(plugin.HasStoredCredential);
            }
        }

        Assert.Equal("Paired", plugin.PairingStatus);
        Assert.Equal("catalog-a", plugin.CatalogId);
        Assert.Empty(code.Text);
        Assert.Equal("new-token", fixture.ReadCredential(fixture.Settings.CredentialReference));
        Assert.Equal(originalDestination, fixture.Settings.CaptureSnapshot());
        Assert.True(plugin.TestConnectionCommand.CanExecute(null));
        Assert.True(reset.IsEnabled);
        panel.DataContext = null;
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var elapsed = Stopwatch.StartNew();
        while (!completed() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            DrainBindings();
            Thread.Sleep(5);
        }
        Assert.True(completed(), "Pairing exchange did not complete.");
    }

    private static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static void DrainBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetWorksWithPresentOrDeletedCredentials(bool deleted)
    {
        var fixture = new Fixture();
        fixture.Settings.Enabled = true;
        fixture.Settings.TargetSchedulerDatabase = "unchanged.sqlite";
        fixture.Pair("catalog-a", "old-token");
        var reference = fixture.Settings.CredentialReference;
        fixture.Credentials["unrelated-profile"] = "keep";
        if (deleted)
        {
            fixture.Credentials.Remove(reference);
        }

        fixture.Settings.ResetPairing();
        fixture.Settings.EnsurePairingMetadata();

        Assert.False(fixture.Settings.HasPairingMetadata);
        Assert.Equal(string.Empty, fixture.Settings.CatalogId);
        Assert.Equal(PairingAvailability.NotPaired,
            fixture.Settings.GetPairingAvailability(fixture.Settings.ServerUrl));
        Assert.False(fixture.Credentials.ContainsKey(reference));
        Assert.Equal("keep", fixture.Credentials["unrelated-profile"]);
        Assert.True(fixture.Settings.Enabled);
        Assert.Equal("unchanged.sqlite", fixture.Settings.TargetSchedulerDatabase);
        Assert.Throws<InvalidOperationException>(() => fixture.Settings.CaptureSnapshot().RequireQueueDestination());

        fixture.Pair("catalog-a", "new-token");
        Assert.Equal(reference, fixture.Settings.CredentialReference);
        Assert.True(fixture.Settings.HasPairingCredential);
    }

    [Fact]
    public void ResetDoesNotMigrateOldPairingBackAndPreservesLegacyQueueReference()
    {
        var fixture = new Fixture();
        fixture.Options.SetValue(PluginSettings.PluginId, "CatalogId", "legacy-catalog");
        fixture.Settings.EnsurePairingMetadata();
        var reference = fixture.Settings.CredentialReference;
        Assert.Equal(PluginSettings.LegacyCredentialReferenceFor(fixture.ProfileId), reference);

        fixture.Settings.ResetPairing();
        fixture.Settings.EnsurePairingMetadata();
        Assert.False(fixture.Settings.HasPairingMetadata);
        Assert.Equal(string.Empty, fixture.Settings.CatalogId);

        fixture.Pair("other-catalog", "other-token");
        Assert.NotEqual(reference, fixture.Settings.CredentialReference);
        var otherReference = fixture.Settings.CredentialReference;
        fixture.Pair("legacy-catalog", "replacement-token");
        Assert.Equal(reference, fixture.Settings.CredentialReference);
        Assert.Equal("other-token", fixture.Credentials[otherReference]);
    }

    [Fact]
    public void ResetClearsMalformedMetadataWithoutDeletingUnrelatedCredentials()
    {
        var fixture = new Fixture();
        fixture.Options.SetValue(PluginSettings.PluginId, "PairingMetadataV1", "not json");
        fixture.Credentials["unrelated"] = "keep";
        Assert.True(fixture.Settings.HasPairingMetadata);

        fixture.Settings.ResetPairing();

        Assert.False(fixture.Settings.HasPairingMetadata);
        Assert.Single(fixture.Credentials);
    }

    [Fact]
    public void CredentialStoreFailureLeavesPairingMetadataIntact()
    {
        var fixture = new Fixture();
        fixture.Pair("catalog-a", "old-token");
        fixture.FailCredentialAccess = true;

        Assert.Equal(PairingAvailability.CredentialUnavailable,
            fixture.Settings.GetPairingAvailability(fixture.Settings.ServerUrl));
        Assert.Throws<Win32Exception>(fixture.Settings.ResetPairing);
        Assert.True(fixture.Settings.HasPairingMetadata);
        Assert.Equal("catalog-a", fixture.Settings.CatalogId);
        fixture.FailCredentialAccess = false;
        Assert.True(fixture.Settings.HasPairingCredential);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool nativeCredentials;
        private readonly HashSet<string> ownedCredentials = [];
        public Guid ProfileId { get; } = Guid.NewGuid();
        public NINA.Profile.PluginSettings Options { get; } = new();
        public Dictionary<string, string?> Credentials { get; } = [];
        public PluginSettings Settings { get; }
        public IProfileService Service { get; }
        public bool FailCredentialAccess { get; set; }

        public Fixture(bool nativeCredentials = false)
        {
            this.nativeCredentials = nativeCredentials;
            var profile = Stub<IProfile>(new()
            {
                ["get_Id"] = ProfileId,
                ["get_Name"] = "Isolated test profile",
                ["get_PluginSettings"] = Options,
            });
            Service = Stub<IProfileService>(new() { ["get_ActiveProfile"] = profile });
            Settings = new PluginSettings(Service,
                target =>
                {
                    if (FailCredentialAccess)
                    {
                        throw new Win32Exception(5);
                    }
                    return ReadCredential(target);
                },
                (target, value) =>
                {
                    if (FailCredentialAccess)
                    {
                        throw new Win32Exception(5);
                    }
                    if (nativeCredentials)
                    {
                        if (!ownedCredentials.Contains(target))
                        {
                            Assert.Null(WindowsCredentialStore.Read(target));
                            ownedCredentials.Add(target);
                        }
                        WindowsCredentialStore.Write(target, value);
                        return;
                    }
                    if (value is null)
                    {
                        Credentials.Remove(target);
                    }
                    else
                    {
                        Credentials[target] = value;
                    }
                });
            Settings.ServerUrl = "https://psf.example/";
        }

        public void Pair(string catalogId, string token) =>
            Settings.StorePairing(Settings.CapturePairingTarget(), catalogId, token);

        public string? ReadCredential(string target) => nativeCredentials
            ? WindowsCredentialStore.Read(target)
            : Credentials.GetValueOrDefault(target);

        public void DeleteCredentialExternally()
        {
            var target = Settings.CredentialReference;
            if (!nativeCredentials)
            {
                Credentials.Remove(target);
                return;
            }

            Assert.Contains(target, ownedCredentials);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmdkey.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add($"/delete:{target}");
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("Deleting the disposable test credential timed out.");
            }
            Assert.Equal(0, process.ExitCode);
            Assert.Null(WindowsCredentialStore.Read(target));
        }

        public void Dispose()
        {
            foreach (var target in ownedCredentials)
            {
                WindowsCredentialStore.Write(target, null);
                Assert.Null(WindowsCredentialStore.Read(target));
            }
        }
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
