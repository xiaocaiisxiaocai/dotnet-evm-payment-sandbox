[CmdletBinding()]
param(
    # Offline runs may opt out, but such a run is not Gate A evidence.
    [switch]$SkipSecretScan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Windows PowerShell 5.1 can otherwise negotiate an obsolete TLS version.
[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'PaymentSandbox.slnx'
$contractsDirectory = Join-Path $repositoryRoot 'contracts'
$gitleaksVersion = '8.29.1'
$runtime = [System.Runtime.InteropServices.RuntimeInformation]
$runningOnWindows = $runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$foundryExecutable = if ($runningOnWindows) { 'forge.exe' } else { 'forge' }
$forgePath = Join-Path $repositoryRoot ".tools/foundry/v1.7.1/$foundryExecutable"

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        # Windows PowerShell 5.1 maps native stderr to non-terminating error records.
        # Let the process finish, then use its exit code as the source of truth.
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $FilePath @Arguments
            $nativeExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($nativeExitCode -ne 0) {
            throw "Command failed with exit code $nativeExitCode`: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-GitleaksPlatformPackage {
    $architecture = $runtime::OSArchitecture
    $x64 = [System.Runtime.InteropServices.Architecture]::X64
    $arm64 = [System.Runtime.InteropServices.Architecture]::Arm64

    if ($runningOnWindows -and $architecture -eq $x64) {
        return [pscustomobject]@{
            Archive = "gitleaks_${gitleaksVersion}_windows_x64.zip"
            Sha256 = 'e4b7d556f0cddbe23d10d8fac2ab0f29f68f019091c6599ffbeaa8a4fb71ac78'
            Format = 'zip'
            Executable = 'gitleaks.exe'
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux) -and $architecture -eq $x64) {
        return [pscustomobject]@{
            Archive = "gitleaks_${gitleaksVersion}_linux_x64.tar.gz"
            Sha256 = 'e4eb209d04e20339d77122a3bdf9cd41351255cfb27ebcb75e85325e04f88924'
            Format = 'tar.gz'
            Executable = 'gitleaks'
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux) -and $architecture -eq $arm64) {
        return [pscustomobject]@{
            Archive = "gitleaks_${gitleaksVersion}_linux_arm64.tar.gz"
            Sha256 = '691f826ce7c1c564c9c02d0f9025e8e70803e3816707a4be6224408a06a81eaa'
            Format = 'tar.gz'
            Executable = 'gitleaks'
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX) -and $architecture -eq $x64) {
        return [pscustomobject]@{
            Archive = "gitleaks_${gitleaksVersion}_darwin_x64.tar.gz"
            Sha256 = '2cd739c684bf3f543f4f37774075c276e40a72bb16c4c5bb9dfd27bf4a4465a7'
            Format = 'tar.gz'
            Executable = 'gitleaks'
        }
    }

    if ($runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX) -and $architecture -eq $arm64) {
        return [pscustomobject]@{
            Archive = "gitleaks_${gitleaksVersion}_darwin_arm64.tar.gz"
            Sha256 = '69836c841d7e648fb30ff4846f8c3587855c5754ed02b8510caaf6008f65d177'
            Format = 'tar.gz'
            Executable = 'gitleaks'
        }
    }

    throw "Unsupported Gitleaks platform: $($runtime::OSDescription), $architecture."
}

function Get-VerifiedGitleaks {
    $package = Get-GitleaksPlatformPackage
    $archiveName = $package.Archive
    $downloadUri = "https://github.com/gitleaks/gitleaks/releases/download/v${gitleaksVersion}/${archiveName}"
    $temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $temporaryRoot = Join-Path $temporaryBase ("payment-sandbox-gitleaks-" + [guid]::NewGuid().ToString('N'))
    $archivePath = Join-Path $temporaryRoot $archiveName

    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    try {
        $downloadSucceeded = $false
        $lastFailure = $null
        $attempts = @(
            @{ Name = 'default client'; UserAgent = $null },
            @{ Name = 'OAI-SearchBot fallback'; UserAgent = 'OAI-SearchBot' }
        )

        foreach ($attempt in $attempts) {
            try {
                if (Test-Path -LiteralPath $archivePath) {
                    Remove-Item -LiteralPath $archivePath -Force
                }

                Write-Host "Downloading Gitleaks with $($attempt.Name)..."
                if ($null -eq $attempt.UserAgent) {
                    Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath -UseBasicParsing
                }
                else {
                    Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath -UserAgent $attempt.UserAgent -UseBasicParsing
                }

                $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualSha256 -ne $package.Sha256) {
                    throw "Gitleaks archive SHA-256 mismatch. Expected $($package.Sha256), got $actualSha256."
                }

                $downloadSucceeded = $true
                break
            }
            catch {
                $lastFailure = $_
                Write-Warning "Download attempt failed: $($_.Exception.Message)"
            }
        }

        if (-not $downloadSucceeded) {
            throw "Could not download a verified Gitleaks archive. Last failure: $lastFailure"
        }

        if ($package.Format -eq 'zip') {
            Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryRoot -Force
        }
        else {
            $tar = Get-Command tar -ErrorAction Stop
            & $tar.Source -xzf $archivePath -C $temporaryRoot $package.Executable
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed to extract Gitleaks (exit $LASTEXITCODE)."
            }
        }

        $executable = Join-Path $temporaryRoot $package.Executable
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Verified Gitleaks archive did not contain $($package.Executable)."
        }

        return [pscustomobject]@{
            Executable = $executable
            TemporaryRoot = $temporaryRoot
            TemporaryBase = $temporaryBase
        }
    }
    catch {
        Remove-VerifiedTemporaryDirectory `
            -Directory $temporaryRoot `
            -TemporaryBase $temporaryBase `
            -ExpectedPrefix 'payment-sandbox-gitleaks-'
        throw
    }
}

function Remove-VerifiedTemporaryDirectory {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$TemporaryBase,
        [Parameter(Mandatory)][string]$ExpectedPrefix
    )

    $resolvedDirectory = [System.IO.Path]::GetFullPath($Directory)
    $resolvedBase = [System.IO.Path]::GetFullPath($TemporaryBase)
    $hasExpectedPrefix = [System.IO.Path]::GetFileName($resolvedDirectory).StartsWith(
        $ExpectedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)
    $temporaryBoundary = $resolvedBase.TrimEnd([char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
    $isInsideTemporaryBase = $resolvedDirectory.StartsWith(
        $temporaryBoundary,
        [System.StringComparison]::OrdinalIgnoreCase)

    if (-not $hasExpectedPrefix -or -not $isInsideTemporaryBase) {
        throw "Refusing to remove unexpected temporary path: $resolvedDirectory"
    }

    if (Test-Path -LiteralPath $resolvedDirectory) {
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}

foreach ($requiredPath in @($solutionPath, $contractsDirectory, (Join-Path $repositoryRoot '.gitleaks.toml'))) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required repository path is missing: $requiredPath"
    }
}

Write-Step 'Verify exact .NET SDK'
$dotnetCommand = Get-Command dotnet -ErrorAction Stop
Push-Location $repositoryRoot
try {
    $dotnetVersion = ((& $dotnetCommand.Source --version 2>&1) -join [Environment]::NewLine).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet --version failed. Install the SDK pinned by global.json.'
    }
}
finally {
    Pop-Location
}

if ($dotnetVersion -ne '10.0.400') {
    throw "Expected .NET SDK 10.0.400, got '$dotnetVersion'. Do not loosen global.json; install the exact SDK."
}

Write-Step 'Restore the committed NuGet graph in locked mode'
Invoke-NativeChecked -FilePath $dotnetCommand.Source `
    -Arguments @('restore', $solutionPath, '--locked-mode') `
    -WorkingDirectory $repositoryRoot

Write-Step 'Build .NET solution'
Invoke-NativeChecked -FilePath $dotnetCommand.Source `
    -Arguments @('build', $solutionPath, '--configuration', 'Release', '--no-restore') `
    -WorkingDirectory $repositoryRoot

Write-Step 'Run .NET tests'
Invoke-NativeChecked -FilePath $dotnetCommand.Source `
    -Arguments @('test', $solutionPath, '--configuration', 'Release', '--no-build', '--no-restore') `
    -WorkingDirectory $repositoryRoot

Write-Step 'Verify repository-local Foundry v1.7.1'
if (-not (Test-Path -LiteralPath $forgePath -PathType Leaf)) {
    throw 'Foundry is not installed in .tools. Run .\scripts\install-foundry.ps1 first.'
}

$forgeVersion = ((& $forgePath --version 2>&1) -join [Environment]::NewLine).Trim()
if ($LASTEXITCODE -ne 0 -or $forgeVersion -notmatch '(^|[^0-9])1\.7\.1([^0-9]|$)') {
    throw "Expected repository-local Foundry v1.7.1. Actual output: $forgeVersion"
}
Write-Host $forgeVersion

Write-Step 'Check Solidity formatting'
Invoke-NativeChecked -FilePath $forgePath -Arguments @('fmt', '--check') -WorkingDirectory $contractsDirectory

Write-Step 'Build Solidity contracts'
Invoke-NativeChecked -FilePath $forgePath -Arguments @('build', '--sizes') -WorkingDirectory $contractsDirectory

Write-Step 'Run Solidity tests'
Invoke-NativeChecked -FilePath $forgePath -Arguments @('test', '-vvv') -WorkingDirectory $contractsDirectory

if ($SkipSecretScan) {
    Write-Warning 'Secret scan was explicitly skipped. This run is not sufficient evidence for Gate A.'
}
else {
    Write-Step 'Download and verify Gitleaks 8.29.1'
    $gitleaks = Get-VerifiedGitleaks
    try {
        $gitleaksVersionOutput = ((& $gitleaks.Executable version 2>&1) -join [Environment]::NewLine).Trim()
        if ($LASTEXITCODE -ne 0 -or $gitleaksVersionOutput -notmatch '(^|[^0-9])8\.29\.1([^0-9]|$)') {
            throw "Expected Gitleaks 8.29.1. Actual output: $gitleaksVersionOutput"
        }

        Write-Step 'Prove the scanner detects a dynamic synthetic secret'
        # Scan a directory because that is the stable input contract of `gitleaks dir`.
        $canaryDirectory = Join-Path $gitleaks.TemporaryRoot 'canary-input'
        New-Item -ItemType Directory -Path $canaryDirectory -Force | Out-Null
        $canaryPath = Join-Path $canaryDirectory 'canary.txt'
        $canaryLabel = 'private_key'
        $canaryPartOne = '0123456789abcdef0123456789abcdef'
        $canaryPartTwo = 'fedcba9876543210fedcba9876543210'
        $canaryText = "$canaryLabel = `"0x$canaryPartOne$canaryPartTwo`""
        [System.IO.File]::WriteAllText(
            $canaryPath,
            $canaryText,
            (New-Object System.Text.UTF8Encoding($false)))

        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $gitleaks.Executable dir $canaryDirectory `
                --config (Join-Path $repositoryRoot '.gitleaks.toml') `
                --redact `
                --no-banner `
                --exit-code 23 1>$null 2>$null
            $canaryExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($canaryExitCode -ne 23) {
            throw "Secret scanner canary was not detected, or the scanner failed (exit $canaryExitCode)."
        }

        Write-Step 'Scan current working tree'
        Invoke-NativeChecked -FilePath $gitleaks.Executable `
            -Arguments @('dir', $repositoryRoot, '--config', (Join-Path $repositoryRoot '.gitleaks.toml'), '--redact', '--no-banner', '--verbose') `
            -WorkingDirectory $repositoryRoot

        # A brand-new repository has no HEAD. Its working tree is still scanned above.
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & git -C $repositoryRoot rev-parse --verify HEAD 1>$null 2>$null
            $hasGitHead = $LASTEXITCODE -eq 0
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($hasGitHead) {
            Write-Step 'Scan complete Git history'
            Invoke-NativeChecked -FilePath $gitleaks.Executable `
                -Arguments @('git', $repositoryRoot, '--config', (Join-Path $repositoryRoot '.gitleaks.toml'), '--redact', '--no-banner', '--verbose') `
                -WorkingDirectory $repositoryRoot
        }
        else {
            Write-Warning 'No Git commit exists yet; complete-history scanning starts after the first commit.'
        }
    }
    finally {
        Remove-VerifiedTemporaryDirectory `
            -Directory $gitleaks.TemporaryRoot `
            -TemporaryBase $gitleaks.TemporaryBase `
            -ExpectedPrefix 'payment-sandbox-gitleaks-'
    }
}

Write-Host "`nAll requested local checks passed." -ForegroundColor Green
