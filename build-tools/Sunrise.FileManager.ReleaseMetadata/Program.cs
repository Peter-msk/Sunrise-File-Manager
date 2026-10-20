using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Sunrise File Manager own-product metadata. Run ONLY after the signed Setup has been verified.
// Offline; no GitHub mutations, credential usage, source upload, or installer auto-execution.
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ReleaseMetadata <source-root> <signed-setup.exe> <version>");
    return 2;
}

try
{
    var root = Path.GetFullPath(args[0]);
    var setup = Path.GetFullPath(args[1]);
    var version = args[2];
    if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant))
        throw new InvalidDataException("Version must be numeric major.minor.patch");
    var versionSource = File.ReadAllText(Path.Combine(root, "installer", "RELEASE_VERSION.txt"), Encoding.UTF8).Trim();
    if (version != versionSource)
        throw new InvalidDataException("Release version mismatch");
    var expectedName = $"Sunrise_File_Manager_{version}_Setup.exe";
    if (!string.Equals(Path.GetFileName(setup), expectedName, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Setup filename does not match the version");
    if (!File.Exists(setup)) throw new FileNotFoundException("Signed installer not found", setup);
    var info = new FileInfo(setup);
    if (info.Length < 1_000_000) throw new InvalidDataException("Installer is unexpectedly small");
    using var stream = File.OpenRead(setup);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    var noticeBody = File.ReadAllText(Path.Combine(root, "release", "NOTICE_BODY.txt"), new UTF8Encoding(false, true)).Trim();
    if (noticeBody.Length is 0 or > 60_000) throw new InvalidDataException("Notice body is empty or oversized");
    var notice = $"Sunrise File Manager {version}\n\n{noticeBody}\n";
    var description = "분할 탐색기, 파일 미리보기, 메모, AI 정리, Sunrise Control v1 중앙 정책, 제품별 공지 및 업데이트 확인";

    // A local successful build does not prove a publicly downloadable release.
    // Publish Setup first, then separately validate and publish the product metadata.
    var metadata = new
    {
        schema_version = "1.1",
        app_id = "sunrise_file_manager",
        product_id = "SUNRISE-FILE-MANAGER-WIN-DESKTOP",
        product_family = "Sunrise File Manager",
        publisher_id = "SUNRISE-SOFTWARE",
        channel = "stable",
        platform = "windows-x64",
        package_kind = "installer",
        version,
        latest_version = version,
        official_release = true,
        release_date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        release_url = "",
        download_url = "",
        download_file_name = expectedName,
        sha256 = hash,
        file_size_bytes = info.Length,
        update_endpoint_configured = false,
        notice_url = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/notice.txt",
        release_notes = description + ". 설치파일은 서명·해시 확인되었으나 별도 공개 배포 전까지 다운로드 링크는 비활성화됩니다."
    };
    var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    // Prepared values are written only after *all* inputs and hash validation succeed.
    var dist = Path.Combine(root, "dist", "release-metadata");
    Directory.CreateDirectory(dist);
    var writes = new []
    {
        (Path.Combine(dist, "latest.json"), json),
        (Path.Combine(dist, "notice.txt"), notice),
        (Path.Combine(root, "latest.json"), json),
        (Path.Combine(root, "notice.txt"), notice)
    };
    foreach (var (target, body) in writes)
    {
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, body, new UTF8Encoding(false));
        File.Move(tmp, target, true);
    }
    Console.WriteLine($"[RELEASE] Product-owned latest.json and notice.txt generated for {version}");
    Console.WriteLine($"[RELEASE] Signed Setup size={info.Length}, sha256={hash}");
    Console.WriteLine("[RELEASE] Download endpoint remains disabled until public artifact publication.");
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or CryptographicException)
{
    Console.Error.WriteLine("[RELEASE METADATA ERROR] " + error.Message);
    return 1;
}
