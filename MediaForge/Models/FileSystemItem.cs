
using System;
using System.IO;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MediaForge.Models
{
    public partial class FileSystemItem : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAnalyzed))]
        [NotifyPropertyChangedFor(nameof(AnalysisLabel))]
        [NotifyPropertyChangedFor(nameof(ItemForeground))]
        [NotifyPropertyChangedFor(nameof(ItemOpacity))]
        public partial VideoDiagnosticRecord? Analysis { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ThumbnailPlaceholderVisibility))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImage))]
        public partial string? ThumbnailUri { get; set; }
        public ImageSource? ThumbnailImage => string.IsNullOrEmpty(ThumbnailUri)
            ? null : new BitmapImage(new Uri(ThumbnailUri)) { DecodePixelWidth = 480 };
        public Visibility ThumbnailPlaceholderVisibility => string.IsNullOrEmpty(ThumbnailUri)
            ? Visibility.Visible : Visibility.Collapsed;

        partial void OnAnalysisChanged(VideoDiagnosticRecord? oldValue, VideoDiagnosticRecord? newValue)
        {
            if (oldValue?.FileIdentity != newValue?.FileIdentity || oldValue?.FileSize != newValue?.FileSize
                || oldValue?.ModifiedUtcTicks != newValue?.ModifiedUtcTicks)
                ThumbnailUri = null;
        }

        public bool IsAnalyzed => Analysis is not null;
        public string AnalysisLabel => IsDirectory ? "" : Analysis?.Summary ?? "Čeká na analýzu";
        public double ItemOpacity => IsDirectory || IsAnalyzed ? 1 : 0.65;
        public SolidColorBrush ItemForeground => new(IsDirectory ? Colors.Goldenrod
            : IsAnalyzed ? Colors.DeepSkyBlue : Colors.Gray);

        // Název souboru nebo složky
        public string Name { get; set; } = string.Empty;

        // Úplná cesta k položce
        public string FullPath { get; set; } = string.Empty;

        // Typ položky
        public bool IsDirectory { get; set; }

        // Velikost v bajtech
        public long Size { get; set; }

        // Datum poslední změny
        public DateTime DateModified { get; set; }

        // Přípona souboru
        public string Extension { get; set; } = string.Empty;

        public FileAttributes Attributes { get; set; }
        public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
        public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;
        public string AttributesText => Attributes.ToString();

        // Název typu pro zobrazení
        public string TypeName
        {
            get
            {
                if (IsLink)
                    return "Odkaz";
                if (IsDirectory)
                    return "Složka";

                if (string.IsNullOrEmpty(Extension))
                    return "Soubor";

                return Extension.TrimStart('.').ToUpperInvariant() + " soubor";
            }
        }


        /// <summary>
        /// Vrací odpovídající WinUI ikonu podle typu položky.
        /// </summary>
        public Symbol DisplayIcon
        {
            get
            {
                if (IsDirectory)
                    return Symbol.Folder;

                return Extension.ToLowerInvariant() switch
                {
                    // Video soubory
                    ".mp4" or ".mkv" or ".avi" or
                    ".mov" or ".wmv" or ".webm" or
                    ".flv" or ".m4v" or ".ts" or ".mpg"
                        => Symbol.Video,

                    // Zvukové soubory
                    ".mp3" or ".wav" or ".flac" or
                    ".aac" or ".ogg" or ".m4a" or
                    ".wma" or ".opus"
                        => Symbol.Audio,

                    // Obrázky
                    ".jpg" or ".jpeg" or ".png" or
                    ".gif" or ".bmp" or ".webp" or
                    ".tiff" or ".heic"
                        => Symbol.Pictures,

                    // Archivy
                    ".zip" or ".rar" or ".7z" or
                    ".tar" or ".gz"
                        => Symbol.Folder,

                    // Ostatní soubory
                    _ => Symbol.Document
                };
            }
        }

        // Formátovaná velikost
        public string FormattedSize
        {
            get
            {
                if (IsDirectory)
                    return string.Empty;

                string[] units = { "B", "KB", "MB", "GB", "TB" };

                double size = Size;
                int unit = 0;

                while (size >= 1024 && unit < units.Length - 1)
                {
                    size /= 1024;
                    unit++;
                }

                return unit == 0
                    ? $"{size:0} {units[unit]}"
                    : $"{size:0.##} {units[unit]}";
            }
        }

        // Formátované datum
        public string FormattedDate =>
            DateModified == DateTime.MinValue
                ? string.Empty
                : DateModified.ToString("g");
    }
}
