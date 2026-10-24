using System.IO;
using System.Text;

namespace Sunrise.FileManager.Services;

public static class EiassTableOfContentsReader
{
    public static string Read(string path, CancellationToken token)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".pdf" or ".docx" or ".hwpx" or ".hwp" or ".txt"))
            throw new ArgumentException("목차 파일은 PDF, DOCX, HWPX, HWP, TXT를 지원합니다.");
        var text = new StringBuilder();
        var result = DocumentContentSearch.ReadTextChunks(path, chunk =>
        {
            if (text.Length + chunk.Length > 1_000_000) throw new InvalidDataException("목차 텍스트가 100만 자를 초과합니다. 목차 부분만 별도 파일로 지정하세요.");
            text.Append(chunk);
            return false;
        }, () => text.ToString().Any(c => !char.IsWhiteSpace(c)), token);
        if (result.Status is ContentSearchStatus.SizeLimit or ContentSearchStatus.Unsupported)
            throw new InvalidDataException(result.Detail ?? "읽을 수 없는 문서입니다.");
        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new InvalidDataException("읽을 수 있는 텍스트가 없습니다. 스캔 목차는 텍스트를 직접 입력하거나 파일 순서를 수동으로 맞추세요.");
        return text.ToString();
    }
}
