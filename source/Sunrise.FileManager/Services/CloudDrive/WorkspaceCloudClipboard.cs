using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R23: clipboard contains only an opaque, per-copy random marker. Cloud IDs, OAuth
/// credentials and remote URLs stay in process memory, never in Windows CF_HDROP.
/// A marker from a different process or an expired/replaced clipboard is rejected.
/// This is explicitly COPY-only until verified cloud Move is implemented.
/// </summary>
public static class WorkspaceCloudClipboard
{
    public const string Format = "Sunrise.FileManager.CloudCopy.v1";
    private static string? _marker;
    private static CloudProvider? _provider;
    private static IReadOnlyList<CrossPaneCopySource>? _sources;

    static WorkspaceCloudClipboard()
    {
        CloudSessionHub.ConnectionChanged += provider =>
        {
            if (_provider == provider) Clear(); // Never reuse remote IDs after account switch/revoke.
        };
    }

    public static void WriteCloudCopy(CloudProvider provider, IReadOnlyList<CrossPaneCopySource> sources)
    {
        if (sources.Count == 0 || sources.Count > CrossPaneBatchCopyService.MaximumFiles ||
            sources.Any(source => source.RemoteFile is null || source.RemoteTransfer is null))
            throw new ArgumentException("유효한 클라우드 파일·폴더를 선택하세요.");
        Clear();
        string marker = Guid.NewGuid().ToString("N");
        var data = new DataObject();
        data.SetData(Format, marker, false);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)), false);
        // Do not persist to the Windows clipboard after Sunrise terminates.
        Clipboard.SetDataObject(data, false);
        _sources = sources.ToArray();
        _provider = provider;
        _marker = marker;
    }

    public static bool TryReadCloudCopy(out IReadOnlyList<CrossPaneCopySource> sources)
    {
        sources = [];
        if (_sources is null || _marker is null || !Clipboard.ContainsData(Format)) return false;
        if (Clipboard.GetData(Format) is not string marker ||
            !string.Equals(marker, _marker, StringComparison.Ordinal)) return false;
        sources = _sources;
        return true;
    }

    public static void Clear()
    {
        _sources = null;
        _provider = null;
        _marker = null;
        // Deliberately do NOT Clipboard.Clear(): other applications own clipboard contents.
    }

    public static bool IsCutFromClipboard()
    {
        object? effect = Clipboard.GetData("Preferred DropEffect");
        byte[]? bytes = effect switch
        {
            MemoryStream stream when stream.Length >= 4 => stream.ToArray(),
            byte[] data when data.Length >= 4 => data,
            _ => null
        };
        return bytes is not null && (BitConverter.ToInt32(bytes, 0) & 2) != 0;
    }
}
