using System.ComponentModel;
using System.IO;
using Sunrise.FileManager.Services;

namespace Sunrise.FileManager.Models;

public sealed class FileEntry : INotifyPropertyChanged
{
    public static string DescribeCloudState(FileAttributes attrs)
    {
        uint flags = (uint)attrs;
        if ((flags & 0x400000) != 0 || (flags & 0x1000) != 0) return "온라인 전용";
        if ((flags & 0x80000) != 0) return "항상 이 장치에 유지";
        if ((flags & 0x100000) != 0) return "공간 확보 대상";
        if ((flags & 0x40000) != 0) return "클라우드 파일";
        return "";
    }
    public string CloudStatus { get; init; } = "";
    public string ParentFolder => Path.GetDirectoryName(FullPath) ?? "";
    public string ModifiedGroup => ModifiedAt == DateTime.MinValue ? "날짜 없음" : ModifiedAt.ToString("yyyy-MM-dd");
    public string SizeGroup => IsDirectory ? "폴더" : SizeBytes < 1024 * 1024 ? "1MB 미만" : SizeBytes < 100L * 1024 * 1024 ? "1~100MB" : SizeBytes < 1024L * 1024 * 1024 ? "100MB~1GB" : "1GB 이상";
    private object? _thumbnailSource;
    private bool _showExtension = true;
    private bool _colorCoding = true;
    private bool _isChecked;
    private bool _isCutPending;
    private bool _isRenaming;
    private string _renameDraft = string.Empty;

