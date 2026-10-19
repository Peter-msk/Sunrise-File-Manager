using System.IO.Compression;
using System.Text;
using Sunrise.FileManager.Models;
using Sunrise.FileManager.Services;

if (args.Contains("--windows-smoke", StringComparer.Ordinal))
{
    await WindowsSmokeTests.RunAsync();
    return;
}

HangulPreviewTests.Run();
await FinalAuditTests.RunAsync();
UiStabilityTests.Run();
FileMemoTests.Run();
await AiPlanTests.RunAsync();
await AiRecoveryTests.RunAsync();
await AiInspectionTests.RunAsync();
await AiNaturalLanguageTests.RunAsync();
await AiAgentPhase34Tests.RunAsync();
await AiOllamaAndExplorerTests.RunAsync();

int passed = 0;
void Check(bool condition, string test)
{
    if (!condition) throw new Exception($"FAILED: {test}");
    passed++;
    Console.WriteLine($"PASS: {test}");
}

string root = Path.Combine(Path.GetTempPath(), "SunriseFMLogic_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var a = new ExplorerTab(root);
    Check(a.CurrentPath == root && !a.CanGoBack, "first tab location");
    string child = Path.Combine(root, "Alpha");
    string other = Path.Combine(root, "Beta");
    a.Navigate(child);
    a.Navigate(other);
    Check(a.CanGoBack && !a.CanGoForward && a.CurrentPath == other, "history navigation");
    Check(a.Back() && a.CurrentPath == child && a.CanGoForward, "back");
    Check(a.Navigate(root) && !a.CanGoForward, "branching history clears forward");
    Check(!a.Navigate(root), "identical path no-op");

    Directory.CreateDirectory(child);
    File.WriteAllText(Path.Combine(root, "z.txt"), "z");
    File.WriteAllText(Path.Combine(root, "a.txt"), "a");
    ListingResult listing = await DirectoryScanner.ReadAsync(root, CancellationToken.None);
    Check(listing.Warning is null && listing.Entries.Count == 3, "folder enumeration");
    Check(listing.Entries[0].IsDirectory && listing.Entries[0].Name == "Alpha", "folders first");
    Check(listing.Entries[1].Name == "a.txt" && listing.Entries[2].Name == "z.txt", "sorted file names");
    Check(listing.Entries[1].SizeBytes == 1L, "size metadata");

    Directory.CreateDirectory(Path.Combine(child, "Nest"));
    FolderChildren children = await FolderTreeScanner.ReadAsync(root, CancellationToken.None);
    Check(children.Paths.Count == 1 && children.Paths[0] == child && !children.Truncated, "folder tree lists directories only");
    string previewTextFile = Path.Combine(root, "preview.md");
    File.WriteAllText(previewTextFile, "미리보기 확인", Encoding.UTF8);
    PreviewContent textPreview = FilePreviewService.Read(previewTextFile, false, CancellationToken.None);
    Check(textPreview.Kind == PreviewKind.Text && textPreview.Body.Contains("미리보기 확인"),
        "P9 UTF-8 markdown preview");
    string imageFile = Path.Combine(root, "image.png");
    File.WriteAllBytes(imageFile, [0x89, 0x50, 0x4E, 0x47]);
    PreviewContent imagePreview = FilePreviewService.Read(imageFile, false, CancellationToken.None);
    Check(imagePreview.Kind == PreviewKind.Image, "P9 image thumbnail request");
    using (var token = new CancellationTokenSource())
    {
        token.Cancel();
        bool previewCanceled = false;
        try { FilePreviewService.Read(previewTextFile, false, token.Token); }
        catch (OperationCanceledException) { previewCanceled = true; }
        Check(previewCanceled, "P9 preview cancellation honored");
    }
    string docx = Path.Combine(root, "sample.docx");
    using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
        writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>DOCX 테스트</w:t></w:r></w:p></w:body></w:document>");
    }
    Check(FilePreviewService.Read(docx, false, CancellationToken.None).Body.Contains("DOCX 테스트"),
        "P9 docx textual preview from bounded zip XML");
    string hwpx = Path.Combine(root, "sample.hwpx");
    using (var zip = ZipFile.Open(hwpx, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
        writer.Write("<hp:sec xmlns:hp='http://www.hancom.co.kr/hwpml/2011/paragraph'><hp:p><hp:t>HWPX 검증</hp:t></hp:p></hp:sec>");
    }
    Check(FilePreviewService.Read(hwpx, false, CancellationToken.None).Body.Contains("HWPX 검증"),
        "P9 hwpx textual preview from bounded zip XML");
    Check(FilePreviewService.Read(previewTextFile, true, CancellationToken.None).Kind == PreviewKind.Information,
        "P9 folder preview does not enumerate contents");
    File.WriteAllBytes(Path.Combine(root, "opaque.bin"), [0, 1, 2]);
    Check(FilePreviewService.Read(Path.Combine(root, "opaque.bin"), false, CancellationToken.None).Kind == PreviewKind.Information,
        "P9 unsupported formats show metadata only");

    FileEntry thumbnailEntry = FileEntry.From(new FileInfo(previewTextFile));
    bool notified = false;
    thumbnailEntry.PropertyChanged += (_, eventArgs) => notified |= eventArgs.PropertyName == nameof(FileEntry.ThumbnailSource);
    thumbnailEntry.ThumbnailSource = new object();
    Check(notified, "P11 thumbnail model emits binding change notification");
    thumbnailEntry.ThumbnailLoading = true;
    Check(thumbnailEntry.ThumbnailLoading, "P11 thumbnail requests can be deduplicated");

    string multiPageText = new string('A', 120) + "\n" + new string('B', 125) + "\n" + new string('C', 160);
    IReadOnlyList<string> pages = PagedPreviewService.Paginate(multiPageText, 140);
    Check(pages.Count >= 3 && string.Concat(pages) == multiPageText,
        "P10 text paging is complete and lossless");
    Check(PagedPreviewService.Paginate("", 140).Count == 1,
        "P10 empty preview has exactly one page");
    bool shortPageLimitRejected = false;
    try { PagedPreviewService.Paginate("abc", 12); }
    catch (ArgumentOutOfRangeException) { shortPageLimitRejected = true; }
    Check(shortPageLimitRejected, "P10 invalid page length rejected");
    PreviewPage pageOne = PagedPreviewService.Read(previewTextFile, 1, CancellationToken.None);
    Check(pageOne.Page == 1 && pageOne.PageCount == 1 && pageOne.Body.Contains("미리보기 확인"),
        "P10 selected markdown displays one page");
    PreviewPage imageOne = PagedPreviewService.Read(imageFile, 500, CancellationToken.None);
    Check(imageOne.Page == 1 && imageOne.PageCount == 1 && imageOne.Kind == PreviewKind.Image,
        "P10 image always displays one page");
    using var canceledPage = new CancellationTokenSource();
    canceledPage.Cancel();
    bool pageCanceled = false;
    try { PagedPreviewService.Read(previewTextFile, 1, canceledPage.Token); }
    catch (OperationCanceledException) { pageCanceled = true; }
    Check(pageCanceled, "P10 canceled page read returns no stale content");

    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    bool cancellationObserved = false;
    try { await DirectoryScanner.ReadAsync(root, canceled.Token); }
    catch (OperationCanceledException) { cancellationObserved = true; }
    Check(cancellationObserved, "cancellation observed");

    bool invalidPathRejected = false;
    try { await DirectoryScanner.ReadAsync(Path.Combine(root, "missing"), CancellationToken.None); }
    catch (IOException) { invalidPathRejected = true; }
    Check(invalidPathRejected, "missing folder reports failure");

    var samples = new List<FileEntry>
    {
        new() { Name="Zulu", FullPath="z-dir", IsDirectory=true, TypeName="폴더", SizeBytes=0, ModifiedAt=DateTime.MinValue },
        new() { Name="alpha.txt", FullPath="alpha", IsDirectory=false, TypeName=".TXT", SizeBytes=120, ModifiedAt=DateTime.MinValue },
        new() { Name="beta.csv", FullPath="beta", IsDirectory=false, TypeName=".CSV", SizeBytes=10, ModifiedAt=DateTime.MinValue }
    };
    var bySize = FileListView.Apply(samples, "", "SizeBytes", true, CancellationToken.None);
    Check(bySize.Select(e => e.Name).SequenceEqual(["Zulu", "alpha.txt", "beta.csv"]),
        "P5 folders-first and descending file size");
    var searched = FileListView.Apply(samples, "ALPHA", "Name", false, CancellationToken.None);
    Check(searched.Count == 1 && searched[0].Name == "alpha.txt", "P5 case-insensitive folder filter");
    using var canceledFilter = new CancellationTokenSource();
    canceledFilter.Cancel();
    bool filterCanceled = false;
    try { FileListView.Apply(samples, "", "Name", false, canceledFilter.Token); }
    catch (OperationCanceledException) { filterCanceled = true; }
    Check(filterCanceled, "P5 stale filter cancellation");

    string transferSource = Path.Combine(root, "TransferSource");
    string transferTarget = Path.Combine(root, "TransferTarget");
    Directory.CreateDirectory(transferSource);
    Directory.CreateDirectory(transferTarget);
    string original = Path.Combine(transferSource, "notice.txt");
    File.WriteAllText(original, "safe-copy");
    TransferResult copied = await TransferService.RunAsync([original], transferTarget,
        TransferKind.Copy, null, CancellationToken.None);
    Check(copied.Completed == 1 && copied.Failed == 0 && File.Exists(original) &&
          File.ReadAllText(Path.Combine(transferTarget, "notice.txt")) == "safe-copy", "file copy preserves original");
    Check(!Directory.EnumerateFileSystemEntries(transferTarget, ".sunrise-staging-*").Any(),
        "completed file copy removes staging artifacts");
    TransferResult collision = await TransferService.RunAsync([original], transferTarget,
        TransferKind.Copy, null, CancellationToken.None);
    Check(collision.Skipped == 1 && collision.Completed == 0 &&
          File.ReadAllText(Path.Combine(transferTarget, "notice.txt")) == "safe-copy", "collision skips without overwrite");

    string directorySource = Path.Combine(transferSource, "Documents");
    Directory.CreateDirectory(Path.Combine(directorySource, "Sub"));
    File.WriteAllText(Path.Combine(directorySource, "Sub", "info.txt"), "nested");
    TransferResult folderCopied = await TransferService.RunAsync([directorySource], transferTarget,
        TransferKind.Copy, null, CancellationToken.None);
    Check(folderCopied.Completed == 1 && File.ReadAllText(Path.Combine(transferTarget,
          "Documents", "Sub", "info.txt")) == "nested", "directory copy includes nested content");
    bool descendantBlocked = false;
    try
    {
        await TransferService.RunAsync([directorySource], Path.Combine(directorySource, "Sub"),
            TransferKind.Copy, null, CancellationToken.None);
    }
    catch (IOException) { descendantBlocked = true; }
    Check(descendantBlocked, "copy into source descendant rejected");

    string movable = Path.Combine(transferSource, "rename-me.txt");
    File.WriteAllText(movable, "atomic-move");
    TransferResult moved = await TransferService.RunAsync([movable], transferTarget,
        TransferKind.Move, null, CancellationToken.None);
    Check(moved.Completed == 1 && !File.Exists(movable) &&
          File.ReadAllText(Path.Combine(transferTarget, "rename-me.txt")) == "atomic-move",
        "move renames without losing original contents");
    bool nestedSelectionBlocked = false;
    try
    {
        await TransferService.RunAsync([directorySource, Path.Combine(directorySource, "Sub", "info.txt")],
            transferTarget, TransferKind.Move, null, CancellationToken.None);
    }
    catch (IOException) { nestedSelectionBlocked = true; }
    Check(nestedSelectionBlocked, "parent and child simultaneous selection rejected");

    TransferResult copyRenamed = await TransferService.RunAsync([original], transferTarget,
        TransferKind.Copy, null, CancellationToken.None, CollisionPolicy.Rename);
    Check(copyRenamed.Completed == 1 && File.ReadAllText(Path.Combine(transferTarget, "notice (1).txt")) == "safe-copy" &&
          File.ReadAllText(Path.Combine(transferTarget, "notice.txt")) == "safe-copy", "collision auto-renames without overwrite");
    TransferResult copyRenamedAgain = await TransferService.RunAsync([original], transferTarget,
        TransferKind.Copy, null, CancellationToken.None, CollisionPolicy.Rename);
    Check(copyRenamedAgain.Completed == 1 && File.Exists(Path.Combine(transferTarget, "notice (2).txt")),
        "collision rename increments unique counter");

    Check(FileActionsService.ValidateName("검토 자료 1") == "검토 자료 1", "Korean filename validation");
    Check(FileActionsService.ValidateName("draft.v2.txt") == "draft.v2.txt", "multiple dots permitted");
    foreach (string invalid in new[] { "", ".", "..", "NUL.txt", "COM1", "test ", "test.", "dir/name" })
    {
        bool rejected = false;
        try { FileActionsService.ValidateName(invalid); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, $"invalid filename rejected: {invalid}");
    }
    string newFolder = FileActionsService.CreateFolder(transferSource, "new-folder");
    Check(Directory.Exists(newFolder), "create folder in active directory");
    bool createCollisionRejected = false;
    try { FileActionsService.CreateFolder(transferSource, "new-folder"); }
    catch (IOException) { createCollisionRejected = true; }
    Check(createCollisionRejected, "create folder does not silently use existing directory");
    string renamedFolder = FileActionsService.Rename(newFolder, "renamed-folder");
    Check(!Directory.Exists(newFolder) && Directory.Exists(renamedFolder), "folder rename");
    string renamedFile = FileActionsService.Rename(original, "notice-new.txt");
    Check(File.Exists(renamedFile) && !File.Exists(original), "file rename");
    File.WriteAllText(Path.Combine(transferSource, "existing.txt"), "original-target");
    bool overwriteRejected = false;
    try { FileActionsService.Rename(renamedFile, "existing.txt"); }
    catch (IOException) { overwriteRejected = true; }
    Check(overwriteRejected && File.ReadAllText(renamedFile) == "safe-copy" &&
          File.ReadAllText(Path.Combine(transferSource, "existing.txt")) == "original-target", "rename collision rejects overwrite");

    bool missingTargetRejected = false;
    try
    {
        await TransferService.RunAsync([renamedFile], Path.Combine(root, "no-such-destination"),
            TransferKind.Copy, null, CancellationToken.None);
    }
    catch (DirectoryNotFoundException) { missingTargetRejected = true; }
    Check(missingTargetRejected && File.Exists(renamedFile),
        "P5 missing target fails before source mutation");

    using var canceledCopy = new CancellationTokenSource();
    canceledCopy.Cancel();
    bool transferCanceled = false;
    try
    {
        await TransferService.RunAsync([renamedFile], transferTarget, TransferKind.Copy,
            null, canceledCopy.Token);
    }
    catch (OperationCanceledException) { transferCanceled = true; }
    Check(transferCanceled, "cancel before file transfer prevents writes");

    Console.WriteLine($"LOGIC TESTS PASS: {passed}");
}
finally
{
    Directory.Delete(root, recursive: true);
}
