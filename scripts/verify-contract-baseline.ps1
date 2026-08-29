[CmdletBinding()]
param(
    [string]$AbiPath,
    [string]$BaselinePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required for consistent JSON behavior across platforms.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$contractsDirectory = Join-Path $repositoryRoot 'contracts'
$runtime = [System.Runtime.InteropServices.RuntimeInformation]
$runningOnWindows = $runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$executableSuffix = if ($runningOnWindows) { '.exe' } else { '' }
$foundryDirectory = Join-Path $repositoryRoot '.tools/foundry/v1.7.1'
$forgePath = Join-Path $foundryDirectory "forge$executableSuffix"
$castPath = Join-Path $foundryDirectory "cast$executableSuffix"
$contractIdentifier = 'src/PaymentRouter.sol:PaymentRouter'

if ([string]::IsNullOrWhiteSpace($AbiPath)) {
    $AbiPath = Join-Path $contractsDirectory 'abi/PaymentRouter.json'
}
if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $contractsDirectory 'baselines/PaymentRouter.v1.json'
}

$AbiPath = [System.IO.Path]::GetFullPath($AbiPath)
$BaselinePath = [System.IO.Path]::GetFullPath($BaselinePath)

function Invoke-NativeCaptureChecked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        # Forge writes some normal diagnostics to stderr. Capture both streams,
        # then let the native exit code determine whether the command succeeded.
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = @(& $FilePath @Arguments 2>&1)
            $nativeExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($nativeExitCode -ne 0) {
            throw "Command failed with exit code $nativeExitCode`: $FilePath $($Arguments -join ' ')"
        }

        return (($output | ForEach-Object { "$_" }) -join [Environment]::NewLine).Trim()
    }
    finally {
        Pop-Location
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if ("$Actual" -cne "$Expected") {
        throw "Contract baseline drift: $Label. Expected '$Expected', got '$Actual'."
    }
}

function ConvertTo-ComparableJson {
    param([Parameter(Mandatory)]$Value)

    return ($Value | ConvertTo-Json -Depth 100 -Compress)
}

function Assert-JsonEquivalent {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    $actualJson = ConvertTo-ComparableJson -Value $Actual
    $expectedJson = ConvertTo-ComparableJson -Value $Expected
    if ($actualJson -cne $expectedJson) {
        throw "Contract baseline drift: $Label differs from the reviewed JSON."
    }
}

foreach ($requiredPath in @(
        $contractsDirectory,
        $forgePath,
        $castPath,
        $AbiPath,
        $BaselinePath,
        (Join-Path $contractsDirectory 'lib/openzeppelin-contracts/package.json'),
        (Join-Path $contractsDirectory 'lib/forge-std/package.json')
    )) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required contract-baseline path is missing: $requiredPath"
    }
}

$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json -Depth 100
$reviewedAbi = Get-Content -LiteralPath $AbiPath -Raw | ConvertFrom-Json -Depth 100
Assert-Equal -Actual $baseline.schemaVersion -Expected 1 -Label 'baseline schemaVersion'
Assert-Equal -Actual $baseline.contract -Expected $contractIdentifier -Label 'contract identifier'

$forgeVersion = Invoke-NativeCaptureChecked -FilePath $forgePath -Arguments @('--version') -WorkingDirectory $repositoryRoot
if ($forgeVersion -notmatch '(^|[^0-9])1\.7\.1([^0-9]|$)') {
    throw "Contract baseline drift: expected Foundry v1.7.1. Actual output: $forgeVersion"
}
if ($forgeVersion -notmatch '(?m)^Commit SHA:\s*([0-9a-fA-F]{40})\s*$') {
    throw "Contract baseline drift: Foundry output did not contain a commit SHA. Actual output: $forgeVersion"
}
Assert-Equal -Actual $baseline.toolchain.foundry.version -Expected '1.7.1' -Label 'reviewed Foundry version'
Assert-Equal `
    -Actual $Matches[1].ToLowerInvariant() `
    -Expected $baseline.toolchain.foundry.commit `
    -Label 'Foundry release commit'

$configText = Invoke-NativeCaptureChecked `
    -FilePath $forgePath `
    -Arguments @('config', '--root', $contractsDirectory, '--json') `
    -WorkingDirectory $repositoryRoot
$config = $configText | ConvertFrom-Json -Depth 100
Assert-Equal -Actual $config.solc -Expected $baseline.toolchain.solc -Label 'Solidity compiler version'
Assert-Equal -Actual $config.evm_version -Expected $baseline.toolchain.evmVersion -Label 'EVM version'
Assert-Equal -Actual $config.optimizer -Expected $baseline.toolchain.optimizerEnabled -Label 'optimizer enabled'
Assert-Equal -Actual $config.optimizer_runs -Expected $baseline.toolchain.optimizerRuns -Label 'optimizer runs'
Assert-Equal -Actual $config.bytecode_hash -Expected $baseline.toolchain.bytecodeHash -Label 'metadata bytecode hash mode'

