using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Data;
using System.Windows.Navigation;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

namespace PsfGuard.Nina.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }

    private void OptionsLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: PsfGuardPlugin plugin })
        {
            plugin.RefreshPairingState();
        }
    }

    private void PrivacyLinkRequestNavigate(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://github.com/theatrus/psf-guard-nina-plugin/blob/main/docs/PRIVACY.md")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            Logger.Error(exception);
            Notification.ShowError("Could not open the data and privacy guide. See docs/PRIVACY.md in the plugin source repository.");
        }
    }

    private void StatusTextTargetUpdated(object sender, DataTransferEventArgs args)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(element)
            ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
