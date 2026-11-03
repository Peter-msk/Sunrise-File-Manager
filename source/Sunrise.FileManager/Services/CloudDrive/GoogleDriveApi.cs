using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>Drive v3 same-account operations. The default deletion path is reversible Trash, never files.delete.</summary>
public sealed class GoogleDriveApi : CloudDriveApiBase, ICloudDriveApi
{
    private const string Base = "https://www.googleapis.com/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";

    public GoogleDriveApi(HttpClient client, Func<CancellationToken, Task<string>> tokenSource)
        : base(client, tokenSource) { }

    protected override bool AllowedApiHost(string host) => host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase);

    private static Uri Item(string id, string suffix = "") => Url($"{Base}/{Segment(id)}{suffix}");

    private static CloudItem Parse(JsonElement obj)
    {
        string id = RequiredString(obj, "id");
        string name = RequiredString(obj, "name");
        bool folder = OptionalString(obj, "mimeType") == FolderMime;
        long? size = null;
        if (obj.TryGetProperty("size", out var val) &&
            (val.ValueKind == JsonValueKind.Number || val.ValueKind == JsonValueKind.String) &&
            long.TryParse(val.ToString(), out long parsed)) size = parsed;
        string? parent = obj.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() > 0
            ? parents[0].GetString() : null;
        return new(id, name, folder, size, parent);
    }

    private const string Fields = "id,name,mimeType,size,parents";

    public async Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string parentId, CancellationToken token)
    {
        var entries = new List<CloudItem>();
        string? pageToken = null;
        var tokensSeen = new HashSet<string>(StringComparer.Ordinal);
        string escapedQuery = parentId.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        for (int page = 0; page < 10000; ++page)
        {
            token.ThrowIfCancellationRequested();
            string query = Uri.EscapeDataString($"'{escapedQuery}' in parents and trashed = false");
            string endpoint = $"{Base}?q={query}&fields={Uri.EscapeDataString($"nextPageToken,files({Fields})")}&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true";
            if (pageToken is not null) endpoint += "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var json = await JsonAsync(HttpMethod.Get, Url(endpoint), null, token).ConfigureAwait(false);
            foreach (var item in Required(json.RootElement, "files").EnumerateArray()) entries.Add(Parse(item));
            pageToken = OptionalString(json.RootElement, "nextPageToken");
            if (string.IsNullOrEmpty(pageToken)) return entries;
            if (!tokensSeen.Add(pageToken)) throw new InvalidDataException("클라우드 목록 페이지가 반복됩니다.");
        }
        throw new InvalidDataException("클라우드 목록 페이지 한도를 초과했습니다.");
    }

    public async Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken token)
    {
        using var json = await JsonAsync(HttpMethod.Post, Url($"{Base}?fields={Fields}&supportsAllDrives=true"),
            new { name = ItemName(name), mimeType = FolderMime, parents = new[] { parentId } }, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public async Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, CancellationToken token)
    {
        using var json = await JsonAsync(HttpMethod.Post, Url($"{Base}?fields={Fields}&supportsAllDrives=true"),
            new { name = ItemName(name), mimeType = "application/octet-stream", parents = new[] { parentId } }, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public async Task<CloudItem> RenameAsync(string itemId, string name, CancellationToken token)
    {
        using var json = await JsonAsync(HttpMethod.Patch, Item(itemId, $"?fields={Fields}&supportsAllDrives=true"),
            new { name = ItemName(name) }, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public async Task<CloudCopyResult> CopyAsync(string itemId, string destinationParentId, string name, CancellationToken token)
    {
        using var metadata = await JsonAsync(HttpMethod.Get, Item(itemId, "?fields=mimeType"), null, token).ConfigureAwait(false);
        if (OptionalString(metadata.RootElement, "mimeType") == FolderMime)
            throw new NotSupportedException("Google Drive 폴더 복사는 하위 항목별 복사 검증 후 별도로 수행해야 합니다.");
        using var json = await JsonAsync(HttpMethod.Post, Item(itemId, $"/copy?fields={Fields}&supportsAllDrives=true"),
            new { name = ItemName(name), parents = new[] { destinationParentId } }, token).ConfigureAwait(false);
        return new(true, RequiredString(json.RootElement, "id"), null);
    }

    public async Task<CloudItem> MoveAsync(string itemId, string destinationParentId, CancellationToken token)
    {
        using var metadata = await JsonAsync(HttpMethod.Get, Item(itemId, "?fields=parents"), null, token).ConfigureAwait(false);
        JsonElement parents = Required(metadata.RootElement, "parents");
        if (parents.ValueKind != JsonValueKind.Array || parents.GetArrayLength() != 1)
            throw new NotSupportedException("원본 상위 폴더를 명확히 확인하지 못해 이동하지 않습니다.");
        string oldParent = parents[0].GetString() ?? throw new InvalidDataException("원본 상위 폴더 ID가 없습니다.");
        if (oldParent == destinationParentId) throw new ArgumentException("대상 폴더가 기존 폴더와 같습니다.");
        string query = $"?addParents={Segment(destinationParentId)}&removeParents={Segment(oldParent)}&fields={Fields}&supportsAllDrives=true";
        using var json = await JsonAsync(HttpMethod.Patch, Item(itemId, query), new { }, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public async Task MoveToTrashAsync(string itemId, CancellationToken token)
    {
        using var _ = await JsonAsync(HttpMethod.Patch, Item(itemId, "?fields=id,trashed&supportsAllDrives=true"),
            new { trashed = true }, token).ConfigureAwait(false);
    }

    public async Task RestoreFromTrashAsync(string itemId, CancellationToken token)
    {
        using var _ = await JsonAsync(HttpMethod.Patch, Item(itemId, "?fields=id,trashed&supportsAllDrives=true"),
            new { trashed = false }, token).ConfigureAwait(false);
    }
}
