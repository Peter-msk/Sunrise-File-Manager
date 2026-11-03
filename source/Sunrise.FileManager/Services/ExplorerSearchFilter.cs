using System.IO;
using System.Text.RegularExpressions;

namespace Sunrise.FileManager.Services;

public enum ExplorerSearchTarget { All, Files, Folders }

/// <summary>Search-only file type selection. Never changes files or traverses junctions.</summary>
public sealed class ExplorerSearchFilter
{
    public static readonly IReadOnlyDictionary<string, (string Label, string Extensions)> Presets =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["All"] = ("모든 형식", ""),
            ["Hangul"] = ("한글", ".hwp,.hwpx,.hwt"),
            ["Pdf"] = ("PDF", ".pdf"),
            ["Photoshop"] = ("포토샵", ".psd,.psb"),
            ["Illustrator"] = ("일러스트", ".ai,.eps,.svg"),
            ["Cad"] = ("CAD · 도면", ".dwg,.dxf,.dgn,.dwf,.ifc"),
            ["Excel"] = ("엑셀 · 표", ".xls,.xlsx,.xlsm,.xlsb,.csv"),
            ["Image"] = ("이미지", ".jpg,.jpeg,.png,.tif,.tiff,.bmp,.gif,.webp,.heic"),
            ["Word"] = ("워드", ".doc,.docx,.docm,.rtf,.odt"),
            ["PowerPoint"] = ("프레젠테이션", ".ppt,.pptx,.pptm,.odp"),
            ["Archive"] = ("압축파일", ".zip,.7z,.rar,.tar,.gz,.bz2,.xz"),
            ["Custom"] = ("직접 입력", ""),
        };

    public ExplorerSearchTarget Target { get; }
    public string Preset { get; }
    public string CustomExtensions { get; }
    private readonly HashSet<string> _extensions;
    public bool IsConstrained => Target != ExplorerSearchTarget.All || Preset != "All";
    public string Summary => Target == ExplorerSearchTarget.Folders ? "폴더만" :
        (Target == ExplorerSearchTarget.Files ? "파일만" : "전체") + " · " +
        (Preset == "Custom" ? CustomExtensions : Presets[Preset].Label);

    public ExplorerSearchFilter(ExplorerSearchTarget target = ExplorerSearchTarget.All,
        string preset = "All", string customExtensions = "")
    {
        if (!Enum.IsDefined(target)) throw new ArgumentException("검색 대상을 선택하세요.");
        if (!Presets.ContainsKey(preset)) throw new ArgumentException("지원하지 않는 파일 종류입니다.");
        Target = target; Preset = preset;
        CustomExtensions = NormalizeExtensions(customExtensions);
        _extensions = ParseSet(preset == "Custom" ? CustomExtensions : Presets[preset].Extensions);
        if (preset == "Custom" && _extensions.Count == 0) throw new ArgumentException("확장자를 하나 이상 입력하세요.");
    }

    public bool Allows(bool isDirectory, string name)
    {
        if (Target == ExplorerSearchTarget.Folders) return isDirectory;
        if (isDirectory) return Target != ExplorerSearchTarget.Files && Preset == "All";
        return Preset == "All" || _extensions.Contains(Path.GetExtension(name));
    }

    public static string NormalizeExtensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Length > 300) throw new ArgumentException("확장자 입력은 300자 이내입니다.");
        var pieces = raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length > 32) throw new ArgumentException("확장자는 32개까지 입력할 수 있습니다.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string piece in pieces)
        {
            string ext = piece.TrimStart('.');
            if (!Regex.IsMatch(ext, "^[A-Za-z0-9]{1,15}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("확장자만 입력하세요. 예: pdf, dwg, hwp (경로·와일드카드 사용 불가)");
            result.Add("." + ext.ToLowerInvariant());
        }
        return string.Join(",", result.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static HashSet<string> ParseSet(string normalized) =>
        normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
