using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// Shared file inspector for the currently active explorer pane (#memo history & editor).
/// File attribute toggles, folder size, SHA-256 hash, and Onboard AI summary generation are modularized in
/// Features/Inspector/FileInspector.AttributesAndAiSummary.cs.
/// </summary>
public partial class FileInspector : UserControl
{
    public event Action? CloseRequested;
    private void CloseInspector_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private FileEntry? _selected;
    private bool _updatingMemo;
    private bool _updatingAttributes;
    private bool _memoDirty;
    private readonly HashSet<string> _displayedMemoWarnings = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _inspectorCts;
    public event Action? MemoSaved;
    public Func<Task<bool>>? RequestOllamaSetup { get; set; }

    public FileInspector()
    {
        InitializeComponent();
        MemoBackupsEnabled.IsChecked = MemoLocalSnapshots.Enabled;
        MemoEditor.IsEnabled = SaveButton.IsEnabled = AiMemoButton.IsEnabled = MemoAuthorInput.IsEnabled = false;
    }

    public bool HasUnsavedMemo => _memoDirty;
    public string? CurrentFilePath => _selected?.FullPath;

    public bool SelectFile(FileEntry? entry)
    {
        if (string.Equals(_selected?.FullPath, entry?.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            _selected = entry;
            UpdateDetails(entry);
            return true;
        }
        if (!ConfirmLeavingMemo()) return false;
        _inspectorCts?.Cancel();
        _selected = entry;
        UpdateDetails(entry);
        LoadMemoHistory();
        return true;
    }

    private void UpdateDetails(FileEntry? entry)
    {
        DetailName.Text = entry?.Name ?? "파일을 선택하세요";
        DetailType.Text = entry is null ? "—" : $"{entry.TypeName} ({entry.AttributesDisplay})";
        DetailSize.Text = entry is null ? "—" : (entry.IsDirectory ? "폴더 ([용량계산] 클릭)" : $"{entry.SizeDisplay} ({entry.SizeBytes:N0} 바이트)");
        DetailModified.Text = entry?.ModifiedDisplay ?? "—";
        if (DetailCreated is not null) DetailCreated.Text = entry?.CreatedDisplay ?? "—";
        DetailLocation.Text = entry is null ? "—" : Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath;
        if (DetailHash is not null) DetailHash.Text = string.Empty;
        if (HashButton is not null) HashButton.IsEnabled = entry is { IsDirectory: false };
        if (FolderSizeButton is not null) FolderSizeButton.IsEnabled = entry is { IsDirectory: true };

        _updatingAttributes = true;
        try
        {
            if (ReadOnlyCheck is not null)
            {
                ReadOnlyCheck.IsEnabled = entry is not null;
                ReadOnlyCheck.IsChecked = entry?.IsReadOnly == true;
            }
            if (HiddenCheck is not null)
            {
                HiddenCheck.IsEnabled = entry is not null;
                HiddenCheck.IsChecked = entry?.IsHidden == true;
            }
        }
        finally { _updatingAttributes = false; }
    }

    private void AttributeCheck_Click(object sender, RoutedEventArgs e) => ApplySelectedFileAttributes();
    private async void FolderSizeButton_Click(object sender, RoutedEventArgs e) => await RunFolderSizeCalculationAsync();
    private async void HashButton_Click(object sender, RoutedEventArgs e) => await RunFileHashCalculationAsync();
    private async void AiMemoButton_Click(object sender, RoutedEventArgs e) => await RunAiMemoSummaryAsync();

    public bool ConfirmLeavingMemo()
    {
        if (!_memoDirty || _selected is null) return true;
        MessageBoxResult choice = MessageBox.Show(Window.GetWindow(this),
            $"'{_selected.Name}'의 저장되지 않은 메모가 있습니다. 저장할까요?",
            "메모 저장 확인", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return SaveMemo();
        LoadMemoHistory(); // Discard the in-memory draft if the caller stays on this file.
        return true;
    }

    private void LoadMemoHistory(string? savedPath = null)
    {
        _updatingMemo = true;
        try
        {
            MemoHistory.ItemsSource = null;
            MemoEditor.Text = string.Empty;
            _memoDirty = false;
            FileEntry? entry = _selected;
            bool canEdit = entry is { IsDirectory: false } && File.Exists(entry.FullPath) &&
                !Path.GetExtension(entry.FullPath).Equals(FileMemoStore.Extension, StringComparison.OrdinalIgnoreCase);
            MemoEditor.IsEnabled = SaveButton.IsEnabled = AiMemoButton.IsEnabled = MemoAuthorInput.IsEnabled = canEdit;
            AcceptMemoChangeButton.IsEnabled = false;
            RestoreMemoButton.IsEnabled = false;
            if (!canEdit)
            {
                MemoStatus.Text = entry?.IsDirectory == true
                    ? "폴더에는 메모를 저장하지 않습니다. 상단 [용량계산]으로 하위 크기를 확인할 수 있습니다."
                    : "파일을 선택하면 #memo 폴더의 기록을 확인하거나 새 메모를 남길 수 있습니다.";
                return;
            }
            string selectedFullPath = entry!.FullPath;
            var records = FileMemoStore.ListForFile(selectedFullPath, out int unreadable);
            string warning = unreadable > 0 ? $" · 손상된 메모 {unreadable}개 제외" : string.Empty;
            // Lookup is deferred until a single record is selected; avoid scanning all AppData
            // histories while rendering an inspector folder with many memo entries.
            RestoreMemoButton.IsEnabled = false;
            MemoHistory.ItemsSource = records;
            var target = savedPath is null ? records.FirstOrDefault() :
                records.FirstOrDefault(r => string.Equals(r.Path, savedPath, StringComparison.OrdinalIgnoreCase)) ?? records.FirstOrDefault();
            if (target is not null)
            {
                MemoHistory.SelectedItem = target;
                MemoEditor.Text = target.Text;
                MemoStatus.Text = $"저장된 메모 {records.Count}개 · 최근: {target.Label}{warning}";
                RestoreMemoButton.IsEnabled = MemoLocalSnapshots.Enabled && MemoLocalSnapshots.List(target.Path).Count > 0;
                WarnIfChanged(target);
            }
            else
            {
                MemoStatus.Text = $"작성된 메모가 없습니다. 저장 시 같은 폴더의 #memo에 생성됩니다.{warning}";
            }
        }
        finally { _updatingMemo = false; }
    }

    private bool SaveMemo()
    {
        if (_selected is not { IsDirectory: false } entry) return false;
        try
        {
            string author = string.IsNullOrWhiteSpace(MemoAuthorInput.Text) ? Environment.UserName : MemoAuthorInput.Text.Trim();
            string dailyPath = FileMemoStore.MemoPath(entry.FullPath, DateTime.Now, author);
            if (File.Exists(dailyPath) && MemoHistory.SelectedItem is FileMemoStore.MemoRecord selected &&
                !string.Equals(selected.Path, dailyPath, StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(Window.GetWindow(this),
                    "이전 메모의 내용을 보고 있습니다. 저장하면 오늘의 내 메모 파일을 덮어씁니다. 계속할까요?",
                    "오늘 메모 덮어쓰기 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            var memo = FileMemoStore.Save(entry.FullPath, MemoEditor.Text, userName: author);
            LoadMemoHistory(memo.Path);
            MemoStatus.Text = $"저장 완료 · {Path.GetFileName(memo.Path)}" +
                (memo.HasIntegrityWarning ? " · 기준정보 확인 필요: " + memo.IntegrityWarning : "");
            MemoSaved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "메모 저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveMemo();

    private void MemoEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingMemo || !MemoEditor.IsEnabled) return;
        _memoDirty = true;
        MemoStatus.Text = "저장되지 않은 변경사항이 있습니다 (Ctrl+S 또는 메모 저장)";
    }

    private void MemoHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMemo || MemoHistory.SelectedItem is not FileMemoStore.MemoRecord record) return;
        if (_memoDirty && MessageBox.Show(Window.GetWindow(this),
                "작성 중인 메모를 버리고 선택한 이전 기록을 불러올까요?",
                "메모 기록 보기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _updatingMemo = true;
        try
        {
            MemoEditor.Text = record.Text;
            _memoDirty = false;
            bool isTodayMine = _selected is not null && string.Equals(record.Path,
                FileMemoStore.MemoPath(_selected.FullPath, DateTime.Now,
                    string.IsNullOrWhiteSpace(MemoAuthorInput.Text) ? Environment.UserName : MemoAuthorInput.Text.Trim()), StringComparison.OrdinalIgnoreCase);
            MemoStatus.Text = isTodayMine
                ? $"오늘 메모 열람 중 · {record.Label}"
                : $"이전 기록 열람 중 · {record.Label} (저장 시 입력된 작성자의 오늘 메모로 기록됨)";
            RestoreMemoButton.IsEnabled = MemoLocalSnapshots.Enabled && MemoLocalSnapshots.List(record.Path).Count > 0;
            WarnIfChanged(record);
        }
        finally { _updatingMemo = false; }
    }

    private void WarnIfChanged(FileMemoStore.MemoRecord record)
    {
        bool structureValid = _selected is not null &&
            string.Equals(Path.GetFileName(record.Path),
                Path.GetFileName(FileMemoStore.MemoPath(_selected.FullPath, record.CreatedLocal, record.UserName)),
                StringComparison.OrdinalIgnoreCase);
        AcceptMemoChangeButton.IsEnabled = record.HasIntegrityWarning && structureValid;
        if (!record.HasIntegrityWarning) return;
        MemoStatus.Text = "⚠ 메모 변경/검증 경고 · " + record.IntegrityWarning;
        if (_displayedMemoWarnings.Add(record.Path + ":" + record.IntegrityWarning))
            MessageBox.Show(Window.GetWindow(this),
                record.IntegrityWarning + "\n\n메모 내용은 계속 표시합니다. 작성자 이름은 신원 인증이 아닙니다. " +
                "정상적인 공동 편집인지 확인한 뒤에만 [변경 확인·기준 갱신]을 선택하세요.",
                "Sunrise 메모 변경 주의", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AcceptMemoChangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (MemoHistory.SelectedItem is not FileMemoStore.MemoRecord record || !record.HasIntegrityWarning) return;
        if (MessageBox.Show(Window.GetWindow(this),
                "현재 메모 파일을 이 PC의 새 비교 기준으로 승인하시겠습니까?\n" +
                "이 작업은 작성자의 신원을 확인하지 않고, 이전 내용으로 복원하지도 않습니다.\n" +
                "원본이 실제로 변경된 이유를 먼저 확인하세요.",
                "메모 변경 기준 승인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        string? error = MemoIntegrityBaseline.AcceptCurrentAsReference(record.Path, record.UserName);
        if (error is not null)
        {
            MessageBox.Show(Window.GetWindow(this), error, "기준 갱신 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LoadMemoHistory(record.Path);
    }

    private void MemoBackupsEnabled_Click(object sender, RoutedEventArgs e)
    {
        bool desired = MemoBackupsEnabled.IsChecked == true;
        if (desired && MessageBox.Show(Window.GetWindow(this),
                "이 PC의 사용자 AppData에 메모 사본을 날짜별로 암호화하여 저장합니다.\n" +
                "원본 문서는 복제하지 않지만 메모에도 업무정보가 있을 수 있습니다.\n" +
                "회사 보안정책이 허용되는 경우에만 활성화하세요. 이력은 다른 PC에서 해독할 수 없습니다.\n" +
                "로컬 이력을 활성화할까요?", "메모 로컬 이력 보안 안내",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            MemoBackupsEnabled.IsChecked = false;
            return;
        }
        try
        {
            MemoLocalSnapshots.SetEnabled(desired);
            if (desired && MemoHistory.SelectedItem is FileMemoStore.MemoRecord record && !record.HasIntegrityWarning)
            {
                string? warning = MemoLocalSnapshots.TrySave(record.Path, record.UserName);
                if (warning is not null)
                    MessageBox.Show(Window.GetWindow(this), warning, "로컬 이력 생성 주의", MessageBoxButton.OK, MessageBoxImage.Warning);
                RestoreMemoButton.IsEnabled = MemoLocalSnapshots.List(record.Path).Count > 0;
            }
            else RestoreMemoButton.IsEnabled = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MemoBackupsEnabled.IsChecked = MemoLocalSnapshots.Enabled;
            MessageBox.Show(Window.GetWindow(this), ex.Message, "로컬 이력 설정 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreMemoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsDirectory: false } entry || MemoHistory.SelectedItem is not FileMemoStore.MemoRecord record) return;
        if (HasUnsavedMemo)
        {
            MessageBox.Show(Window.GetWindow(this), "먼저 작성 중인 메모를 저장하거나 취소하세요.", "메모 복원", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!MemoLocalSnapshots.Enabled)
        {
            MessageBox.Show(Window.GetWindow(this), "로컬 이력 저장이 현재 비활성화되어 있습니다. '로컬 이력'을 먼저 활성화해야 복원할 수 있습니다.",
                "메모 복원", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var versions = MemoLocalSnapshots.List(record.Path);
            if (versions.Count == 0) return;
            var chooser = new MemoVersionPickerWindow(record.Path, record.Text, versions)
            { Owner = Window.GetWindow(this) };
            if (chooser.ShowDialog() != true || chooser.SelectedVersion is not { } selectedVersion) return;
            if (MessageBox.Show(Window.GetWindow(this),
                $"선택한 {selectedVersion.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 버전을 공유 메모에 복원할까요?\n" +
                "현재 메모는 교체되며, 다른 사용자의 최신 편집도 덮어쓸 수 있습니다.\n" +
                "현재 내용은 우선 이 PC의 암호화 이력에 보존합니다.",
                "선택한 메모 복원 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            MemoLocalSnapshots.Restore(record.Path, selectedVersion, entry.FullPath);
            LoadMemoHistory(record.Path);
            MemoStatus.Text = "로컬 이력에서 메모를 복원했습니다. 공유 메모의 내용을 확인하세요.";
            MemoSaved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { MessageBox.Show(Window.GetWindow(this), ex.Message, "메모 복원 실패", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public bool SaveShortcutIfFocused()
    {
        if (!IsKeyboardFocusWithin || !MemoEditor.IsEnabled) return false;
        return SaveMemo();
    }
}
