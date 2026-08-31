using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Data;

namespace PsfGuard.Nina.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
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
