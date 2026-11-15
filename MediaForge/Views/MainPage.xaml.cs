using MediaForge.Models;
using MediaForge.Services;
using MediaForge.Helpers;
using MediaForge.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;

namespace MediaForge;

public sealed partial class MainPage : Page
{
    public FileExplorerViewModel ViewModel { get; } = new();
    public FfmpegSetupViewModel Ffmpeg { get; } = new();
    public string HomePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private bool _dialogActive
    {
        get => ViewModel.IsInteractionActive;
        set => ViewModel.IsInteractionActive = value;
    }
    private bool _restoringSelection;

    public MainPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += MainPage_Loaded;
        ViewModel.ViewChanged += (_, _) =>
        {
            var selected = ViewModel.SelectedItems.ToArray();
            _restoringSelection = true;
            try
            {
                FilesListView.SelectedItems.Clear();
                foreach (var item in selected) FilesListView.SelectedItems.Add(item);
            }
            finally { _restoringSelection = false; }
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.ShowDetails)) UpdateResponsiveLayout();
            if (e.PropertyName == nameof(ViewModel.ShowThumbnails)) UpdateFilesLayout();
        };
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        UpdateResponsiveLayout();
        await ConversionViewModel.Instance.RecoverTemporaryFilesAsync();
        await ViewModel.InitializeAsync();
        await Ffmpeg.InitializeAsync();
        await ViewModel.RestartBackgroundAnalysisAsync();
        if (Ffmpeg.ShouldOfferSetup && !_dialogActive) await ShowFfmpegSetupAsync();
        await ConversionViewModel.Instance.InitializeAsync();
    }

    private async void ConvertVideos_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var conversion = ConversionViewModel.Instance;
            conversion.AddInputs(ViewModel.SelectedItems.Where(item => !item.IsDirectory && !item.IsLink)
                .Select(item => item.FullPath));
            Views.ConversionWindows.ShowControls();
            await conversion.InitializeAsync();
        }
        catch (Exception ex) { ViewModel.ReportError("Nelze otevřít převody", ex); }
    }

    private async void FfmpegStatus_Click(object sender, RoutedEventArgs e) => await ShowFfmpegSetupAsync();

    private async Task ShowFfmpegSetupAsync()
    {
        if (_dialogActive) return;
        _dialogActive = true;
        try
        {
            await ViewModel.PauseBackgroundAnalysisAsync();
            await new Views.FfmpegSetupDialog(Ffmpeg) { XamlRoot = XamlRoot }.ShowAsync();
        }
        catch (Exception ex) { ViewModel.ReportError("Nelze otevřít přípravu FFmpeg", ex); }
        finally
        {
            _dialogActive = false;
            await ViewModel.ResumeBackgroundAnalysisAsync();
        }
    }

    private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();
    private void SidebarToggle_Changed(object sender, RoutedEventArgs e) => UpdateResponsiveLayout();
    private void ConversionToggle_Changed(object sender, RoutedEventArgs e) => UpdateResponsiveLayout();

    public void ShowConversionPanel()
    {
        ConversionToggleButton.IsChecked = true;
        UpdateResponsiveLayout();
    }
    private void UpdateFilesLayout()
    {
        var selected = ViewModel.SelectedItems.ToArray();
        _restoringSelection = true;
        try
        {
            bool cards = ViewModel.ShowThumbnails;
            FilesColumnHeaders.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
            FilesListHost.MinWidth = cards ? 260 : 620;
            FilesScrollViewer.HorizontalScrollBarVisibility = cards ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            FilesListView.ItemsPanel = (ItemsPanelTemplate)Resources[cards ? "ThumbnailCardsPanel" : "FileRowsPanel"];
            FilesListView.ItemTemplate = (DataTemplate)Resources[cards ? "VideoCardTemplate" : "FileRowTemplate"];
            FilesListView.SelectedItems.Clear();
            foreach (var item in selected) FilesListView.SelectedItems.Add(item);
        }
        finally { _restoringSelection = false; }
    }
    private void UpdateResponsiveLayout()
    {
        if (SidebarPane is null || DetailsPane is null || ConversionPane is null) return;
        bool showConversion = ConversionToggleButton.IsChecked == true;
        bool sideBySide = ActualWidth >= 1250;
        ConversionPane.Visibility = showConversion ? Visibility.Visible : Visibility.Collapsed;
        ConversionColumn.Width = new GridLength(showConversion && sideBySide ? 560 : 0);
        Grid.SetColumn(ConversionPane, sideBySide ? 1 : 0);
        Grid.SetRow(ConversionPane, sideBySide ? 0 : 1);
        FilesWorkspaceRow.Height = new GridLength(1, GridUnitType.Star);
        ConversionWorkspaceRow.Height = showConversion && !sideBySide
            ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        bool showSidebar = SidebarToggleButton.IsChecked == true;
        SidebarColumn.Width = new GridLength(showSidebar
            ? showConversion && sideBySide ? 170 : ActualWidth >= 1100 ? 220 : 190 : 0);
        SidebarPane.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        double explorerWidth = ActualWidth - (showConversion && sideBySide ? 560 : 0);
        DetailsPane.Visibility = explorerWidth >= 1200 && ViewModel.ShowDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Pressed(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _dialogActive) return;
        // Conversion controls share the page, but must not trigger explorer shortcuts.
        if (FindAncestor<Views.ConversionPanel>(e.OriginalSource) is not null) return;
        bool ctrl = Pressed(VirtualKey.Control);
        bool alt = Pressed(VirtualKey.Menu);
        bool shift = Pressed(VirtualKey.Shift);
        if (ctrl && !alt && e.Key is VirtualKey.L or VirtualKey.F && ViewModel.IsIdle)
        {
            var textBox = e.Key == VirtualKey.L ? AddressTextBox : SearchTextBox;
            textBox.Focus(FocusState.Keyboard);
            textBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (FindAncestor<TextBox>(e.OriginalSource) is not null
            || FindAncestor<PasswordBox>(e.OriginalSource) is not null
            || FindAncestor<RichEditBox>(e.OriginalSource) is not null) return;
        if (!ViewModel.IsIdle) return;
        if (ctrl && !alt && e.Key == VirtualKey.A)
        {
            FilesListView.SelectAll();
            e.Handled = true;
            return;
        }
        if (alt && !ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Left: e.Handled = true; await ViewModel.GoBackCommand.ExecuteAsync(null); return;
                case VirtualKey.Right: e.Handled = true; await ViewModel.GoForwardCommand.ExecuteAsync(null); return;
                case VirtualKey.Up: e.Handled = true; await ViewModel.GoUpCommand.ExecuteAsync(null); return;
                case VirtualKey.Enter: e.Handled = true; await ShowPropertiesAsync(); return;
            }
        }
        if (!alt && !ctrl && e.Key == VirtualKey.F5)
        { e.Handled = true; await ViewModel.RefreshCommand.ExecuteAsync(null); return; }
        // File-editing shortcuts apply only within the list, never a text input or sidebar.
        if (FindAncestor<ListView>(e.OriginalSource) != FilesListView) return;
        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case VirtualKey.C: ViewModel.CopyToClipboardCommand.Execute(null); break;
                case VirtualKey.X: ViewModel.CutToClipboardCommand.Execute(null); break;
                case VirtualKey.V: await ViewModel.PasteClipboardCommand.ExecuteAsync(null); break;
                case VirtualKey.N when shift: await ShowNameDialogAsync(false); break;
                default: return;
            }
            e.Handled = true;
        }
        else if (!ctrl && !alt)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    e.Handled = true;
                    if (ViewModel.HasSingleSelection) await ViewModel.OpenItemCommand.ExecuteAsync(ViewModel.SelectedItem);
                    break;
                case VirtualKey.F2: e.Handled = true; await ShowNameDialogAsync(true); break;
                case VirtualKey.Delete:
                    e.Handled = true;
                    // Shift+Delete deliberately keeps safe recycle-bin semantics.
                    await ConfirmRecycleAsync();
                    break;
                case VirtualKey.Escape: FilesListView.SelectedItems.Clear(); e.Handled = true; break;
            }
        }
    }

    private async void AddressTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { ViewModel.AddressPath = ViewModel.CurrentPath; e.Handled = true; }
        if (e.Key != VirtualKey.Enter || !ViewModel.IsIdle) return;
        e.Handled = true;
        ViewModel.AddressPath = AddressTextBox.Text;
        await ViewModel.NavigateToAddressCommand.ExecuteAsync(null);
    }

    private async void NavigationLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !_dialogActive)
            await ViewModel.NavigateCommand.ExecuteAsync(path);
    }
    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogActive || !ViewModel.IsIdle) return;
        _dialogActive = true;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                Title = "Otevřít složku s videi", CommitButtonText = "Otevřít složku", ViewMode = PickerViewMode.List
            };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await ViewModel.NavigateCommand.ExecuteAsync(folder.Path);
        }
        catch (Exception ex) { ViewModel.ReportError("Složku nelze otevřít", ex); }
        finally { _dialogActive = false; }
    }
    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsIdle || sender is not Button { Tag: string tag } || !int.TryParse(tag, out int index)) return;
        if (ViewModel.SortIndex == index) ViewModel.SortDescending = !ViewModel.SortDescending;
        else { ViewModel.SortDescending = false; ViewModel.SortIndex = index; }
    }

    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_restoringSelection)
            ViewModel.UpdateSelection(FilesListView.SelectedItems.OfType<FileSystemItem>().ToArray());
    }
    private async void FilesListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_dialogActive || !ViewModel.IsIdle) return;
        if (FindAncestor<ListViewItem>(e.OriginalSource)?.Content is FileSystemItem item)
        {
            e.Handled = true;
            if (item.IsDirectory) await ViewModel.OpenItemCommand.ExecuteAsync(item);
            else if (!item.IsLink)
            {
                try
                {
                    var conversion = ConversionViewModel.Instance;
                    conversion.AddInputs((string[])[item.FullPath]);
                    ShowConversionPanel();
                    await conversion.InitializeAsync();
                }
                catch (Exception ex) { ViewModel.ReportError("Video nelze přidat do fronty", ex); }
            }
        }
    }
    private void FilesListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = FindAncestor<ListViewItem>(e.OriginalSource);
        if (row?.Content is FileSystemItem item)
        {
            // Preserve a multi-selection only when the clicked row belongs to it.
            if (!FilesListView.SelectedItems.Contains(item))
            { FilesListView.SelectedItems.Clear(); FilesListView.SelectedItems.Add(item); }
        }
        else FilesListView.SelectedItems.Clear();
    }
    private void FilesMenu_Opening(object sender, object e)
    {
        MenuPreview.IsEnabled = ViewModel.CanPreview;
        MenuOpen.IsEnabled = MenuRename.IsEnabled = MenuProperties.IsEnabled = ViewModel.HasSingleSelection;
        MenuCut.IsEnabled = MenuCopy.IsEnabled = MenuCopyTo.IsEnabled = MenuMoveTo.IsEnabled =
            MenuDelete.IsEnabled = ViewModel.HasSelection;
        MenuPaste.IsEnabled = ViewModel.CanPaste;
        MenuNewFolder.IsEnabled = ViewModel.CanCreate;
        MenuVerify.IsEnabled = ViewModel.CanVerify;
    }
    private async void OpenItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasSingleSelection) await ViewModel.OpenItemCommand.ExecuteAsync(ViewModel.SelectedItem);
    }
    private void SelectAllButton_Click(object sender, RoutedEventArgs e) => FilesListView.SelectAll();
    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e) => FilesListView.SelectedItems.Clear();

    private async void CreateFolderButton_Click(object sender, RoutedEventArgs e) => await ShowNameDialogAsync(false);
    private async void RenameItemButton_Click(object sender, RoutedEventArgs e) => await ShowNameDialogAsync(true);
    private async void SmartRename_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogActive || !ViewModel.HasSingleSelection
            || ViewModel.SelectedItem is not { IsDirectory: false, IsLink: false } item) return;
        _dialogActive = true;
        try
        {
            if (FfmpegService.Instance.Current is not { } installation)
                throw new InvalidOperationException("Nejprve připravte FFmpeg pro přečtení metadat.");
            await ViewModel.PauseBackgroundAnalysisAsync();
            var record = await Task.Run(async () =>
            {
                VideoConversionService.EnsureSafePath(item.FullPath);
                return await VideoDiagnosticsService.ProbeAsync(item.FullPath, installation, _ => { }, CancellationToken.None);
            });
            var audio = MediaLanguage.SelectAudio(record.Streams, MediaLanguage.SystemLanguage);
            string name = SmartVideoNameService.Suggest(record, System.IO.Path.GetExtension(item.FullPath), audio: audio);
            var text = new TextBox { Text = name, MaxLength = 255, Header = "Navržený název (lze upravit)" };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = $"Původní název: {item.Name}\n\nTitul z metadat, jinak původní název; rozlišení a kodek videa,"
                    + " zvuk dle jazyka Windows. Obsah ani stopy souboru se nemění.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(text);
            var dialog = new ContentDialog
            {
                Title = "Chytře přejmenovat podle metadat",
                Content = content, PrimaryButtonText = "Přejmenovat", CloseButtonText = "Zrušit",
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.RenameFromMetadataAsync(text.Text, record);
        }
        catch (Exception ex) { ViewModel.ReportError("Chytré přejmenování nelze dokončit", ex); }
        finally
        {
            _dialogActive = false;
            await ViewModel.ResumeBackgroundAnalysisAsync();
        }
    }

    private async Task ShowNameDialogAsync(bool rename)
    {
        if (_dialogActive || (rename ? !ViewModel.HasSingleSelection : !ViewModel.CanCreate)) return;
        _dialogActive = true;
        try
        {
            var item = ViewModel.SelectedItem;
            var input = new TextBox
            {
                Text = rename ? item!.Name : "Nová složka",
                PlaceholderText = "Název", MaxLength = 255
            };
            var dialog = new ContentDialog
            {
                Title = rename ? "Přejmenovat položku" : "Vytvořit složku",
                Content = input, PrimaryButtonText = rename ? "Přejmenovat" : "Vytvořit",
                CloseButtonText = "Zrušit", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot
            };
            input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
            dialog.Opened += (_, _) =>
            {
                input.Focus(FocusState.Programmatic);
                int length = rename && !item!.IsDirectory ? item.Name.Length - item.Extension.Length : input.Text.Length;
                input.Select(0, Math.Max(0, length));
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            // Do not trim: trailing periods/spaces must be rejected, not silently renamed.
            if (rename) await ViewModel.RenameItemCommand.ExecuteAsync(input.Text);
            else await ViewModel.CreateFolderCommand.ExecuteAsync(input.Text);
        }
        catch (Exception ex) { ViewModel.ReportError("Dialog nelze dokončit", ex); }
        finally { _dialogActive = false; }
    }

    private async void DeleteFilesButton_Click(object sender, RoutedEventArgs e) => await ConfirmRecycleAsync();
    private async Task ConfirmRecycleAsync()
    {
        if (_dialogActive || !ViewModel.HasSelection) return;
        _dialogActive = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Přesunout do Koše?",
                Content = ViewModel.SelectedItemsCount == 1
                    ? $"Položka „{ViewModel.SelectedItem!.Name}“ bude přesunuta do Koše."
                        + (ViewModel.SelectedItem.IsDirectory ? "\n\nSložka se přesune celá, včetně skrytých a nevideo souborů, které se zde nezobrazují." : "")
                    : $"{ViewModel.SelectedItemsCount} vybraných položek bude přesunuto do Koše."
                        + (ViewModel.SelectedItems.Any(item => item.IsDirectory)
                            ? "\n\nSložky se přesunou celé, včetně skrytých a nevideo souborů, které se zde nezobrazují." : ""),
                PrimaryButtonText = "Přesunout do Koše", CloseButtonText = "Zrušit",
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { ViewModel.ReportError("Přesun do Koše nelze dokončit", ex); }
        finally { _dialogActive = false; }
    }

    private async void CopyFilesButton_Click(object sender, RoutedEventArgs e) => await PickDestinationAsync(false, false);
    private async void MoveFilesButton_Click(object sender, RoutedEventArgs e) => await PickDestinationAsync(true, false);
    private async void CopyAndVerifyButton_Click(object sender, RoutedEventArgs e) => await PickDestinationAsync(false, true);
    private async Task PickDestinationAsync(bool move, bool verify)
    {
        if (_dialogActive || !ViewModel.HasSelection || (verify && !ViewModel.CanVerify)) return;
        _dialogActive = true;
        IsEnabled = false;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                Title = move ? "Vyberte cíl přesunu (stejný svazek)" : "Vyberte cíl kopírování",
                CommitButtonText = "Vybrat složku", ViewMode = PickerViewMode.List
            };
            var destination = await picker.PickSingleFolderAsync();
            if (destination is null || string.IsNullOrWhiteSpace(destination.Path)) return;
            IsEnabled = true;
            if (move) await ViewModel.MoveSelectedCommand.ExecuteAsync(destination.Path);
            else if (verify) await ViewModel.CopyAndVerifySelectedDirectoryCommand.ExecuteAsync(destination.Path);
            else await ViewModel.CopySelectedCommand.ExecuteAsync(destination.Path);
        }
        catch (Exception ex) { ViewModel.ReportError("Nelze vybrat cílovou složku", ex); }
        finally { IsEnabled = true; _dialogActive = false; }
    }

    private async void PropertiesButton_Click(object sender, RoutedEventArgs e) => await ShowPropertiesAsync();
    private async Task ShowPropertiesAsync()
    {
        if (_dialogActive || !ViewModel.HasSingleSelection) return;
        _dialogActive = true;
        try
        {
            await new ContentDialog
            {
                Title = ViewModel.DetailsTitle,
                Content = new ScrollViewer
                {
                    Content = new TextBlock { Text = ViewModel.DetailsText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                },
                CloseButtonText = "Zavřít", XamlRoot = XamlRoot
            }.ShowAsync();
        }
        catch (Exception ex) { ViewModel.ReportError("Nelze zobrazit vlastnosti", ex); }
        finally { _dialogActive = false; }
    }
}
