using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Controls;

/// <summary>
/// First-party property surface. Reads file-system metadata directly, delegates
/// writable document metadata to the existing property handler and retains the
/// canonical Windows properties sheet for shell-only/advanced extensions.
/// It never enumerates folder children unless the user explicitly requests it.
/// </summary>
public partial class FilePropertiesWindow : Window
{
    private readonly string[] _paths;
    private readonly Func<bool>? _beforeModify;
    private readonly CancellationTokenSource _closeToken = new();
    private bool _initializingAttributes;
    private bool _isFolder;
    private FileAttributes _originalAttributes;
    public bool WasModified { get; private set; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
        string objectName, string? propertyPage);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint str, string assoc,
        string? extra, StringBuilder? output, ref uint length);

    private bool IsSingle => _paths.Length == 1;
    private string FilePath => _paths[0];

    public FilePropertiesWindow(IReadOnlyList<string> paths, Func<bool>? beforeModify = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 1000) throw new ArgumentException("1~1,000개 파일 또는 폴더를 선택하세요.", nameof(paths));
        _paths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _beforeModify = beforeModify;
        InitializeComponent();
        Closed += (_, _) => _closeToken.Cancel();
        Loaded += async (_, _) =>
        {
            try { RefreshProperties(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(this, ex.Message, "파일 정보 불러오기", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }
            await LoadMetadataAsync();
        };
    }

    private void RefreshProperties()
    {
        GeneralInfo.Children.Clear();
        DetailsInfo.Children.Clear();
        SignatureInfo.Children.Clear();
        VersionInfo.Children.Clear();
        HashResult.Text = string.Empty;
        GeneralStatus.Text = string.Empty;
        _initializingAttributes = true;
        try
        {
            if (IsSingle) FillSingleProperties();
            else FillMultipleProperties();
        }
        finally { _initializingAttributes = false; }
    }

    private void FillSingleProperties()
    {
        string path = FilePath;
        _isFolder = Directory.Exists(path);
        bool isFile = File.Exists(path);
        if (!isFile && !_isFolder) throw new IOException("선택 항목이 이동되었거나 더 이상 존재하지 않습니다: " + path);
        var info = _isFolder ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        _originalAttributes = info.Attributes;
        HeaderFileName.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
        HeaderSubline.Text = path;
        AddRow(GeneralInfo, "유형", _isFolder ? "파일 폴더" : (Path.GetExtension(path) is string ext && ext.Length > 0 ? ext.ToUpperInvariant() + " 파일" : "파일"));
        AddRow(GeneralInfo, "위치", Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path);
        AddRow(GeneralInfo, "전체 경로", path);
        if (isFile)
        {
            long length = ((FileInfo)info).Length;
            AddRow(GeneralInfo, "크기", $"{FileEntry.HumanSize(length)} ({length:N0} 바이트)");
            AddRow(GeneralInfo, "디스크 할당", "클러스터·압축·클라우드 저장 정책에 따라 달라질 수 있음 (Windows 원본 확인)");
            string app = GetAssociatedApp(path);
            AddRow(GeneralInfo, "기본 연결 프로그램", app);
        }
        else AddRow(GeneralInfo, "크기", "하위 폴더 합계는 탐색창 상세정보의 '용량계산'에서 확인");
        AddRow(GeneralInfo, "만든 날짜", info.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(GeneralInfo, "수정한 날짜", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(GeneralInfo, "액세스한 날짜", info.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(DetailsInfo, "파일명", HeaderFileName.Text);
        AddRow(DetailsInfo, "확장자", _isFolder ? "—" : Path.GetExtension(path));
        AddRow(DetailsInfo, "특성", info.Attributes.ToString());
        AddRow(DetailsInfo, "위치", path);
        ReadOnlyCheck.IsChecked = (_originalAttributes & FileAttributes.ReadOnly) != 0;
        HiddenCheck.IsChecked = (_originalAttributes & FileAttributes.Hidden) != 0;
        ReadOnlyCheck.IsEnabled = !_isFolder;
        HiddenCheck.IsEnabled = true;
        AttributesNote.Text = _isFolder
            ? "폴더의 읽기 전용 체크는 Windows 탐색기의 특수 동작과 달라 변경을 지원하지 않습니다. 숨김 특성만 수정할 수 있습니다."
            : "파일 특성 변경은 '특성 변경 적용'을 누를 때만 반영됩니다.";
        ApplyAttributesButton.IsEnabled = false;
        EditMetadataButton.IsEnabled = isFile;
        HashButton.IsEnabled = isFile;
        FillSignature(path, isFile);
        FillVersion(path, isFile);
    }

    private void FillMultipleProperties()
    {
        HeaderFileName.Text = $"선택 항목 {_paths.Length:N0}개";
        HeaderSubline.Text = "일괄 정보 보기 · 파일이나 폴더 내용은 변경되지 않습니다";
        long totalBytes = 0;
        int files = 0, folders = 0, unavailable = 0;
        foreach (string path in _paths)
        {
            try
            {
                if (File.Exists(path)) { totalBytes = checked(totalBytes + new FileInfo(path).Length); files++; }
                else if (Directory.Exists(path)) folders++;
                else unavailable++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { unavailable++; }
        }
        AddRow(GeneralInfo, "선택 항목", $"{_paths.Length:N0}개");
        AddRow(GeneralInfo, "파일", $"{files:N0}개");
        AddRow(GeneralInfo, "폴더", $"{folders:N0}개 (폴더 하위 내용은 집계하지 않음)");
        AddRow(GeneralInfo, "파일 크기 합계", $"{FileEntry.HumanSize(totalBytes)} ({totalBytes:N0} 바이트)");
        if (unavailable > 0) AddRow(GeneralInfo, "확인 불가", $"{unavailable:N0}개");
        AddRow(DetailsInfo, "선택", "여러 항목을 선택한 경우 개별 메타데이터 편집·속성 변경·해시 계산은 지원하지 않습니다.");
        AddRow(SignatureInfo, "서명", "파일을 하나 선택하면 서명 인증서 정보를 확인할 수 있습니다.");
        AddRow(VersionInfo, "버전", "파일을 하나 선택하면 버전 정보를 확인할 수 있습니다.");
        ReadOnlyCheck.IsEnabled = false;
        HiddenCheck.IsEnabled = false;
        ReadOnlyCheck.IsChecked = false;
        HiddenCheck.IsChecked = false;
        ApplyAttributesButton.IsEnabled = false;
        EditMetadataButton.IsEnabled = false;
        HashButton.IsEnabled = false;
        MetadataStatus.Text = "여러 파일의 메타데이터 편집은 지원하지 않습니다.";
        WindowsFallbackButton.IsEnabled = _paths.All(p => string.Equals(Path.GetDirectoryName(p), Path.GetDirectoryName(FilePath), StringComparison.OrdinalIgnoreCase));
        WindowsFallbackButton.ToolTip = WindowsFallbackButton.IsEnabled
            ? "같은 폴더에서 선택한 여러 파일의 Windows 속성 열기"
            : "Windows 원본 속성은 같은 폴더에서 선택한 파일만 함께 열 수 있습니다.";
    }

    private static string GetAssociatedApp(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Length == 0) return "연결 프로그램 없음";
        try
        {
            uint length = 0;
            AssocQueryString(0, 4, ext, null, null, ref length); // ASSOCSTR_FRIENDLYAPPNAME
            if (length is < 1 or > 4096) return "Windows 연결 설정에서 확인";
            var buffer = new StringBuilder((int)length);
            return AssocQueryString(0, 4, ext, null, buffer, ref length) == 0
                ? buffer.ToString() : "Windows 연결 설정에서 확인";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { return "Windows 연결 설정에서 확인"; }
    }

    private static void AddRow(Panel panel, string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var body = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetColumn(body, 1);
        grid.Children.Add(heading); grid.Children.Add(body); panel.Children.Add(grid);
    }

    private async Task LoadMetadataAsync()
    {
        if (!IsSingle || _isFolder) return;
        MetadataStatus.Text = "Windows 메타데이터를 확인하고 있습니다…";
        try
        {
            var data = await WindowsMetadataService.ReadAsync(FilePath);
            if (_closeToken.IsCancellationRequested) return;
            MetadataInfo.Children.Clear();
            foreach (var (name, label) in new[]
            {
                ("System.Title", "제목"), ("System.Subject", "주제"),
                ("System.Comment", "설명"), ("System.Keywords", "태그"),
                ("System.Author", "작성자"), ("System.Rating", "평점")
            }) AddRow(MetadataInfo, label, data.GetValueOrDefault(name, ""));
            MetadataStatus.Text = "파일 형식에 따라 일부 필드가 지원되지 않을 수 있습니다.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            if (!_closeToken.IsCancellationRequested)
                MetadataStatus.Text = "이 파일 형식은 Windows 메타데이터 제공 기능을 지원하지 않거나 접근할 수 없습니다: " + ex.Message;
        }
    }

    private void FillSignature(string path, bool isFile)
    {
        if (!isFile) { AddRow(SignatureInfo, "서명", "폴더에는 파일 디지털 서명 정보가 없습니다."); return; }
        if (!new[] { ".exe", ".dll", ".sys", ".ocx", ".msi" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        { AddRow(SignatureInfo, "대상", "서명 확인이 필요한 실행 파일을 선택하세요. 일반 문서 서명 형식은 Windows 원본 또는 문서 전용 검증기에서 확인할 수 있습니다."); return; }
        try
        {
            // .NET 10 has no X509CertificateLoader API that extracts Authenticode
            // signer certs from PE files. Scope the SDK warning to this one native
            // extraction call; this is display-only, NOT a trust decision.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var details = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            AddRow(SignatureInfo, "상태", "서명 인증서 표시 가능 · 무결성/신뢰 확인은 별도 필요");
            AddRow(SignatureInfo, "발급 대상", details.GetNameInfo(X509NameType.SimpleName, false));
            AddRow(SignatureInfo, "발급 기관", details.Issuer);
            AddRow(SignatureInfo, "인증서 만료", details.NotAfter.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRow(SignatureInfo, "지문", details.Thumbprint);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        { AddRow(SignatureInfo, "상태", "표시 가능한 내장 서명 인증서 없음 · 카탈로그 서명 여부는 Windows 원본 속성에서 확인"); }
    }

    private void FillVersion(string path, bool isFile)
    {
        if (!isFile || !new[] { ".exe", ".dll", ".sys", ".ocx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        { AddRow(VersionInfo, "정보", "Windows 파일 버전 리소스가 있는 실행 파일을 선택하세요."); return; }
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            AddRow(VersionInfo, "제품명", info.ProductName ?? "");
            AddRow(VersionInfo, "제품 버전", info.ProductVersion ?? "");
            AddRow(VersionInfo, "파일 버전", info.FileVersion ?? "");
            AddRow(VersionInfo, "설명", info.FileDescription ?? "");
            AddRow(VersionInfo, "회사", info.CompanyName ?? "");
            AddRow(VersionInfo, "저작권", info.LegalCopyright ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { AddRow(VersionInfo, "상태", "버전 정보를 읽지 못했습니다: " + ex.Message); }
    }

    private void AttributeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_initializingAttributes || !IsSingle) return;
        bool readOnlyChanged = !_isFolder && (ReadOnlyCheck.IsChecked == true) != _originalAttributes.HasFlag(FileAttributes.ReadOnly);
        bool hiddenChanged = (HiddenCheck.IsChecked == true) != _originalAttributes.HasFlag(FileAttributes.Hidden);
        ApplyAttributesButton.IsEnabled = readOnlyChanged || hiddenChanged;
    }

    private void ApplyAttributes_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSingle || !ApplyAttributesButton.IsEnabled) return;
        if (_beforeModify?.Invoke() == false)
        { GeneralStatus.Text = "파일 작업이 진행 중이거나 현재 변경이 허용되지 않습니다."; return; }
        try
        {
            bool hidden = HiddenCheck.IsChecked == true;
            bool readOnly = ReadOnlyCheck.IsChecked == true;
            if (hidden != _originalAttributes.HasFlag(FileAttributes.Hidden))
            { ExplorerExtraActions.SetAttribute(FilePath, FileAttributes.Hidden, hidden); WasModified = true; }
            if (!_isFolder && readOnly != _originalAttributes.HasFlag(FileAttributes.ReadOnly))
            { ExplorerExtraActions.SetAttribute(FilePath, FileAttributes.ReadOnly, readOnly); WasModified = true; }
            RefreshProperties();
            GeneralStatus.Text = "파일 특성을 적용했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            GeneralStatus.Text = "파일 특성을 저장하지 못했습니다: " + ex.Message;
            RefreshProperties();
            GeneralStatus.Text = "일부 특성이 적용되었을 수 있습니다. 현재 표시 값을 확인하세요. " + ex.Message;
        }
    }

    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSingle || _isFolder) return;
        if (_beforeModify?.Invoke() == false)
        { MetadataStatus.Text = "현재 파일을 변경할 수 없습니다."; return; }
        var editor = new MetadataEditorWindow(FilePath) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            WasModified = true;
            _ = LoadMetadataAsync();
        }
    }

    private async void Hash_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSingle || _isFolder) return;
        HashButton.IsEnabled = false;
        HashResult.Text = "파일 SHA-256 계산 중…";
        try
        {
            string hex = await Task.Run(() => FileActionsService.ComputeSha256Hex(FilePath));
            if (!_closeToken.IsCancellationRequested) HashResult.Text = "SHA-256  " + hex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!_closeToken.IsCancellationRequested) HashResult.Text = "해시 계산 실패: " + ex.Message; }
        finally { if (!_closeToken.IsCancellationRequested) HashButton.IsEnabled = true; }
    }

    private void WindowsFallback_Click(object sender, RoutedEventArgs e)
    {
        // Windows' own property sheet can mutate attributes and permissions.
        // Do not offer that bypass while the owning pane is busy/locked.
        if (_beforeModify?.Invoke() == false)
        {
            MessageBox.Show(this, "현재 파일 변경이 허용되지 않아 Windows 원본 속성을 열 수 없습니다.",
                "Sunrise 파일 속성", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            if (IsSingle)
            {
                if (!SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x2, FilePath, null))
                    throw new Win32Exception("Windows 원본 속성을 열지 못했습니다.");
            }
            else WindowsShellContextMenu.ShowProperties(this, _paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or Win32Exception)
        { MessageBox.Show(this, ex.Message, "Windows 원본 속성", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
