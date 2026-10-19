using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

public partial class BatchRenameDialog : Window
{
    public sealed record PreviewRow(string SourcePath, string OldName, string NewName, string Status, bool CanApply);

    private readonly IReadOnlyList<string> _sourcePaths;
    private List<PreviewRow> _currentRows = [];
    public IReadOnlyList<(string OldPath, string NewName)> ApprovedRenames { get; private set; } = [];

    public BatchRenameDialog(IReadOnlyList<string> sourcePaths)
    {
        InitializeComponent();
        _sourcePaths = sourcePaths;
        SubtitleText.Text = $"선택한 {_sourcePaths.Count:N0}개 항목의 이름을 일괄 변경합니다. 파일에 연결된 #memo도 함께 이동합니다.";
        RebuildPreview();
    }

    private void Rule_Changed(object sender, RoutedEventArgs e) => RebuildPreview();

    private void RebuildPreview()
    {
        if (PreviewGrid is null || ValidationStatus is null || ApplyButton is null) return;
        string prefix = PrefixBox?.Text ?? "";
        string suffix = SuffixBox?.Text ?? "";
        string find = FindBox?.Text ?? "";
        string replace = ReplaceBox?.Text ?? "";
        bool useNumber = NumberingCheck?.IsChecked == true;
        if (!int.TryParse(StartNumberBox?.Text, out int startNum) || startNum < 0) startNum = 1;

        var rows = new List<PreviewRow>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int seq = startNum;
        int validChangeCount = 0;
        int conflictCount = 0;

        foreach (string path in _sourcePaths)
        {
            bool isDir = Directory.Exists(path);
            string oldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? "";
            string ext = isDir ? "" : Path.GetExtension(oldName);
            string stem = isDir ? oldName : oldName[..(oldName.Length - ext.Length)];

            if (!string.IsNullOrEmpty(find))
                stem = stem.Replace(find, replace, StringComparison.CurrentCultureIgnoreCase);

            string numberPart = useNumber ? $"_{seq++:D3}" : "";
            string candidateName = prefix + stem + suffix + numberPart + ext;

            string status;
            bool canApply = false;
            try
            {
                FileActionsService.ValidateName(candidateName);
                string targetFull = Path.Combine(parent, candidateName);
                if (string.Equals(oldName, candidateName, StringComparison.Ordinal))
                {
                    status = "변경 없음";
                }
                else if (!seenTargets.Add(targetFull) ||
                         ((File.Exists(targetFull) || Directory.Exists(targetFull)) &&
                          !string.Equals(path, targetFull, StringComparison.OrdinalIgnoreCase)))
                {
                    status = "이름 충돌";
                    conflictCount++;
                }
                else
                {
                    status = "변경 가능";
                    canApply = true;
                    validChangeCount++;
                }
            }
            catch (ArgumentException ex)
            {
                status = "오류: " + ex.Message;
                conflictCount++;
            }

            rows.Add(new PreviewRow(path, oldName, candidateName, status, canApply));
        }

        _currentRows = rows;
        PreviewGrid.ItemsSource = rows;
        ValidationStatus.Text = conflictCount > 0
            ? $"⚠ 이름 충돌 또는 형식 오류 {conflictCount}건이 있습니다."
            : validChangeCount > 0
                ? $"변경 대상 {validChangeCount}건 준비 완료"
                : "접두어, 접미어, 치환 문자열 또는 일련번호를 입력하세요.";
        ApplyButton.IsEnabled = conflictCount == 0 && validChangeCount > 0;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var approved = _currentRows.Where(r => r.CanApply).Select(r => (r.SourcePath, r.NewName)).ToList();
        if (approved.Count == 0) return;
        ApprovedRenames = approved;
        DialogResult = true;
    }
}
