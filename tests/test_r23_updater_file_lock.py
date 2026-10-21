"""Regression contracts for the v1.0.0 updater sharing violation.

Source-level validation only: actual Windows file-handle behavior requires a
Windows smoke test against the built WPF application.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SERVICE = (ROOT / "source/Sunrise.FileManager/Services/SunriseReleaseService.cs").read_text("utf8")


def close_brace(source: str, start: int) -> int:
    opening = source.index("{", start)
    depth = 0
    for pos in range(opening, len(source)):
        if source[pos] == "{":
            depth += 1
        elif source[pos] == "}":
            depth -= 1
            if depth == 0:
                return pos
    raise AssertionError("Writer scope is not closed")


class UpdaterFileHandleContract(unittest.TestCase):
    def test_exclusive_writer_disposed_before_rename(self):
        method = SERVICE.split("public static async Task<string> DownloadAsync(", 1)[1]
        begin = method.index("await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None")
        end = close_brace(method, begin)
        move = method.index("File.Move(temp, target, true);")
        self.assertLess(end, move, "Windows cannot rename the still-open exclusive writer")
        self.assertNotIn("await using var file = new FileStream", method)
        self.assertIn("await file.FlushAsync();", method[begin:end])
        self.assertIn("digest.AppendData(buffer, 0, n)", method[begin:end])

    def test_hash_and_size_verified_after_writer_closes(self):
        method = SERVICE.split("public static async Task<string> DownloadAsync(", 1)[1]
        end = close_brace(method, method.index("await using (var file = new FileStream("))
        verify = method.index("if (total != release.FileSize ||")
        move = method.index("File.Move(temp, target, true);")
        self.assertLess(end, verify)
        self.assertLess(verify, move)
        self.assertIn("Downloaded release SHA256 mismatch", method[verify:move])

    def test_unverified_download_never_executed(self):
        self.assertIn("release.CanDownload", SERVICE)
        self.assertIn("IsApprovedDownload(this)", SERVICE)
        self.assertIn("response.EnsureSuccessStatusCode();", SERVICE)
        self.assertIn('if (File.Exists(temp)) File.Delete(temp)', SERVICE)
        self.assertIn('throw new InvalidDataException("Unexpected download host")', SERVICE)


if __name__ == "__main__":
    unittest.main()
