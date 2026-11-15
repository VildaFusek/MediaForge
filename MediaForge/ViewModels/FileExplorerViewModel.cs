using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaForge.Models;
using MediaForge.Services;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaForge.ViewModels;

public partial class FileExplorerViewModel : ObservableObject
{
    private readonly FileSystemService _fileSystemService = new();
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private readonly List<string> _clipboardPaths = new();
    private List<FileSystemItem> _allItems = new();
    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<FileSystemItem> Items { get; } = new();
    public ObservableCollection<FileSystemItem> Drives { get; } = new();
    public ObservableCollection<FileSystemItem> QuickAccess { get; } = new();
    public ObservableCollection<FileSystemItem> Breadcrumbs { get; } = new();
    public ObservableCollection<FileSystemItem> SelectedItems { get; } = new();

    [ObservableProperty] public partial string CurrentPath { get; set; } = "";
    [ObservableProperty] public partial string AddressPath { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "Připraveno";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsCopying { get; set; }
    [ObservableProperty] public partial bool IsVerifying { get; set; }
    [ObservableProperty] public partial double CopyProgress { get; set; }
    [ObservableProperty] public partial string CopyStatus { get; set; } = "";
    [ObservableProperty] public partial string VerificationStatus { get; set; } = "";
    [ObservableProperty] public partial string ClipboardStatus { get; set; } = "Interní schránka je prázdná";
    [ObservableProperty] public partial bool ClipboardIsCut { get; set; }
    [ObservableProperty] public partial bool HasClipboardItems { get; set; }
    [ObservableProperty] public partial FileSystemItem? SelectedItem { get; set; }
    [ObservableProperty] public partial string SelectionStatus { get; set; } = "Nic nevybráno";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowHidden { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial bool SortDescending { get; set; }
    [ObservableProperty] public partial bool ShowDetails { get; set; } = true;
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial bool HasOperation { get; set; }

    public bool IsIdle => !IsLoading && !IsCopying;
    public bool CanGoBack => IsIdle && _backHistory.Count > 0;
    public bool CanGoForward => IsIdle && _forwardHistory.Count > 0;
    public bool CanGoUp => IsIdle && !string.IsNullOrEmpty(CurrentPath)
        && Directory.GetParent(CurrentPath) is not null;
    public bool CanCreate => IsIdle && !string.IsNullOrEmpty(CurrentPath);
    public bool HasSelection => IsIdle && SelectedItems.Count > 0;
    public bool HasSingleSelection => IsIdle && SelectedItems.Count == 1;
    public bool CanPaste => CanCreate && HasClipboardItems;
    public bool CanVerify => HasSingleSelection && SelectedItem?.IsDirectory == true;
    public bool CanSmartRename => HasSingleSelection && SelectedItem is { IsDirectory: false, IsLink: false };
    public int SelectedItemsCount => SelectedItems.Count;
    public Visibility EmptyVisibility => !IsLoading && Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailsVisibility => ShowDetails ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OperationVisibility => HasOperation ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => _allItems.Count == 0 ? "Tato složka je prázdná" : "Žádné odpovídající položky";
    public string FolderTitle => string.IsNullOrEmpty(CurrentPath) ? "Soubory a složky"
        : new DirectoryInfo(CurrentPath).Name;
    public string ItemCountText => $"{Items.Count} z {_allItems.Count} položek";
    public string DetailsTitle => SelectedItem?.Name ?? (SelectedItems.Count > 1
        ? $"{SelectedItems.Count} vybraných položek" : "Podrobnosti");
    public string DetailsText => SelectedItem is { } item
        ? $"{item.TypeName}\n\n{item.FullPath}\n\nZměněno: {item.FormattedDate}\n"
          + (item.IsDirectory ? "Velikost složky se automaticky nepočítá." : $"Velikost: {item.FormattedSize} ({item.Size:N0} B)")
          + $"\n\nAtributy: {item.AttributesText}"
          + (item.IsDirectory ? "" : $"\n\n{item.Analysis?.Details ?? "Video dosud nebylo analyzováno."}")
        : SelectedItems.Count > 1 ? SelectionStatus + "\n\nVelikost zahrnuje jen vybrané soubory, ne obsah složek."
        : "Vyberte soubor nebo složku.\n\nDvojklik nebo Enter: otevřít\nF2: přejmenovat\nDelete: přesunout do Koše\nCtrl+Shift+N: nová složka\nCtrl+C / X / V: interní schránka\nAlt+← / → / ↑: navigace\nF5: aktualizovat\nCtrl+L: zadat cestu\nCtrl+F: filtrovat aktuální složku";

    public FileExplorerViewModel()
    {
        AddQuickAccess("Domů", Environment.SpecialFolder.UserProfile);
        AddQuickAccess("Plocha", Environment.SpecialFolder.DesktopDirectory);
        AddQuickAccess("Dokumenty", Environment.SpecialFolder.MyDocuments);
        AddQuickAccess("Obrázky", Environment.SpecialFolder.MyPictures);
        AddQuickAccess("Hudba", Environment.SpecialFolder.MyMusic);
        AddQuickAccess("Videa", Environment.SpecialFolder.MyVideos);
        // Downloads may be redirected; resolve the Windows Known Folder, not a guessed path.
        string? downloads = KnownFolderService.GetDownloadsPath();
        if (!string.IsNullOrEmpty(downloads))
            QuickAccess.Add(new() { Name = "Stažené soubory", FullPath = downloads, IsDirectory = true });
    }

    private void AddQuickAccess(string name, Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrEmpty(path))
            QuickAccess.Add(new() { Name = name, FullPath = path, IsDirectory = true });
    }

    partial void OnIsLoadingChanged(bool value) => NotifyState();
    partial void OnIsCopyingChanged(bool value) => NotifyState();
    partial void OnHasClipboardItemsChanged(bool value) => NotifyState();
    partial void OnShowDetailsChanged(bool value) => OnPropertyChanged(nameof(DetailsVisibility));
    partial void OnHasOperationChanged(bool value) => OnPropertyChanged(nameof(OperationVisibility));
    partial void OnSearchTextChanged(string value) => ApplyView();
    partial void OnShowHiddenChanged(bool value) => ApplyView();
    partial void OnSortIndexChanged(int value) => ApplyView();
    partial void OnSortDescendingChanged(bool value) => ApplyView();

    private void NotifyState()
    {
        foreach (string name in new[] { nameof(IsIdle), nameof(CanGoBack), nameof(CanGoForward),
            nameof(CanGoUp), nameof(CanCreate), nameof(HasSelection), nameof(HasSingleSelection),
            nameof(CanPaste), nameof(CanVerify), nameof(CanSmartRename), nameof(CanPreview), nameof(EmptyVisibility), nameof(EmptyTitle),
            nameof(FolderTitle), nameof(ItemCountText), nameof(DetailsTitle), nameof(DetailsText) })
            OnPropertyChanged(name);
    }

    public void UpdateSelection(IEnumerable<FileSystemItem> selection)
    {
        var items = selection.ToList();
        SelectedItems.Clear();
        foreach (var item in items) SelectedItems.Add(item);
        SelectedItem = items.Count == 1 ? items[0] : null;
        long size = items.Where(i => !i.IsDirectory).Sum(i => i.Size);
        SelectionStatus = items.Count == 0 ? "Nic nevybráno"
            : $"Vybráno: {items.Count} • Soubory: {FormatSize(size)}";
        OnPropertyChanged(nameof(SelectedItemsCount));
        NotifyState();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private void ApplyView(bool incremental = false)
    {
        RefreshMetadataFilterOptions();
        var selectedPaths = SelectedItems.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = _allItems.Where(i => (ShowHidden || !i.IsHidden)
            && i.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)
            && MatchesMetadataFilters(i));
        // Folders always first, independently of sort direction.
        Func<FileSystemItem, IComparable> key = SortIndex switch
        {
            1 => i => i.DateModified,
            2 => i => i.TypeName,
            3 => i => i.Size,
            _ => i => i.Name
        };
        var groups = filtered.OrderByDescending(i => i.IsDirectory);
        var comparer = Comparer<IComparable>.Create((a, b) => a is string left && b is string right
            ? StringComparer.CurrentCultureIgnoreCase.Compare(left, right) : a.CompareTo(b));
        var sorted = SortDescending ? groups.ThenByDescending(key, comparer) : groups.ThenBy(key, comparer);
        var desired = sorted.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        bool changed = !Items.SequenceEqual(desired);
        if (!incremental)
        {
            Items.Clear();
            foreach (var item in desired) Items.Add(item);
        }
        else if (changed)
        {
            var retained = desired.ToHashSet();
            for (int index = Items.Count - 1; index >= 0; index--)
                if (!retained.Contains(Items[index])) Items.RemoveAt(index);
            for (int index = 0; index < desired.Count; index++)
            {
                if (index < Items.Count && ReferenceEquals(Items[index], desired[index])) continue;
                int oldIndex = Items.IndexOf(desired[index]);
                if (oldIndex >= 0) Items.Move(oldIndex, index);
                else Items.Insert(index, desired[index]);
            }
        }
        UpdateSelection(Items.Where(i => selectedPaths.Contains(i.FullPath)));
        if (changed || !incremental) ViewChanged?.Invoke(this, EventArgs.Empty);
        NotifyState();
    }

    // The page restores ListView selection after a sort/filter resets its collection.
    public event EventHandler? ViewChanged;

    public void ReportError(string context, Exception exception)
    {
        AppLog.Error(context, exception);
        ErrorMessage = $"{context}: {exception.Message}";
        HasError = true;
        StatusText = ErrorMessage;
    }

    private async Task<bool> LoadDirectoryAsync(string path)
    {
        if (!IsIdle) return false;
        var selectedPaths = string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)
            ? SelectedItems.Select(item => item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IsLoading = true;
        StatusText = "Načítání složky…";
        try
        {
            StopDirectoryWatcher();
            await StopBackgroundAnalysisAsync();
            await ReconcileExternalChangesAsync();
            var result = await Task.Run(() =>
                (_fileSystemService.GetDirectoryContents(path), _fileSystemService.GetDrives()));
            _allItems = result.Item1;
            Drives.Clear();
            foreach (var drive in result.Item2) Drives.Add(drive);
            CurrentPath = path;
            AddressPath = path;
            UpdateSelection(_allItems.Where(item => selectedPaths.Contains(item.FullPath)));
            ApplyView();
            BuildBreadcrumbs(path);
            StatusText = ItemCountText;
            HasError = false;
            return true;
        }
        catch (DirectoryNotFoundException ex)
        {
            await Task.Run(() =>
            {
                try
                {
                    using var catalogLock = _catalog.AcquireMutationLock();
                    _catalog.RemoveMissingTree(path);
                }
                catch (Exception syncError) { AppLog.Error("Kontrola odstraněné složky " + path, syncError); }
            });
            if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _allItems.Clear();
                ApplyView();
            }
            ReportError("Nelze otevřít složku", ex);
            return false;
        }
        catch (Exception ex) { ReportError("Nelze otevřít složku", ex); return false; }
        finally
        {
            IsLoading = false;
            if (!string.IsNullOrEmpty(CurrentPath))
            {
                WatchDirectory(CurrentPath);
                await RestartBackgroundAnalysisAsync();
            }
        }
    }

