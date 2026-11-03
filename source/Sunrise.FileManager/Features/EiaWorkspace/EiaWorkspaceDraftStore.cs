using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sunrise.FileManager.Features.EiaWorkspace;

/// <summary>
/// DEV6: opt-in, local-only append-only store for UNAPPROVED template drafts.
/// The caller must explicitly invoke SaveDraft. Never writes to project workspaces,
/// approved profiles, NAS, cloud, session, user memos or the updater.
/// This store is NOT a company approval system, signature service or a backup.
/// </summary>
public static class EiaWorkspaceDraftStore
{
    public const string StoreFolderName = "EiaWorkspaceDrafts";
    private const string DraftSection = "drafts";
    private const int MaxSavedJsonBytes = 512 * 1024;
    private const int MaxRevision = 999999;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string DefaultRoot
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new InvalidOperationException("사용자 로컬 앱 데이터 경로를 확인할 수 없습니다.");
            return Sunrise.FileManager.Services.SunriseGroupPaths.Local(StoreFolderName);
        }
    }

    /// <summary>
    /// Explicitly persist one new draft revision. The dedicated store root must already
    /// exist and be under the current user's LocalApplicationData; never create it on read.
    /// Each revision is written to a private random temporary name and committed with
    /// File.Move (same directory, no overwrite). Interrupted writes do not touch earlier revisions.
    /// </summary>
    public static string SaveDraft(string storeRoot, EiaWorkspaceProfileRevision revision)
    {
        EiaWorkspaceProfilePlanner.Verify(revision);
        if (revision.RevisionNumber > MaxRevision)
            throw new InvalidDataException("초안 개정번호 상한을 초과했습니다.");
        string root = VerifyRoot(storeRoot);
        string relative = RevisionPath(revision.CompanyCode, revision.RequirementCode,
            revision.ProfileId, revision.RevisionNumber);
        string destination = ConfinedFilePath(root, relative);
        string profileDirectory = Path.GetDirectoryName(destination)!;
        EnsureDirectories(root, profileDirectory);
        // Do not implicitly fill gaps or replace a file that already exists.
        if (revision.RevisionNumber == 1)
        {
            if (GetExistingRevisionNumbers(profileDirectory).Count != 0)
                throw new InvalidDataException("이미 개정 이력이 있습니다. 최초 개정을 다시 작성할 수 없습니다.");
        }
        else
        {
            var numbers = GetExistingRevisionNumbers(profileDirectory);
            if (numbers.Count != revision.RevisionNumber - 1 || numbers[^1] != revision.RevisionNumber - 1)
                throw new InvalidDataException("개정 이력에 누락·중복·선행 개정이 있습니다.");
            var previous = ReadRevisionFile(root, RevisionPath(revision.CompanyCode,
                revision.RequirementCode, revision.ProfileId, revision.RevisionNumber - 1),
                revision.CompanyCode, revision.RequirementCode, revision.ProfileId,
                revision.RevisionNumber - 1);
            EiaWorkspaceProfilePlanner.VerifySuccessor(previous, revision);
        }
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidDataException("기존 개정본 파일은 덮어쓸 수 없습니다.");
        byte[] payload = StrictUtf8.GetBytes(EiaWorkspaceProfilePlanner.ExportDraftJson(revision));
        if (payload.Length > MaxSavedJsonBytes)
            throw new InvalidDataException("초안 저장 크기 제한을 초과했습니다.");
        string temporary = Path.Combine(profileDirectory, ".pending-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool createdTemporary = false;
        try
        {
            EnsureNoReparse(root, profileDirectory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                createdTemporary = true;
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            EnsureNoReparse(root, profileDirectory);
            try { File.Move(temporary, destination); }
            catch (IOException ex) { throw new InvalidDataException("개정본 확정에 실패했습니다. 경로 또는 동시 저장을 확인하세요.", ex); }
            createdTemporary = false;
            var checkedRevision = ReadRevisionFile(root, relative, revision.CompanyCode,
                revision.RequirementCode, revision.ProfileId, revision.RevisionNumber);
            if (checkedRevision.FingerprintSha256 != revision.FingerprintSha256)
                throw new InvalidDataException("저장 후 재검증에서 개정본 내용이 일치하지 않습니다.");
            return destination;
        }
        finally
        {
            // Delete only this invocation's unique staging file, never an existing revision.
            if (createdTemporary && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>Read a single existing revision; never repairs, migrates or writes.</summary>
    public static EiaWorkspaceProfileRevision LoadDraft(string storeRoot, string company,
        string requirement, string profileId, int revisionNumber)
    {
        string root = VerifyRoot(storeRoot);
        string relative = RevisionPath(company, requirement, profileId, revisionNumber);
        return ReadRevisionFile(root, relative, company, requirement, profileId, revisionNumber);
    }

    /// <summary>Read the latest contiguous revision, rejecting any broken history.</summary>
    public static EiaWorkspaceProfileRevision LoadLatest(string storeRoot, string company,
        string requirement, string profileId)
    {
        string root = VerifyRoot(storeRoot);
        string relative = RevisionPath(company, requirement, profileId, 1);
        string folder = Path.GetDirectoryName(ConfinedFilePath(root, relative))!;
        EnsureNoReparse(root, folder);
        var numbers = GetExistingRevisionNumbers(folder);
        if (numbers.Count == 0)
            throw new FileNotFoundException("저장된 프로파일 초안이 없습니다.");
        EiaWorkspaceProfileRevision? previous = null;
        foreach (int number in numbers)
        {
            if (number != (previous?.RevisionNumber ?? 0) + 1)
                throw new InvalidDataException("프로파일 개정 이력이 연속되지 않습니다.");
            var current = ReadRevisionFile(root, RevisionPath(company, requirement,
                profileId, number), company, requirement, profileId, number);
            if (previous is not null)
                EiaWorkspaceProfilePlanner.VerifySuccessor(previous, current);
            previous = current;
        }
        return previous!;
    }

    private static EiaWorkspaceProfileRevision ReadRevisionFile(string root, string relative,
        string company, string requirement, string profileId, int expectedRevision)
    {
        string file = ConfinedFilePath(root, relative);
        EnsureNoReparse(root, Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
            throw new InvalidDataException("지정한 초안 개정본이 없습니다.");
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("링크된 초안 파일은 읽을 수 없습니다.");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length > MaxSavedJsonBytes)
            throw new InvalidDataException("저장된 초안 파일 크기가 올바르지 않습니다.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        string json;
        try { json = StrictUtf8.GetString(memory.ToArray()); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("초안 파일의 UTF-8 인코딩이 손상되었습니다.", ex); }
        var revision = EiaWorkspaceProfilePlanner.ImportDraftJson(json);
        if (revision.CompanyCode != company || revision.RequirementCode != requirement
            || revision.ProfileId != profileId || revision.RevisionNumber != expectedRevision)
            throw new InvalidDataException("개정본 내부 식별자가 저장 위치와 다릅니다.");
        return revision;
    }

    private static string RevisionPath(string company, string requirement,
        string profileId, int number)
    {
        CheckSegment(company); CheckSegment(requirement); CheckSegment(profileId);
        if (number < 1 || number > MaxRevision)
            throw new ArgumentOutOfRangeException(nameof(number));
        return Path.Combine(DraftSection, company, requirement, profileId, $"revision-{number:D6}.json");
    }

    private static void CheckSegment(string value)
    {
        if (!string.Equals(EiaWorkspacePreviewPlanner.ValidateSegment(value,
                "프로파일 저장 식별자", 48), value, StringComparison.Ordinal))
            throw new ArgumentException("프로파일 저장 식별자가 정규화되지 않았습니다.");
    }

    private static string VerifyRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("로컬 초안 저장소의 절대경로가 필요합니다.");
        string root = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                StoreFolderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("전용 초안 저장소 디렉터리만 사용할 수 있습니다.");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("로컬 앱 데이터 경로를 확인할 수 없습니다.");
        string allowedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(local)) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(@"\\", StringComparison.Ordinal)
            || !Directory.Exists(root))
            throw new InvalidDataException("사용자 로컬 앱 데이터 내부의 이미 생성된 전용 저장소만 허용합니다.");
        EnsureNoReparse(allowedPrefix, root);
        return root;
    }

    private static string ConfinedFilePath(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("초안 저장소 경계 밖의 경로입니다.");
        return path;
    }

    private static void EnsureDirectories(string root, string target)
    {
        string parent = root;
        string suffix = Path.GetRelativePath(root, target);
        if (suffix == "." || suffix.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(suffix))
            throw new InvalidDataException("초안 디렉터리 경로가 올바르지 않습니다.");
        foreach (string name in suffix.Split(Path.DirectorySeparatorChar))
        {
            parent = Path.Combine(parent, name);
            if (File.Exists(parent))
                throw new InvalidDataException("파일이 초안 디렉터리 생성을 방해합니다.");
            EnsureNoReparse(root, Path.GetDirectoryName(parent)!);
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("정션·심볼릭 링크 경로에는 초안을 저장하지 않습니다.");
            Directory.CreateDirectory(parent);
            EnsureNoReparse(root, parent);
        }
    }

    private static void EnsureNoReparse(string root, string leaf)
    {
        string start = Path.GetFullPath(root);
        string current = Path.GetFullPath(leaf);
        if (!current.Equals(start, StringComparison.OrdinalIgnoreCase)
            && !current.StartsWith(Path.TrimEndingDirectorySeparator(start) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("초안 저장 경로를 벗어났습니다.");
        // Start at the local-appdata ancestor so a redirected root itself is refused.
        while (true)
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("정션·심볼릭 링크 경로에 초안을 저장할 수 없습니다.");
            if (current.Equals(Path.GetPathRoot(current), StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static List<int> GetExistingRevisionNumbers(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var numbers = new List<int>();
        foreach (string name in Directory.EnumerateFileSystemEntries(directory))
        {
            string filename = Path.GetFileName(name);
            if (filename.StartsWith(".pending-", StringComparison.Ordinal))
                throw new InvalidDataException("이전 임시 저장 흔적이 있습니다. 검토 전 자동 복구하지 않습니다.");
            if (filename.Length != "revision-000001.json".Length
                || !filename.StartsWith("revision-", StringComparison.Ordinal)
                || !filename.EndsWith(".json", StringComparison.Ordinal)
                || !int.TryParse(filename.AsSpan(9, 6), out int number)
                || number < 1 || number > MaxRevision)
                throw new InvalidDataException("프로파일 저장소에 알 수 없는 항목이 존재합니다.");
            numbers.Add(number);
        }
        numbers.Sort();
        if (numbers.Count != numbers.Distinct().Count())
            throw new InvalidDataException("프로파일 개정번호가 중복되었습니다.");
        return numbers;
    }
}
