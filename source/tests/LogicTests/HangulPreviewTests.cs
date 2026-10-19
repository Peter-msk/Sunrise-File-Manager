using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sunrise.FileManager.Services;

static class HangulPreviewTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("HWPX: " + name); passed++; Console.WriteLine("PASS HWPX: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "SunriseHwpx_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string Make(string name, Action<ZipArchive> content)
        { string path = Path.Combine(root, name + ".hwpx"); using var zip = ZipFile.Open(path, ZipArchiveMode.Create); content(zip); return path; }
        void Text(ZipArchive z, string body) { using var w = new StreamWriter(z.CreateEntry("Preview/PrvText.txt").Open(), new UTF8Encoding(false)); w.Write(body); }
        try
        {
            string text = Make("text", z => Text(z, "한글 문서 미리보기"));
            byte[] before = SHA256.HashData(File.ReadAllBytes(text));
            var preview = FilePreviewService.Read(text, false, CancellationToken.None);
            Check(preview.Kind == PreviewKind.Text && preview.Body == "한글 문서 미리보기", "stored Korean text");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(text))), "source bytes unchanged");
            Check(Directory.GetFiles(root).Length == 1, "no extracted files or conversions");
            string huge = Make("large", z => { Text(z, "85MB 문서의 저장된 텍스트"); using var p = z.CreateEntry("BinData/padding.bin", CompressionLevel.NoCompression).Open(); byte[] block = new byte[8192]; for (int n = 0; n < 85 * 128; n++) p.Write(block); });
            Check(new FileInfo(huge).Length > 85L * 1024 * 1024, "representative large archive fixture");
            Check(FilePreviewService.Read(huge, false, CancellationToken.None).Body.Contains("85MB"), "large archive uses preview without full body extraction");
            string limited = Make("limited", z => Text(z, new string('가', 20000)));
            Check(FilePreviewService.Read(limited, false, CancellationToken.None).Body.Length < 16100, "text prefix bounded");
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jF9kAAAAASUVORK5CYII=");
            string image = Make("image", z => { Text(z, "fallback"); using var w = z.CreateEntry("Preview/PrvImage.png").Open(); w.Write(png); });
            var picture = FilePreviewService.Read(image, false, CancellationToken.None);
            Check(picture.Kind == PreviewKind.Image && picture.ImageBytes!.SequenceEqual(png), "stored first-page PNG takes priority");
            var page = PagedPreviewService.Read(image, 99, CancellationToken.None);
            Check(page.PageCount == 1 && page.Page == 1 && page.ImageBytes!.SequenceEqual(png), "first-page image is not presented as full document pagination");
            string invalid = Make("invalid-image", z => { Text(z, "fallback"); using var w = z.CreateEntry("Preview/PrvImage.png").Open(); w.Write(new byte[32]); });
            Check(FilePreviewService.Read(invalid, false, CancellationToken.None).Body == "fallback", "invalid PNG falls back to stored text");
            byte[] excessive = (byte[])png.Clone(); excessive[16] = 0x7f;
            string dimensions = Make("dimensions", z => { Text(z, "safe fallback"); using var w = z.CreateEntry("Preview/PrvImage.png").Open(); w.Write(excessive); });
            Check(FilePreviewService.Read(dimensions, false, CancellationToken.None).Body == "safe fallback", "extreme image dimensions rejected before decode");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool canceled = false;
            try { FilePreviewService.Read(text, false, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "cancellation before archive read");
            string hwp = Path.Combine(root, "original.hwp"); File.WriteAllText(hwp, "not a parsed HWP fixture");
            var original = FilePreviewService.Read(hwp, false, CancellationToken.None);
            Check(original.Kind == PreviewKind.Information && original.Body.Contains("연결 앱"), "HWP limitation gives explicit original-open action");
            Check(new PanelSession().DetailsVisible == false, "new explorer starts with file list space instead of details sidebar");
            Check(!UiLayoutPolicy.Workspace(1200, 800, 1, true).SideDock, "AI does not squeeze a medium-width file list");
            Check(UiLayoutPolicy.Workspace(1440, 800, 1, true).SideDock, "AI docks beside a wide file list");
            Console.WriteLine($"HWPX/UI TESTS PASS: {passed}");
        }
        finally { Directory.Delete(root, true); }
    }
}
