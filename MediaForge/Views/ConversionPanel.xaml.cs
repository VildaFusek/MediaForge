using MediaForge.Services;
using MediaForge.Models;
using MediaForge.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace MediaForge.Views;

public sealed partial class ConversionPanel : UserControl
{
    private ConversionViewModel ViewModel => ConversionViewModel.Instance;

    public ConversionPanel()
    {
        InitializeComponent();
        Surface.DataContext = ViewModel;
    }

    private void Monitor_Click(object sender, RoutedEventArgs e) => ConversionWindows.ShowMonitor();

    private async void Jobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var job = (sender as ListView)?.SelectedItem as ConversionJob;
        ViewModel.SelectedJob = job;
        await ViewModel.LoadInputTracksAsync(job is { Status: "Čeká" } ? job.Input : null);
    }

    private async void ReloadTracks_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.LoadInputTracksAsync(ViewModel.SelectedInput, refresh: true);

    private void Encoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: EncoderOption encoder }
            && ViewModel.Encoders.Any(option => ReferenceEquals(option, encoder)))
            ViewModel.SelectedEncoder = encoder;
    }

    private void VideoTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedInput is { } input
            && sender is ComboBox { SelectedItem: ConversionTrackOption track }
            && input.VideoTracks.Any(option => ReferenceEquals(option, track)))
            input.SelectedVideo = track;
    }

    private void AudioTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedInput is { } input
            && sender is ComboBox { SelectedItem: ConversionTrackOption track }
            && input.AudioTracks.Any(option => ReferenceEquals(option, track)))
            input.SelectedAudio = track;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            if (files is not null) ViewModel.AddInputs(files.Select(f => f.Path));
        }
        catch (Exception ex) { ViewModel.Message = "Nelze vybrat vstupy: " + ex.Message; }
    }

    private async void OutputFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId).PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.UseSourceDirectory = false;
                ViewModel.OutputDirectory = folder.Path;
            }
        }
        catch (Exception ex) { ViewModel.Message = "Nelze vybrat cílovou složku: " + ex.Message; }
    }

    private async void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.SelectedJob?.LogPath is not { Length: > 0 } path) return;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
                ViewModel.Message = "Systém nemá aplikaci pro otevření protokolu. Cestu lze zkopírovat.";
        }
        catch (Exception ex) { ViewModel.Message = "Nelze otevřít protokol: " + ex.Message; }
    }

    private async void PreviewSmartName_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = ViewModel.SelectedInput;
            if (input?.Metadata is not { } metadata || input.SelectedVideo?.Stream is not { } video)
            {
                ViewModel.Message = "Ve frontě nejprve vyberte čekající video a počkejte na načtení stop.";
                return;
            }
            var preview = SmartVideoNameService.ForOutputPreview(metadata,
                ViewModel.SelectedJob?.Settings.Profile ?? ViewModel.GetPreparedProfile(),
                video, input.SelectedAudio?.Stream);
            string name = SmartVideoNameService.Suggest(preview, ".mp4",
                audio: preview.Streams.FirstOrDefault(s => s.Type == "audio"));
            var dialog = new ContentDialog
            {
                Title = "Náhled chytrého názvu výstupu",
                Content = new TextBlock
                {
                    Text = name + "\n\nTento název se použije pouze při kolizi původního názvu s příponou .mp4."
                        + " Náhled vychází z vybraného profilu, finální název ze skutečného výstupu."
                        + " Při další kolizi se přidá číselný dovětek. Zdrojový soubor se nepřejmenuje.",
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                },
                CloseButtonText = "Zavřít", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { ViewModel.Message = "Nelze navrhnout název: " + ex.Message; }
    }
}
