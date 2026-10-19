using Sunrise.FileManager.Services;
using System.IO;
using System.Text;

internal static class FileMemoTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FileMemo FAILED: " + name);
            ++passed;
            Console.WriteLine("PASS Memo: " + name);
        }
        string root = Path.Combine(Path.GetTempPath(), "SunriseMemo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "회의 자료.hwpx");
            File.WriteAllText(file, "실제 원본 내용", Encoding.UTF8);
            var date = new DateTime(2026, 10, 18, 10, 20, 30);
            string name = FileMemoStore.MemoPath(file, date, "test-PC");
            Check(name.EndsWith("회의 자료.hwpx_memo_26.10.18_test-PC.sunmemo", StringComparison.Ordinal), "date and Windows user in proprietary filename");
            Check(!Directory.Exists(Path.Combine(root, "#memo")), "no write on selecting a file");
            var saved = FileMemoStore.Save(file, "첫 번째 메모", date, "test-PC");
            Check(File.Exists(saved.Path) && Directory.Exists(Path.Combine(root, "#memo")), "store in source sibling folder");
            Check(FileMemoStore.Read(saved.Path, file).Text == "첫 번째 메모", "round trip UTF-8 Korean");
            Check(File.ReadAllText(file, Encoding.UTF8) == "실제 원본 내용", "never edit original");
            var modified = FileMemoStore.Save(file, "변경한 내용", date.AddHours(1), "test-PC");
            Check(saved.Path == modified.Path && FileMemoStore.ListForFile(file).Count == 1 &&
                  FileMemoStore.Read(saved.Path, file).Text == "변경한 내용", "atomic same-day update");
            FileMemoStore.Save(file, "다음 날", date.AddDays(1), "test-PC");
            Check(FileMemoStore.ListForFile(file).Count == 2, "previous-day history retained");
            FileMemoStore.Save(file, "다른 Windows 사용자", date, "other-user");
            Check(FileMemoStore.ListForFile(file).Count == 3, "username separates daily memo");
            string other = Path.Combine(root, "다른 파일.hwpx"); File.WriteAllText(other, "other");
            Check(FileMemoStore.ListForFile(other).Count == 0, "different target does not inherit memo");
            bool mismatch = false;
            try { FileMemoStore.Read(saved.Path, other); }
            catch (InvalidDataException) { mismatch = true; }
            Check(mismatch, "memo cannot be loaded as another file");
            bool maxLength = false;
            try { FileMemoStore.Save(file, new string('x', FileMemoStore.MaxTextLength + 1), date, "test-PC"); }
            catch (ArgumentOutOfRangeException) { maxLength = true; }
            Check(maxLength, "memo length guard");
            Check(FileMemoStore.ListForFile(file).Count == 3, "rejected oversize save does not overwrite");
            string corrupt = Path.Combine(root, "#memo", "회의 자료.hwpx_memo_26.10.20_bad.sunmemo");
            File.WriteAllBytes(corrupt, [0x01, 0x02, 0x03]);
            Check(FileMemoStore.ListForFile(file).Count == 3, "corrupt memo is isolated");
            bool corruptBlocksMove = false;
            try { FileActionsService.Rename(file, "거부되어야함.hwpx"); }
            catch (IOException) { corruptBlocksMove = true; }
            Check(corruptBlocksMove && File.Exists(file) && FileMemoStore.ListForFile(file).Count == 3,
                "unreadable memo blocks rename before moving original");
            File.Delete(corrupt);

            string renamed = FileActionsService.Rename(file, "회의 자료 최종.hwpx");
            var renamedRecords = FileMemoStore.ListForFile(renamed);
            Check(File.Exists(renamed) && !File.Exists(file) && renamedRecords.Count == 3,
                "rename follows with all memos");
            Check(renamedRecords.All(r => FileMemoStore.Read(r.Path, renamed).Text == r.Text &&
                Path.GetFileName(r.Path).StartsWith("회의 자료 최종.hwpx_memo_", StringComparison.Ordinal)),
                "rename rewrites both proprietary memo filenames and embedded filenames");
            Check(FileMemoStore.ListForFile(file).Count == 0, "old memo filenames cleaned after rename");

            string targetDir = Path.Combine(root, "이동 대상"); Directory.CreateDirectory(targetDir);
            TransferResult transfer = TransferService.RunAsync([renamed], targetDir, TransferKind.Move,
                null, CancellationToken.None).GetAwaiter().GetResult();
            string moved = Path.Combine(targetDir, Path.GetFileName(renamed));
            Check(transfer.Completed == 1 && transfer.Failed == 0 && File.Exists(moved) &&
                FileMemoStore.ListForFile(moved).Count == 3, "inter-panel transfer moves source memos");
            Check(FileMemoStore.ListForFile(renamed).Count == 0 &&
                File.ReadAllText(moved, Encoding.UTF8) == "실제 원본 내용", "move preserves file bytes and retires old memo mapping");

            // Pre-existing destination memo must not be overwritten by a name-only rename.
            string destCandidate = Path.Combine(targetDir, "중복 이름.hwpx");
            File.WriteAllText(destCandidate, "placeholder");
            var occupied = FileMemoStore.Save(destCandidate, "다른 사람의 자료", date, "test-PC");
            File.Delete(destCandidate);
            bool collisionBlocked = false;
            try { FileActionsService.Rename(moved, "중복 이름.hwpx"); }
            catch (IOException) { collisionBlocked = true; }
            Check(collisionBlocked && File.Exists(moved) && File.Exists(occupied.Path) &&
                  FileMemoStore.ListForFile(moved).Count == 3,
                  "memo destination collision leaves original file and existing notes untouched");

            // Moving a directory automatically moves the #memo subfolder as its child.
            string folderOld = Path.Combine(root, "폴더 A"); Directory.CreateDirectory(folderOld);
            string nestedFile = Path.Combine(folderOld, "안전진단.pdf"); File.WriteAllText(nestedFile, "test");
            FileMemoStore.Save(nestedFile, "폴더 이동 유지", date, "test-PC");
            FileActionsService.Rename(folderOld, "폴더 B");
            string nestedNew = Path.Combine(root, "폴더 B", "안전진단.pdf");
            Check(File.Exists(nestedNew) && FileMemoStore.ListForFile(nestedNew).Single().Text == "폴더 이동 유지",
                "directory rename carries internal memo directory");
            // R19: manually supplied display names stay shareable; filename/inner-author
            // discrepancies are visible as warnings rather than silently being accepted.
            string reviewed = Path.Combine(root, "작성자 검증.txt");
            File.WriteAllText(reviewed, "memo target");
            var authored = FileMemoStore.Save(reviewed, "원본 공유 메모", date, "홍길동");
            Check(authored.UserName == "홍길동" && Path.GetFileName(authored.Path).Contains("홍길동"),
                "custom author persisted as shareable memo metadata");
            using (var fs = new FileStream(authored.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.SetLength(0);
                fs.Write(Encoding.UTF8.GetBytes("SUNRISE_MEMO_V1\n"));
                using var gzip = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
                byte[] replaced = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new { FileName = Path.GetFileName(reviewed), UserName = "다른 사람", CreatedLocal = date, Text = "수정된 본문" });
                gzip.Write(replaced);
            }
            var suspicious = FileMemoStore.Read(authored.Path, reviewed);
            Check(suspicious.Text == "수정된 본문" && suspicious.HasIntegrityWarning,
                "name tampering warning displays still readable memo");
            bool detectedBeforeOverwrite = false;
            try { FileMemoStore.Save(reviewed, "overwrite blocked", date, "홍길동"); }
            catch (InvalidDataException) { detectedBeforeOverwrite = true; }
            Check(detectedBeforeOverwrite, "tampered same-day memo is not silently overwritten");
            Console.WriteLine($"Memo logic: {passed} passed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