    private void BuildBreadcrumbs(string path)
    {
        var chain = new Stack<DirectoryInfo>();
        for (DirectoryInfo? info = new(path); info is not null; info = info.Parent) chain.Push(info);
        Breadcrumbs.Clear();
        foreach (var info in chain)
            Breadcrumbs.Add(new() { Name = info.Name, FullPath = info.FullName, IsDirectory = true });
    }

    [RelayCommand]
    private async Task NavigateAsync(string? path)
    {
        if (!IsIdle || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, 
                string.IsNullOrEmpty(CurrentPath) ? Environment.CurrentDirectory : CurrentPath));
            string previous = CurrentPath;
            if (await LoadDirectoryAsync(target))
            {
                if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, target, StringComparison.OrdinalIgnoreCase))
                { _backHistory.Push(previous); _forwardHistory.Clear(); }
                NotifyState();
            }
        }
        catch (Exception ex) { ReportError("Neplatná cesta", ex); }
    }

    [RelayCommand]
    private async Task NavigateToAddressAsync() =>
        await NavigateAsync(Environment.ExpandEnvironmentVariables(AddressPath.Trim().Trim('"')));

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (!CanGoBack) return;
        string previous = CurrentPath;
        if (await LoadDirectoryAsync(_backHistory.Peek()))
        { _backHistory.Pop(); _forwardHistory.Push(previous); NotifyState(); }
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (!CanGoForward) return;
        string previous = CurrentPath;
        if (await LoadDirectoryAsync(_forwardHistory.Peek()))
        { _forwardHistory.Pop(); _backHistory.Push(previous); NotifyState(); }
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (CanGoUp) await NavigateAsync(Directory.GetParent(CurrentPath)?.FullName);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsIdle && !string.IsNullOrEmpty(CurrentPath)) await LoadDirectoryAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task OpenItemAsync(FileSystemItem? item)
    {
        if (!IsIdle || item is null) return;
        if (item.IsDirectory) { await NavigateAsync(item.FullPath); return; }
        try
        {
            // Use the registered application. No shell command-string concatenation.
            await Task.Run(() => Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }));
        }
        catch (Exception ex) { ReportError("Soubor nelze otevřít ve výchozí aplikaci", ex); }
    }

    [RelayCommand]
    private void CopyToClipboard() => SetClipboard(false);
    [RelayCommand]
    private void CutToClipboard() => SetClipboard(true);
    private void SetClipboard(bool cut)
    {
        if (!HasSelection) return;
        _clipboardPaths.Clear();
        _clipboardPaths.AddRange(SelectedItems.Select(i => i.FullPath));
        ClipboardIsCut = cut;
        UpdateClipboardStatus();
    }
    private void UpdateClipboardStatus()
    {
        HasClipboardItems = _clipboardPaths.Count > 0;
        ClipboardStatus = HasClipboardItems
            ? $"Schránka MediaForge: {_clipboardPaths.Count} položek • {(ClipboardIsCut ? "Přesun v rámci svazku" : "Kopírování")}"
            : "Interní schránka je prázdná";
    }
    [RelayCommand]
    private void ClearClipboard()
    {
        if (!IsIdle) return;
        _clipboardPaths.Clear();
        ClipboardIsCut = false;
        UpdateClipboardStatus();
    }
    [RelayCommand]
    private async Task PasteClipboardAsync()
    {
        if (CanPaste) await TransferAsync(_clipboardPaths.ToArray(), CurrentPath, ClipboardIsCut, false);
    }
    [RelayCommand]
    private async Task CopySelectedAsync(string? destinationFolder)
    {
        if (HasSelection && !string.IsNullOrWhiteSpace(destinationFolder))
            await TransferAsync(SelectedItems.Select(i => i.FullPath).ToArray(), destinationFolder, false, false);
    }
    [RelayCommand]
    private async Task MoveSelectedAsync(string? destinationFolder)
    {
        if (HasSelection && !string.IsNullOrWhiteSpace(destinationFolder))
            await TransferAsync(SelectedItems.Select(i => i.FullPath).ToArray(), destinationFolder, true, false);
    }
    [RelayCommand]
    private async Task CopyAndVerifySelectedDirectoryAsync(string? destinationFolder)
    {
        if (CanVerify && !string.IsNullOrWhiteSpace(destinationFolder))
            await TransferAsync([SelectedItem!.FullPath], destinationFolder, false, true);
    }

    private async Task TransferAsync(string[] sources, string destinationFolder, bool move, bool verify)
    {
        if (!IsIdle) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsCopying = true;
        HasOperation = true;
        HasError = false;
        CopyProgress = 0;
        CopyStatus = "Příprava operace…";
        VerificationStatus = "";
        int completed = 0;
        bool acceptProgress = true;
        string summary = "";
        try
        {
            await StopBackgroundAnalysisAsync();
            StopDirectoryWatcher();
            // Reject every known collision before starting a multi-item operation.
            await Task.Run(() =>
            {
                FileSystemService.EnsureNoReparsePoints(destinationFolder);
                using var catalogLock = _catalog.AcquireMutationLock();
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string source in sources)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    FileSystemService.EnsureNoReparsePoints(source);
                    string target = Path.Combine(destinationFolder, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
                    if (!targets.Add(target) || Path.Exists(target))
                        throw new IOException($"Cíl již existuje: {target}. Nic se nepřepisuje; zvolte jinou složku nebo přejmenujte položku.");
                    string prefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
                    if (Directory.Exists(source) && Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Cílová složka nesmí ležet uvnitř zdroje.");
                    if (!move) _catalog.RemoveMissingTree(target);
                }
            }, cancellation.Token);
            foreach (string source in sources)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                string destination = Path.Combine(destinationFolder, name);
                CopyStatus = $"{(move ? "Přesouvání" : "Kopírování")}: {name}";
                int index = completed;
                var fileProgress = new Progress<double>(percent =>
                {
                    if (acceptProgress && completed == index && !IsVerifying)
                        CopyProgress = (index + percent / 100) / sources.Length * 100;
                });
                var directoryProgress = new Progress<FileSystemService.CopyProgressInfo>(info =>
                {
                    if (!acceptProgress || completed != index || IsVerifying) return;
                    CopyProgress = (index + info.Percentage / 100) / sources.Length * 100;
                    CopyStatus = $"Kopírování: {info.CurrentFile}";
                });
                var phases = new Progress<FileSystemService.FileOperationStatus>(phase =>
                {
                    if (!acceptProgress) return;
                    IsVerifying = phase.Phase == FileSystemService.FileOperationPhase.Verifying;
                    VerificationStatus = phase.Message;
                });
                if (move)
                    await RunCatalogMutationAsync(source, destination,
                        () => Task.Run(() => _fileSystemService.MoveItemAsync(source, destination, cancellation.Token)),
                        () =>
                        {
                            completed++;
                            _clipboardPaths.RemoveAll(p => string.Equals(p, source, StringComparison.OrdinalIgnoreCase));
                        });
                else
                {
                    await Task.Run(async () =>
                    {
                        if (verify) await _fileSystemService.CopyAndVerifyDirectoryAsync(
                            source, destination, cancellation.Token, directoryProgress, phases);
                        else if (Directory.Exists(source)) await _fileSystemService.CopyDirectoryAsync(
                            source, destination, cancellation.Token, directoryProgress);
                        else await _fileSystemService.CopyFileAsync(source, destination, fileProgress, cancellation.Token);
                    }, cancellation.Token);
                    completed++;
                }
            }
            acceptProgress = false;
            CopyProgress = 100;
            summary = $"{(move ? "Přesunuto" : "Zkopírováno")}: {completed} položek.";
            if (verify) VerificationStatus = "SHA-256 ověřeno. Zdroj zůstal zachován.";
        }
        catch (OperationCanceledException)
        {
            summary = $"Zrušeno. Dokončeno: {completed}/{sources.Length}. Hotové cíle a případná část složky zůstávají zachovány.";
        }
        catch (Exception ex)
        {
            ReportError("Operace nebyla dokončena", ex);
            summary = $"Dokončeno: {completed}/{sources.Length}. {ex.Message} Případné částečné cíle zůstaly zachovány.";
        }
        finally
        {
            acceptProgress = false;
            IsVerifying = false;
            IsCopying = false;
            _operationCancellation = null;
            UpdateClipboardStatus();
            bool failed = HasError;
            await RefreshAsync();
            HasError = failed || HasError;
            CopyStatus = summary;
            StatusText = summary;
        }
    }

    [RelayCommand]
    private void CancelCopy()
    {
        _operationCancellation?.Cancel();
        CopyStatus = "Rušení… čekání na bezpečné dokončení aktuálního kroku.";
    }

    public void CancelPendingOperation() => _operationCancellation?.Cancel();

    [RelayCommand]
    private async Task CreateFolderAsync(string? name)
    {
        if (!CanCreate || string.IsNullOrWhiteSpace(name)) return;
        await RunMutationAsync(() => Task.Run(() => _fileSystemService.CreateDirectory(CurrentPath, name)),
            $"Složka „{name}“ byla vytvořena.");
    }
    [RelayCommand]
    private Task RenameItemAsync(string? name) => RenameCoreAsync(name);

    public Task RenameFromMetadataAsync(string name, VideoDiagnosticRecord expected) => RenameCoreAsync(name, expected);

    private async Task RenameCoreAsync(string? name, VideoDiagnosticRecord? expected = null)
    {
        if (!HasSingleSelection || string.IsNullOrWhiteSpace(name)) return;
        string source = SelectedItem!.FullPath;
        await RunMutationAsync(async () =>
        {
            FileSystemService.ValidateItemName(name);
            if (expected is not null && !string.Equals(expected.FilePath, source, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Výběr souboru se změnil; vytvořte nový návrh názvu.");
            string destination = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source))
                ?? throw new IOException("Kořenový adresář nelze přejmenovat."), name);
            await RunCatalogMutationAsync(source, destination,
                () => Task.Run(() =>
                {
                    if (expected is not null)
                    {
                        var current = Helpers.VideoFileIdentity.Read(source);
                        if (current.Identity != expected.FileIdentity || current.Size != expected.FileSize
                            || current.ModifiedUtcTicks != expected.ModifiedUtcTicks)
                            throw new IOException("Soubor se od návrhu názvu změnil; načtěte jeho metadata znovu.");
                    }
                    _fileSystemService.RenameItem(source, name);
                }));
        },
            $"Položka přejmenována na „{name}“.");
    }
    private async Task RunMutationAsync(Func<Task> action, string success)
    {
        IsLoading = true;
        string message = success;
        bool failed = false;
        try
        {
            StopDirectoryWatcher();
            await StopBackgroundAnalysisAsync();
            await action();
        }
        catch (Exception ex) { ReportError("Operace selhala", ex); message = ErrorMessage; failed = true; }
        finally { IsLoading = false; }
        await RefreshAsync();
        HasError = failed || HasError;
        StatusText = message;
    }

    // Invoked only after a confirmation dialog in the page.
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!HasSelection) return;
        var items = SelectedItems.ToArray();
        IsLoading = true;
        int completed = 0;
        string? failure = null;
        try
        {
            StopDirectoryWatcher();
            await StopBackgroundAnalysisAsync();
            foreach (var item in items)
            {
                await RunCatalogMutationAsync(item.FullPath, null,
                    () => Task.Run(() => _fileSystemService.MoveToRecycleBinAsync(item.FullPath)),
                    () => completed++);
            }
        }
        catch (Exception ex) { ReportError("Přesun do Koše selhal", ex); failure = ErrorMessage; }
        finally { IsLoading = false; }
        await RefreshAsync();
        HasError = failure is not null || HasError;
        StatusText = $"Do Koše přesunuto: {completed}/{items.Length}." + (failure is null ? "" : $" {failure}");
    }

    public Task InitializeAsync() => NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
