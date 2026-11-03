using System.IO;
using System.Runtime.InteropServices;
using Sunrise.FileManager.Models;
namespace Sunrise.FileManager.Services;

// Shell owns the item identities and restoration. No physical $Recycle.Bin file is exposed to file operations.
public sealed record RecycleRestoreResult(int Requested, int Missing, IReadOnlyList<string> Errors, int Completed = 0, bool Aborted = false);
public sealed record RecycleEmptyResult(int Remaining, bool Aborted);
public static class RecycleBinService
{
    private static string Key(string identity) => "recycle-item:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity));
    private static string DecodeKey(string key)
    {
        const string prefix = "recycle-item:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("휴지통 항목의 식별자가 아닙니다.", nameof(key));
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(key[prefix.Length..]));
    }
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
    public static Task<RecycleRestoreResult> RestoreAsync(IReadOnlyList<string> keys, CancellationToken token)
        => ExecuteVerbAsync(keys, token, "undelete");
    // A single IFileOperation transaction: one application confirmation, no per-item Shell verbs.
    public static Task<RecycleRestoreResult> PermanentlyDeleteAsync(IReadOnlyList<string> keys, CancellationToken token)
        => Sta(() =>
        {
            var selected = keys.Distinct(StringComparer.Ordinal).ToArray();
            if (selected.Length == 0) return new RecycleRestoreResult(0, 0, [], 0);
            if (selected.Length > 20000) throw new IOException("한 번에 20,000개 이하를 선택하세요.");
            var identities = selected.Select(DecodeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();
            var before = ReadLiveIdentities(token);
            var live = identities.Where(before.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int missing = identities.Count - live.Count;
            if (live.Count == 0) return new RecycleRestoreResult(0, missing, [], 0);
            var operation = RecycleBinNativeDelete.Delete(live, token);
            // Queue success != deletion success. Re-read the namespace after the one batch.
            var after = ReadLiveIdentities(CancellationToken.None);
            int completed = live.Count(identity => !after.Contains(identity));
            var errors = operation.Errors.ToList();
            if (operation.Aborted) errors.Add("Windows에서 일부 삭제 작업이 취소되었습니다.");
            if (completed < live.Count && errors.Count == 0)
                errors.Add("일부 항목이 휴지통에 남아 있습니다. 권한 및 사용 중인 파일을 확인하세요.");
            return new RecycleRestoreResult(operation.Queued, missing, errors, completed, operation.Aborted);
        }, token);

    private static HashSet<string> ReadLiveIdentities(CancellationToken token)
    {
        object? shell = null, folder = null, items = null;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace(10) ?? throw new IOException("휴지통을 열 수 없습니다.");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            for (int i = 0; i < count; ++i)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try { item = ((dynamic)items).Item(i); found.Add((string)((dynamic)item!).Path); }
                catch (COMException ex) { throw new IOException("휴지통을 다시 조회하지 못했습니다. 삭제 결과를 확정할 수 없습니다.", ex); }
                finally { Release(item); }
            }
            return found;
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    // Windows owns whole-bin deletion, including metadata and every volume. No direct $Recycle.Bin IO.
    public static Task<RecycleEmptyResult> EmptyAsync(CancellationToken token) => Sta(() =>
    {
        token.ThrowIfCancellationRequested();
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000001); // SHERB_NOCONFIRMATION
        bool aborted = hr == unchecked((int)0x800704C7);
        if (hr < 0 && !aborted) Marshal.ThrowExceptionForHR(hr);
        return new RecycleEmptyResult(ReadLiveIdentities(CancellationToken.None).Count, aborted);
    }, token);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
    private static Task<RecycleRestoreResult> ExecuteVerbAsync(IReadOnlyList<string> keys, CancellationToken token, string verb) => Sta(() =>
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
            // Snapshot COM identities before invoking verbs; each action changes the live collection.
            int requested = 0;
            foreach (object item in matched)
            {
                token.ThrowIfCancellationRequested();
                try { ((dynamic)item).InvokeVerb(verb); requested++; }
                catch (COMException ex) { if (errors.Count < 20) errors.Add("Windows Shell 작업 실패: " + ex.Message); }
            }
            return new RecycleRestoreResult(requested, requestedKeys.Count - matched.Count, errors, requested);
        }
        finally
        {
            foreach (object item in matched) Release(item);
            Release(items); Release(folder); Release(shell);
        }
    }, token);
}