    // Ephemeral row editor state; never serialized and never used as a path identity.
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
        }
    }

    public string RenameDraft
    {
        get => _renameDraft;
        set
        {
            if (_renameDraft == value) return;
            _renameDraft = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RenameDraft)));
        }
    }

    public bool ThumbnailLoading { get; set; }
    public object? ThumbnailSource
    {
        get => _thumbnailSource;
        set
        {
            if (ReferenceEquals(_thumbnailSource, value)) return;
            _thumbnailSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailSource)));
        }
    }

    public bool ShowExtension
    {
        get => _showExtension;
        set
        {
            if (_showExtension == value) return;
            _showExtension = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowExtension)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    public bool ColorCoding
    {
        get => _colorCoding;
        set
        {
            if (_colorCoding == value) return;
            _colorCoding = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorCoding)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameForeground)));
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public bool IsCutPending
    {
        get => _isCutPending;
        set
        {
            if (_isCutPending == value) return;
            _isCutPending = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCutPending)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowOpacity)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsNavigationLocation { get; init; }
    public string? LocationSummary { get; init; }
    public bool IsVirtual { get; init; }
    public string OriginalLocation { get; init; } = "";
    public DateTime DeletedAt { get; init; }
    public string DeletedDisplay => DeletedAt == DateTime.MinValue ? "—" : DeletedAt.ToString("yyyy-MM-dd HH:mm");

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required string TypeName { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime ModifiedAt { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.MinValue;
    public bool IsHidden { get; init; }
    public bool IsReadOnly { get; init; }
    public bool HasMemo { get; init; }

    public string DisplayName => IsDirectory || _showExtension
        ? Name
        : (Path.GetFileNameWithoutExtension(Name) is { Length: > 0 } stem ? stem : Name);

    public string MemoBadge => HasMemo ? "📝" : "";

    public string AttributesDisplay
    {
        get
        {
            var flags = new List<string>();
            if (IsDirectory) flags.Add("폴더");
            if (IsReadOnly) flags.Add("읽기전용");
            if (IsHidden) flags.Add("숨김");
            if (HasMemo) flags.Add("#memo");
            return flags.Count == 0 ? "일반" : string.Join(" · ", flags);
        }
    }

    public string Icon => IsDirectory ? "" : Path.GetExtension(Name).ToUpperInvariant() switch
    {
        ".HWP" or ".HWPX" => "HWP",
        ".DOC" or ".DOCX" => "DOC",
        ".XLS" or ".XLSX" or ".CSV" => "XLS",
        ".PPT" or ".PPTX" => "PPT",
        ".PDF" => "PDF",
        ".PNG" or ".JPG" or ".JPEG" or ".GIF" or ".WEBP" or ".BMP" or ".SVG" => "IMG",
        ".ZIP" or ".7Z" or ".RAR" or ".TAR" or ".GZ" => "ZIP",
        ".MP4" or ".MKV" or ".AVI" or ".MOV" or ".WMV" => "VID",
        ".MP3" or ".WAV" or ".FLAC" or ".M4A" => "AUD",
        ".EXE" or ".MSI" or ".CMD" or ".BAT" => "APP",
        ".CS" or ".PY" or ".JS" or ".TS" or ".JSON" or ".XML" or ".HTML" or ".CSS" => "DEV",
        ".TXT" or ".MD" or ".LOG" => "TXT",
        _ => "FILE"
    };

    public string IconColor => Icon switch
    {
        "HWP" => "#1463A6",
        "DOC" => "#245CB4",
        "XLS" => "#24704A",
        "PPT" => "#C4512D",
        "PDF" => "#AE3A3A",
        "IMG" => "#0E7490",
        "ZIP" => "#9A6700",
        "VID" => "#6D28D9",
        "AUD" => "#BE185D",
        "APP" => "#1E293B",
        "DEV" => "#0F766E",
        "TXT" => "#475569",
        _ => "#52657A"
    };

    /// <summary>
    /// Q-Dir style extension color filter for instant visual scanning.
    /// </summary>
    public string NameForeground
    {
        get
        {
            // Dark mode cannot reuse the dark navy/green category colors of light mode.
            if (_darkTheme)
            {
                if (!_colorCoding) return "#EDF4FF";
                if (IsHidden) return "#AFC3D7";
                if (IsDirectory) return "#F3F7FF";
                return Icon switch
                {
                    "APP" => "#D7C1FF",
                    "ZIP" => "#FFD38C",
                    "HWP" or "DOC" or "PDF" or "XLS" or "PPT" => "#B4DFFF",
                    "IMG" or "VID" or "AUD" => "#A4EDDA",
                    "DEV" => "#B5E4FF",
                    _ => "#EDF4FF"
                };
            }
            if (!_colorCoding) return "#172B43";
            if (IsHidden) return "#52657A";
            if (IsDirectory) return "#163A55";
            return Icon switch
            {
                "APP" => "#5B21B6",
                "ZIP" => "#8C5200",
                "HWP" or "DOC" or "PDF" or "XLS" or "PPT" => "#0B4F82",
                "IMG" or "VID" or "AUD" => "#0D685B",
                "DEV" => "#1E5E75",
                _ => "#172B43"
            };
        }
    }

    private bool _darkTheme;
    public void RefreshThemeColors(bool isDark = false)
    {
        _darkTheme = isDark;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameForeground)));
    }

    public double RowOpacity => _isCutPending ? 0.52 : (IsHidden ? 0.62 : 1.0);

    public string ModifiedDisplay => ModifiedAt == DateTime.MinValue ? "—" : ModifiedAt.ToString("yyyy-MM-dd HH:mm");
    public string CreatedDisplay => CreatedAt == DateTime.MinValue ? "—" : CreatedAt.ToString("yyyy-MM-dd HH:mm");
    public string SizeDisplay => LocationSummary ?? (IsDirectory ? "—" : HumanSize(SizeBytes));

    public static FileEntry From(FileSystemInfo item, bool hasMemo = false)
    {
        bool directory = item is DirectoryInfo;
        FileAttributes attrs = default;
        try { attrs = item.Attributes; } catch { }
        return new FileEntry
        {
            Name = item.Name,
            FullPath = item.FullName,
            IsDirectory = directory,
            TypeName = directory ? "폴더" : FriendlyTypeName(item.Name),
            SizeBytes = item is FileInfo file ? file.Length : 0L,
            ModifiedAt = item.LastWriteTime,
            CreatedAt = SafeCreationTime(item),
            IsHidden = (attrs & FileAttributes.Hidden) != 0,
            IsReadOnly = (attrs & FileAttributes.ReadOnly) != 0,
            CloudStatus = DescribeCloudState(attrs),
            HasMemo = hasMemo
        };
    }

    private static DateTime SafeCreationTime(FileSystemInfo item)
    {
        try { return item.CreationTime; }
        catch { return DateTime.MinValue; }
    }

    public static string FriendlyTypeName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return "파일";
        return ext.ToUpperInvariant();
    }

    public static string HumanSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        double size = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:N1} {units[unit]}";
    }
}
