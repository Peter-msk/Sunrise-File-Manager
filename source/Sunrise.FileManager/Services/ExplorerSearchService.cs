using System.IO;
using System.Runtime.InteropServices;
using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

public enum ExplorerSearchScope { Current, Descendants, Contents, Index }
public sealed record ExplorerSearchResult(IReadOnlyList<FileEntry> Entries, string? Warning);

/// <summary>Read-only search. Junctions are never followed; explicit modes disclose their coverage.</summary>
public static class ExplorerSearchService
{
    public const int MaxResults = 100_000;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    public static async Task<ExplorerSearchResult> SearchAsync(string folder, string query,
        ExplorerSearchScope scope, bool showHidden, CancellationToken token)
    {
        var work = Task.Run(async () =>
        {
            await Slots.WaitAsync(token).ConfigureAwait(false);
            try { return scope == ExplorerSearchScope.Index ? SearchIndex(folder, query, showHidden, token)
                : SearchTree(folder, query, scope == ExplorerSearchScope.Contents, showHidden, token); }
            finally { Slots.Release(); }
        }, token);
        // Read-only native calls may finish later, but callers stop waiting immediately and
        // the bounded worker retains ownership of its COM resources until completion.
        return await work.WaitAsync(token).ConfigureAwait(false);
    }

    private static ExplorerSearchResult SearchTree(string root, string query, bool contents, bool showHidden, CancellationToken token)
    {
        var results = new List<FileEntry>();
        bool structured = ExplorerQuery.IsStructured(query);
        ExplorerQuery? metadataFilter = structured ? ExplorerQuery.Parse(query) : null;
        var folders = new Stack<string>();
        folders.Push(Path.GetFullPath(root));
        int unreadable = 0, unsupported = 0, limited = 0, noText = 0;
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string folder = folders.Pop();
            try
            {
                if (ReparsePointPolicy.IsBlocked(folder)) { unreadable++; continue; }
                foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (ReparsePointPolicy.IsBlocked(item.FullName, item.Attributes)) { unreadable++; continue; }
                        if (!showHidden && item.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                        bool isFolder = item is DirectoryInfo;
                        if (isFolder) folders.Push(item.FullName);
                        bool match = metadataFilter is not null
                            ? metadataFilter.Matches(FileEntry.From(item))
                            : item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                        if (metadataFilter is null && !isFolder && contents && !match)
                        {
                            var content = DocumentContentSearch.Find(item.FullName, query, token);
                            match = content.Status == ContentSearchStatus.Match;
                            if (content.Status == ContentSearchStatus.Unsupported) unsupported++;
                            if (content.Status == ContentSearchStatus.SizeLimit) limited++;
                            if (content.Status == ContentSearchStatus.NoText) noText++;
                        }
                        if (match) results.Add(FileEntry.From(item));
                        if (results.Count >= MaxResults)
                            return new(results, $"검색 결과 {MaxResults:N0}개에서 중단했습니다. 나머지는 미검색입니다. 읽기 불가 {unreadable}개 · 미지원 {unsupported}개 · 제한 {limited}개 · 텍스트 없음 {noText}개");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Xml.XmlException
                        or UglyToad.PdfPig.Core.PdfDocumentFormatException or UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
                    { unreadable++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        string coverage = contents ? "본문 전체 순차 검색: 텍스트·PDF·DOCX·HWPX·일반 HWP 5·XLSX·PPTX. " : "";
        return new(results, coverage + $"읽기/링크 생략 {unreadable:N0}개 · 미지원 {unsupported:N0}개 · 크기 제한/일부 미검색 {limited:N0}개 · 추출 텍스트 없음 {noText:N0}개");
    }

    public static string BuildIndexSql(string folder, string query)
    {
        string scope = new Uri(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri.Replace("'", "''");
        string word = query.Replace("'", "''");
        string phrase = query.Replace("\"", " ").Replace("'", "''");
        return $"SELECT TOP {MaxResults + 1} System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='{scope}' AND (System.FileName LIKE '%{word}%' OR CONTAINS(System.Search.Contents, '\"{phrase}\"'))";
    }

    private static ExplorerSearchResult SearchIndex(string folder, string query, bool showHidden, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 인덱스 검색은 Windows에서 지원합니다.");
        if (ExplorerQuery.IsStructured(query)) throw new IOException("고급 속성 검색은 '하위 폴더 포함 · 이름' 범위로 검색하세요. Windows 인덱스에서는 일반 검색어만 지원합니다.");
        object? connection = null, recordset = null, fields = null, command = null;
        CancellationTokenRegistration cancellation = default;
        Task? cancelRequest = null;
        try
        {
            connection = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection", throwOnError: true)!);
            dynamic db = connection!;
            db.ConnectionTimeout = 5; db.CommandTimeout = 10;
            db.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            token.ThrowIfCancellationRequested();
            command = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Command", throwOnError: true)!);
            dynamic request = command!;
            request.ActiveConnection = db; request.CommandText = BuildIndexSql(folder, query); request.CommandTimeout = 10;
            cancellation = token.Register(() =>
            {
                cancelRequest = Task.Run(() =>
                {
                    try { request.Cancel(); }
                    catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
                });
            });
            token.ThrowIfCancellationRequested();
            recordset = request.Execute();
            dynamic rows = recordset;
            fields = rows.Fields;
            var result = new List<FileEntry>();
            while (!rows.EOF)
            {
                token.ThrowIfCancellationRequested();
                object field = ((dynamic)fields).Item(0);
                string? path;
                try { path = Convert.ToString(((dynamic)field).Value); }
                finally { Marshal.FinalReleaseComObject(field); }
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        FileSystemInfo item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                        if (item.Exists && (showHidden || !item.Attributes.HasFlag(FileAttributes.Hidden))) result.Add(FileEntry.From(item));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (result.Count > MaxResults) return new(result.Take(MaxResults).ToArray(), "인덱스 결과 한도 도달. 검색 범위를 좁혀주세요.");
                rows.MoveNext();
            }
            return new(result, "Windows 인덱스에 등록된 파일만 검색했습니다. 누락 시 하위 폴더 검색을 선택하세요.");
        }
        catch (COMException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (COMException ex) { throw new IOException("Windows Search 서비스를 사용할 수 없습니다. 하위 폴더 검색을 선택하세요. " + ex.Message, ex); }
        finally
        {
            cancellation.Dispose();
            cancelRequest?.GetAwaiter().GetResult();
            if (fields is not null) Marshal.FinalReleaseComObject(fields);
            if (recordset is not null) { try { ((dynamic)recordset).Close(); } catch (COMException) { } Marshal.FinalReleaseComObject(recordset); }
            if (command is not null) Marshal.FinalReleaseComObject(command);
            if (connection is not null) { try { ((dynamic)connection).Close(); } catch (COMException) { } Marshal.FinalReleaseComObject(connection); }
        }
    }
}
