using System.IO;

namespace Sunrise.FileManager.Services;

public sealed record UndoEntry(
    string Description,
    IReadOnlyList<(string CurrentPath, string? OriginalPath, bool CreatedNew)> Operations,
    bool SafeFileRename = false);

public sealed class ExplorerUndoService
{
    private readonly Stack<UndoEntry> _history = new();
    private readonly Stack<UndoEntry> _redoHistory = new();
    private const int MaxHistory = 30;
    private readonly Action<string> _recycle;
    public ExplorerUndoService() : this(FileActionsService.Recycle) { }
    internal ExplorerUndoService(Action<string> recycle) => _recycle = recycle;

    public bool CanUndo => _history.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;
    public string? NextUndoDescription => _history.TryPeek(out var top) ? top.Description : null;
    public string? NextRedoDescription => _redoHistory.TryPeek(out var top) ? top.Description : null;

    public void RecordRename(string oldPath, string newPath)
    {
        _redoHistory.Clear();
        Push(_history, new UndoEntry($"이름 변경 취소 ({Path.GetFileName(newPath)} → {Path.GetFileName(oldPath)})",
            [(newPath, oldPath, false)]));
    }

    public void RecordBatchRename(IReadOnlyList<(string OldPath, string NewPath)> pairs, bool safeFileRename = false)
    {
        if (pairs.Count == 0) return;
        _redoHistory.Clear();
        Push(_history, new UndoEntry($"일괄 이름 변경 취소 ({pairs.Count}개 항목)",
            pairs.Select(p => (p.NewPath, (string?)p.OldPath, false)).ToArray(), safeFileRename));
    }

    public void RecordMovedItems(IReadOnlyList<(string OldPath, string NewPath)> pairs)
    {
        if (pairs.Count == 0) return;
        _redoHistory.Clear();
        Push(_history, new UndoEntry($"파일 이동 취소 ({pairs.Count}개 항목 원래 폴더로 복원)",
            pairs.Select(p => (p.NewPath, (string?)p.OldPath, false)).ToArray()));
    }

    public void RecordCreated(string createdPath, string label)
    {
        _redoHistory.Clear();
        Push(_history, new UndoEntry($"{label} 생성 취소 ({Path.GetFileName(createdPath)})",
            [(createdPath, null, true)]));
    }

    public void RecordDuplicates(IReadOnlyList<string> createdPaths)
    {
        if (createdPaths.Count == 0) return;
        _redoHistory.Clear();
        Push(_history, new UndoEntry($"사본 생성 취소 ({createdPaths.Count}개 항목)",
            createdPaths.Select(p => (p, (string?)null, true)).ToArray()));
    }

    private static void Push(Stack<UndoEntry> stack, UndoEntry entry)
    {
        if (stack.Count >= MaxHistory)
        {
            var items = stack.ToArray().Take(MaxHistory - 1).Reverse().ToArray();
            stack.Clear();
            foreach (var item in items) stack.Push(item);
        }
        stack.Push(entry);
    }

