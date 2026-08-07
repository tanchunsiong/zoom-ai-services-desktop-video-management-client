using System.Windows;
using ZTranscribe.App.ViewModels;

namespace ZTranscribe.App;

public partial class FloatingCaptionWindow : Window
{
    public FloatingCaptionWindow(LiveModeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
