
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaForge.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Runtime.InteropServices;


namespace MediaForge.Services
{
    public class FileSystemService

    {

        public enum FileOperationPhase
        {
            Preparing,
            Copying,
            Verifying,
            Completed
        }

        public sealed record FileOperationStatus(
            FileOperationPhase Phase,
            double Percentage,
            string Message);


        public string CreateDirectory(string parentPath, string folderName)
        {
            if (string.IsNullOrWhiteSpace(parentPath))
                throw new ArgumentException("Není zadán cílový adresář.");

            if (!Directory.Exists(parentPath))
                throw new DirectoryNotFoundException(
                    $"Adresář neexistuje: {parentPath}");

            ValidateItemName(folderName);

            string newPath = Path.Combine(parentPath, folderName);

            if (Path.Exists(newPath))
                throw new IOException("Položka s tímto názvem již existuje.");

            EnsureNoReparsePoints(parentPath);
            CreateNewDirectory(newPath);

            return newPath;
        }


        public async Task MoveToRecycleBinAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath) &&
                !Directory.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "Soubor nebo složka neexistuje.",
                    fullPath);
            }

            cancellationToken.ThrowIfCancellationRequested();

            EnsureNoReparsePoints(fullPath);
            await RecycleBinService.RecycleAsync(fullPath, cancellationToken);
        }


        public async Task<bool> CopyAndVerifyDirectoryAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default,
            IProgress<CopyProgressInfo>? progress = null,
            IProgress<FileOperationStatus>? operationStatus = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string source = Path.GetFullPath(sourcePath);
            string destination = Path.GetFullPath(destinationPath);

            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException(
                    $"Zdrojový adresář neexistuje: {source}");
            }

            if (Path.Exists(destination))
            {
                throw new IOException(
                    "Cílový adresář již existuje.");
            }

            // FÁZE 1: Příprava
            operationStatus?.Report(new FileOperationStatus(
                FileOperationPhase.Preparing,
                0,
                "Příprava kopírování..."));

            cancellationToken.ThrowIfCancellationRequested();

            // FÁZE 2: Kopírování
            operationStatus?.Report(new FileOperationStatus(
                FileOperationPhase.Copying,
                0,
                "Kopírování souborů..."));

            await CopyDirectoryAsync(
                source,
                destination,
                cancellationToken,
                progress);

            cancellationToken.ThrowIfCancellationRequested();

            // FÁZE 3: Ověřování
            operationStatus?.Report(new FileOperationStatus(
                FileOperationPhase.Verifying,
                0,
                "Ověřování integrity SHA-256..."));

            bool verified = await VerifyDirectoryIntegrityAsync(
                source,
                destination,
                cancellationToken);

            if (!verified)
            {
                throw new IOException(
                    "Kontrola integrity adresáře selhala. " +
                    "Zdrojová složka zůstala zachována.");
            }

            // FÁZE 4: Dokončení
            operationStatus?.Report(new FileOperationStatus(
                FileOperationPhase.Completed,
                100,
                "Kopírování a ověření dokončeno."));

            return true;
        }



        /// <summary>
        /// Asynchronně vypočítá SHA-256 kontrolní součet souboru.
        /// </summary>
        public async Task<string> CalculateFileHashAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            string fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "Soubor pro výpočet SHA-256 neexistuje.",
                    fullPath);
            }

            EnsureNoReparsePoints(fullPath);
            const int bufferSize = 1024 * 1024;

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            byte[] hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken);

            return Convert.ToHexString(hash);
        }


        /// <summary>
        /// Ověří, zda mají dva soubory totožný obsah.
        /// Nejprve porovná velikost a následně SHA-256.
        /// </summary>
        public async Task<bool> VerifyFileIntegrityAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string source = Path.GetFullPath(sourcePath);
            string destination = Path.GetFullPath(destinationPath);

            var sourceInfo = new FileInfo(source);
            var destinationInfo = new FileInfo(destination);

            if (!sourceInfo.Exists)
            {
                throw new FileNotFoundException(
                    "Zdrojový soubor neexistuje.",
                    source);
            }

            if (!destinationInfo.Exists)
            {
                throw new FileNotFoundException(
                    "Cílový soubor neexistuje.",
                    destination);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Rozdílná velikost znamená rozdílný obsah.
            if (sourceInfo.Length != destinationInfo.Length)
                return false;

            // Výpočet hashů.
            string sourceHash = await CalculateFileHashAsync(
                source,
                cancellationToken);

            string destinationHash = await CalculateFileHashAsync(
                destination,
                cancellationToken);

            return string.Equals(
                sourceHash,
                destinationHash,
                StringComparison.OrdinalIgnoreCase);
        }


        public async Task MoveItemAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string source = Path.GetFullPath(sourcePath);
            string destination = Path.GetFullPath(destinationPath);

            cancellationToken.ThrowIfCancellationRequested();

            bool isFile = File.Exists(source);
            bool isDirectory = Directory.Exists(source);

            if (!isFile && !isDirectory)
                throw new FileNotFoundException(
                    "Zdrojová položka neexistuje.", source);

            if (Path.Exists(destination))
                throw new IOException(
                    "Cílová položka již existuje.");

            string? destinationParent = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(destination));

            if (string.IsNullOrWhiteSpace(destinationParent) ||
                !Directory.Exists(destinationParent))
            {
                throw new DirectoryNotFoundException(
                    "Cílový adresář neexistuje.");
            }

            // Symbolické odkazy a junctions zatím nepřesouváme.
            if ((File.GetAttributes(source) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new NotSupportedException(
                    "Přesouvání symbolických odkazů není podporováno.");
            }

            // Ochrana před přesunem složky do sebe samotné.
            if (isDirectory)
            {
                string normalizedSource =
                    Path.TrimEndingDirectorySeparator(source);

                string sourcePrefix = normalizedSource +
                    Path.DirectorySeparatorChar;

                if (string.Equals(
                        normalizedSource,
                        Path.TrimEndingDirectorySeparator(destination),
                        StringComparison.OrdinalIgnoreCase) ||
                    destination.StartsWith(
                        sourcePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "Složku nelze přesunout do sebe samotné.");
                }
            }

            EnsureNoReparsePoints(source);
            EnsureNoReparsePoints(destinationParent);
            // Bez MOVEFILE_COPY_ALLOWED: Windows nesmí nahradit přesun
            // mezi svazky kopírováním a smazáním zdroje.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MoveFileEx(source, destination, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(error == 17
                        ? "Přesun mezi svazky není podporován. Použijte Kopírovat do…; zdroj zůstane zachován."
                        : $"Přesun selhal: {new System.ComponentModel.Win32Exception(error).Message}");
                }
            }, cancellationToken);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingName, string newName, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);

        private static void CreateNewDirectory(string path)
        {
            if (!CreateDirectoryNative(path, IntPtr.Zero))
                throw new IOException($"Nelze vytvořit novou složku „{path}“: " +
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        // Kontrolujeme i předky: junction v cílové cestě může obejít
        // textovou kontrolu „cíl uvnitř zdroje“.
        public static void EnsureNoReparsePoints(string path)
        {
            string? current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException(
                        $"Operace přes symbolické odkazy nebo junctions není podporována: {current}");
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            }
        }


        public async Task<bool> VerifyDirectoryIntegrityAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string source = Path.GetFullPath(sourcePath);
            string destination = Path.GetFullPath(destinationPath);

            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(
                    $"Zdrojová složka neexistuje: {source}");

            if (!Directory.Exists(destination))
                throw new DirectoryNotFoundException(
                    $"Cílová složka neexistuje: {destination}");

            EnsureNoReparsePoints(source);
            EnsureNoReparsePoints(destination);

            // Připravíme seznam relativních cest.
            var sourceFiles = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            var destinationFiles = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            var sourceDirectories = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var destinationDirectories = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            // Lokální funkce pro načtení adresářového stromu.
            static void EnumerateTree(
                string root,
                Dictionary<string, string> files,
                HashSet<string> directories,
                CancellationToken token)
            {
                var pending = new Stack<string>();
                pending.Push(root);

                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();

                    string current = pending.Pop();
                    var info = new DirectoryInfo(current);

                    if ((info.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        throw new NotSupportedException(
                            "Ověřování symbolických odkazů není podporováno.");
                    }

                    string relativeDirectory =
                        Path.GetRelativePath(root, current);

                    directories.Add(relativeDirectory);

                    foreach (var file in info.EnumerateFiles())
                    {
                        token.ThrowIfCancellationRequested();

                        if ((file.Attributes &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            throw new NotSupportedException(
                                "Ověřování symbolických odkazů není podporováno.");
                        }

                        string relativeFile =
                            Path.GetRelativePath(root, file.FullName);

                        files.Add(relativeFile, file.FullName);
                    }

                    foreach (var directory in info.EnumerateDirectories())
                    {
                        if ((directory.Attributes &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            throw new NotSupportedException(
                                "Ověřování junctions není podporováno.");
                        }

                        pending.Push(directory.FullName);
                    }
                }
            }

            // Načítání stromů mimo UI vlákno.
            await Task.Run(() =>
            {
                EnumerateTree(
                    source,
                    sourceFiles,
                    sourceDirectories,
                    cancellationToken);

                EnumerateTree(
                    destination,
                    destinationFiles,
                    destinationDirectories,
                    cancellationToken);

            }, cancellationToken);

            // Kontrola struktury adresářů.
            if (!sourceDirectories.SetEquals(destinationDirectories))
                return false;

            // Kontrola počtu souborů.
            if (sourceFiles.Count != destinationFiles.Count)
                return false;

            // Kontrola názvů a obsahu jednotlivých souborů.
            foreach (var entry in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!destinationFiles.TryGetValue(
                        entry.Key,
                        out string? destinationFile))
                {
                    return false;
                }

                bool verified = await VerifyFileIntegrityAsync(
                    entry.Value,
                    destinationFile,
                    cancellationToken);

                if (!verified)
                    return false;
            }

            return true;
        }



        public async Task CopyFileAsync(
            string sourcePath,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string source = Path.GetFullPath(sourcePath);
            string destination = Path.GetFullPath(destinationPath);

            if (!File.Exists(source))
                throw new FileNotFoundException(
                    "Zdrojový soubor neexistuje.", source);

            if (string.Equals(
                source,
                destination,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Zdrojová a cílová cesta jsou stejné.");
            }

            if (Path.Exists(destination))
                throw new IOException(
                    "Cílová položka již existuje.");

            string? destinationDirectory =
                Path.GetDirectoryName(destination);

            if (string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(destinationDirectory))
            {
                throw new DirectoryNotFoundException(
                    "Cílový adresář neexistuje.");
            }

            EnsureNoReparsePoints(source);
            EnsureNoReparsePoints(destinationDirectory);

            // Unikátní dočasný soubor ve stejné složce.
            string tempPath = Path.Combine(
                destinationDirectory,
                $".{Path.GetFileName(destination)}." +
                $"{Guid.NewGuid():N}.tmp");

            const int bufferSize = 1024 * 1024;

            bool tempCreated = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using (var input = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan))
                {
                    await using (var output = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize,
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan))
                    {
                        tempCreated = true;

                        long totalBytes = input.Length;
                        long copiedBytes = 0;

                        byte[] buffer = new byte[bufferSize];

                        int bytesRead;

                        while ((bytesRead = await input.ReadAsync(
                            buffer.AsMemory(),
                            cancellationToken)) > 0)
                        {
                            await output.WriteAsync(
                                buffer.AsMemory(0, bytesRead),
                                cancellationToken);

                            copiedBytes += bytesRead;

                            double percentage = totalBytes == 0
                                ? 100
                                : (double)copiedBytes / totalBytes * 100;

                            progress?.Report(percentage);
                        }

                        await output.FlushAsync(cancellationToken);
                    }
                }

                // Zrušení je možné i těsně po dokončení přenosu.
                cancellationToken.ThrowIfCancellationRequested();

                // Bez přepsání existujícího cílového souboru.
                // Dočasný soubor je ve stejném adresáři.
                File.Move(tempPath, destination, overwrite: false);

                tempCreated = false;
                progress?.Report(100);
            }
            catch
            {
                // Odstraníme pouze náš vlastní dočasný soubor.
                if (tempCreated)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException cleanupError)
                    {
                        System.Diagnostics.Trace.TraceError($"Nelze uklidit {tempPath}: {cleanupError}");
                        throw new IOException($"Úklid selhal. Zůstal dočasný soubor: {tempPath}", cleanupError);
                    }
                    catch (UnauthorizedAccessException cleanupError)
                    {
                        System.Diagnostics.Trace.TraceError($"Nelze uklidit {tempPath}: {cleanupError}");
                        throw new IOException($"Úklid selhal. Zůstal dočasný soubor: {tempPath}", cleanupError);
                    }
                }

                throw;
            }
        }



        public sealed record CopyProgressInfo(
            long TransferredBytes,
            long TotalBytes,
            string CurrentFile)
        {
            public double Percentage =>
                TotalBytes <= 0
                    ? 100
                    : Math.Clamp(
                        (double)TransferredBytes / TotalBytes * 100,
                        0,
                        100);
        }


        /// <summary>
        /// Spočítá velikost souborů v adresáři a jeho podsložkách.
        /// Symbolické odkazy a junctions nepřechází.
        /// </summary>
        public long GetDirectorySize(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string root = Path.GetFullPath(path);

            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(root);

            EnsureNoReparsePoints(root);

            long totalSize = 0;
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                var directoryInfo = new DirectoryInfo(current);

                if ((directoryInfo.Attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new NotSupportedException("Složka obsahuje odkaz; operace byla zastavena.");
                }

                foreach (var file in directoryInfo.EnumerateFiles())
                {
                    if ((file.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        throw new NotSupportedException("Složka obsahuje odkaz; operace byla zastavena.");
                    }

                    totalSize = checked(totalSize + file.Length);
                }

                foreach (var directory in directoryInfo.EnumerateDirectories())
                {
                    if ((directory.Attributes &
                          FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory.FullName);
                    }
                    else
                        throw new NotSupportedException("Složka obsahuje odkaz; operace byla zastavena.");
                }
            }

            return totalSize;
        }




        public async Task CopyDirectoryAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default,
            IProgress<CopyProgressInfo>? progress = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string source = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sourcePath));

            string destination = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(destinationPath));

            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(
                    $"Zdrojová složka neexistuje: {source}");

            EnsureNoReparsePoints(source);
            EnsureNoReparsePoints(Path.GetDirectoryName(destination)
                ?? throw new IOException("Chybí cílová složka."));

            if ((File.GetAttributes(source) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Kopírování odkazovaných složek není podporováno.");
            }

            if (Path.Exists(destination))
                throw new IOException(
                    "Cílová položka již existuje.");

            // Zákaz kopírování složky do sebe samotné.
            string sourcePrefix = Path.EndsInDirectorySeparator(source)
                ? source
                : source + Path.DirectorySeparatorChar;

            if (string.Equals(
                    source,
                    destination,
                    StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(
                    sourcePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Složku nelze kopírovat do sebe samotné.");
            }

            // Jediný plán pro všechny kopírované položky.
            var directories = new List<string>();

            var files = new List<(
                string Source,
                string RelativePath,
                long Size)>();

            var pending = new Stack<string>();
            pending.Push(source);

            long totalBytes = 0;

            // FÁZE 1: Příprava plánu.
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string current = pending.Pop();

                var directoryInfo = new DirectoryInfo(current);

                if ((directoryInfo.Attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new NotSupportedException("Složka obsahuje odkaz; operace byla zastavena.");
                }

                directories.Add(
                    Path.GetRelativePath(source, current));

                foreach (var file in directoryInfo.EnumerateFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if ((file.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                        throw new NotSupportedException("Složka obsahuje odkaz; operace byla zastavena.");

                    long size = file.Length;

                    totalBytes = checked(totalBytes + size);

                    files.Add((
                        file.FullName,
                        Path.GetRelativePath(source, file.FullName),
                        size));
                }

                foreach (var directory in
                         directoryInfo.EnumerateDirectories())
                {
                    if ((directory.Attributes &
                         FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory.FullName);
                    }
                    else
                        throw new NotSupportedException("Složka obsahuje odkaz; operace byla zastavena.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // FÁZE 2: Vytvoření cílových adresářů.
            foreach (string relative in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string target = relative == "."
                    ? destination
                    : Path.Combine(destination, relative);

                EnsureNoReparsePoints(Path.GetDirectoryName(target)!);
                CreateNewDirectory(target);
            }

            // FÁZE 3: Kopírování souborů.
            long completedBytes = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string target = Path.Combine(
                    destination,
                    file.RelativePath);

                long baseBytes = completedBytes;
                string fileName = Path.GetFileName(file.Source);

                var fileProgress = new InlineProgress<double>(percentage =>
                {
                    long currentBytes = baseBytes +
                        (long)(file.Size *
                            Math.Clamp(percentage, 0, 100) / 100.0);

                    progress?.Report(new CopyProgressInfo(
                        currentBytes,
                        totalBytes,
                        fileName));
                });

                await CopyFileAsync(
                    file.Source,
                    target,
                    fileProgress,
                    cancellationToken);

                completedBytes = checked(
                    completedBytes + file.Size);

                progress?.Report(new CopyProgressInfo(
                    completedBytes,
                    totalBytes,
                    fileName));
            }

            progress?.Report(new CopyProgressInfo(
                totalBytes,
                totalBytes,
                string.Empty));
        }



        public string RenameItem(string sourcePath, string newName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Není zadána cesta k položce.");

            ValidateItemName(newName);

            string fullPath = Path.GetFullPath(sourcePath);

            EnsureNoReparsePoints(fullPath);

            string? parentPath = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(fullPath));

            if (string.IsNullOrWhiteSpace(parentPath))
                throw new IOException("Kořenový adresář nelze přejmenovat.");

            string destinationPath = Path.Combine(parentPath, newName);

            if (string.Equals(
                fullPath,
                destinationPath,
                StringComparison.Ordinal))
            {
                return fullPath;
            }

            if (Path.Exists(destinationPath) &&
                !string.Equals(
                    fullPath,
                    destinationPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Položka s tímto názvem již existuje.");
            }

            if (File.Exists(fullPath))
            {
                File.Move(fullPath, destinationPath);
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Move(fullPath, destinationPath);
            }
            else
            {
                throw new FileNotFoundException(
                    "Zdrojový soubor nebo složka neexistuje.",
                    fullPath);
            }

            return destinationPath;
        }

        internal static void ValidateItemName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Název nesmí být prázdný.");

            if (name.Length > 255 || name is "." or ".." ||
                name.EndsWith(' ') ||
                name.EndsWith('.'))
            {
                throw new ArgumentException("Neplatný název položky.");
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains('\\') ||
                name.Contains('/') ||
                name.Contains(':'))
            {
                throw new ArgumentException(
                    "Název obsahuje nepovolené znaky.");
            }

            // Základní ochrana před rezervovanými názvy Windows
            string baseName = name.Split('.')[0].TrimEnd().ToUpperInvariant();

            string[] reservedNames =
            {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
        "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³", "CONIN$", "CONOUT$"
    };

            if (Array.Exists(reservedNames, reserved => reserved == baseName))
                throw new ArgumentException(
                    "Tento název je ve Windows rezervovaný.");
        }

        /// <summary>
        /// Vrátí dostupné diskové jednotky.
        /// </summary>
        public List<FileSystemItem> GetDrives()
        {
            var drives = new List<FileSystemItem>();

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    drives.Add(new FileSystemItem
                    {
                        Name = drive.Name,
                        FullPath = drive.RootDirectory.FullName,
                        IsDirectory = true,
                        DateModified = DateTime.MinValue
                    });
                }
                catch (IOException)
                {
                    // Jednotka není dostupná.
                }
                catch (UnauthorizedAccessException)
                {
                    // Nedostatečná oprávnění.
                }
            }

            return drives;
        }

        /// <summary>
        /// Načte složky a soubory v zadaném adresáři.
        /// </summary>
        public List<FileSystemItem> GetDirectoryContents(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "Cesta k adresáři nesmí být prázdná.",
                    nameof(path));

            var directory = new DirectoryInfo(path);

            if (!directory.Exists)
                throw new DirectoryNotFoundException(
                    $"Adresář neexistuje: {path}");

            var items = new List<FileSystemItem>();

            // Načtení podsložek
            foreach (var folder in directory.EnumerateDirectories())
            {
                try
                {
                    items.Add(new FileSystemItem
                    {
                        Name = folder.Name,
                        FullPath = folder.FullName,
                        IsDirectory = true,
                        DateModified = folder.LastWriteTime,
                        Extension = string.Empty,
                        Size = 0,
                        Attributes = folder.Attributes
                    });
                }
                catch (IOException)
                {
                    // Nelze načíst informace o složce.
                }
                catch (UnauthorizedAccessException)
                {
                    // Složka není přístupná.
                }
            }

            // Načtení souborů
            foreach (var file in directory.EnumerateFiles())
            {
                try
                {
                    if (!VideoDiagnosticsService.IsVideoFile(file.Name)) continue;
                    items.Add(new FileSystemItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        Size = file.Length,
                        DateModified = file.LastWriteTime,
                        Extension = file.Extension,
                        Attributes = file.Attributes
                    });
                }
                catch (IOException)
                {
                    // Nelze načíst informace o souboru.
                }
                catch (UnauthorizedAccessException)
                {
                    // Soubor není přístupný.
                }
            }

            // Složky první, následně soubory podle názvu
            return items
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Vrátí nadřazený adresář.
        /// </summary>
        public string? GetParentDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return Directory.GetParent(path)?.FullName;
        }

        /// <summary>
        /// Ověří existenci adresáře.
        /// </summary>
        public bool DirectoryExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && Directory.Exists(path);
        }
    }

}
