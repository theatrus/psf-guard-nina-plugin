using System.ComponentModel.Composition;
using System.Windows;

namespace PsfGuard.Nina.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }
}
