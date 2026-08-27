[CmdletBinding()]
param(
    # A valid existing installation is reused unless this switch is supplied.
    [switch]$Force,

    # Adds the repository-local directory to this process and to GITHUB_PATH when present.
    [switch]$AddToPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Windows PowerShell 5.1 can otherwise negotiate an obsolete TLS version.
[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12

$foundryVersion = '1.7.1'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installDirectory = Join-Path $repositoryRoot ".tools/foundry/v${foundryVersion}"

function Get-PlatformPackage {
    $runtime = [System.Runtime.InteropServices.RuntimeInformation]
    $architecture = $runtime::OSArchitecture
    $x64 = [System.Runtime.InteropServices.Architecture]::X64
    $arm64 = [System.Runtime.InteropServices.Architecture]::Arm64

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) -and $architecture -eq $x64) {
        return [pscustomobject]@{
            Archive = "foundry_v${foundryVersion}_win32_amd64.zip"
            Sha256 = '6d41121b4bbb809845821c903619cfee75ed364f2bdc58a6787c9b0454114537'
            Format = 'zip'
            ExecutableSuffix = '.exe'
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux) -and $architecture -eq $x64) {
        return [pscustomobject]@{
            Archive = "foundry_v${foundryVersion}_linux_amd64.tar.gz"
            Sha256 = 'cf7e688ed0c4c48adffca788b496076e31060b67ac5afe1e43dbb5499c20c88b'
            Format = 'tar.gz'
            ExecutableSuffix = ''
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux) -and $architecture -eq $arm64) {
        return [pscustomobject]@{
            Archive = "foundry_v${foundryVersion}_linux_arm64.tar.gz"
            Sha256 = 'c8fe8fa09ae3aba2c81b510c6f9da3a9d468029b9580e690b245b3f0aea687ae'
            Format = 'tar.gz'
            ExecutableSuffix = ''
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX) -and $architecture -eq $x64) {
        return [pscustomobject]@{
            Archive = "foundry_v${foundryVersion}_darwin_amd64.tar.gz"
            Sha256 = 'c7fd1f5c9bf718d30b5cb6fc94eac605039de2aa50afc4c545a4dddc1e411acb'
            Format = 'tar.gz'
            ExecutableSuffix = ''
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX) -and $architecture -eq $arm64) {
        return [pscustomobject]@{
            Archive = "foundry_v${foundryVersion}_darwin_arm64.tar.gz"
            Sha256 = 'eacdc67718fac857cad9e19c7f6729dd80de731d09df81856391d093cfcab547'
            Format = 'tar.gz'
            ExecutableSuffix = ''
        }
    }

    throw "Unsupported operating system or architecture: $($runtime::OSDescription), $architecture."
}

$platformPackage = Get-PlatformPackage
$archiveName = $platformPackage.Archive
$downloadUri = "https://github.com/foundry-rs/foundry/releases/download/v${foundryVersion}/${archiveName}"
$expectedExecutables = @('forge', 'cast', 'anvil', 'chisel') |
    ForEach-Object { "$_$($platformPackage.ExecutableSuffix)" }

function Get-NativeOutput {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Native command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Test-FoundryInstall {
    param([Parameter(Mandatory)][string]$Directory)

    foreach ($name in $expectedExecutables) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) {
            return $false
        }
    }

    try {
        $forge = Join-Path $Directory $expectedExecutables[0]
        $versionOutput = Get-NativeOutput -FilePath $forge -Arguments @('--version')
        return $versionOutput -match '(^|[^0-9])1\.7\.1([^0-9]|$)'
    }
    catch {
        return $false
    }
}