foreach ($dependency in @(
        @{
            Label = 'OpenZeppelin Contracts'
            Directory = Join-Path $contractsDirectory 'lib/openzeppelin-contracts'
            Expected = $baseline.toolchain.openzeppelinContracts
        },
        @{
            Label = 'forge-std'
            Directory = Join-Path $contractsDirectory 'lib/forge-std'
            Expected = $baseline.toolchain.forgeStd
        }
    )) {
    $package = Get-Content -LiteralPath (Join-Path $dependency.Directory 'package.json') -Raw | ConvertFrom-Json
    $commit = Invoke-NativeCaptureChecked `
        -FilePath 'git' `
        -Arguments @('-C', $dependency.Directory, 'rev-parse', 'HEAD') `
        -WorkingDirectory $repositoryRoot
    $dependencyStatus = Invoke-NativeCaptureChecked `
        -FilePath 'git' `
        -Arguments @('-C', $dependency.Directory, 'status', '--porcelain', '--untracked-files=all') `
        -WorkingDirectory $repositoryRoot
    Assert-Equal -Actual $package.version -Expected $dependency.Expected.version -Label "$($dependency.Label) package version"
    Assert-Equal -Actual $commit.ToLowerInvariant() -Expected $dependency.Expected.commit -Label "$($dependency.Label) commit"
    if (-not [string]::IsNullOrWhiteSpace($dependencyStatus)) {
        throw "Contract baseline drift: $($dependency.Label) submodule working tree is not clean."
    }
}

$actualAbiText = Invoke-NativeCaptureChecked `
    -FilePath $forgePath `
    -Arguments @('inspect', '--root', $contractsDirectory, $contractIdentifier, 'abi', '--json') `
    -WorkingDirectory $repositoryRoot
$actualAbi = $actualAbiText | ConvertFrom-Json -Depth 100
Assert-JsonEquivalent -Actual $actualAbi -Expected $reviewedAbi -Label 'PaymentRouter ABI'

foreach ($interfaceField in @(
        @{ Inspect = 'methodIdentifiers'; Baseline = 'methodIdentifiers'; Label = 'function selectors' },
        @{ Inspect = 'errors'; Baseline = 'errors'; Label = 'error selectors' },
        @{ Inspect = 'events'; Baseline = 'events'; Label = 'event topics' }
    )) {
    $actualText = Invoke-NativeCaptureChecked `
        -FilePath $forgePath `
        -Arguments @('inspect', '--root', $contractsDirectory, $contractIdentifier, $interfaceField.Inspect, '--json') `
        -WorkingDirectory $repositoryRoot
    $actual = $actualText | ConvertFrom-Json -Depth 100
    $expected = $baseline.interface.($interfaceField.Baseline)
    Assert-JsonEquivalent -Actual $actual -Expected $expected -Label $interfaceField.Label
}

$storageText = Invoke-NativeCaptureChecked `
    -FilePath $forgePath `
    -Arguments @('inspect', '--root', $contractsDirectory, $contractIdentifier, 'storage-layout', '--json') `
    -WorkingDirectory $repositoryRoot
$storageLayout = $storageText | ConvertFrom-Json -Depth 100
Assert-JsonEquivalent -Actual $storageLayout -Expected $baseline.storageLayout -Label 'storage layout'

$runtimeBytecode = Invoke-NativeCaptureChecked `
    -FilePath $forgePath `
    -Arguments @('inspect', '--root', $contractsDirectory, $contractIdentifier, 'deployedBytecode') `
    -WorkingDirectory $repositoryRoot
if ($runtimeBytecode -notmatch '^0x[0-9a-f]+$' -or (($runtimeBytecode.Length - 2) % 2) -ne 0) {
    throw 'Forge returned malformed PaymentRouter runtime bytecode.'
}

$runtimeSize = [int](($runtimeBytecode.Length - 2) / 2)
$runtimeHash = Invoke-NativeCaptureChecked `
    -FilePath $castPath `
    -Arguments @('keccak', $runtimeBytecode) `
    -WorkingDirectory $repositoryRoot
Assert-Equal -Actual $runtimeSize -Expected $baseline.runtime.sizeBytes -Label 'runtime byte size'
Assert-Equal -Actual $runtimeHash.ToLowerInvariant() -Expected $baseline.runtime.keccak256 -Label 'runtime bytecode Keccak-256'

Write-Host 'PaymentRouter v1 contract baseline: PASSED' -ForegroundColor Green
Write-Host "  abi                     : $AbiPath"
Write-Host "  runtimeSizeBytes        : $runtimeSize"
Write-Host "  runtimeBytecodeKeccak256: $runtimeHash"
Write-Host "  storageSlots            : $(@($storageLayout.storage).Count)"
