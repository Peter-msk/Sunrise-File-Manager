# Sunrise Group build-time Authenticode certificate selection and identity verification.
# Uses Windows PowerShell's built-in certificate APIs. Never starts a freshly compiled helper EXE.
# Respects the host's PowerShell execution policy; no bypass or antivirus exclusions.
# Does not create/export certificates, private keys, PFX files, or secrets.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Prepare', 'VerifyEmbedded')]
    [string]$Action,
    [Parameter(Mandatory=$true)]
    [string]$Path,
    [string]$Thumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$groupName = 'Sunrise Group'
$codeSigningOid = '1.3.6.1.5.5.7.3.3'

function Assert-GroupIdentity([System.Security.Cryptography.X509Certificates.X509Certificate2]$cert) {
    $simpleName = $cert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($simpleName -cne $groupName -or $cert.Subject -notmatch '(?:^|,\s*)CN\s*=\s*Sunrise Group(?:,|$)') {
        throw 'Certificate subject does not exactly identify CN=Sunrise Group.'
    }
}

function Test-Eligible([System.Security.Cryptography.X509Certificates.X509Certificate2]$cert) {
    try {
        Assert-GroupIdentity $cert
        if (-not $cert.HasPrivateKey) { return $false }
        $now = [datetime]::UtcNow
        if ($cert.NotBefore.ToUniversalTime() -gt $now -or $cert.NotAfter.ToUniversalTime() -le $now) { return $false }
        $ekuOk = $false
        foreach ($extension in $cert.Extensions) {
            if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
                foreach ($oid in $extension.EnhancedKeyUsages) {
                    if ($oid.Value -eq $codeSigningOid) { $ekuOk = $true }
                }
            }
        }
        if (-not $ekuOk) { return $false }
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        if ($null -eq $rsa) { return $false }
        try { return ($rsa.KeySize -ge 3072) }
        finally { $rsa.Dispose() }
    }
    catch { return $false }
}

try {
    if ($Action -eq 'Prepare') {
        $eligible = @(Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object { Test-Eligible $_ } |
            Sort-Object -Property NotAfter -Descending)
        if ($eligible.Count -lt 1) {
            throw 'No eligible CN=Sunrise Group code-signing certificate in CurrentUser/My. Contact IT to provision the existing group certificate; this build never creates an unapproved certificate.'
        }
        $chosen = $eligible[0]
        $output = [System.IO.Path]::GetFullPath($Path)
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output)) | Out-Null
        $lines = @('THUMBPRINT=' + $chosen.Thumbprint.Replace(' ', ''),
                   'SUBJECT=' + $chosen.Subject,
                   'EXPIRY_UTC=' + $chosen.NotAfter.ToUniversalTime().ToString('o'))
        [System.IO.File]::WriteAllText($output, ($lines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Output ('[SIGNING] Reusing CN=Sunrise Group code-signing certificate from CurrentUser/My; expires {0:u}.' -f $chosen.NotAfter.ToUniversalTime())
    }
    else {
        if ([string]::IsNullOrWhiteSpace($Thumbprint) -or $Thumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
            throw 'An exact 40-character SHA-1 certificate thumbprint is required for signer identity comparison.'
        }
        $signedFile = [System.IO.Path]::GetFullPath($Path)
        if (-not [System.IO.File]::Exists($signedFile)) { throw "Signed file does not exist: $signedFile" }
        $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
        if ($null -eq $signature.SignerCertificate -or $signature.SignatureType.ToString() -ne 'Authenticode') {
            throw 'No embedded Authenticode signer certificate exists.'
        }
        $embedded = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($signedFile)
        try {
            $signer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($embedded)
            try {
                Assert-GroupIdentity $signer
                if ($signer.Thumbprint -ine $Thumbprint -or $signature.SignerCertificate.Thumbprint -ine $Thumbprint) {
                    throw 'Embedded signer certificate differs from the selected Sunrise Group certificate.'
                }
                Write-Output ('[SIGNING] Embedded signer matched for {0}; Windows trust result={1}.' -f [System.IO.Path]::GetFileName($signedFile), $signature.Status)
            }
            finally { $signer.Dispose() }
        }
        finally { $embedded.Dispose() }
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine('[SIGNING ERROR] ' + $_.Exception.Message)
    exit 1
}
