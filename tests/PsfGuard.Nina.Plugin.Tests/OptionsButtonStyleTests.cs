using System.Dynamic;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PsfGuard.Nina.Plugin.Tests;

public sealed class OptionsButtonStyleTests
{
    [Fact]
    public void SettingsButtonsUseNinaTemplatesAndSupportPairingRecovery()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                VerifyButtons();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF button checks timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void VerifyButtons()
    {
        var application = new Application();
        try
        {
            PairingRecoveryTests.VerifyMissingCredentialCommandStates();
            // Load the actual host templates, including their unusual foreground inheritance.
            foreach (var key in new[]
            {
                "ButtonBackgroundBrush", "ButtonBackgroundSelectedBrush", "BorderBrush",
                "BackgroundBrush", "PrimaryBrush", "SecondaryBrush", "SecondaryBackgroundBrush",
                "TertiaryBackgroundBrush", "ButtonForegroundDisabledBrush",
            })
            {
                application.Resources[key] = new SolidColorBrush(Colors.DarkGray);
            }
            var palette = new SolidColorBrush(Colors.White);
            var foreground = new SolidColorBrush();
            BindingOperations.SetBinding(foreground, SolidColorBrush.ColorProperty,
                new Binding(nameof(SolidColorBrush.Color)) { Source = palette });
            application.Resources["ButtonForegroundBrush"] = foreground;
            foreach (var file in new[] { "Button", "TextBlock" })
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/NINA.WPF.Base;component/Resources/Styles/{file}.xaml"),
                });
            }
            application.Resources.MergedDictionaries.Add(new Options());

            string[] commandNames =
            [
                "PairCommand", "ResetPairingCommand", "TestConnectionCommand", "StartQueuedUploadsCommand", "RetryBlockedCommand",
                "ReconcileCommand", "PushAllCommand", "PullMergedCatalogCommand", "PushPlanningCommand",
                "PullPlanningCommand", "PushGradesCommand", "PullGradesCommand", "ApplyPreviewCommand",
                "ForgetPreviewCommand",
            ];
            var command = new TestCommand();
            IDictionary<string, object?> viewModel = new ExpandoObject();
            foreach (var name in commandNames)
            {
                viewModel[name] = command;
            }
            viewModel["Enabled"] = true;
            viewModel["IsSettingsEditable"] = true;
            var template = (DataTemplate)application.Resources["PSF Guard Sync_Options"];
            var panel = (FrameworkElement)template.LoadContent();
            panel.DataContext = viewModel;
            panel.Measure(new Size(760, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            panel.UpdateLayout();
            DrainBindings();

            var buttons = Descendants<Button>(panel).ToArray();
            Assert.Equal(commandNames.Length, buttons.Length);
            Assert.Equal(commandNames.Order(), buttons.Select(button =>
                BindingOperations.GetBinding(button, Button.CommandProperty)!.Path.Path).Order());
            var nativeStyle = (Style)application.Resources["StandardButton"];
            var nativeTemplate = nativeStyle.Setters.OfType<Setter>()
                .Single(setter => setter.Property == Control.TemplateProperty).Value;
            foreach (var button in buttons)
            {
                Assert.Same(command, button.Command);
                Assert.Same(nativeTemplate, button.Template);
                Assert.True(button.IsEnabled);
                var label = Assert.Single(Descendants<TextBlock>(button));
                Assert.Same(button.Content, label);
                Assert.False(string.IsNullOrWhiteSpace(label.Text));
                Assert.Equal(label.Text, new ButtonAutomationPeer(button).GetName());
                Assert.Same(foreground, label.Foreground);
                Assert.NotSame(button.Background, label.Foreground);
                Assert.Equal(button.Padding, label.Margin);
            }

            // Theme changes must reach every label without recreating the settings page.
            palette.Color = Colors.Black;
            DrainBindings();
            foreach (var button in buttons)
            {
                var label = Assert.Single(Descendants<TextBlock>(button));
                Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
            }

            command.SetEnabled(false);
            DrainBindings();
            foreach (var button in buttons)
            {
                Assert.False(button.IsEnabled);
                Assert.Equal(0.4, button.Opacity);
            }
            command.SetEnabled(true);
            DrainBindings();
            Assert.All(buttons, button =>
            {
                Assert.True(button.IsEnabled);
                Assert.Equal(1.0, button.Opacity);
            });
        }
        finally
        {
            application.Shutdown();
        }
    }

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

    private sealed class TestCommand : ICommand
    {
        private bool enabled = true;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => enabled;
        public void Execute(object? parameter) => throw new NotSupportedException();

        public void SetEnabled(bool value)
        {
            enabled = value;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
