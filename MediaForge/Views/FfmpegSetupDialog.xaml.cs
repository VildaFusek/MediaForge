using MediaForge.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MediaForge.Views;

public sealed partial class FfmpegSetupDialog : ContentDialog
{
    private readonly FfmpegSetupViewModel _viewModel;

    public FfmpegSetupDialog(FfmpegSetupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Keep cancellation and its cleanup visible; close is available after completion.
        if (_viewModel.IsBusy) args.Cancel = true;
    }
}
