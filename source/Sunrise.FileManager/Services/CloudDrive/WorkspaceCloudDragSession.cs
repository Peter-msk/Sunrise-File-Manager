using System.IO;
using System.Windows;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>
/// R24: process-local, COPY-ONLY cloud drag identity. Only a random marker enters
/// WPF's DataObject; remote IDs, provider access tokens and signed URLs stay in memory.
/// Do not advertise FileDrop for cloud content (there is no local file to hand to Explorer).
/// </summary>
public static class WorkspaceCloudDragSession
{
    public const string Format = "Sunrise.FileManager.CloudDrag.v1";
    private static string? _marker;
    private static CloudProvider? _provider;
    private static IReadOnlyList<CrossPaneCopySource>? _sources;

    static WorkspaceCloudDragSession()
    {
        CloudSessionHub.ConnectionChanged += provider =>
        {
            if (_provider == provider) Clear();
        };
    }

    public static DataObject Begin(CloudProvider provider, IReadOnlyList<CrossPaneCopySource> sources)
    {
        if (sources is null || sources.Count == 0 || sources.Count > CrossPaneBatchCopyService.MaximumFiles ||
            sources.Any(source => source.RemoteFile is null || source.RemoteTransfer is null || source.RemoteApi is null))
            throw new ArgumentException("유효한 클라우드 드래그 항목 1~128개가 필요합니다.");
        Clear();
        _marker = Guid.NewGuid().ToString("N");
        _provider = provider;
        _sources = sources.ToArray();
        var data = new DataObject();
        data.SetData(Format, _marker, false);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)), false);
        return data;
    }

    public static bool TryResolve(IDataObject data, out IReadOnlyList<CrossPaneCopySource> sources)
    {
        sources = [];
        if (_sources is null || _marker is null || !data.GetDataPresent(Format)) return false;
        if (data.GetData(Format) is not string supplied ||
            !string.Equals(supplied, _marker, StringComparison.Ordinal)) return false;
        sources = _sources;
        return true;
    }

    public static bool IsCurrent(IReadOnlyList<CrossPaneCopySource> sources) =>
        _marker is not null && ReferenceEquals(_sources, sources);

    public static void Clear()
    {
        _marker = null;
        _provider = null;
        _sources = null;
    }
}
