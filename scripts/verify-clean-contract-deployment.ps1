[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 19545,

    # The executable is built from the real workspace before this wrapper
    # creates its source-only snapshot.  Passing it explicitly keeps the clean
    # replay free of bin/obj artifacts while still exercising Week 14.
    [string]$OrchestratorHarnessDll
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required for consistent path and process behavior.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$observationScript = Join-Path $PSScriptRoot 'observe-week2-transaction.ps1'
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ("payment-sandbox-clean-" + [guid]::NewGuid().ToString('N'))
$snapshotRoot = Join-Path $temporaryRoot 'source'
$expectedPrefix = 'payment-sandbox-clean-'

function Invoke-GitCaptureChecked {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git -C $repositoryRoot @Arguments 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($nativeExitCode -ne 0) {
        throw "Git command failed with exit code $nativeExitCode`: git -C $repositoryRoot $($Arguments -join ' ')"
    }

    return $output
}

function Remove-VerifiedSnapshot {
    $resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedBase = [System.IO.Path]::GetFullPath($temporaryBase)
    $temporaryBoundary = $resolvedBase.TrimEnd([char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
    $hasExpectedPrefix = [System.IO.Path]::GetFileName($resolvedRoot).StartsWith(
        $expectedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)
    $isInsideTemporaryBase = $resolvedRoot.StartsWith(
        $temporaryBoundary,
        [System.StringComparison]::OrdinalIgnoreCase)

    if (-not $hasExpectedPrefix -or -not $isInsideTemporaryBase) {
        throw "Refusing to remove unexpected clean-snapshot path: $resolvedRoot"
    }

    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}

foreach ($requiredPath in @($repositoryRoot, $observationScript, (Join-Path $repositoryRoot '.gitmodules'))) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required clean-deployment path is missing: $requiredPath"
    }
}
[void](Get-Command git -ErrorAction Stop)

# A leading space means the submodule is initialized at the recorded gitlink.
# '-', '+', or 'U' means missing, drifted, or conflicted source and must fail.
$directSubmodulePaths = @('contracts/lib/openzeppelin-contracts', 'contracts/lib/forge-std')
$submoduleStatus = @(Invoke-GitCaptureChecked -Arguments @('submodule', 'status'))
if ($submoduleStatus.Count -eq 0) {
    throw 'No initialized contract submodules were found.'
}
foreach ($line in $submoduleStatus) {
    if (-not "$line".StartsWith(' ', [System.StringComparison]::Ordinal)) {
        throw "Contract submodule is not at its recorded commit: $line"
    }
}
foreach ($submodulePath in $directSubmodulePaths) {
    $dirtyPaths = @(Invoke-GitCaptureChecked -Arguments @(
            '-C', $submodulePath, 'status', '--porcelain', '--untracked-files=all'
        ))
    if ($dirtyPaths.Count -ne 0) {
        throw "Contract submodule working tree is not clean: $submodulePath"
    }
}

# The snapshot deliberately contains only paths known to Git. Requiring new
# source/scripts to be staged prevents an untracked dependency from making the
# normal workspace pass while silently disappearing from the clean replay.
$untrackedCriticalPaths = @(
    Invoke-GitCaptureChecked -Arguments @(
        'ls-files', '--others', '--exclude-standard', '--',
        'contracts/src', 'contracts/test', 'contracts/script', 'scripts'
    )
)
if ($untrackedCriticalPaths.Count -ne 0) {
    throw "Stage new contract or verification files before clean replay: $($untrackedCriticalPaths -join ', ')"
}

$operationError = $null
$cleanupError = $null
$trackedFileCount = 0
try {
    New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null

    # NUL-delimited names preserve spaces and other ordinary path characters.
    # Expand only this repository's two direct contract dependencies. Their own
    # optional test gitlinks are unrelated to PaymentRouter compilation and are
    # deliberately excluded instead of becoming hidden network prerequisites.
    $trackedPaths = [System.Collections.Generic.List[string]]::new()
    $rootTrackedRaw = [string]::Concat((Invoke-GitCaptureChecked -Arguments @('ls-files', '-z')))
    foreach ($rootPath in $rootTrackedRaw.Split([char]0, [System.StringSplitOptions]::RemoveEmptyEntries)) {
        if ($rootPath -notin $directSubmodulePaths) {
            $trackedPaths.Add($rootPath)
        }
    }

    foreach ($submodulePath in $directSubmodulePaths) {
        $submoduleTrackedRaw = [string]::Concat((Invoke-GitCaptureChecked -Arguments @(
                    '-C', $submodulePath, 'ls-files', '-z'
                )))
        foreach ($submoduleRelativePath in $submoduleTrackedRaw.Split(
                [char]0,
                [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $sourceCandidate = Join-Path $repositoryRoot (Join-Path $submodulePath $submoduleRelativePath)
            if (Test-Path -LiteralPath $sourceCandidate -PathType Leaf) {
                $trackedPaths.Add("$submodulePath/$submoduleRelativePath")
                continue
            }

            $stageEntry = [string]::Concat((Invoke-GitCaptureChecked -Arguments @(
                        '-C', $submodulePath, 'ls-files', '--stage', '--', $submoduleRelativePath
                    )))
            if ($stageEntry -notmatch '^160000 ') {
                throw "Tracked dependency file is missing: $sourceCandidate"
            }
        }
    }

    if ($trackedPaths.Count -eq 0) {
        throw 'Git returned no tracked files for the clean snapshot.'
    }

    $repositoryBoundary = $repositoryRoot.TrimEnd([char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($relativeGitPath in $trackedPaths) {
        $relativePath = $relativeGitPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
        if (-not $sourcePath.StartsWith($repositoryBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Git returned a path outside the repository: $relativeGitPath"
        }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Tracked snapshot file is missing: $sourcePath"
        }

        $destinationPath = Join-Path $snapshotRoot $relativePath
        $destinationDirectory = [System.IO.Path]::GetDirectoryName($destinationPath)
        [void][System.IO.Directory]::CreateDirectory($destinationDirectory)
        [System.IO.File]::Copy($sourcePath, $destinationPath, $true)
        $trackedFileCount += 1
    }

    Write-Host "Replaying deployment from $trackedFileCount tracked files in an isolated directory..." -ForegroundColor Cyan

    # The observer uses the original repository's checksum-verified Foundry
    # binaries, but Forge compiles and broadcasts only the isolated source root.
    & $observationScript `
        -Port $Port `
        -SourceRoot $snapshotRoot `
        -OrchestratorHarnessDll $OrchestratorHarnessDll
}
catch {
    $operationError = $_
}
finally {
    try {
        Remove-VerifiedSnapshot
    }
    catch {
        $cleanupError = $_
    }
}

if ($null -ne $operationError -and $null -ne $cleanupError) {
    throw [System.AggregateException]::new(
        'Clean deployment replay and temporary-directory cleanup both failed.',
        [System.Exception[]]@($operationError.Exception, $cleanupError.Exception))
}
if ($null -ne $operationError) {
    throw $operationError
}
if ($null -ne $cleanupError) {
    throw $cleanupError
}

Write-Host 'Clean tracked-source deployment replay: PASSED' -ForegroundColor Green
Write-Host "  trackedFileCount        : $trackedFileCount"
Write-Host '  temporaryDirectoryCleaned: true'
