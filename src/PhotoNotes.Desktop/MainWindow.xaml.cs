using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using PhotoNotes.Core;

namespace PhotoNotes.Desktop;

public sealed record ImageRow(ImageRecord Image, int IdenticalPeers, string Destination);

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ImageScanner _scanner = new();
    private readonly CopyService _copier = new();
    private readonly NoteStore _noteStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoNotes", "Notes"));
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableNotes = new(StringComparer.Ordinal);
    private ScanResult? _scan;
    private CopyPlan? _plan;
    private CancellationTokenSource? _operation;
    private string _sourceRoot = string.Empty;
    private string _destinationRoot = string.Empty;
    private bool _omitIdenticalCopies = true;
    private bool _isBusy;
    private string _status = "Choose a source folder, then scan. All processing stays on this machine.";
    private string _summary = "No scan yet.";
    private string _issuesText = string.Empty;
    private IReadOnlyList<ImageRow> _rows = [];
    private ImageRow? _selectedRow;
    private string _noteText = string.Empty;
    private string _savedNoteText = string.Empty;
    private bool _refreshingRows;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceRoot
    {
        get => _sourceRoot;
        set
        {
            if (_sourceRoot == value) return;
            if (!CanLeaveNote())
            {
                Dispatcher.BeginInvoke(() => Notify(nameof(SourceRoot)));
                return;
            }
            ResetNoteDraft();
            _sourceRoot = value;
            _scan = null;
            Rows = [];
            SelectedRow = null;
            Summary = "Source changed. Scan this folder to continue.";
            IssuesText = string.Empty;
            InvalidatePlan();
            Notify();
        }
    }

    public string DestinationRoot
    {
        get => _destinationRoot;
        set
        {
            if (_destinationRoot == value) return;
            _destinationRoot = value;
            InvalidatePlan();
            Notify();
        }
    }

    public bool OmitIdenticalCopies
    {
        get => _omitIdenticalCopies;
        set
        {
            if (_omitIdenticalCopies == value) return;
            _omitIdenticalCopies = value;
            InvalidatePlan();
            Notify();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; Notify(); NotifyActions(); }
    }

    public bool IsIdle => !IsBusy;
    public bool CanPreview => IsIdle && _scan is not null;
    public bool CanCopy => IsIdle && _plan is { Entries.Count: > 0 };
    public bool CanExport => IsIdle && _scan is not null;
    public string Status { get => _status; private set { _status = value; Notify(); } }
    public string Summary { get => _summary; private set { _summary = value; Notify(); } }
    public string IssuesText { get => _issuesText; private set { _issuesText = value; Notify(); } }
    public IReadOnlyList<ImageRow> Rows { get => _rows; private set { _rows = value; Notify(); } }
    public ImageRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_refreshingRows) return;
            if (_selectedRow?.Image.ContentHash != value?.Image.ContentHash)
            {
                if (!CanLeaveNote())
                {
                    Dispatcher.BeginInvoke(() => Notify(nameof(SelectedRow)));
                    return;
                }
                _savedNoteText = value?.Image.ContentHash is { } hash
                    ? _notes.GetValueOrDefault(hash, string.Empty) : string.Empty;
                NoteText = _savedNoteText;
            }
            _selectedRow = value;
            Notify();
            NotifyNoteActions();
        }
    }

    public string NoteText
    {
        get => _noteText;
        set
        {
            _noteText = value;
            Notify();
            NotifyNoteActions();
        }
    }

    public bool CanEditNote => IsIdle && SelectedRow?.Image.ContentHash is { } hash
        && !_unreadableNotes.Contains(hash);
    public bool HasUnsavedNote => NoteText != _savedNoteText;
    public bool CanSaveNote => CanEditNote && HasUnsavedNote && NoteText.Length <= NoteStore.MaxDescriptionLength;
    public bool CanRevertNote => CanEditNote && HasUnsavedNote;
    public string NoteCharacterCount => $"{NoteText.Length} / {NoteStore.MaxDescriptionLength} characters" +
                                        (HasUnsavedNote ? " (unsaved)" : string.Empty);
    public string NoteIdentity => SelectedRow?.Image.ContentHash is { } hash
        ? _unreadableNotes.Contains(hash)
            ? "Notes could not be loaded. See Issues; the existing note will not be overwritten."
            : $"Shared content ID: {hash}"
        : "Select an image with a content hash to add notes.";

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the image directory to scan" };
        if (picker.ShowDialog(this) == true) SourceRoot = picker.FolderName;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a separate destination directory" };
        if (picker.ShowDialog(this) == true) DestinationRoot = picker.FolderName;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceRoot))
        {
            ShowInputError("Choose a source directory first.");
            return;
        }
        if (!CanLeaveNote()) return;
        ResetNoteDraft();
        await RunOperationAsync(async token =>
        {
            _scan = null;
            _plan = null;
            Rows = [];
            SelectedRow = null;
            IssuesText = string.Empty;
            Summary = "Scanning...";
            var progress = new Progress<ScanProgress>(p =>
                Status = $"Scanned {p.FilesProcessed} images. {p.CurrentPath}");
            _scan = await _scanner.ScanAsync(SourceRoot, progress, token);
            _notes.Clear();
            _unreadableNotes.Clear();
            var noteIssues = new List<ScanIssue>();
            foreach (var hash in _scan.Images.Select(i => i.ContentHash).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                try
                {
                    _notes[hash] = await _noteStore.LoadAsync(hash, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _unreadableNotes.Add(hash);
                    noteIssues.Add(new($"Notes for {hash}", ex.Message));
                }
            }
            RefreshRows();
            IssuesText = FormatIssues(_scan.Issues.Concat(noteIssues));
            Summary = $"{_scan.Images.Count} images | {_scan.Images.Count(i => i.HasMetadata)} with metadata | " +
                      $"{_scan.DuplicateFiles} extra identical files | {_scan.Issues.Count + noteIssues.Count} issues";
            Status = "Scan complete. Choose a destination and preview copies. Review the Issues tab before copying.";
        });
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null) return;
        if (string.IsNullOrWhiteSpace(DestinationRoot))
        {
            ShowInputError("Choose a destination directory first.");
            return;
        }
        await RunOperationAsync(_ =>
        {
            _plan = _copier.CreatePlan(_scan, DestinationRoot, OmitIdenticalCopies);
            RefreshRows();
            Status = $"Preview ready: {_plan.Entries.Count} files. Dated photos use Year/Month/Day; others use Undated. " +
                     "Existing files are never overwritten.";
            return Task.CompletedTask;
        });
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { Entries.Count: > 0 } plan) return;
        if (MessageBox.Show(this, $"Copy {plan.Entries.Count} files to:\n{plan.DestinationRoot}\n\n" +
                "Originals will not be modified. Continue?", "Confirm previewed copies",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync(async token =>
        {
            try
            {
                var progress = new Progress<CopyProgress>(p =>
                    Status = $"Copied {p.Copied}/{p.Total}. {p.CurrentPath}");
                var result = await Task.Run(() => _copier.CopyAsync(plan, progress, token), token);
                var copyIssues = FormatIssues(result.Issues);
                if (copyIssues.Length > 0) IssuesText += Environment.NewLine + copyIssues;
                var outcome = result.IsCanceled ? "Copy canceled" : "Copy finished";
                Status = $"{outcome}: {result.Copied} copied, {result.Issues.Count} failed. Originals unchanged.";
            }
            finally
            {
                InvalidatePlan();
            }
        });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null) return;
        if (HasUnsavedNote)
        {
            ShowInputError("Save or revert the unsaved note before exporting a report.");
            return;
        }
        var picker = new SaveFileDialog { Filter = "JSON report (*.json)|*.json", FileName = "PhotoNotes-report.json" };
        if (picker.ShowDialog(this) != true) return;
        await RunOperationAsync(async token =>
        {
            var report = new
            {
                Version = 2,
                Scan = _scan,
                Plan = _plan,
                NotesByContentHash = _notes.Where(pair => pair.Value.Length > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                OperationIssues = IssuesText
            };
            await using var stream = new FileStream(picker.FileName, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true }, token);
            Status = $"Report saved to {picker.FileName}. Reports include local paths, metadata, and saved notes.";
        });
    }

    private async void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSaveNote || SelectedRow?.Image.ContentHash is not { } hash) return;
        var description = NoteText;
        await RunOperationAsync(async token =>
        {
            await _noteStore.SaveAsync(hash, description, token);
            _notes[hash] = description;
            _savedNoteText = description;
            NotifyNoteActions();
            Status = "Note saved locally. Identical and renamed copies share this note; source images are unchanged.";
        });
    }

    private void RevertNote_Click(object sender, RoutedEventArgs e) => NoteText = _savedNoteText;

    private void VoiceTyping_Click(object sender, RoutedEventArgs e)
    {
        NotesInput.Focus();
        NotesInput.CaretIndex = NotesInput.Text.Length;
        Status = "Press Win+H to start Windows voice typing, then speak. Review the text and select Save note. " +
                 "Windows may require internet, microphone permission, and online speech recognition.";
    }

    private bool CanLeaveNote()
    {
        if (!HasUnsavedNote) return true;
        Status = "Unsaved note: select Save note to keep it, or Revert to discard changes before leaving this image.";
        return false;
    }

    private void ResetNoteDraft()
    {
        _savedNoteText = string.Empty;
        NoteText = string.Empty;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status = "Canceled. Any completed copies remain; originals are unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Operation failed: {ex.Message}";
            IssuesText += $"{Environment.NewLine}{Status}";
            MessageBox.Show(this, ex.Message, "PhotoNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operation = null;
            IsBusy = false;
        }
    }

    private void InvalidatePlan()
    {
        _plan = null;
        RefreshRows();
        NotifyActions();
    }

    private void RefreshRows()
    {
        if (_scan is null) return;
        var selectedPath = SelectedRow?.Image.SourcePath;
        var destinations = _plan?.Entries.ToDictionary(p => p.Image.SourcePath, p => p.DestinationPath,
            StringComparer.OrdinalIgnoreCase);
        var counts = _scan.Images.Where(i => i.ContentHash is not null)
            .GroupBy(i => i.ContentHash!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        _refreshingRows = true;
        try
        {
            Rows = _scan.Images.Select(image => new ImageRow(image,
                image.ContentHash is { } hash ? counts[hash] - 1 : 0,
                destinations?.GetValueOrDefault(image.SourcePath) ??
                (image.CanCopy ? $"Suggested: {image.SuggestedFolder}" : "Excluded: read/metadata error"))).ToArray();
        }
        finally
        {
            _refreshingRows = false;
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Image.SourcePath == selectedPath) ?? Rows.FirstOrDefault();
    }

    private void NotifyActions()
    {
        Notify(nameof(IsIdle));
        Notify(nameof(CanPreview));
        Notify(nameof(CanCopy));
        Notify(nameof(CanExport));
        NotifyNoteActions();
    }

    private void NotifyNoteActions()
    {
        Notify(nameof(CanEditNote));
        Notify(nameof(CanSaveNote));
        Notify(nameof(CanRevertNote));
        Notify(nameof(HasUnsavedNote));
        Notify(nameof(NoteCharacterCount));
        Notify(nameof(NoteIdentity));
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private static string FormatIssues(IEnumerable<ScanIssue> issues) =>
        string.Join(Environment.NewLine, issues.Select(i => $"{i.Path}: {i.Message}"));

    private void ShowInputError(string message) =>
        MessageBox.Show(this, message, "PhotoNotes", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!IsBusy)
        {
            e.Cancel = !CanLeaveNote();
            return;
        }
        e.Cancel = true;
        Status = "Cancel the current operation before closing.";
    }
}
