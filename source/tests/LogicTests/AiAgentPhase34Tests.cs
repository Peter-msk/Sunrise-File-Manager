using System.IO.Compression;
using System.Text;
using Sunrise.FileManager.Services;

public static class AiAgentPhase34Tests
{
    private static int _passed;
    private static void Check(bool ok, string caption)
    {
        if (!ok) throw new Exception("FAILED AI P3/P4: " + caption);
        ++_passed;
        Console.WriteLine("PASS AI P3/P4: " + caption);
    }
    private static void Reject(Action action, string caption)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { Check(true, caption); return; }
        throw new Exception("FAILED AI P3/P4: " + caption);
    }
    private static void Office(string path, string entry, string body, bool storedPreview = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var text = zip.CreateEntry(entry);
        using (var writer = new StreamWriter(text.Open(), new UTF8Encoding(false)))
            writer.Write(body);
        if (storedPreview)
        {
            var preview = zip.CreateEntry("Preview/PrvText.txt");
            using var writer = new StreamWriter(preview.Open(), new UTF8Encoding(false));
            writer.Write("unrelated cached preview text");
        }
    }
    public static async Task RunAsync()
    {
        Check(AiNaturalLanguageService.Parse("파일명 앞에 '완료_' 붙여줘").RenamePrefix == "완료_", "rename prefix parsed");
        Check(AiNaturalLanguageService.Parse("파일명 뒤에 '_최종' 붙여줘").RenameSuffix == "_최종", "rename suffix parsed");
        Check(AiNaturalLanguageService.Parse("선택한 파일을 휴지통으로 보내줘").Action == "recycle", "recycle intent parsed");
        Check(AiNaturalLanguageService.Parse("본문에 '안성천' 포함된 DOCX 파일 찾아줘").ContentContains == "안성천", "document content condition parsed");
        Reject(() => AiNaturalLanguageService.Parse("파일 전부 영구삭제해 줘"), "permanent deletion refused");
        Reject(() => AiNaturalLanguageService.Parse("본문 내용을 이해해서 이동해 줘"), "unbounded semantic inference refused");
        Reject(() => AiNaturalLanguageService.Parse("파일을 복사하고 휴지통에 보내줘"), "mixed operations refused");
        string root = Path.Combine(Path.GetTempPath(), "SFMP34_" + Guid.NewGuid().ToString("N"));
        string destination = root + "_output";
        string audit = root + "_audit";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(destination);
        try
        {
            string hwpx = Path.Combine(root, "한글문서.hwpx");
            string docx = Path.Combine(root, "수환경.docx");
            string pdf = Path.Combine(root, "english.pdf");
            string unlisted = Path.Combine(root, "video.mp4");
            Office(hwpx, "Contents/section0.xml", "<hp:p xmlns:hp='urn:hwp'><hp:t>안성천 수질 조사</hp:t></hp:p>", storedPreview:true);
            Office(docx, "word/document.xml", "<w:p xmlns:w='urn:word'><w:t>안성천 하천 관리</w:t></w:p>");
            // The optional PDF fixture is never required for compilation. It is absent
            // from company-only source packages and must not be accessed in company builds.
            string pdfFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "river_report.pdf");
            bool hasPdfFixture = File.Exists(pdfFixture);
            if (hasPdfFixture) File.Copy(pdfFixture, pdf);
            File.WriteAllText(unlisted, "안성천");
            var selection = hasPdfFixture ? new[] { hwpx, docx, pdf, unlisted } : new[] { hwpx, docx, unlisted };
            Reject(() => AiAgentOrchestrator.Prepare(root, selection, null, "본문에 '안성천' 포함된 파일 찾아줘",
                false, CollisionPolicy.Skip, CancellationToken.None), "document read must be approved");
            var found = AiAgentOrchestrator.Prepare(root, selection, null,
                "본문에 '안성천' 포함된 파일 찾아줘", false, CollisionPolicy.Skip, CancellationToken.None, contentReadApproved:true);
            Check(found.SearchOnly && found.Matches.Count == 2 && found.Matches.Contains(hwpx) && found.Matches.Contains(docx),
                "HWPX and DOCX inner XML searched (not saved thumbnail)");
            Check(found.UnsupportedDocuments == 1 && found.UnreadableDocuments == 0, "unsupported documents counted");
            if (hasPdfFixture)
            {
                var pdfFound = AiAgentOrchestrator.Prepare(root, selection, null, "본문에 'river' 포함된 PDF 파일 찾아줘",
                    false, CollisionPolicy.Skip, CancellationToken.None, contentReadApproved:true);
                Check(pdfFound.Matches.Count == 1 && pdfFound.Matches[0] == pdf, "PDF visible text searched");
                var noPageHeading = AiAgentOrchestrator.Prepare(root, selection, null, "본문에 '페이지' 포함된 PDF 파일 찾아줘",
                    false, CollisionPolicy.Skip, CancellationToken.None, contentReadApproved:true);
                Check(noPageHeading.Matches.Count == 0, "synthetic PDF page headers never match");
            }
            else
            {
                Console.WriteLine("SKIPPED AI P3/P4 PDF fixture tests: river_report.pdf not supplied.");
            }
            var planned = AiAgentOrchestrator.Prepare(root, selection, destination,
                "본문에 '안성천' 포함된 파일 복사해 줘", false, CollisionPolicy.Skip, CancellationToken.None, contentReadApproved:true);
            Check(planned.ValidatedPlan is not null && planned.ValidatedPlan.Rows.Count == 2 &&
                !Directory.EnumerateFileSystemEntries(destination).Any(), "document search proposal is read only");
            var executed = await AiPlanService.ExecuteAsync(planned.ValidatedPlan!, true, audit, null, CancellationToken.None,
                journal: new AiJournalStore(Path.Combine(audit, "Journal"), new TestJournalProtection()));
            Check(executed.Completed == 2 && File.Exists(Path.Combine(destination, "한글문서.hwpx")) &&
                File.Exists(Path.Combine(destination, "수환경.docx")), "approved content copy uses existing engine");
            string text = Path.Combine(root, "report.txt");
            File.WriteAllText(text, "draft");
            FileMemoStore.Save(text, "keep memo", new DateTime(2026, 10, 18), "phase34");
            var rename = AiAgentOrchestrator.Prepare(root, [text], null, "파일명 앞에 '완료_' 붙여줘",
                false, CollisionPolicy.Skip, CancellationToken.None);
            Check(rename.ValidatedPlan is not null && rename.PreviewRows[0].After.EndsWith("완료_report.txt"),
                "rename preview preserves extension");
            var renamed = await AiPlanService.ExecuteAsync(rename.ValidatedPlan!, true, audit, null, CancellationToken.None,
                journal: new AiJournalStore(Path.Combine(audit, "Journal"), new TestJournalProtection()));
            string target = Path.Combine(root, "완료_report.txt");
            var memoRows = FileMemoStore.ListForFile(target, out int unreadable);
            Check(renamed.Completed == 1 && File.Exists(target) && !File.Exists(text) && unreadable == 0 &&
                memoRows.Count == 1 && memoRows[0].Text == "keep memo", "rename retains #memo association");
            Reject(() => AiAgentOrchestrator.Prepare(root, [target], null, "선택한 파일을 휴지통으로 보내줘",
                false, CollisionPolicy.Skip, CancellationToken.None), "recycle with file-associated memos refused");
            string simple = Path.Combine(root, "simple.txt");
            File.WriteAllText(simple, "independent");
            var recycle = AiAgentOrchestrator.Prepare(root, [simple], null, "선택한 파일을 휴지통으로 보내줘",
                false, CollisionPolicy.Skip, CancellationToken.None);
            Reject(() => AiPlanService.ExecuteAsync(recycle.ValidatedPlan!, true, audit, null,
                CancellationToken.None, recycleApproved:false).GetAwaiter().GetResult(), "recycle requires second approval");
            Check(File.Exists(simple) && File.Exists(target), "recycle rejection makes no changes");
            Reject(() => AiAgentOrchestrator.Prepare(root, [], null, "휴지통으로 보내줘",
                true, CollisionPolicy.Skip, CancellationToken.None), "recursive recycling forbidden");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
            try { Directory.Delete(destination, true); } catch (IOException) { }
            try { Directory.Delete(audit, true); } catch (IOException) { }
        }
        Console.WriteLine($"AI P3/P4 tests: {_passed} PASS");
    }
}
