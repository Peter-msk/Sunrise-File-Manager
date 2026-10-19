using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Runs on the build machine only. Never copied to the end-user package.
// PFX/private keys are never exported, logged, or placed in the source tree.
internal static class Program
{
    private const string GroupCn = "Sunrise Group";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows build host required");
            if (args.Length == 0) throw new ArgumentException("Usage: prepare <output-path> [--create-if-missing] | verify-embedded <path> <sha1>");
            if (args[0] == "prepare" && args.Length >= 2)
            {
                bool create = args.Skip(2).Any(x => x.Equals("--create-if-missing", StringComparison.OrdinalIgnoreCase));
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                var eligible = store.Certificates.OfType<X509Certificate2>().Where(IsEligible).OrderByDescending(c => c.NotAfter).ToArray();
                if (eligible.Length == 0 && create)
                {
                    CreateGroupCertificate(store);
                    eligible = store.Certificates.OfType<X509Certificate2>().Where(IsEligible).OrderByDescending(c => c.NotAfter).ToArray();
                }
                if (eligible.Length == 0)
                    throw new InvalidOperationException("No usable CN=Sunrise Group code-signing certificate in CurrentUser\\My. See docs/SIGNING_R14.md; use SUNRISE_ALLOW_CERT_CREATE=1 only with approval.");
                var selected = eligible[0];
                string outputPath = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath,
                    $"THUMBPRINT={selected.Thumbprint?.Replace(" ", "", StringComparison.Ordinal)}{Environment.NewLine}" +
                    $"SUBJECT={selected.Subject}{Environment.NewLine}" +
                    $"EXPIRY_UTC={selected.NotAfter.ToUniversalTime():O}{Environment.NewLine}");
                Console.WriteLine($"[SIGNING] Selected {selected.Subject} (CurrentUser/My), expires {selected.NotAfter:u}");
                return 0;
            }
            if (args[0] == "verify-embedded" && args.Length == 3)
            {
                string path = Path.GetFullPath(args[1]);
                if (!File.Exists(path)) throw new FileNotFoundException("Signed binary was not found", path);
                using X509Certificate signer = X509Certificate.CreateFromSignedFile(path);
                using var signer2 = new X509Certificate2(signer);
                string wanted = args[2].Replace(" ", "", StringComparison.Ordinal);
                if (!string.Equals(signer2.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Embedded signer certificate is not the selected Sunrise Group certificate.");
                if (!IsExactGroupSubject(signer2)) throw new InvalidOperationException("Embedded publisher is not CN=Sunrise Group.");
                Console.WriteLine($"[SIGNING] Embedded certificate matched for {Path.GetFileName(path)}");
                // This verifies certificate identity, not trusted root/SmartScreen reputation.
                return 0;
            }
            throw new ArgumentException("Invalid command or argument count");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[SIGNING ERROR] " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static bool IsExactGroupSubject(X509Certificate2 c)
    {
        // GetNameInfo avoids treating an unrelated OU/SAN with this text as the publisher.
        return string.Equals(c.GetNameInfo(X509NameType.SimpleName, false), GroupCn, StringComparison.OrdinalIgnoreCase) &&
               c.SubjectName.Name?.Split(',').Any(x => x.Trim().Equals("CN=Sunrise Group", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsEligible(X509Certificate2 c)
    {
        if (!IsExactGroupSubject(c) || !c.HasPrivateKey) return false;
        var now = DateTime.UtcNow;
        if (c.NotBefore.ToUniversalTime() > now || c.NotAfter.ToUniversalTime() <= now) return false;
        bool hasEku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(ext =>
            ext.EnhancedKeyUsages.OfType<Oid>().Any(oid => oid.Value == CodeSigningEku));
        if (!hasEku) return false;
        try
        {
            using RSA? rsa = c.GetRSAPrivateKey();
            return rsa is { KeySize: >= 3072 };
        }
        catch (CryptographicException) { return false; }
    }

    private static void CreateGroupCertificate(X509Store store)
    {
        // Explicit opt-in; key stays in the current Windows user's non-exportable CNG KSP.
        string keyName = "Sunrise Group Code Signing " + Guid.NewGuid().ToString("N");
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using CngKey key = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest("CN=Sunrise Group", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var eku = new OidCollection { new(CodeSigningEku, "Code Signing") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        store.Add(cert);
        Console.WriteLine("[SIGNING] Created one non-exportable Sunrise Group build certificate in CurrentUser/My.");
        Console.WriteLine("[SIGNING] Self-signed certificate is not a publicly trusted publisher credential.");
    }
}
