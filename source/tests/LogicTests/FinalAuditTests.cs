using System.Text.Json;
using Sunrise.FileManager.Services;

static class FinalAuditTests
{
    public static async Task RunAsync()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FINAL: " + name); passed++; Console.WriteLine("PASS FINAL: " + name); }
        string root = Path.Combine(Path.GetTempPath(), "SunriseFinal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "session.json");
        try
        {
            Check(SessionStore.Load(path, out var missing).VisiblePanels == 1 && missing is null, "missing session defaults");
            foreach (string json in new[] { "null", "{", "{\"VisiblePanels\":0}", "{\"Panels\":null}", "{\"Panels\":[null]}",
                "{\"Panels\":[{\"TabPaths\":null}]}", "{\"Panels\":[{\"TabPaths\":[null]}]}", "{\"Panels\":[{\"PreviewHeight\":-1}]}" })
            {
                File.WriteAllText(path, json);
                var loaded = SessionStore.Load(path, out var warning);
                Check(warning is not null && loaded.VisiblePanels == 1 && File.ReadAllText(path) == json, "invalid session warns and preserves original");
            }
            File.WriteAllText(path, new string(' ', 1024 * 1024 + 1));
            Check(SessionStore.Load(path, out var large).VisiblePanels == 1 && large is not null, "oversized session bounded");
            File.WriteAllText(path, "{\"VisiblePanels\":2,\"UnknownLegacyField\":true,\"Panels\":[{\"TabPaths\":[]}]}");
            Check(SessionStore.Load(path, out var legacy).VisiblePanels == 2 && legacy is null, "legacy unknown fields remain compatible");
            var session = new AppSession { VisiblePanels = 3, LayoutVersion = 10, Panels = [new PanelSession { TabPaths = [root] }] };
            SessionStore.Save(session, path);
            var roundtrip = SessionStore.Load(path, out var roundtripWarning);
            Check(roundtrip.VisiblePanels == 3 && roundtrip.Panels[0].TabPaths[0] == root && roundtripWarning is null, "atomic session roundtrip");
            string previous = File.ReadAllText(path);
            bool rejected = false;
            try { SessionStore.Save(new AppSession { VisiblePanels = 99 }, path); } catch (ArgumentException) { rejected = true; }
            Check(rejected && File.ReadAllText(path) == previous, "invalid save preserves prior session");
            bool oversizedRejected = false;
            try { SessionStore.Save(new AppSession { Panels = Enumerable.Range(0, 4).Select(_ => new PanelSession { TabPaths = Enumerable.Repeat(new string('가', 32767), 12).ToList() }).ToList() }, path); }
            catch (ArgumentException) { oversizedRejected = true; }
            Check(oversizedRejected && File.ReadAllText(path) == previous, "oversized save preserves prior session");
            await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() => SessionStore.Save(new AppSession { VisiblePanels = i % 4 + 1 }, path))));
            var concurrent = SessionStore.Load(path, out var concurrentWarning);
            Check(concurrentWarning is null && concurrent.VisiblePanels is >= 1 and <= 4, "concurrent saves publish complete JSON");
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "session staging cleaned");
            foreach (string name in new[] { "a:b", "a?b", "a*b", "a|b", "a<b", "a>b", "a\"b", "a\nb", "CON.txt", "COM¹.txt", "tail.", "tail " })
            {
                bool invalid = false;
                try { FileActionsService.ValidateName(name); } catch (ArgumentException) { invalid = true; }
                Check(invalid, "Windows name rejected: " + JsonSerializer.Serialize(name));
            }
            Check(FileActionsService.ValidateName("정상 문서 (1).txt") == "정상 문서 (1).txt", "valid Unicode name retained");
            Console.WriteLine($"FINAL AUDIT TESTS PASS: {passed}");
        }
        finally { Directory.Delete(root, true); }
    }
}