function Add-FoundryToPath {
    param([Parameter(Mandatory)][string]$Directory)

    $existingEntries = $env:PATH -split [System.IO.Path]::PathSeparator
    $alreadyPresent = $existingEntries |
        Where-Object { $_.TrimEnd('/', '\') -ieq $Directory.TrimEnd('/', '\') } |
        Select-Object -First 1

    if ($null -eq $alreadyPresent) {
        $env:PATH = "$Directory$([System.IO.Path]::PathSeparator)$env:PATH"
    }

    # GitHub Actions imports this file into PATH for later workflow steps.
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PATH)) {
        [System.IO.File]::AppendAllText(
            $env:GITHUB_PATH,
            $Directory + [Environment]::NewLine,
            (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Invoke-VerifiedDownload {
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    # Retry with the documented crawler UA only when a normal public download fails
    # or returns incomplete content. A successful request still has to match SHA-256.
    $attempts = @(
        @{ Name = 'default client'; UserAgent = $null },
        @{ Name = 'OAI-SearchBot fallback'; UserAgent = 'OAI-SearchBot' }
    )

    $lastFailure = $null
    foreach ($attempt in $attempts) {
        try {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force
            }

            Write-Host "Downloading $archiveName with $($attempt.Name)..."
            if ($null -eq $attempt.UserAgent) {
                Invoke-WebRequest -Uri $Uri -OutFile $Destination -UseBasicParsing
            }
            else {
                Invoke-WebRequest -Uri $Uri -OutFile $Destination -UserAgent $attempt.UserAgent -UseBasicParsing
            }

            $actualSha256 = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
                throw "SHA-256 mismatch. Expected $ExpectedSha256, got $actualSha256."
            }

            return
        }
        catch {
            $lastFailure = $_
            Write-Warning "Download attempt failed: $($_.Exception.Message)"
        }
    }

    throw "Could not download a verified Foundry archive. Last failure: $lastFailure"
}

function Expand-VerifiedArchive {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Format
    )

    if ($Format -eq 'zip') {
        Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
        return
    }

    $tar = Get-Command tar -ErrorAction Stop
    & $tar.Source -xzf $Archive -C $Destination
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed to extract the verified archive (exit $LASTEXITCODE)."
    }
}

if (-not $Force -and (Test-FoundryInstall -Directory $installDirectory)) {
    if ($AddToPath) {
        Add-FoundryToPath -Directory $installDirectory
    }

    Write-Host "Foundry v$foundryVersion is already installed at: $installDirectory" -ForegroundColor Green
    Write-Host 'verify.ps1 uses this repository-local copy directly.'
    return
}

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ("payment-sandbox-foundry-" + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryRoot $archiveName
$expandedDirectory = Join-Path $temporaryRoot 'expanded'

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $expandedDirectory -Force | Out-Null

    Invoke-VerifiedDownload `
        -Uri $downloadUri `
        -Destination $archivePath `
        -ExpectedSha256 $platformPackage.Sha256
    Expand-VerifiedArchive `
        -Archive $archivePath `
        -Destination $expandedDirectory `
        -Format $platformPackage.Format

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    foreach ($name in $expectedExecutables) {
        $source = Get-ChildItem -LiteralPath $expandedDirectory -Filter $name -File -Recurse |
            Select-Object -First 1
        if ($null -eq $source) {
            throw "Verified archive did not contain expected executable: $name"
        }

        # Copy only the four known tools; do not recursively delete the install directory.
        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $installDirectory $name) -Force
    }

    if ($platformPackage.ExecutableSuffix -eq '') {
        # Copy-Item does not guarantee that Unix execute bits survive every archive/filesystem
        # combination. Set them explicitly before attempting to run forge.
        $chmod = Get-Command chmod -ErrorAction Stop
        $installedPaths = $expectedExecutables |
            ForEach-Object { Join-Path $installDirectory $_ }
        $chmodArguments = @('+x') + $installedPaths
        & $chmod.Source @chmodArguments
        if ($LASTEXITCODE -ne 0) {
            throw "chmod failed to mark the Foundry tools executable (exit $LASTEXITCODE)."
        }
    }

    if (-not (Test-FoundryInstall -Directory $installDirectory)) {
        throw 'Foundry files were copied, but the installed forge version is not v1.7.1.'
    }

    if ($AddToPath) {
        Add-FoundryToPath -Directory $installDirectory
    }

    Write-Host "Installed checksum-verified Foundry v${foundryVersion}:" -ForegroundColor Green
    Write-Host "  $installDirectory"
    Write-Host 'Run ./scripts/verify.ps1 from the repository root to build and test the project.'
}
finally {
    # Validate both the parent boundary and random prefix before recursive cleanup.
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $hasExpectedPrefix = [System.IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith(
        'payment-sandbox-foundry-',
        [System.StringComparison]::OrdinalIgnoreCase)
    $temporaryBoundary = $temporaryBase.TrimEnd([char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
    $isInsideTemporaryBase = $resolvedTemporaryRoot.StartsWith(
        $temporaryBoundary,
        [System.StringComparison]::OrdinalIgnoreCase)

    if ($hasExpectedPrefix -and $isInsideTemporaryBase -and (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
