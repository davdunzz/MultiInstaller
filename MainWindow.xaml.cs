using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;

namespace MultiInstaller;

public partial class MainWindow : Window
{
    public ObservableCollection<InstallerItem> Installers { get; } = new();

    private readonly string _applicationFolder;
    private readonly string _installersFolder;
    private readonly string _catalogPath;

    private CancellationTokenSource? _cancellationTokenSource;
    private Process? _currentProcess;
    private bool _installationRunning;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

        _applicationFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiInstaller");

        _installersFolder = Path.Combine(
            _applicationFolder,
            "Installers");

        _catalogPath = Path.Combine(
            _applicationFolder,
            "catalog.json");

        Directory.CreateDirectory(_applicationFolder);
        Directory.CreateDirectory(_installersFolder);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadCatalog();
        WriteLog("MultiInstaller avviato.");
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleziona gli installer",
            Filter = "Installer Windows (*.exe;*.msi)|*.exe;*.msi",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            AddInstallerFiles(dialog.FileNames);
        }
    }

    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DropArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];

        if (files is null || files.Length == 0)
            return;

        AddInstallerFiles(files);
    }

    private void AddInstallerFiles(IEnumerable<string> files)
    {
        var validFiles = files
            .Where(File.Exists)
            .Where(file =>
            {
                var extension = Path.GetExtension(file);

                return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (validFiles.Count == 0)
        {
            MessageBox.Show(
                "Non sono stati trovati file .exe o .msi validi.",
                "MultiInstaller",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        foreach (var sourcePath in validFiles)
        {
            try
            {
                var originalFileName = Path.GetFileName(sourcePath);
                var destinationPath = GetUniqueDestinationPath(originalFileName);

                File.Copy(sourcePath, destinationPath, false);

                var item = new InstallerItem
                {
                    Id = Guid.NewGuid(),
                    Name = GetProgramName(sourcePath),
                    FileName = Path.GetFileName(destinationPath),
                    FilePath = destinationPath,
                    IsSelected = true,
                    Status = "Pronto"
                };

                Installers.Add(item);

                WriteLog($"Aggiunto: {item.Name}");
            }
            catch (Exception exception)
            {
                WriteLog($"Errore durante l'aggiunta di {sourcePath}: {exception.Message}");
            }
        }

        SaveCatalog();
    }

    private string GetUniqueDestinationPath(string fileName)
    {
        var destinationPath = Path.Combine(_installersFolder, fileName);

        if (!File.Exists(destinationPath))
            return destinationPath;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 2;

        while (File.Exists(destinationPath))
        {
            destinationPath = Path.Combine(
                _installersFolder,
                $"{nameWithoutExtension}_{counter}{extension}");

            counter++;
        }

        return destinationPath;
    }

    private static string GetProgramName(string filePath)
    {
        try
        {
            if (Path.GetExtension(filePath)
                .Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(filePath);

                if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                {
                    return versionInfo.ProductName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                {
                    return versionInfo.FileDescription.Trim();
                }
            }
        }
        catch
        {
            // Utilizza il nome del file come alternativa.
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);

        return fileName
            .Replace("_", " ")
            .Replace("-", " ")
            .Trim();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var installer in Installers)
        {
            installer.IsSelected = true;
        }
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var installer in Installers)
        {
            installer.IsSelected = false;
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installationRunning)
            return;

        if (InstallersGrid.SelectedItem is not InstallerItem selectedItem)
        {
            MessageBox.Show(
                "Seleziona prima un programma dall'elenco.",
                "MultiInstaller",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var answer = MessageBox.Show(
            $"Vuoi rimuovere \"{selectedItem.Name}\"?",
            "Conferma rimozione",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            if (File.Exists(selectedItem.FilePath))
            {
                File.Delete(selectedItem.FilePath);
            }
        }
        catch (Exception exception)
        {
            WriteLog($"Non è stato possibile eliminare il file: {exception.Message}");
        }

        Installers.Remove(selectedItem);
        SaveCatalog();

        WriteLog($"Rimosso: {selectedItem.Name}");
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installationRunning)
            return;

        var selectedInstallers = Installers
            .Where(item => item.IsSelected)
            .ToList();

        if (selectedInstallers.Count == 0)
        {
            MessageBox.Show(
                "Seleziona almeno un programma da installare.",
                "MultiInstaller",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var missingFiles = selectedInstallers
            .Where(item => !File.Exists(item.FilePath))
            .ToList();

        if (missingFiles.Count > 0)
        {
            MessageBox.Show(
                "Uno o più installer non sono più disponibili.",
                "MultiInstaller",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            foreach (var item in missingFiles)
            {
                item.Status = "File mancante";
            }

            return;
        }

        _installationRunning = true;
        _cancellationTokenSource = new CancellationTokenSource();

        SetInterfaceEnabled(false);

        InstallationProgressBar.Value = 0;
        GeneralStatusText.Text = "Installazione in corso...";

        var completed = 0;
        var successful = 0;
        var failed = 0;

        foreach (var installer in selectedInstallers)
        {
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                installer.Status = "Annullato";
                break;
            }

            installer.Status = "Installazione...";
            GeneralStatusText.Text = $"Installazione di {installer.Name}";

            WriteLog($"Avvio installazione: {installer.Name}");

            try
            {
                var exitCode = await InstallProgramAsync(
                    installer,
                    _cancellationTokenSource.Token);

                if (exitCode is 0 or 1641 or 3010)
                {
                    installer.Status = exitCode == 3010
                        ? "Completato - riavvio richiesto"
                        : "Completato";

                    successful++;

                    WriteLog(
                        $"Installazione completata: {installer.Name} " +
                        $"(codice {exitCode})");
                }
                else
                {
                    installer.Status = $"Errore ({exitCode})";
                    failed++;

                    WriteLog(
                        $"Installazione non riuscita: {installer.Name} " +
                        $"(codice {exitCode})");
                }
            }
            catch (OperationCanceledException)
            {
                installer.Status = "Annullato";
                WriteLog($"Installazione annullata: {installer.Name}");
                break;
            }
            catch (Exception exception)
            {
                installer.Status = "Errore";
                failed++;

                WriteLog(
                    $"Errore durante l'installazione di {installer.Name}: " +
                    exception.Message);
            }
            finally
            {
                _currentProcess = null;

                completed++;

                InstallationProgressBar.Value =
                    completed * 100.0 / selectedInstallers.Count;
            }
        }

        var cancelled = _cancellationTokenSource.IsCancellationRequested;

        GeneralStatusText.Text = cancelled
            ? "Operazione annullata"
            : $"Operazione terminata: {successful} completati, {failed} errori";

        WriteLog(
            cancelled
                ? "Operazione annullata dall'utente."
                : $"Operazione terminata. Completati: {successful}, errori: {failed}.");

        SetInterfaceEnabled(true);

        _installationRunning = false;
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
    }

    private async Task<int> InstallProgramAsync(
        InstallerItem installer,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(installer.FilePath);

        ProcessStartInfo startInfo;

        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{installer.FilePath}\" /qn /norestart",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer.FilePath)
            };
        }
        else
        {
            var silentArguments = DetectSilentArguments(installer);

            startInfo = new ProcessStartInfo
            {
                FileName = installer.FilePath,
                Arguments = silentArguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer.FilePath)
            };

            if (string.IsNullOrWhiteSpace(silentArguments))
            {
                WriteLog(
                    $"Installazione silenziosa non riconosciuta per {installer.Name}. " +
                    "Potrebbe aprirsi la normale procedura guidata.");
            }
        }

        _currentProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Non è stato possibile avviare l'installer.");

        await _currentProcess.WaitForExitAsync(cancellationToken);

        return _currentProcess.ExitCode;
    }

    private static string DetectSilentArguments(InstallerItem installer)
    {
        var searchText =
            $"{installer.Name} {installer.FileName}".ToLowerInvariant();

        if (searchText.Contains("chrome"))
            return "/silent /install";

        if (searchText.Contains("firefox"))
            return "-ms";

        if (searchText.Contains("vlc"))
            return "/S";

        if (searchText.Contains("7-zip") || searchText.Contains("7zip"))
            return "/S";

        if (searchText.Contains("winrar"))
            return "/S";

        if (searchText.Contains("notepad++"))
            return "/S";

        if (searchText.Contains("discord"))
            return "-s";

        if (searchText.Contains("spotify"))
            return "/silent";

        if (searchText.Contains("zoom"))
            return "/quiet /norestart";

        if (searchText.Contains("adobe") ||
            searchText.Contains("acrobat") ||
            searchText.Contains("reader"))
        {
            return "/sAll /rs /rps /msi EULA_ACCEPT=YES";
        }

        return string.Empty;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_installationRunning)
            return;

        var answer = MessageBox.Show(
            "Vuoi annullare l'installazione in corso?",
            "Conferma annullamento",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        _cancellationTokenSource?.Cancel();

        try
        {
            if (_currentProcess is { HasExited: false })
            {
                _currentProcess.Kill(true);
            }
        }
        catch (Exception exception)
        {
            WriteLog(
                $"Non è stato possibile interrompere il processo: {exception.Message}");
        }
    }

    private void SetInterfaceEnabled(bool enabled)
    {
        AddFilesButton.IsEnabled = enabled;
        InstallButton.IsEnabled = enabled;
        InstallersGrid.IsEnabled = enabled;
        CancelButton.IsEnabled = !enabled;
    }

    private void LoadCatalog()
    {
        if (!File.Exists(_catalogPath))
            return;

        try
        {
            var json = File.ReadAllText(_catalogPath);

            var savedItems = JsonSerializer.Deserialize<List<InstallerItem>>(json)
                             ?? new List<InstallerItem>();

            Installers.Clear();

            foreach (var item in savedItems)
            {
                item.Status = File.Exists(item.FilePath)
                    ? "Pronto"
                    : "File mancante";

                Installers.Add(item);
            }
        }
        catch (Exception exception)
        {
            WriteLog($"Errore nel caricamento del catalogo: {exception.Message}");
        }
    }

    private void SaveCatalog()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(
                Installers.ToList(),
                options);

            File.WriteAllText(_catalogPath, json);
        }
        catch (Exception exception)
        {
            WriteLog($"Errore nel salvataggio del catalogo: {exception.Message}");
        }
    }

    private void WriteLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }
}

public class InstallerItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "Pronto";

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;

        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;

        set
        {
            if (_status == value)
                return;

            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
