using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using MultiInstaller.Models;
using MultiInstaller.Services;

namespace MultiInstaller;

public partial class MainWindow : Window
{
    private static readonly string[] AllowedExtensions = [".exe", ".msi", ".msix", ".appx"];

    public ObservableCollection<InstallerItem> Items { get; } = [];

    private readonly CatalogService _catalog = new();
    private readonly InstallerRunner _runner = new();
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (InstallerItem item in await _catalog.LoadAsync())
        {
            item.RefreshFileState();
            Items.Add(item);
        }
        UpdateCount();
        Log("Pronto. Trascina gli installer nell'area superiore oppure premi SCEGLI FILE.");
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isRunning)
        {
            MessageBoxResult result = MessageBox.Show(this,
                "È in corso un'installazione. Vuoi annullarla e chiudere?",
                "Installazione in corso", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cancellation?.Cancel();
        }

        await SaveCatalogAsync();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Seleziona uno o più installer",
            Filter = "Installer Windows (*.exe;*.msi;*.msix;*.appx)|*.exe;*.msi;*.msix;*.appx",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
            _ = AddFilesAsync(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void DropArea_Drop(object sender, DragEventArgs e) => HandleDrop(e);

    private void HandleDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _ = AddFilesAsync(files);
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (_isRunning) return;

        List<string> files = paths
            .Where(File.Exists)
            .Where(x => AllowedExtensions.Contains(Path.GetExtension(x).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(this, "Trascina file .exe, .msi, .msix o .appx.",
                "Nessun installer valido", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (string sourcePath in files)
        {
            try
            {
                string copiedPath = _catalog.CopyIntoLibrary(sourcePath);
                string extension = Path.GetExtension(copiedPath).TrimStart('.').ToUpperInvariant();
                string displayName = BuildDisplayName(copiedPath);

                InstallerItem item = new()
                {
                    Name = displayName,
                    FilePath = copiedPath,
                    Type = extension,
                    Arguments = SilentArgumentDetector.Detect(copiedPath),
                    IsSelected = true
                };

                Items.Add(item);
                Log($"Aggiunto: {item.Name}");
            }
            catch (Exception ex)
            {
                Log($"ERRORE durante l'aggiunta: {ex.Message}");
            }
        }

        await SaveCatalogAsync();
        UpdateCount();
    }

    private static string BuildDisplayName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path)
            .Replace('_', ' ')
            .Replace('-', ' ');

        while (name.Contains("  ")) name = name.Replace("  ", " ");
        return name.Trim();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        List<InstallerItem> selectedRows = ProgramsGrid.SelectedItems.Cast<InstallerItem>().ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show(this, "Seleziona una o più righe da rimuovere.",
                "Nessuna riga selezionata", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = MessageBox.Show(this,
            $"Rimuovere {selectedRows.Count} programmi dalla libreria?",
            "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        foreach (InstallerItem item in selectedRows)
        {
            Items.Remove(item);
            try
            {
                if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
            }
            catch (Exception ex)
            {
                Log($"File non eliminato: {ex.Message}");
            }
        }

        await SaveCatalogAsync();
        UpdateCount();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (InstallerItem item in Items) item.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (InstallerItem item in Items) item.IsSelected = false;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        List<InstallerItem> selected = Items.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Spunta almeno un programma da installare.",
                "Nessun programma", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetRunning(true);
        _cancellation = new CancellationTokenSource();
        ProgressBar.Value = 0;
        int completed = 0;
        int successCount = 0;

        try
        {
            foreach (InstallerItem item in selected)
            {
                if (_cancellation.IsCancellationRequested) break;

                item.RefreshFileState();
                item.Status = "In corso...";
                ProgressText.Text = $"Installazione di {item.Name}";
                Log($"AVVIO: {item.Name}");

                InstallResult result = await _runner.RunAsync(item, _cancellation.Token);
                item.Status = result.Success ? "Completato" : "Errore";
                if (result.Success) successCount++;
                Log($"{(result.Success ? "OK" : "ERRORE")}: {item.Name} — {result.Message}");

                completed++;
                ProgressBar.Value = completed * 100.0 / selected.Count;
            }
        }
        finally
        {
            SetRunning(false);
            ProgressText.Text = _cancellation.IsCancellationRequested
                ? "Installazione annullata"
                : $"Operazione terminata: {successCount}/{selected.Count} completati";
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        Log("Richiesto annullamento.");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void SetRunning(bool running)
    {
        _isRunning = running;
        InstallButton.IsEnabled = !running;
        AddButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        ProgramsGrid.IsEnabled = !running;
    }

    private async Task SaveCatalogAsync()
    {
        try
        {
            await _catalog.SaveAsync(Items);
        }
        catch (Exception ex)
        {
            Log($"ERRORE salvataggio: {ex.Message}");
        }
    }

    private void UpdateCount() => CountText.Text = $"{Items.Count} programmi";

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