    public (bool Success, string Message) UndoLast()
    {
        if (!_history.TryPop(out UndoEntry? entry))
            return (false, "실행 취소할 최근 작업이 없습니다.");

        if (entry.SafeFileRename) return ReplaySafeRename(entry, _history, _redoHistory);

        int doneCount = 0;
        var errors = new List<string>();
        var redoOps = new List<(string CurrentPath, string? OriginalPath, bool CreatedNew)>();
        var retryOps = new List<(string CurrentPath, string? OriginalPath, bool CreatedNew)>();

        foreach (var (currentPath, originalPath, createdNew) in entry.Operations.Reverse())
        {
            int before = doneCount, errorsBefore = errors.Count;
            try
            {
                if (createdNew)
                {
                    if (ReparsePointPolicy.IsBlocked(currentPath))
                        throw new IOException("연결·접합점은 실행 취소에서 삭제하지 않습니다.");
                    if (Directory.Exists(currentPath))
                    {
                        if (!Directory.EnumerateFileSystemEntries(currentPath).Any())
                        {
                            Directory.Delete(currentPath, recursive: false);
                            redoOps.Add((currentPath, null, true));
                            doneCount++;
                        }
                        else
                        {
                            errors.Add($"{Path.GetFileName(currentPath)}: 폴더 내부에 항목이 있어 자동 삭제하지 않았습니다.");
                        }
                    }
                    else if (File.Exists(currentPath))
                    {
                        _recycle(currentPath); // Failure must preserve the file; never fall back to permanent deletion.
                        doneCount++;
                    }
                }
                else if (!string.IsNullOrEmpty(originalPath))
                {
                    if (File.Exists(currentPath) || Directory.Exists(currentPath))
                    {
                        FileActionsService.MoveEntry(currentPath, originalPath);
                        redoOps.Add((originalPath, currentPath, false));
                        doneCount++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or OperationCanceledException or System.Security.Cryptography.CryptographicException)
            {
                errors.Add($"{Path.GetFileName(currentPath)}: {ex.Message}");
            }
            finally
            {
                if (doneCount == before)
                {
                    retryOps.Add((currentPath, originalPath, createdNew));
                    if (errors.Count == errorsBefore) errors.Add($"{Path.GetFileName(currentPath)}: 작업 대상이 없어 복원하지 못했습니다.");
                }
            }
        }

        if (retryOps.Count > 0) { retryOps.Reverse(); Push(_history, new UndoEntry(entry.Description, retryOps)); }

        if (redoOps.Count > 0)
        {
            redoOps.Reverse();
            Push(_redoHistory, new UndoEntry($"다시 실행 ({entry.Description.Replace(" 취소", "")})", redoOps));
        }

        if (errors.Count > 0)
            return (doneCount > 0, $"{entry.Description} · 완료 {doneCount}건, 제한 {errors.Count}건 ({errors[0]})");
        return (true, $"실행 취소 완료: {entry.Description} ({doneCount}건)");
    }

    public (bool Success, string Message) RedoLast()
    {
        if (!_redoHistory.TryPop(out UndoEntry? entry))
            return (false, "다시 실행할 작업이 없습니다.");

        if (entry.SafeFileRename) return ReplaySafeRename(entry, _redoHistory, _history);

        int doneCount = 0;
        var errors = new List<string>();
        var undoOps = new List<(string CurrentPath, string? OriginalPath, bool CreatedNew)>();
        var retryOps = new List<(string CurrentPath, string? OriginalPath, bool CreatedNew)>();

        foreach (var (currentPath, targetPath, recreateDir) in entry.Operations)
        {
            int before = doneCount, errorsBefore = errors.Count;
            try
            {
                if (recreateDir)
                {
                    if (!Directory.Exists(currentPath) && !File.Exists(currentPath))
                    {
                        Directory.CreateDirectory(currentPath);
                        undoOps.Add((currentPath, null, true));
                        doneCount++;
                    }
                }
                else if (!string.IsNullOrEmpty(targetPath))
                {
                    if (File.Exists(currentPath) || Directory.Exists(currentPath))
                    {
                        FileActionsService.MoveEntry(currentPath, targetPath);
                        undoOps.Add((targetPath, currentPath, false));
                        doneCount++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or OperationCanceledException or System.Security.Cryptography.CryptographicException)
            {
                errors.Add($"{Path.GetFileName(currentPath)}: {ex.Message}");
            }
            finally
            {
                if (doneCount == before)
                {
                    retryOps.Add((currentPath, targetPath, recreateDir));
                    if (errors.Count == errorsBefore) errors.Add($"{Path.GetFileName(currentPath)}: 대상이 없거나 같은 이름이 있어 다시 실행하지 못했습니다.");
                }
            }
        }

        if (retryOps.Count > 0) Push(_redoHistory, new UndoEntry(entry.Description, retryOps));

        if (undoOps.Count > 0)
        {
            Push(_history, new UndoEntry(entry.Description.Replace("다시 실행 (", "").TrimEnd(')') + " 취소", undoOps));
        }

        if (errors.Count > 0)
            return (doneCount > 0, $"{entry.Description} · 완료 {doneCount}건, 제한 {errors.Count}건 ({errors[0]})");
        return (true, $"다시 실행 완료: {entry.Description} ({doneCount}건)");
    }
    private static (bool Success, string Message) ReplaySafeRename(UndoEntry entry, Stack<UndoEntry> source, Stack<UndoEntry> destination)
    {
        try
        {
            var pairs = FileActionsService.BatchRenameFilesSafely(entry.Operations.Select(op =>
                (op.CurrentPath, Path.GetFileName(op.OriginalPath!))).ToArray());
            Push(destination, new UndoEntry(entry.Description, pairs.Select(p => (p.NewPath, (string?)p.OldPath, false)).ToArray(), true));
            return (true, $"일괄 이름 변경 복원 완료 ({pairs.Count}건)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Push(source, entry);
            return (false, ex.Message);
        }
    }
}
