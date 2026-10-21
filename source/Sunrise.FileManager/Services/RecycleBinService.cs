using System.IO;
using System.Runtime.InteropServices;
using Sunrise.FileManager.Models;
namespace Sunrise.FileManager.Services;

// Shell owns the item identities and restoration. No physical $Recycle.Bin file is exposed to file operations.
public sealed record RecycleRestoreResult(int Requested, int Missing, IReadOnlyList<string> Errors);
public static class RecycleBinService
{
    private static string Key(string identity) => "recycle-item:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity));
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static Task<T> Sta<T>(Func<T> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Sunrise Recycle Shell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    public static Task<ListingResult> ReadAsync(CancellationToken token) => Sta(() =>
    {
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new IOException("Windows Shell을 사용할 수 없습니다."));
            folder = ((dynamic)shell!).NameSpace(10) ?? throw new IOException("휴지통을 열 수 없습니다.");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            var entries = new List<FileEntry>();
            int skipped = 0;
            for (int i = 0; i < Math.Min(count, 20000); i++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    dynamic entry = item!;
                    string identity = (string)entry.Path;
                    string original = Convert.ToString(entry.ExtendedProperty("System.Recycle.DeletedFrom")) ?? "";
                    object? deleted = entry.ExtendedProperty("System.Recycle.DateDeleted");
                    object? size = entry.ExtendedProperty("System.Size");
                    entries.Add(new FileEntry
                    {
                        Name = (string)entry.Name, FullPath = Key(identity), IsDirectory = (bool)entry.IsFolder,
                        TypeName = Convert.ToString(entry.Type) ?? "파일", SizeBytes = size is null ? 0 : Convert.ToInt64(size),
                        ModifiedAt = Convert.ToDateTime(entry.ModifyDate), DeletedAt = deleted is DateTime dt ? dt : DateTime.MinValue,
                        OriginalLocation = original, IsVirtual = true, IsReadOnly = true
                    });
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or OverflowException) { skipped++; }
                finally { Release(item); }
            }
            return new ListingResult(entries, count > 20000 ? "항목이 20,000개를 초과합니다. 검색 전 일부 목록만 표시됩니다." : skipped > 0 ? $"읽을 수 없거나 조회 중 이동된 {skipped}개 항목은 표시하지 못했습니다." : null, DriveSummary: "휴지통 · 원래 위치로 복원 가능");
        }
        finally { Release(items); Release(folder); Release(shell); }
    }, token);
    public static Task<RecycleRestoreResult> RestoreAsync(IReadOnlyList<string> keys, CancellationToken token) => Sta(() =>
    {
        var requestedKeys = keys.ToHashSet(StringComparer.Ordinal);
        if (requestedKeys.Count > 20000) throw new IOException("한 번에 20,000개 이하를 선택하세요.");
        object? shell = null, folder = null, items = null;
        var matched = new List<object>();
        var errors = new List<string>();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace(10);
            if (folder is null) throw new IOException("휴지통을 열 수 없습니다.");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            for (int i = 0; i < count && matched.Count < requestedKeys.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    if (!requestedKeys.Contains(Key((string)((dynamic)item!).Path))) continue;
                    matched.Add(item!); item = null;
                }
                catch (COMException ex) { if (errors.Count < 20) errors.Add("목록 변경 또는 접근 제한: " + ex.Message); }
                finally { Release(item); }
            }
            // Snapshot all matching Shell items before invoking verbs; restoration can mutate the live collection.
            int requested = 0;
            foreach (object item in matched)
            {
                token.ThrowIfCancellationRequested();
                try { ((dynamic)item).InvokeVerb("undelete"); requested++; }
                catch (COMException ex) { if (errors.Count < 20) errors.Add("복원 요청 실패: " + ex.Message); }
            }
            return new RecycleRestoreResult(requested, requestedKeys.Count - matched.Count, errors);
        }
        finally
        {
            foreach (object item in matched) Release(item);
            Release(items); Release(folder); Release(shell);
        }
    }, token);
}
