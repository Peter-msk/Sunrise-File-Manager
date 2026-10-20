using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Sunrise Capture 4.2.0 verified build flow: signed installer -> product metadata
// -> release info / product manifest / signature report / upload instructions
// -> required-artifact gate. .NET implementation remains File Manager-owned.
// No network publishing, certificate creation, private key access, or installer execution.
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
        throw new InvalidDataException("Version must be major.minor.patch");
    var versionSource = File.ReadAllText(Path.Combine(root, "installer", "RELEASE_VERSION.txt"), Encoding.UTF8).Trim();
    if (version != versionSource) throw new InvalidDataException("Release version mismatch");
    var expectedName = $"Sunrise_File_Manager_{version}_Setup.exe";
    if (!string.Equals(Path.GetFileName(setup), expectedName, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Installer name/version mismatch");
    if (!File.Exists(setup)) throw new FileNotFoundException("Signed Setup missing", setup);
    var info = new FileInfo(setup);
    if (info.Length < 1_000_000) throw new InvalidDataException("Installer is unexpectedly small");
    // The CMD pipeline must finish ISCC Authenticode signing and SignTool verify first.
    var logs = Path.Combine(root, "build-logs");
    var verifyLog = Path.Combine(logs, "R15_verify_setup.log");
    var signerFile = Path.Combine(logs, "R14_signing_certificate.txt");
    if (!File.Exists(verifyLog) || new FileInfo(verifyLog).Length == 0)
        throw new InvalidDataException("SignTool verification evidence missing");
    if (!File.Exists(signerFile)) throw new InvalidDataException("Pinned signing identity evidence missing");
    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadAllLines(signerFile, Encoding.UTF8))
    {
        int eq = line.IndexOf('=');
        if (eq > 0) properties[line[..eq].Trim()] = line[(eq + 1)..].Trim();
    }
    if (!properties.TryGetValue("THUMBPRINT", out string? thumbprint) ||
        !Regex.IsMatch(thumbprint, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
        throw new InvalidDataException("Signer SHA1 is missing or invalid");
    if (!properties.TryGetValue("SUBJECT", out string? signer) ||
        !signer.Contains("Sunrise Group", StringComparison.Ordinal))
        throw new InvalidDataException("Signer subject mismatch");

    using var stream = File.OpenRead(setup);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    var noticeBody = File.ReadAllText(Path.Combine(root, "release", "NOTICE_BODY.txt"), new UTF8Encoding(false, true)).Trim();
    if (noticeBody.Length is 0 or > 60_000) throw new InvalidDataException("Invalid notice body");
    var notice = $"Sunrise File Manager {version}\n\n{noticeBody}\n";
    var releaseNotes = "분할 탐색기, 파일 미리보기, 메모, AI 정리, Sunrise Control v1 중앙 정책, 제품별 공지 및 업데이트 확인";
    var releaseTag = "v" + version;
    var releasePage = $"https://github.com/Peter-msk/Sunrise-File-Manager/releases/tag/{releaseTag}";
    var sourceUrl = "https://github.com/Peter-msk/Sunrise-File-Manager";
    var noticeUrl = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/notice.txt";
    var latestUrl = "https://raw.githubusercontent.com/Peter-msk/Sunrise-File-Manager/main/latest.json";
    var generatedUtc = DateTimeOffset.UtcNow;

    // These URLs identify the owning product, NOT a published/downloadable installer.
    // The repo is private. Publishing needs an explicitly approved read-only channel.
    var latest = new
    {
        schema_version = "1.1", app_id = "sunrise_file_manager", product_id = "SUNRISE-FILE-MANAGER-WIN-DESKTOP",
        product_family = "Sunrise File Manager", publisher_id = "SUNRISE-SOFTWARE", channel = "stable",
        platform = "windows-x64", package_kind = "installer", version, latest_version = version,
        official_release = true, release_date = generatedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        release_url = "", download_url = "", download_file_name = expectedName, sha256 = hash,
        file_size_bytes = info.Length, update_endpoint_configured = false, notice_url = noticeUrl,
        release_notes = releaseNotes + ". 공개 릴리스 승인 전까지 다운로드 링크는 비활성화됩니다."
    };
    var releaseInfo = new
    {
        schema_version = "1.0", app = "Sunrise File Manager", version,
        product_id = "SUNRISE-FILE-MANAGER-WIN-DESKTOP", channel = "stable", package_kind = "installer",
        build_completed_at_utc = generatedUtc.ToString("O", CultureInfo.InvariantCulture),
        setup_file = expectedName, setup_path = setup, installer_size_bytes = info.Length, sha256 = hash,
        code_signing_subject = signer, code_signing_thumbprint = thumbprint.ToUpperInvariant(),
        signature_verification = "R15_verify_setup.log (verified in BUILD_RELEASE_INSTALLER.cmd)",
        release_tag = releaseTag, intended_release_page = releasePage,
        release_url = "", download_url = "", update_endpoint_configured = false,
        product_latest_url = latestUrl, product_notice_url = noticeUrl,
        publication_status = "LOCAL_SIGNED_BUILD_PENDING_PUBLICATION"
    };
    var productManifest = new
    {
        schema_version = "1.0", product_name = "Sunrise File Manager",
        product_id = "SUNRISE-FILE-MANAGER-WIN-DESKTOP", version, publisher = "Sunrise Group",
        distribution = new { repository = sourceUrl, proposed_release_page = releasePage,
            latest_json = latestUrl, notice = noticeUrl, public_distribution_validated = false },
        artifacts = new { setup = expectedName, sha256 = hash, size_bytes = info.Length },
        control = new { id = "sunrise_file_manager", source = "central control.txt", scope = "operation/update/minimum_version" }
    };
    var uploadGuide = $"Sunrise File Manager {version} 배포 준비\n\n" +
        $"제품 저장소: {sourceUrl}\n예정 릴리스: {releasePage}\n" +
        $"설치파일: {expectedName}\n크기: {info.Length} bytes\nSHA256: {hash}\n" +
        $"서명 Subject: {signer}\n서명 SHA1: {thumbprint.ToUpperInvariant()}\n\n" +
        "1. Windows 설치/서명/제거 실기 검증 완료 후 배포를 승인하십시오.\n" +
        "2. 공개 읽기 가능한 제품 전용 배포 경로를 먼저 확인하십시오 (비공개 GitHub raw/release는 익명 사용 불가).\n" +
        "3. 설치파일을 배포하고 실제 URL/크기/SHA256을 대조한 뒤 latest.json을 게시하십시오.\n" +
        "4. notice.txt도 File Manager 제품 채널에 게시하십시오. Sunrise Control은 운영/업데이트 정책만 유지합니다.\n" +
        "5. 빌드 스크립트는 게시·다운로드·설치를 자동 수행하지 않습니다.\n";
    var signatureReport = "Sunrise File Manager Authenticode build verification evidence\n" +
        $"Setup: {expectedName}\nSHA256: {hash}\nSize: {info.Length} bytes\n" +
        $"Signer Subject: {signer}\nSigner SHA1: {thumbprint.ToUpperInvariant()}\n" +
        "SignTool: BUILD_RELEASE_INSTALLER.cmd checked the signed Setup before metadata creation.\n" +
        "Verify log: build-logs/R15_verify_setup.log\n" +
        "WARNING: A self-signed certificate does not establish a publicly trusted publisher.\n";

    var options = new JsonSerializerOptions { WriteIndented = true };
    string Json(object value) => JsonSerializer.Serialize(value, options) + "\n";
    var dist = Path.Combine(root, "dist", "release-metadata");
    var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine(dist, "latest.json")] = Json(latest),
        [Path.Combine(dist, "notice.txt")] = notice,
        [Path.Combine(dist, "release_info.json")] = Json(releaseInfo),
        [Path.Combine(dist, "sunrise_file_manager_product_manifest.json")] = Json(productManifest),
        [Path.Combine(dist, "UPLOAD_TO_GITHUB.txt")] = uploadGuide,
        [Path.Combine(dist, "SIGNATURE_VERIFY_RESULT.txt")] = signatureReport,
        [Path.Combine(root, "latest.json")] = Json(latest),
        [Path.Combine(root, "notice.txt")] = notice
    };
    // Prepare every output before modifying publication candidates.
    foreach (var (target, body) in writes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target + ".tmp", body, new UTF8Encoding(false));
    }
    var backups = new Dictionary<string, byte[]?>();
    try
    {
        foreach (var (target, _) in writes)
        {
            backups[target] = File.Exists(target) ? File.ReadAllBytes(target) : null;
            File.Move(target + ".tmp", target, true);
        }
    }
    catch
    {
        foreach (var (target, bytes) in backups)
        {
            try
            {
                if (bytes is null) File.Delete(target);
                else File.WriteAllBytes(target, bytes);
            }
            catch (IOException) { /* Surface primary failure without hiding it. */ }
        }
        throw;
    }
    finally
    {
        foreach (var target in writes.Keys)
        {
            try { File.Delete(target + ".tmp"); } catch (IOException) { }
        }
    }
    // Explicit Capture-style required-artifact gate.
    foreach (var target in writes.Keys)
        if (!File.Exists(target) || new FileInfo(target).Length == 0)
            throw new InvalidDataException("Missing release output: " + target);
    Console.WriteLine($"[BUILD_STAGE_OK] release_metadata version={version}");
    Console.WriteLine($"[BUILD_STAGE_OK] release_artifact_verify files={writes.Count}");
    Console.WriteLine($"[RELEASE] Setup SHA256={hash}, bytes={info.Length}");
    Console.WriteLine("[RELEASE] Product latest.json/notice.txt generated. Publication remains disabled.");
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or CryptographicException)
{
    Console.Error.WriteLine("[RELEASE METADATA ERROR] " + error.Message);
    return 1;
}
