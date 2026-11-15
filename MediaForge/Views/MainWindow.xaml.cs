using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MediaForge
{
    /// <summary>
    /// The application window. This hosts a Frame that displays pages. Add your
    /// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
    /// can use Page features such as navigation events and the Loaded lifecycle.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private bool _closeDialogActive;
        private bool _analysisShutdownPending;
        private bool _analysisShutdownComplete;
        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            AppWindow.SetIcon("Assets/AppIcon.ico");
            AppWindow.Resize(new SizeInt32(1360, 860));
            AppWindow.Closing += AppWindow_Closing;
            Views.ConversionWindows.ActivateControls = () =>
            {
                WorkspaceTabs.SelectedItem = ConversionTab;
                Activate();
            };

            // Navigate the root frame to the main page on startup.
            RootFrame.Navigate(typeof(MainPage));
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem { Tag: string name }
                && Enum.TryParse<ElementTheme>(name, out var theme))
                ShellRoot.RequestedTheme = theme;
        }

        private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_analysisShutdownComplete) return;
            var conversion = ViewModels.ConversionViewModel.Instance;
            if (conversion.IsDetecting || conversion.IsLoadingTracks)
            {
                args.Cancel = true;
                conversion.CancelDetection();
                return;
            }
            if (conversion.IsRunning)
            {
                args.Cancel = true;
                if (_closeDialogActive) return;
                _closeDialogActive = true;
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Probíhá fronta převodů",
                        Content = "Před ukončením aplikace dokončete nebo zrušte převod. Čekající fronta se po restartu neobnovuje. Zavření monitoru ani přepnutí karty převod neruší.",
                        PrimaryButtonText = "Zrušit převod a zastavit frontu",
                        CloseButtonText = "Pokračovat v práci",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = ShellRoot.XamlRoot
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                        conversion.CancelCommand.Execute(null);
                }
                catch (Exception ex)
                {
                    if (RootFrame.Content is MainPage errorPage)
                        errorPage.ViewModel.ReportError("Nelze zavřít aplikaci během převodu", ex);
                }
                finally { _closeDialogActive = false; }
                return;
            }
            if (RootFrame.Content is MainPage { Ffmpeg.IsBusy: true } setupPage)
            {
                args.Cancel = true;
                setupPage.Ffmpeg.CancelCommand.Execute(null);
                return;
            }
            if (RootFrame.Content is not MainPage page) return;
            if (page.ViewModel.IsInteractionActive)
            {
                args.Cancel = true;
                return;
            }
            if (page.ViewModel.IsIdle)
            {
                args.Cancel = true;
                if (_analysisShutdownPending) return;
                _analysisShutdownPending = true;
                conversion.IsShuttingDown = true;
                try
                {
                    await page.ViewModel.ShutdownAnalysisAsync();
                    Views.ConversionWindows.CloseAll();
                    _analysisShutdownComplete = true;
                    Close();
                }
                catch (Exception ex)
                {
                    _analysisShutdownPending = false;
                    conversion.IsShuttingDown = false;
                    page.ViewModel.ReportError("Nelze bezpečně ukončit proces na pozadí", ex);
                }
                return;
            }
            args.Cancel = true;
            if (_closeDialogActive) return;
            _closeDialogActive = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Probíhá souborová operace",
                    Content = "Před zavřením počkejte na dokončení operace. Kopírování lze zrušit; dokončené cíle a případné částečné složky zůstanou zachovány. Poté okno zavřete znovu.",
                    PrimaryButtonText = "Zrušit probíhající kopírování",
                    IsPrimaryButtonEnabled = page.ViewModel.IsCopying,
                    CloseButtonText = "Pokračovat v práci",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = ShellRoot.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    page.ViewModel.CancelPendingOperation();
            }
            catch (Exception ex) { page.ViewModel.ReportError("Nelze zavřít okno během operace", ex); }
            finally { _closeDialogActive = false; }
        }
    }
}
