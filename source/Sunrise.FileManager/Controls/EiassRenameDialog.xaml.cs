using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class EiassRenameDialog : Window
{
    public sealed class Row(string source) : INotifyPropertyChanged
    {
        public string SourcePath { get; } = source;
        public string OldName => Path.GetFileName(SourcePath);
        private string _code = "", _title = EiassFileNameService.InitialTitle(source), _newName = "", _status = "";
        public string Code { get => _code; set { _code = value; Changed(); } }
        public string Title { get => _title; set { _title = value; Changed(); } }
        public string NewName { get => _newName; set { _newName = value; Changed(); } }
        public string Status { get => _status; set { _status = value; Changed(); } }
        public string ContentsMatch { get; set; } = "";
        public bool CanApply { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }

    private readonly ObservableCollection<Row> _rows = [];
    private bool _ready, _updating, _contentsUsed, _reading;
    private readonly CancellationTokenSource _readCancellation = new();
    public IReadOnlyList<(string SourcePath, string NewName)> ApprovedRenames { get; private set; } = [];
    private int Digits => DigitsBox.SelectedIndex == 1 ? 5 : 4;

    public EiassRenameDialog(IReadOnlyList<string> sources)
    {
        InitializeComponent();
        string[] pdfs = sources.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pdfs.Length is < 1 or > 1000) throw new ArgumentException("PDF 파일을 1~1,000개 선택하세요.");
        foreach (string path in pdfs)
        {
            var row = new Row(Path.GetFullPath(path));
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(Row.Code) or nameof(Row.Title) && !_updating)
                {
                    if (_contentsUsed && e.PropertyName == nameof(Row.Title)) row.ContentsMatch = "제목 수정 · 재확인";
                    InvalidateOrder(); Rebuild();
                }
            };
            _rows.Add(row);
        }
        PreviewGrid.ItemsSource = _rows;
        SubtitleText.Text = $"PDF {pdfs.Length}개 선택 · 제외 {sources.Count - pdfs.Length}개 (폴더 / PDF 외 파일). 말머리 + 목차번호 + 문서 제목을 미리 확인한 뒤 적용하세요.";
        StageBox.ItemsSource = EiassFileNameService.Stages;
        StageBox.SelectedIndex = 0;
        StageBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (_ready) Rebuild(); }));
        _ready = true;
        Renumber();
        Closed += (_, _) => _readCancellation.Cancel();
    }

    private void Rule_Changed(object sender, RoutedEventArgs e) { if (_ready) Rebuild(); }
    private void Digits_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) Renumber(); }
    private void Number_Click(object sender, RoutedEventArgs e) => Renumber();
    private void InvalidateOrder() { if (_contentsUsed) OrderConfirmedCheck.IsChecked = false; }

    private void Renumber()
    {
        if (!_ready) return;
        try
        {
            string[] numbers = EiassFileNameService.Number(_rows.Count, Digits, StartBox.Text, StepBox.Text);
            _updating = true;
            for (int i = 0; i < numbers.Length; i++) _rows[i].Code = numbers[i];
            _updating = false;
            InvalidateOrder();
            Rebuild();
        }
        catch (ArgumentException ex) { _updating = false; ValidationStatus.Text = ex.Message; ApplyButton.IsEnabled = false; }
    }

    private void Rebuild()
    {
        if (!_ready || _updating) return;
        bool climate = AssessmentBox.SelectedIndex == 5;
        StageBox.IsEnabled = AssessmentBox.SelectedIndex < 3;
        var names = new Dictionary<Row, string>();
        foreach (var row in _rows)
        {
            row.CanApply = false;
            try
            {
                if (climate) { row.NewName = row.OldName; row.Status = "이름 유지"; continue; }
                row.NewName = EiassFileNameService.MakeName(row.SourcePath, row.Code, row.Title, Digits,
                    StageBox.IsEnabled ? StageBox.Text.Trim() : null, CleanCheck.IsChecked == true);
                names[row] = Path.Combine(Path.GetDirectoryName(row.SourcePath)!, row.NewName);
                row.Status = row.OldName == row.NewName ? "변경 없음" : "변경 가능";
                row.CanApply = row.OldName != row.NewName;
            }
            catch (ArgumentException ex) { row.NewName = ""; row.Status = ex.Message; }
        }
        var activeSources = names.Where(p => p.Key.CanApply).Select(p => p.Key.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in names)
        {
            var row = pair.Key;
            if (_rows.Count(r => r.Code == row.Code) > 1)
            { row.CanApply = false; row.Status = "목차번호 중복 · 수정 필요"; }
            if (names.Values.Count(p => p.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) > 1 ||
                ((File.Exists(pair.Value) || Directory.Exists(pair.Value)) && !pair.Value.Equals(row.SourcePath, StringComparison.OrdinalIgnoreCase) && !activeSources.Contains(pair.Value)))
            { row.CanApply = false; row.Status = "이름 충돌 · 덮어쓰지 않음"; }
            if (!File.Exists(row.SourcePath)) { row.CanApply = false; row.Status = "원본 파일 없음"; }
        }
        int errors = _rows.Count(r => !climate && (r.NewName.Length == 0 || (!r.CanApply && r.OldName != r.NewName) || !File.Exists(r.SourcePath)));
        foreach (var row in _rows) if (row.ContentsMatch.Length > 0) row.Status += " / " + row.ContentsMatch;
        int changes = _rows.Count(r => r.CanApply);
        bool confirmed = !_contentsUsed || OrderConfirmedCheck.IsChecked == true;
        ValidationStatus.Text = climate ? "기후변화영향평가는 지정 파일명 규칙이 없어 현재 이름을 유지합니다."
            : errors > 0 ? $"형식 오류 또는 이름 충돌 {errors}건 · 표에서 수정하세요."
            : !confirmed ? "목차와 자동 일치하지 않은 항목은 위·아래 버튼으로 조정하고 파일 순서 확인에 체크하세요."
            : $"변경 대상 {changes}개 · 변경 후 이름과 순서를 확인하세요.";
        ApplyButton.IsEnabled = !_reading && !climate && confirmed && errors == 0 && changes > 0;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int offset)
    {
        PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true); PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true);
        int index = PreviewGrid.SelectedIndex;
        if (index < 0 || index + offset < 0 || index + offset >= _rows.Count) return;
        Row selected = _rows[index]; _rows.Move(index, index + offset); PreviewGrid.SelectedItem = selected;
        Renumber(); PreviewGrid.ScrollIntoView(selected);
    }

    private void Contents_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!string.IsNullOrWhiteSpace(ContentsBox.Text) || _contentsUsed)
        {
            _contentsUsed = true; OrderConfirmedCheck.Visibility = Visibility.Visible; InvalidateOrder();
            ContentsStatus.Text = "목차를 입력·수정했습니다. ‘입력한 목차로 순서 맞추기’를 누르거나 순서를 직접 조정하세요.";
        }
        Rebuild();
    }

    private async void LoadContents_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "목차 부분이 담긴 파일 지정", Filter = "목차 문서|*.pdf;*.docx;*.hwpx;*.hwp;*.txt", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        _reading = true; LoadContentsButton.IsEnabled = MatchContentsButton.IsEnabled = false; Rebuild();
        ContentsStatus.Text = "목차 텍스트를 읽고 있습니다…";
        try
        {
            string text = await Task.Run(() => EiassTableOfContentsReader.Read(picker.FileName, _readCancellation.Token));
            if (_readCancellation.IsCancellationRequested) return;
            ContentsBox.Text = text;
            ContentsStatus.Text = Path.GetFileName(picker.FileName) + " 읽기 완료 · 추출한 제목을 확인한 뒤 순서 맞추기를 누르세요.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_readCancellation.IsCancellationRequested) ContentsStatus.Text = "목차 읽기 실패: " + ex.Message; }
        finally { _reading = false; LoadContentsButton.IsEnabled = MatchContentsButton.IsEnabled = true; Rebuild(); }
    }

    private void MatchContents_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var contents = EiassFileNameService.ParseContents(ContentsBox.Text);
            if (contents.Count == 0) throw new ArgumentException("목차 제목을 한 줄에 하나씩 입력하세요.");
            Row[] original = _rows.ToArray();
            int?[] matches = EiassFileNameService.MatchContents(original.Select(r => r.Title).ToArray(), contents);
            for (int i = 0; i < original.Length; i++) original[i].ContentsMatch = matches[i] is int n ? $"목차 {n + 1}번째 일치" : "미일치/중복 · 수동 확인";
            var ordered = original.Select((row, i) => (row, rank: matches[i] ?? int.MaxValue)).OrderBy(p => p.rank).Select(p => p.row).ToArray();
            _rows.Clear(); foreach (var row in ordered) _rows.Add(row);
            _contentsUsed = true; OrderConfirmedCheck.Visibility = Visibility.Visible;
            ContentsStatus.Text = $"목차 {contents.Count}항목 · 일치 {matches.Count(m => m.HasValue)}개 · 미일치/중복 {matches.Count(m => !m.HasValue)}개는 아래쪽에 기존 순서로 유지했습니다. 일치하지 않는 목차 항목은 번호 생성에 포함되지 않습니다.";
            Renumber();
        }
        catch (ArgumentException ex) { ContentsStatus.Text = ex.Message; InvalidateOrder(); Rebuild(); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true); PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Rebuild();
        if (!ApplyButton.IsEnabled) return;
        ApprovedRenames = _rows.Where(r => r.CanApply).Select(r => (r.SourcePath, r.NewName)).ToArray();
        DialogResult = true;
    }
}
