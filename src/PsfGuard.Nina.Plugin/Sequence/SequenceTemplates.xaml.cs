using System.ComponentModel.Composition;
using System.Windows;

namespace PsfGuard.Nina.Plugin.Sequence;

[Export(typeof(ResourceDictionary))]
public partial class SequenceTemplates : ResourceDictionary
{
    public SequenceTemplates()
    {
        InitializeComponent();
    }
}
