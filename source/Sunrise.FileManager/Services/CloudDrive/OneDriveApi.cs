using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Sunrise.FileManager.Services.CloudDrive;

/// <summary>Microsoft Graph v1.0. Drive ID is fixed per adapter to forbid accidental cross-drive moves.</summary>
public sealed class OneDriveApi : CloudDriveApiBase, ICloudDriveApi, IConditionalCloudRetirement
{
    private readonly string _driveId;
    private string Prefix => "https://graph.microsoft.com/v1.0/drives/" + Segment(_driveId) + "/items/";

    public OneDriveApi(HttpClient client, Func<CancellationToken, Task<string>> tokenSource, string driveId)
        : base(client, tokenSource) { _driveId = string.IsNullOrWhiteSpace(driveId) ? throw new ArgumentException("Drive ID가 필요합니다.") : driveId; }

    protected override bool AllowedApiHost(string host) => host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase);
    private Uri Item(string id, string suffix = "") => Url(Prefix + Segment(id) + suffix);

    private static CloudItem Parse(JsonElement obj)
    {
        string id = RequiredString(obj, "id");
        string name = RequiredString(obj, "name");
        bool folder = obj.TryGetProperty("folder", out var folderValue) && folderValue.ValueKind == JsonValueKind.Object;
        long? size = obj.TryGetProperty("size", out var value) && value.TryGetInt64(out long number) ? number : null;
        string? parent = obj.TryGetProperty("parentReference", out var parentValue) ? OptionalString(parentValue, "id") : null;
        // Microsoft Graph driveItem.eTag is an opaque server version for If-Match.
        // Missing eTag means that verified MOVE must refuse remote retirement.
        string? version = OptionalString(obj, "eTag");
        return new(id, name, folder, size, parent, version);
    }

    public async Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string parentId, CancellationToken token)
    {
        var entries = new List<CloudItem>();
        Uri? next = Item(parentId, "/children?$select=id,name,size,folder,parentReference,eTag&$top=200");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 0; page < 10000 && next is not null; page++)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(next.AbsoluteUri)) throw new InvalidDataException("클라우드 목록 페이지가 반복됩니다.");
            using var json = await JsonAsync(HttpMethod.Get, next, null, token).ConfigureAwait(false);
            foreach (var item in Required(json.RootElement, "value").EnumerateArray()) entries.Add(Parse(item));
            string? nextUrl = OptionalString(json.RootElement, "@odata.nextLink");
            next = string.IsNullOrEmpty(nextUrl) ? null : Url(nextUrl);
        }
        if (next is not null) throw new InvalidDataException("클라우드 목록 페이지 한도를 초과했습니다.");
        return entries;
    }

    public async Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken token)
    {
        var body = new Dictionary<string, object>
        {
            ["name"] = ItemName(name),
            ["folder"] = new { },
            ["@microsoft.graph.conflictBehavior"] = "fail"
        };
        using var json = await JsonAsync(HttpMethod.Post, Item(parentId, "/children"), body, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, CancellationToken token)
    {
        // PUT to a named child creates a zero-byte file. The caller must check for name
        // collisions before invoking this method; Windows-style overwrite is not implied.
        // Full collision-proof creation with If-None-Match belongs to the upload stage.
        throw new NotSupportedException("OneDrive 새 파일 생성은 이름 충돌 보호가 포함된 업로드 모듈에서 지원합니다.");
    }

    public async Task<CloudItem> RenameAsync(string itemId, string name, CancellationToken token)
    {
        using var json = await JsonAsync(HttpMethod.Patch, Item(itemId), new { name = ItemName(name) }, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public async Task<CloudItem> MoveAsync(string itemId, string destinationParentId, CancellationToken token)
    {
        // Microsoft Graph driveItem PATCH can only move within the same drive.
        using var json = await JsonAsync(HttpMethod.Patch, Item(itemId), new
        {
            parentReference = new { id = destinationParentId, driveId = _driveId }
        }, token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    public async Task<CloudCopyResult> CopyAsync(string itemId, string destinationParentId, string name, CancellationToken token)
    {
        // Copy is always asynchronous. Never mark the result completed based solely on 202 Accepted.
        using var response = await SendAsync(HttpMethod.Post, Item(itemId, "/copy"), new
        {
            name = ItemName(name),
            parentReference = new { id = destinationParentId, driveId = _driveId }
        }, token).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            throw new InvalidDataException("OneDrive 복사 작업 접수 응답이 올바르지 않습니다.");
        Uri? monitor = response.Headers.Location;
        if (monitor is null || !monitor.IsAbsoluteUri || monitor.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("OneDrive 비동기 복사 진행 상태 주소가 없습니다.");
        // This is an untrusted opaque monitor URL; future poller must validate its host and redirects.
        return new(false, null, monitor);
    }

    public async Task MoveToTrashIfVersionMatchesAsync(string itemId, string expectedVersionTag, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expectedVersionTag))
            throw new NotSupportedException("클라우드 원본 버전이 없어 안전한 이동을 진행할 수 없습니다.");
        // Graph v1.0 driveItem DELETE supports If-Match and fails with HTTP 412 on a stale tag.
        // A direct DELETE without this precondition would reintroduce a TOCTOU race.
        using var response = await SendAsync(HttpMethod.Delete, Item(itemId), null, token,
            ifMatch: expectedVersionTag).ConfigureAwait(false);
    }

    public async Task MoveToTrashAsync(string itemId, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Delete, Item(itemId), null, token).ConfigureAwait(false);
        // Graph's driveItem DELETE routes to the recycle bin, not permanent deletion.
    }
}
