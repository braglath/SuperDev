# Pack a self-contained win-x64 publish folder into a signed MSIX.
# A PFX from WINDOWS_PFX_BASE64 is used when present. Otherwise the package is
# signed with a certificate created for this run, which Windows does not trust.

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Output,

    [string]$Pfx,
    [string]$Password
)

$ErrorActionPreference = "Stop"

function Escape-XmlText([string]$Value) {
    return [System.Security.SecurityElement]::Escape($Value)
}

function Find-SdkTool([string]$Name) {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }
        $tool = Get-ChildItem -Path $root -Filter $Name -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($tool) {
            return $tool.FullName
        }
    }
    throw "Could not find $Name in the Windows SDK."
}

function New-RunCertificate {
    $subject = "CN=Super Dev"
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    $generated = [guid]::NewGuid().ToString("N")
    $secure = ConvertTo-SecureString -String $generated -Force -AsPlainText
    $path = Join-Path ([System.IO.Path]::GetTempPath()) "superdev-dev.pfx"
    Export-PfxCertificate -Cert $cert -FilePath $path -Password $secure | Out-Null
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    return @{
        Path = $path
        Password = $generated
        Subject = $cert.Subject
        Temporary = $true
    }
}

function Read-CertificateSubject([string]$Path, [string]$Secret) {
    $secure = ConvertTo-SecureString -String $Secret -Force -AsPlainText
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Path, $secure, "Exportable")
    return $cert.Subject
}

function Invoke-MakeAppx([string]$MakeAppx, [string]$Stage, [string]$Package) {
    $log = & $MakeAppx pack /d $Stage /p $Package /o 2>&1
    $log | ForEach-Object { Write-Host $_ }
    return @{
        Code = $LASTEXITCODE
        Log = ($log | Out-String)
    }
}

if ($Version -match '^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
    $revision = if ($Matches[4]) { $Matches[4] } else { "0" }
    $packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$revision"
}
else {
    throw "Version '$Version' is not a version like 1.2.0"
}

$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$exe = Join-Path $publishRoot "Super Dev.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish folder does not contain Super Dev.exe"
}

$layout = Join-Path ([System.IO.Path]::GetTempPath()) ("superdev-msix-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $layout | Out-Null
try {
    Copy-Item -Path (Join-Path $publishRoot "*") -Destination $layout -Recurse -Force
    Get-ChildItem -Path $layout -Recurse -Filter *.pdb -File | Remove-Item -Force

    $assets = Join-Path $layout "Assets"
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    $iconDir = Join-Path $PSScriptRoot "..\packaging\windows\Assets"
    Copy-Item -Path (Join-Path $iconDir "*.png") -Destination $assets -Force

    $signing = $null
    if (-not [string]::IsNullOrWhiteSpace($Pfx)) {
        if ([string]::IsNullOrWhiteSpace($Password)) {
            throw "A PFX was provided without a password."
        }
        $signing = @{
            Path = (Resolve-Path -LiteralPath $Pfx).Path
            Password = $Password
            Subject = Read-CertificateSubject $Pfx $Password
            Temporary = $false
        }
    }
    else {
        Write-Host "No Windows certificate was provided. Signing with a temporary certificate."
        $signing = New-RunCertificate
    }

    $manifestTemplate = Join-Path $PSScriptRoot "..\packaging\windows\Package.appxmanifest"
    $manifestText = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $manifestTemplate))
    $manifestText = $manifestText.Replace("__VERSION__", $packageVersion)
    $manifestText = $manifestText.Replace("__PUBLISHER__", (Escape-XmlText $signing.Subject))
    $manifestPath = Join-Path $layout "AppxManifest.xml"
    [System.IO.File]::WriteAllText($manifestPath, $manifestText)

    $makeAppx = Find-SdkTool "makeappx.exe"
    $package = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
    $parent = Split-Path -Parent $package
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }

    $packed = Invoke-MakeAppx $makeAppx $layout $package
    if ($packed.Code -ne 0 -and $packed.Log -match 'FileType|superdev\.json|schema') {
        Write-Host "Packaging rejected .superdev.json. Packing again with the superdev protocol only."
        $manifestText = [regex]::Replace(
            $manifestText,
            '(?s)\s*<!-- file-association:start -->.*?<!-- file-association:end -->',
            '')
        [System.IO.File]::WriteAllText($manifestPath, $manifestText)
        $packed = Invoke-MakeAppx $makeAppx $layout $package
    }
    if ($packed.Code -ne 0) {
        throw "makeappx failed with exit code $($packed.Code)"
    }

    $signtool = Find-SdkTool "signtool.exe"
    & $signtool sign /fd SHA256 /f $signing.Path /p $signing.Password /tr http://timestamp.digicert.com /td SHA256 $package
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Timestamp server was unavailable. Signing without a timestamp."
        & $signtool sign /fd SHA256 /f $signing.Path /p $signing.Password $package
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed with exit code $LASTEXITCODE"
        }
    }

    $cer = [System.IO.Path]::ChangeExtension($package, ".cer")
    $secure = ConvertTo-SecureString -String $signing.Password -Force -AsPlainText
    $publicCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        $signing.Path,
        $secure,
        "Exportable")
    [System.IO.File]::WriteAllBytes(
        $cer,
        $publicCert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    if ($signing.Temporary -and (Test-Path -LiteralPath $signing.Path)) {
        Remove-Item -LiteralPath $signing.Path -Force
    }
}
finally {
    if (Test-Path -LiteralPath $layout) {
        Remove-Item -LiteralPath $layout -Recurse -Force
    }
}
