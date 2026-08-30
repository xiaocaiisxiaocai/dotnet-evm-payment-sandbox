[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 8545,

    # Week 4 can replay the same observation against an isolated tracked-file
    # snapshot while continuing to use this repository's verified tool install.
    [string]$SourceRoot,

    # Week 14 may add a separately built .NET harness.  The observer supplies
    # only public deployment facts; the harness creates and owns its temporary
    # signing key, and never returns raw signed bytes to this script.
    [string]$OrchestratorHarnessDll
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required for consistent process and JSON behavior across platforms.'
}

$toolRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $toolRepositoryRoot
}
else {
    [System.IO.Path]::GetFullPath($SourceRoot)
}
$contractsDirectory = Join-Path $repositoryRoot 'contracts'
$runtime = [System.Runtime.InteropServices.RuntimeInformation]
$runningOnWindows = $runtime::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$executableSuffix = if ($runningOnWindows) { '.exe' } else { '' }
$foundryDirectory = Join-Path $toolRepositoryRoot '.tools/foundry/v1.7.1'
$forgePath = Join-Path $foundryDirectory "forge$executableSuffix"
$castPath = Join-Path $foundryDirectory "cast$executableSuffix"
$anvilPath = Join-Path $foundryDirectory "anvil$executableSuffix"
$rpcUrl = "http://127.0.0.1:$Port"
$script:rpcRequestId = 0
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$expectedChainId = [System.Numerics.BigInteger]::Parse('31337')
$paymentAmount = [System.Numerics.BigInteger]::Parse('1250000')
$explicitFailureGasLimit = [System.Numerics.BigInteger]::Parse('300000')
$nativeCommandTimeoutSeconds = 180
$receiptTimeoutSeconds = 30

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-HexEqual {
    param(
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if (-not $Actual.Equals($Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Assertion failed: $Label. Expected '$Expected', got '$Actual'."
    }
}

function ConvertFrom-RpcQuantity {
    param([Parameter(Mandatory)][string]$HexValue)

    if ($HexValue -notmatch '^0x[0-9a-fA-F]+$') {
        throw "Invalid JSON-RPC quantity: '$HexValue'."
    }

    $digits = $HexValue.Substring(2)
    # Prefixing zero keeps values whose highest bit is set positive when .NET
    # parses hexadecimal BigInteger text using two's-complement semantics.
    return [System.Numerics.BigInteger]::Parse(
        "0$digits",
        [System.Globalization.NumberStyles]::AllowHexSpecifier,
        [System.Globalization.CultureInfo]::InvariantCulture)
}

function ConvertFrom-AbiWord {
    param([Parameter(Mandatory)][string]$Word)

    if ($Word -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Invalid 32-byte ABI word: '$Word'."
    }

    return [System.Numerics.BigInteger]::Parse(
        "0$Word",
        [System.Globalization.NumberStyles]::AllowHexSpecifier,
        [System.Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-RpcQuantity {
    param([Parameter(Mandatory)][System.Numerics.BigInteger]$Value)

    if ($Value -lt [System.Numerics.BigInteger]::Zero) {
        throw 'JSON-RPC quantities cannot be negative.'
    }

    if ($Value.IsZero) {
        return '0x0'
    }

    return '0x' + $Value.ToString('x', [System.Globalization.CultureInfo]::InvariantCulture).TrimStart('0')
}

function Normalize-Address {
    param(
        [Parameter(Mandatory)][string]$Address,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Address -notmatch '^0x[0-9a-fA-F]{40}$') {
        throw "Invalid $Label address: '$Address'."
    }

    return $Address.ToLowerInvariant()
}

function ConvertFrom-IndexedAddress {
    param(
        [Parameter(Mandatory)][string]$Topic,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Topic -notmatch '^0x0{24}[0-9a-fA-F]{40}$') {
        throw "Invalid indexed $Label address topic: '$Topic'."
    }

    return Normalize-Address -Address ("0x" + $Topic.Substring($Topic.Length - 40)) -Label $Label
}

function Test-PortIsAvailable {
    param([Parameter(Mandatory)][int]$CandidatePort)

    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Parse('127.0.0.1'),
        $CandidatePort)

    try {
        # Binding the same endpoint Anvil will use detects listeners on loopback
        # and wildcard IPv4 addresses without contacting or trusting that service.
        $listener.Start()
    }
    catch {
        throw "Port $CandidatePort is already occupied or unavailable. Refusing to reuse an existing RPC service."
    }
    finally {
        $listener.Stop()
    }
}

function Stop-ProcessWithin {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$Label,
        [int]$TimeoutMilliseconds = 5000
    )

    if ($Process.HasExited) {
        return
    }

    $treeKillFailure = $null
    try {
        # PowerShell 7 runs on a .NET runtime that supports terminating the
        # descendants of a process. The root-only fallback is defensive for an
        # OS-level tree-enumeration failure; all current commands are direct
        # repository-local binaries and Anvil does not create a helper process.
        $Process.Kill($true)
    }
    catch {
        $treeKillFailure = $_.Exception.Message
        try {
            $Process.Kill()
        }
        catch {
            throw "$Label could not be terminated. Tree kill failed: $treeKillFailure. Root kill failed: $($_.Exception.Message)"
        }
    }

    if (-not $Process.WaitForExit($TimeoutMilliseconds)) {
        $detail = if ($null -eq $treeKillFailure) { '' } else { " Tree kill failed first: $treeKillFailure." }
        throw "$Label did not stop within $TimeoutMilliseconds milliseconds.$detail"
    }
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [int]$TimeoutSeconds = 60
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['NO_COLOR'] = '1'
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start native command: $FilePath"
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-ProcessWithin -Process $process -Label "Native command '$FilePath'"
            throw "Native command timed out after $TimeoutSeconds seconds: $FilePath $($Arguments -join ' ')"
        }

        $streamTasks = [System.Threading.Tasks.Task[]]@($standardOutputTask, $standardErrorTask)
        if (-not [System.Threading.Tasks.Task]::WaitAll($streamTasks, 5000)) {
            throw "Native command exited but its output streams did not close within five seconds: $FilePath"
        }
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult().Trim()
        $standardError = $standardErrorTask.GetAwaiter().GetResult().Trim()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        $diagnostic = (@($standardOutput, $standardError) | Where-Object { $_ }) -join [Environment]::NewLine
        if ($diagnostic.Length -gt 6000) {
            $diagnostic = $diagnostic.Substring($diagnostic.Length - 6000)
        }
        throw "Native command failed with exit code $exitCode`: $FilePath $($Arguments -join ' ')`n$diagnostic"
    }

    return $standardOutput
}

function Start-TemporaryAnvil {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][int]$ListenPort
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $contractsDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['NO_COLOR'] = '1'
    foreach ($argument in @('--host', '127.0.0.1', '--port', "$ListenPort", '--chain-id', '31337', '--quiet')) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $processStarted = $false
    try {
        if (-not $process.Start()) {
            throw 'Could not start repository-local Anvil.'
        }
        $processStarted = $true

        # Anvil normally prints development private keys. Both streams remain
        # redirected and drained so secrets never reach this script's output.
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        return [pscustomobject]@{
            Process = $process
            StandardOutputTask = $standardOutputTask
            StandardErrorTask = $standardErrorTask
        }
    }
    catch {
        # The caller cannot run its finally block until a handle is returned.
        # Clean up here if handle construction itself fails after process start.
        $startupError = $_
        $cleanupError = $null
        if ($processStarted -and -not $process.HasExited) {
            try {
                Stop-ProcessWithin -Process $process -Label 'Anvil during startup cleanup'
            }
            catch {
                $cleanupError = $_
            }
        }
        $process.Dispose()
        if ($null -ne $cleanupError) {
            throw [System.AggregateException]::new(
                'Anvil startup and cleanup both failed.',
                [System.Exception[]]@($startupError.Exception, $cleanupError.Exception))
        }
        throw $startupError
    }
}

function Stop-TemporaryAnvil {
    param([Parameter(Mandatory)]$Handle)

    $process = $Handle.Process
    try {
        if (-not $process.HasExited) {
            Stop-ProcessWithin -Process $process -Label "Anvil process $($process.Id)"
        }

        # Observe the tasks so redirected buffers are released. Their content is
        # intentionally discarded because Anvil startup text contains test keys.
        $streamTasks = [System.Threading.Tasks.Task[]]@($Handle.StandardOutputTask, $Handle.StandardErrorTask)
        if (-not [System.Threading.Tasks.Task]::WaitAll($streamTasks, 5000)) {
            throw 'Anvil stopped but its output streams did not close within five seconds.'
        }
        [void]$Handle.StandardOutputTask.GetAwaiter().GetResult()
        [void]$Handle.StandardErrorTask.GetAwaiter().GetResult()
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-JsonRpc {
    param(
        [Parameter(Mandatory)][string]$Method,
        [object[]]$Parameters = @(),
        [int]$TimeoutSeconds = 5
    )

    $script:rpcRequestId += 1
    $request = [ordered]@{
        jsonrpc = '2.0'
        id = $script:rpcRequestId
        method = $Method
        params = @($Parameters)
    }
    $body = $request | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod `
        -Uri $rpcUrl `
        -Method Post `
        -ContentType 'application/json' `
        -Body $body `
        -TimeoutSec $TimeoutSeconds

    if ($response.PSObject.Properties.Name -contains 'error') {
        $rpcError = $response.error
        throw "JSON-RPC $Method failed ($($rpcError.code)): $($rpcError.message)"
    }
    if (-not ($response.PSObject.Properties.Name -contains 'result')) {
        throw "JSON-RPC $Method returned neither result nor error."
    }

    return $response.result
}

function Wait-ForAnvil {
    param(
        [Parameter(Mandatory)]$Handle,
        [int]$TimeoutSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastFailure = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Handle.Process.HasExited) {
            throw "Anvil exited before becoming ready (exit $($Handle.Process.ExitCode))."
        }

        try {
            $clientVersion = Invoke-JsonRpc -Method 'web3_clientVersion' -TimeoutSeconds 2
            if ($clientVersion -match '^anvil/v1\.7\.1(?:$|/)') {
                # The port probe and process bind cannot be atomic. Give a losing
                # Anvil process time to report a bind failure, then confirm both
                # the owned child and its RPC identity before trusting the port.
                Start-Sleep -Milliseconds 100
                if ($Handle.Process.HasExited) {
                    throw "Anvil exited during RPC ownership confirmation (exit $($Handle.Process.ExitCode))."
                }
                $confirmedVersion = Invoke-JsonRpc -Method 'web3_clientVersion' -TimeoutSeconds 2
                if ($Handle.Process.HasExited -or $confirmedVersion -ne $clientVersion) {
                    throw 'The RPC endpoint is not stably owned by the Anvil process started by this script.'
                }
                return $clientVersion
            }
            $lastFailure = "Unexpected RPC client version: '$clientVersion'."
        }
        catch {
            $lastFailure = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Anvil did not become ready within $TimeoutSeconds seconds. Last RPC error: $lastFailure"
}

function Wait-ForTransactionReceipt {
    param(
        [Parameter(Mandatory)][string]$TransactionHash,
        [int]$TimeoutSeconds = $receiptTimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $receipt = Invoke-JsonRpc -Method 'eth_getTransactionReceipt' -Parameters @($TransactionHash)
        if ($null -ne $receipt) {
            return $receipt
        }
        Start-Sleep -Milliseconds 100
    }

    throw "Transaction receipt was not available within $TimeoutSeconds seconds: $TransactionHash"
}

function Send-UnlockedTransaction {
    param(
        [Parameter(Mandatory)][string]$From,
        [Parameter(Mandatory)][string]$To,
        [Parameter(Mandatory)][string]$Data,
        [System.Numerics.BigInteger]$GasLimit = [System.Numerics.BigInteger]::Zero
    )

    $transaction = [ordered]@{
        from = $From
        to = $To
        data = $Data
    }

    if ($GasLimit -gt [System.Numerics.BigInteger]::Zero) {
        # Explicit gas prevents eth_sendTransaction from estimating and rejecting
        # the intentionally reverting Week 2 transaction before it is broadcast.
        $transaction.gas = ConvertTo-RpcQuantity -Value $GasLimit
    }
    else {
        $estimatedGasHex = Invoke-JsonRpc -Method 'eth_estimateGas' -Parameters @($transaction)
        $estimatedGas = ConvertFrom-RpcQuantity -HexValue $estimatedGasHex
        $paddedGas = $estimatedGas + ($estimatedGas / 5) + 10000
        $transaction.gas = ConvertTo-RpcQuantity -Value $paddedGas
    }

    $transactionHash = Invoke-JsonRpc -Method 'eth_sendTransaction' -Parameters @($transaction)
    if ($transactionHash -notmatch '^0x[0-9a-fA-F]{64}$') {
        throw "eth_sendTransaction returned an invalid hash: '$transactionHash'."
    }

    return [pscustomobject]@{
        Hash = $transactionHash.ToLowerInvariant()
        Receipt = Wait-ForTransactionReceipt -TransactionHash $transactionHash
    }
}

function Get-CastOutput {
    param([Parameter(Mandatory)][string[]]$Arguments)

    return Invoke-NativeCapture `
        -FilePath $castPath `
        -Arguments $Arguments `
        -WorkingDirectory $repositoryRoot `
        -TimeoutSeconds 30
}

function Get-Calldata {
    param(
        [Parameter(Mandatory)][string]$Signature,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $calldata = Get-CastOutput -Arguments (@('calldata', $Signature) + $Arguments)
    if ($calldata -notmatch '^0x[0-9a-fA-F]+$' -or (($calldata.Length - 2) % 2) -ne 0) {
        throw "cast calldata returned invalid data for $Signature`: '$calldata'."
    }

    return $calldata.ToLowerInvariant()
}

function Get-TokenBalance {
    param(
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$Account
    )

    $balanceCalldata = Get-Calldata -Signature 'balanceOf(address)' -Arguments @($Account)
    $call = [ordered]@{ to = $Token; data = $balanceCalldata }
    $result = Invoke-JsonRpc -Method 'eth_call' -Parameters @($call, 'latest')
    if ($result -notmatch '^0x[0-9a-fA-F]{64}$') {
        throw "balanceOf returned invalid ABI data for $Account`: '$result'."
    }

    return ConvertFrom-AbiWord -Word $result.Substring(2)
}

function Get-UniqueDeployment {
    param(
        [Parameter(Mandatory)][object[]]$Transactions,
        [Parameter(Mandatory)][string]$ContractName
    )

    # Do not name this variable `$matches`: PowerShell's case-insensitive
    # automatic `$Matches` variable is replaced by every regex operation.
    $deploymentMatches = @($Transactions | Where-Object { $_.contractName -eq $ContractName })
    if ($deploymentMatches.Count -ne 1) {
        throw "Expected exactly one $ContractName deployment in broadcast JSON, found $($deploymentMatches.Count)."
    }

    $address = Normalize-Address -Address $deploymentMatches[0].contractAddress -Label $ContractName
    if ($deploymentMatches[0].hash -notmatch '^0x[0-9a-fA-F]{64}$') {
        throw "Broadcast JSON contains an invalid $ContractName transaction hash."
    }

    return [pscustomobject]@{
        Address = $address
        Hash = $deploymentMatches[0].hash.ToLowerInvariant()
    }
}

function Assert-SuccessReceipt {
    param(
        [Parameter(Mandatory)]$Receipt,
        [Parameter(Mandatory)][string]$Label
    )

    $status = ConvertFrom-RpcQuantity -HexValue $Receipt.status
    Assert-Condition -Condition ($status -eq [System.Numerics.BigInteger]::One) -Message "$Label receipt status must be 1"
}

foreach ($requiredPath in @($contractsDirectory, $forgePath, $castPath, $anvilPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required repository-local path is missing: $requiredPath. Run scripts/install-foundry.ps1 first."
    }
}

$dotnetPath = $null
if (-not [string]::IsNullOrWhiteSpace($OrchestratorHarnessDll)) {
    $OrchestratorHarnessDll = [System.IO.Path]::GetFullPath($OrchestratorHarnessDll)
    if (-not (Test-Path -LiteralPath $OrchestratorHarnessDll -PathType Leaf)) {
        throw "Week 14 orchestrator harness is missing: $OrchestratorHarnessDll"
    }

    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
}

# The installer verifies the downloaded release archive's SHA-256. This runtime
# check then catches a missing, stale, or wrong-version installation; it is not
# a second per-binary integrity check and must not be described as one.
foreach ($tool in @(
        @{ Name = 'forge'; Path = $forgePath },
        @{ Name = 'cast'; Path = $castPath },
        @{ Name = 'anvil'; Path = $anvilPath })) {
    $versionOutput = Invoke-NativeCapture `
        -FilePath $tool.Path `
        -Arguments @('--version') `
        -WorkingDirectory $repositoryRoot `
        -TimeoutSeconds 15
    if ($versionOutput -notmatch '(^|[^0-9])1\.7\.1([^0-9]|$)') {
        throw "Expected repository-local $($tool.Name) v1.7.1. Actual output: $versionOutput"
    }
}

Test-PortIsAvailable -CandidatePort $Port

$anvilHandle = $null
$summary = $null
$observationError = $null
$cleanupError = $null
try {
    Write-Host "Starting isolated Anvil on $rpcUrl..." -ForegroundColor Cyan
    $anvilHandle = Start-TemporaryAnvil -Executable $anvilPath -ListenPort $Port
    $anvilClientVersion = Wait-ForAnvil -Handle $anvilHandle

    $chainIdHex = Invoke-JsonRpc -Method 'eth_chainId'
    $chainId = ConvertFrom-RpcQuantity -HexValue $chainIdHex
    Assert-Condition `
        -Condition ($chainId -eq $expectedChainId) `
        -Message "temporary RPC chainId must be 31337, got $chainId"
    Assert-Condition -Condition (-not $anvilHandle.Process.HasExited) -Message 'owned Anvil must remain alive after chain validation'

    $accounts = @(Invoke-JsonRpc -Method 'eth_accounts')
    Assert-Condition -Condition ($accounts.Count -ge 2) -Message 'Anvil must expose at least two unlocked accounts'
    $payer = Normalize-Address -Address $accounts[0] -Label 'payer/deployer'
    $merchant = Normalize-Address -Address $accounts[1] -Label 'merchant'
    Assert-Condition -Condition ($payer -ne $merchant) -Message 'payer and merchant must be different accounts'

    Write-Host 'Broadcasting DeployLocal with the first unlocked RPC account...' -ForegroundColor Cyan
    $broadcastStartedUtc = [DateTime]::UtcNow
    [void](Invoke-NativeCapture `
            -FilePath $forgePath `
            -Arguments @(
                'script',
                'script/DeployLocal.s.sol:DeployLocal',
                '--rpc-url', $rpcUrl,
                '--broadcast',
                '--unlocked',
                '--sender', $payer,
                '--slow',
                '--non-interactive') `
            -WorkingDirectory $contractsDirectory `
            -TimeoutSeconds $nativeCommandTimeoutSeconds)

    $broadcastPath = Join-Path $contractsDirectory 'broadcast/DeployLocal.s.sol/31337/run-latest.json'
    if (-not (Test-Path -LiteralPath $broadcastPath -PathType Leaf)) {
        throw "Forge reported success but did not create broadcast JSON: $broadcastPath"
    }
    $broadcastFile = Get-Item -LiteralPath $broadcastPath
    Assert-Condition `
        -Condition ($broadcastFile.LastWriteTimeUtc -ge $broadcastStartedUtc.AddSeconds(-2)) `
        -Message 'run-latest.json must be produced by this invocation, not a stale run'

    $broadcast = Get-Content -LiteralPath $broadcastPath -Raw | ConvertFrom-Json -Depth 100
    if ($broadcast.PSObject.Properties.Name -contains 'chain') {
        Assert-Condition `
            -Condition ([System.Numerics.BigInteger]::Parse("$($broadcast.chain)") -eq $expectedChainId) `
            -Message 'broadcast JSON chain must be 31337'
    }
    $broadcastTransactions = @($broadcast.transactions)
    Assert-Condition -Condition ($broadcastTransactions.Count -ge 3) -Message 'DeployLocal must broadcast three creations'

    $routerDeployment = Get-UniqueDeployment -Transactions $broadcastTransactions -ContractName 'PaymentRouter'
    $usdcDeployment = Get-UniqueDeployment -Transactions $broadcastTransactions -ContractName 'TestUSDC'
    $token18Deployment = Get-UniqueDeployment -Transactions $broadcastTransactions -ContractName 'TestToken18'
    foreach ($deployment in @($routerDeployment, $usdcDeployment, $token18Deployment)) {
        $deploymentReceipt = Wait-ForTransactionReceipt -TransactionHash $deployment.Hash
        Assert-SuccessReceipt -Receipt $deploymentReceipt -Label 'deployment'
        $deployedCode = Invoke-JsonRpc -Method 'eth_getCode' -Parameters @($deployment.Address, 'latest')
        Assert-Condition `
            -Condition ($deployedCode -match '^0x[0-9a-fA-F]+$' -and $deployedCode -ne '0x') `
            -Message "deployment at $($deployment.Address) must contain runtime code"
    }

    $paymentId = (Get-CastOutput -Arguments @('keccak', 'week2-successful-payment')).ToLowerInvariant()
    $failedPaymentId = (Get-CastOutput -Arguments @('keccak', 'week2-reverted-payment')).ToLowerInvariant()
    Assert-Condition -Condition ($paymentId -match '^0x[0-9a-f]{64}$') -Message 'successful paymentId must be bytes32'
    Assert-Condition -Condition ($failedPaymentId -match '^0x[0-9a-f]{64}$') -Message 'failed paymentId must be bytes32'

    $mintCalldata = Get-Calldata `
        -Signature 'mint(address,uint256)' `
        -Arguments @($payer, $paymentAmount.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    $mintTransaction = Send-UnlockedTransaction `
        -From $payer `
        -To $usdcDeployment.Address `
        -Data $mintCalldata
    Assert-SuccessReceipt -Receipt $mintTransaction.Receipt -Label 'mint'

    $approveCalldata = Get-Calldata `
        -Signature 'approve(address,uint256)' `
        -Arguments @($routerDeployment.Address, $paymentAmount.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    $approveTransaction = Send-UnlockedTransaction `
        -From $payer `
        -To $usdcDeployment.Address `
        -Data $approveCalldata
    Assert-SuccessReceipt -Receipt $approveTransaction.Receipt -Label 'approve'

    $merchantBalanceBefore = Get-TokenBalance -Token $usdcDeployment.Address -Account $merchant
    $routerBalanceBefore = Get-TokenBalance -Token $usdcDeployment.Address -Account $routerDeployment.Address
    $payerBalanceBefore = Get-TokenBalance -Token $usdcDeployment.Address -Account $payer

    $paySignature = 'pay(bytes32,address,address,uint256)'
    $paySelector = (Get-CastOutput -Arguments @('sig', $paySignature)).ToLowerInvariant()
    Assert-Condition -Condition ($paySelector -match '^0x[0-9a-f]{8}$') -Message 'pay selector must be four bytes'
    $payCalldata = Get-Calldata `
        -Signature $paySignature `
        -Arguments @(
            $paymentId,
            $usdcDeployment.Address,
            $merchant,
            $paymentAmount.ToString([System.Globalization.CultureInfo]::InvariantCulture))

    Write-Host 'Broadcasting successful payment and reading its mined evidence...' -ForegroundColor Cyan
    $payTransactionResult = Send-UnlockedTransaction `
        -From $payer `
        -To $routerDeployment.Address `
        -Data $payCalldata
    $payReceipt = $payTransactionResult.Receipt
    Assert-SuccessReceipt -Receipt $payReceipt -Label 'payment'

    $payTransaction = Invoke-JsonRpc -Method 'eth_getTransactionByHash' -Parameters @($payTransactionResult.Hash)
    Assert-Condition -Condition ($null -ne $payTransaction) -Message 'successful payment transaction must be queryable'
    $transactionFrom = Normalize-Address -Address $payTransaction.from -Label 'transaction from'
    $transactionTo = Normalize-Address -Address $payTransaction.to -Label 'transaction to'
    Assert-HexEqual -Actual $transactionFrom -Expected $payer -Label 'payment transaction from'
    Assert-HexEqual -Actual $transactionTo -Expected $routerDeployment.Address -Label 'payment transaction to'
    Assert-Condition -Condition ($payTransaction.input.Length -ge 10) -Message 'payment input must contain a selector'
    Assert-HexEqual -Actual $payTransaction.input.Substring(0, 10) -Expected $paySelector -Label 'payment input selector'
    Assert-HexEqual -Actual $payTransaction.input -Expected $payCalldata -Label 'payment input calldata'
    Assert-HexEqual -Actual $payReceipt.transactionHash -Expected $payTransactionResult.Hash -Label 'receipt transaction hash'
    Assert-HexEqual -Actual $payTransaction.blockHash -Expected $payReceipt.blockHash -Label 'payment block hash'

    $paymentEventTopic = (
        Get-CastOutput -Arguments @('keccak', 'PaymentRecorded(bytes32,address,address,address,uint256)')
    ).ToLowerInvariant()
    Assert-Condition -Condition ($paymentEventTopic -match '^0x[0-9a-f]{64}$') -Message 'event topic0 must be bytes32'

    $matchingPaymentLogs = @($payReceipt.logs | Where-Object {
            $_.address -and
            $_.address.Equals($routerDeployment.Address, [System.StringComparison]::OrdinalIgnoreCase) -and
            @($_.topics).Count -gt 0 -and
            $_.topics[0].Equals($paymentEventTopic, [System.StringComparison]::OrdinalIgnoreCase)
        })
    Assert-Condition -Condition ($matchingPaymentLogs.Count -eq 1) -Message 'successful receipt must contain exactly one PaymentRecorded log'
    $paymentLog = $matchingPaymentLogs[0]
    $paymentTopics = @($paymentLog.topics)
    Assert-Condition -Condition ($paymentTopics.Count -eq 4) -Message 'PaymentRecorded must contain topic0 plus three indexed fields'
    Assert-HexEqual -Actual $paymentTopics[0] -Expected $paymentEventTopic -Label 'PaymentRecorded topic0'
    Assert-HexEqual -Actual $paymentTopics[1] -Expected $paymentId -Label 'PaymentRecorded paymentId'
    $decodedPayer = ConvertFrom-IndexedAddress -Topic $paymentTopics[2] -Label 'payer'
    $decodedMerchant = ConvertFrom-IndexedAddress -Topic $paymentTopics[3] -Label 'merchant'
    Assert-HexEqual -Actual $decodedPayer -Expected $payer -Label 'decoded event payer'
    Assert-HexEqual -Actual $decodedMerchant -Expected $merchant -Label 'decoded event merchant'

    if ($paymentLog.data -notmatch '^0x[0-9a-fA-F]{128}$') {
        throw "PaymentRecorded data must contain exactly two ABI words: '$($paymentLog.data)'."
    }
    $eventData = $paymentLog.data.Substring(2)
    $tokenWord = $eventData.Substring(0, 64)
    if ($tokenWord -notmatch '^0{24}[0-9a-fA-F]{40}$') {
        throw "PaymentRecorded token word is not a canonical ABI address: '$tokenWord'."
    }
    $decodedToken = Normalize-Address -Address ("0x" + $tokenWord.Substring(24)) -Label 'event token'
    $decodedAmount = ConvertFrom-AbiWord -Word $eventData.Substring(64, 64)
    Assert-HexEqual -Actual $decodedToken -Expected $usdcDeployment.Address -Label 'decoded event token'
    Assert-Condition -Condition ($decodedAmount -eq $paymentAmount) -Message 'decoded event amount must equal 1,250,000 raw units'

    $merchantBalanceAfter = Get-TokenBalance -Token $usdcDeployment.Address -Account $merchant
    $routerBalanceAfter = Get-TokenBalance -Token $usdcDeployment.Address -Account $routerDeployment.Address
    $payerBalanceAfter = Get-TokenBalance -Token $usdcDeployment.Address -Account $payer
    $merchantDelta = $merchantBalanceAfter - $merchantBalanceBefore
    Assert-Condition -Condition ($merchantDelta -eq $paymentAmount) -Message 'merchant balance delta must equal the payment amount'
    Assert-Condition -Condition ($routerBalanceBefore.IsZero) -Message 'Router balance before payment must be zero'
    Assert-Condition -Condition ($routerBalanceAfter.IsZero) -Message 'Router balance after payment must remain zero'
    Assert-Condition `
        -Condition (($payerBalanceBefore - $payerBalanceAfter) -eq $paymentAmount) `
        -Message 'payer token balance must decrease by the exact payment amount'

    $paymentGasUsed = ConvertFrom-RpcQuantity -HexValue $payReceipt.gasUsed
    $paymentGasLimit = ConvertFrom-RpcQuantity -HexValue $payTransaction.gas
    $paymentNonce = ConvertFrom-RpcQuantity -HexValue $payTransaction.nonce
    Assert-Condition -Condition ($paymentGasUsed -gt [System.Numerics.BigInteger]::Zero) -Message 'successful payment gasUsed must be positive'
    Assert-Condition -Condition ($paymentGasLimit -gt [System.Numerics.BigInteger]::Zero) -Message 'successful payment gas limit must be positive'
    Assert-Condition -Condition ($paymentGasUsed -le $paymentGasLimit) -Message 'successful payment gasUsed cannot exceed its gas limit'
    Assert-Condition -Condition ($payReceipt.blockHash -match '^0x[0-9a-fA-F]{64}$') -Message 'successful receipt blockHash must be bytes32'
    if (-not ($payReceipt.PSObject.Properties.Name -contains 'effectiveGasPrice')) {
        throw 'Successful payment receipt is missing effectiveGasPrice.'
    }
    $paymentEffectiveGasPrice = ConvertFrom-RpcQuantity -HexValue $payReceipt.effectiveGasPrice
    $paymentGasCost = $paymentGasUsed * $paymentEffectiveGasPrice
    Assert-Condition -Condition ($paymentEffectiveGasPrice -gt [System.Numerics.BigInteger]::Zero) -Message 'successful effectiveGasPrice must be positive'
    Assert-Condition -Condition ($paymentGasCost -gt [System.Numerics.BigInteger]::Zero) -Message 'successful gas cost must be positive'

    # A real reverted transaction needs an explicit gas limit. Without it, this
    # helper calls eth_estimateGas first, which detects the revert before send.
    $failureCalldata = Get-Calldata `
        -Signature $paySignature `
        -Arguments @($failedPaymentId, $usdcDeployment.Address, $merchant, '0')
    $failureNonceBefore = ConvertFrom-RpcQuantity -HexValue (
        Invoke-JsonRpc -Method 'eth_getTransactionCount' -Parameters @($payer, 'latest'))
    $failureMerchantBalanceBefore = Get-TokenBalance -Token $usdcDeployment.Address -Account $merchant
    $failureRouterBalanceBefore = Get-TokenBalance -Token $usdcDeployment.Address -Account $routerDeployment.Address
    $failurePayerBalanceBefore = Get-TokenBalance -Token $usdcDeployment.Address -Account $payer

    Write-Host 'Broadcasting an intentionally reverting zero-amount payment...' -ForegroundColor Cyan
    $failedTransactionResult = Send-UnlockedTransaction `
        -From $payer `
        -To $routerDeployment.Address `
        -Data $failureCalldata `
        -GasLimit $explicitFailureGasLimit
    $failedReceipt = $failedTransactionResult.Receipt
    $failedStatus = ConvertFrom-RpcQuantity -HexValue $failedReceipt.status
    $failedGasUsed = ConvertFrom-RpcQuantity -HexValue $failedReceipt.gasUsed
    $failureNonceAfter = ConvertFrom-RpcQuantity -HexValue (
        Invoke-JsonRpc -Method 'eth_getTransactionCount' -Parameters @($payer, 'latest'))
    $failureMerchantBalanceAfter = Get-TokenBalance -Token $usdcDeployment.Address -Account $merchant
    $failureRouterBalanceAfter = Get-TokenBalance -Token $usdcDeployment.Address -Account $routerDeployment.Address
    $failurePayerBalanceAfter = Get-TokenBalance -Token $usdcDeployment.Address -Account $payer

    Assert-Condition -Condition ($failedStatus.IsZero) -Message 'intentional zero-amount payment receipt status must be 0'
    Assert-Condition -Condition ($failedGasUsed -gt [System.Numerics.BigInteger]::Zero) -Message 'reverted transaction must consume gas'
    Assert-Condition -Condition (@($failedReceipt.logs).Count -eq 0) -Message 'reverted transaction must retain no logs'
    Assert-Condition -Condition ($failureNonceAfter -eq ($failureNonceBefore + 1)) -Message 'reverted transaction must advance sender nonce by one'
    Assert-Condition -Condition ($failureMerchantBalanceAfter -eq $failureMerchantBalanceBefore) -Message 'reverted transaction must not change merchant token balance'
    Assert-Condition -Condition ($failureRouterBalanceAfter -eq $failureRouterBalanceBefore) -Message 'reverted transaction must not change Router token balance'
    Assert-Condition -Condition ($failureRouterBalanceAfter.IsZero) -Message 'Router balance must remain zero after revert'
    Assert-Condition -Condition ($failurePayerBalanceAfter -eq $failurePayerBalanceBefore) -Message 'reverted transaction must not change payer token balance'

    $failedTransaction = Invoke-JsonRpc -Method 'eth_getTransactionByHash' -Parameters @($failedTransactionResult.Hash)
    Assert-Condition -Condition ($null -ne $failedTransaction) -Message 'reverted transaction must remain queryable'
    $failedTransactionFrom = Normalize-Address -Address $failedTransaction.from -Label 'reverted transaction from'
    $failedTransactionTo = Normalize-Address -Address $failedTransaction.to -Label 'reverted transaction to'
    $failedTransactionNonce = ConvertFrom-RpcQuantity -HexValue $failedTransaction.nonce
    $failedGasLimit = ConvertFrom-RpcQuantity -HexValue $failedTransaction.gas
    Assert-HexEqual -Actual $failedTransactionFrom -Expected $payer -Label 'reverted transaction from'
    Assert-HexEqual -Actual $failedTransactionTo -Expected $routerDeployment.Address -Label 'reverted transaction to'
    Assert-HexEqual -Actual $failedTransaction.input.Substring(0, 10) -Expected $paySelector -Label 'reverted input selector'
    Assert-HexEqual -Actual $failedTransaction.input -Expected $failureCalldata -Label 'reverted input calldata'
    Assert-HexEqual -Actual $failedReceipt.transactionHash -Expected $failedTransactionResult.Hash -Label 'reverted receipt transaction hash'
    Assert-Condition -Condition ($failedTransactionNonce -eq $failureNonceBefore) -Message 'reverted transaction nonce must equal the pre-send latest nonce'
    Assert-Condition -Condition ($failedGasLimit -eq $explicitFailureGasLimit) -Message 'reverted transaction must retain the explicit gas limit'
    Assert-Condition -Condition ($failedGasUsed -lt $failedGasLimit) -Message 'reverted gasUsed must remain below its gas limit, ruling out out-of-gas'
    Assert-HexEqual -Actual $failedTransaction.blockHash -Expected $failedReceipt.blockHash -Label 'reverted block hash'
    Assert-Condition -Condition ($failedReceipt.blockHash -match '^0x[0-9a-fA-F]{64}$') -Message 'reverted receipt blockHash must be bytes32'

    # Receipt status says only that execution failed. Anvil's trace ties this
    # particular mined transaction to PaymentRouter.InvalidAmount rather than
    # accepting an unrelated revert or out-of-gas failure as equivalent.
    $traceOptions = [ordered]@{
        disableStorage = $true
        disableStack = $true
        enableMemory = $false
        enableReturnData = $false
    }
    $failedTrace = Invoke-JsonRpc `
        -Method 'debug_traceTransaction' `
        -Parameters @($failedTransactionResult.Hash, $traceOptions) `
        -TimeoutSeconds 15
    Assert-Condition -Condition ($failedTrace.failed -eq $true) -Message 'debug trace must mark the transaction as failed'
    $failedReturnData = "$($failedTrace.returnValue)"
    if (-not $failedReturnData.StartsWith('0x', [System.StringComparison]::OrdinalIgnoreCase)) {
        $failedReturnData = "0x$failedReturnData"
    }
    $invalidAmountSelector = (Get-CastOutput -Arguments @('sig', 'InvalidAmount()')).ToLowerInvariant()
    Assert-HexEqual -Actual $failedReturnData -Expected $invalidAmountSelector -Label 'reverted InvalidAmount selector'

    if (-not ($failedReceipt.PSObject.Properties.Name -contains 'effectiveGasPrice')) {
        throw 'Reverted transaction receipt is missing effectiveGasPrice.'
    }
    $failedEffectiveGasPrice = ConvertFrom-RpcQuantity -HexValue $failedReceipt.effectiveGasPrice
    $failedGasCost = $failedGasUsed * $failedEffectiveGasPrice
    Assert-Condition -Condition ($failedEffectiveGasPrice -gt [System.Numerics.BigInteger]::Zero) -Message 'reverted effectiveGasPrice must be positive'
    Assert-Condition -Condition ($failedGasCost -gt [System.Numerics.BigInteger]::Zero) -Message 'reverted gas cost must be positive'
    Assert-Condition -Condition (-not $anvilHandle.Process.HasExited) -Message 'owned Anvil must remain alive through the final observation'

    if ($null -ne $dotnetPath) {
        Write-Host 'Running Week 14 locally signed transaction lifecycle...' -ForegroundColor Cyan
        $week14Output = Invoke-NativeCapture `
            -FilePath $dotnetPath `
            -Arguments @(
                $OrchestratorHarnessDll,
                '--rpc-url', $rpcUrl,
                '--router', $routerDeployment.Address,
                '--token', $usdcDeployment.Address,
                '--merchant', $merchant,
                '--runtime-hash', '0x8308fbd23f6bd4bcb4284281ab9388b2a437297aa512a8308b4c2e390205e92c') `
            -WorkingDirectory $repositoryRoot `
            -TimeoutSeconds 90
        Write-Host $week14Output
        Assert-Condition -Condition (-not $anvilHandle.Process.HasExited) -Message 'owned Anvil must remain alive through Week 14 verification'
    }

    $summary = [pscustomobject]@{
        AnvilClientVersion = $anvilClientVersion
        ChainId = $chainId
        RpcUrl = $rpcUrl
        Payer = $payer
        Merchant = $merchant
        Router = $routerDeployment.Address
        TestUSDC = $usdcDeployment.Address
        TestToken18 = $token18Deployment.Address
        RouterDeploymentHash = $routerDeployment.Hash
        UsdcDeploymentHash = $usdcDeployment.Hash
        Token18DeploymentHash = $token18Deployment.Hash
        MintHash = $mintTransaction.Hash
        ApproveHash = $approveTransaction.Hash
        PaymentHash = $payTransactionResult.Hash
        PaymentFrom = $transactionFrom
        PaymentTo = $transactionTo
        PaymentSelector = $paySelector
        PaymentInputBytes = [int](($payTransaction.input.Length - 2) / 2)
        PaymentNonce = $paymentNonce
        PaymentGasLimit = $paymentGasLimit
        PaymentStatus = ConvertFrom-RpcQuantity -HexValue $payReceipt.status
        PaymentBlock = ConvertFrom-RpcQuantity -HexValue $payReceipt.blockNumber
        PaymentBlockHash = $payReceipt.blockHash.ToLowerInvariant()
        PaymentGasUsed = $paymentGasUsed
        PaymentEffectiveGasPrice = $paymentEffectiveGasPrice
        PaymentGasCost = $paymentGasCost
        EventTopic0 = $paymentEventTopic
        EventPaymentId = $paymentTopics[1].ToLowerInvariant()
        EventPayer = $decodedPayer
        EventToken = $decodedToken
        EventMerchant = $decodedMerchant
        EventAmount = $decodedAmount
        MerchantBalanceBefore = $merchantBalanceBefore
        MerchantBalanceAfter = $merchantBalanceAfter
        MerchantDelta = $merchantDelta
        RouterBalanceAfter = $routerBalanceAfter
        FailedHash = $failedTransactionResult.Hash
        FailedFrom = $failedTransactionFrom
        FailedTo = $failedTransactionTo
        FailedSelector = $failedTransaction.input.Substring(0, 10).ToLowerInvariant()
        FailedInputBytes = [int](($failedTransaction.input.Length - 2) / 2)
        FailedTransactionNonce = $failedTransactionNonce
        FailedGasLimit = $failedGasLimit
        FailedStatus = $failedStatus
        FailedBlock = ConvertFrom-RpcQuantity -HexValue $failedReceipt.blockNumber
        FailedBlockHash = $failedReceipt.blockHash.ToLowerInvariant()
        FailedGasUsed = $failedGasUsed
        FailedEffectiveGasPrice = $failedEffectiveGasPrice
        FailedGasCost = $failedGasCost
        FailedRevertSelector = $failedReturnData.ToLowerInvariant()
        FailedLogCount = @($failedReceipt.logs).Count
        FailureNonceBefore = $failureNonceBefore
        FailureNonceAfter = $failureNonceAfter
        FailureMerchantBalance = $failureMerchantBalanceAfter
        FailureRouterBalance = $failureRouterBalanceAfter
        FailurePayerBalance = $failurePayerBalanceAfter
    }
}
catch {
    $observationError = $_
}
finally {
    try {
        if ($null -ne $anvilHandle) {
            Stop-TemporaryAnvil -Handle $anvilHandle
        }
    }
    catch {
        $cleanupError = $_
    }
    finally {
        $stopwatch.Stop()
    }
}

if ($null -ne $observationError -and $null -ne $cleanupError) {
    throw [System.AggregateException]::new(
        'The observation and Anvil cleanup both failed.',
        [System.Exception[]]@($observationError.Exception, $cleanupError.Exception))
}
if ($null -ne $observationError) {
    throw $observationError
}
if ($null -ne $cleanupError) {
    throw $cleanupError
}

if ($null -eq $summary) {
    throw 'Observation did not produce a summary.'
}

Write-Host "`n=== Week 2 Transaction Observation ===" -ForegroundColor Green
Write-Host 'Environment'
Write-Host "  rpcUrl                 : $($summary.RpcUrl)"
Write-Host "  chainId                : $($summary.ChainId)"
Write-Host "  anvilClient            : $($summary.AnvilClientVersion)"
Write-Host 'Accounts'
Write-Host "  payer/deployer         : $($summary.Payer)"
Write-Host "  merchant               : $($summary.Merchant)"
Write-Host 'Contracts (parsed from broadcast JSON)'
Write-Host "  PaymentRouter          : $($summary.Router)"
Write-Host "  TestUSDC               : $($summary.TestUSDC)"
Write-Host "  TestToken18            : $($summary.TestToken18)"
Write-Host 'Setup transaction hashes'
Write-Host "  deploy.router          : $($summary.RouterDeploymentHash)"
Write-Host "  deploy.usdc            : $($summary.UsdcDeploymentHash)"
Write-Host "  deploy.token18         : $($summary.Token18DeploymentHash)"
Write-Host "  mint                    : $($summary.MintHash)"
Write-Host "  approve                 : $($summary.ApproveHash)"
Write-Host 'Successful payment transaction'
Write-Host "  hash                    : $($summary.PaymentHash)"
Write-Host "  from                    : $($summary.PaymentFrom)"
Write-Host "  to                      : $($summary.PaymentTo)"
Write-Host "  input.selector          : $($summary.PaymentSelector)"
Write-Host "  input.bytes             : $($summary.PaymentInputBytes)"
Write-Host "  nonce                   : $($summary.PaymentNonce)"
Write-Host "  gasLimit                : $($summary.PaymentGasLimit)"
Write-Host 'Successful payment receipt'
Write-Host "  status                  : $($summary.PaymentStatus)"
Write-Host "  blockNumber             : $($summary.PaymentBlock)"
Write-Host "  blockHash               : $($summary.PaymentBlockHash)"
Write-Host "  gasUsed                 : $($summary.PaymentGasUsed)"
Write-Host "  effectiveGasPriceWei    : $($summary.PaymentEffectiveGasPrice)"
Write-Host "  gasCostWei              : $($summary.PaymentGasCost)"
Write-Host 'Decoded PaymentRecorded log'
Write-Host "  topic0                  : $($summary.EventTopic0)"
Write-Host "  paymentId               : $($summary.EventPaymentId)"
Write-Host "  payer                   : $($summary.EventPayer)"
Write-Host "  token                   : $($summary.EventToken)"
Write-Host "  merchant                : $($summary.EventMerchant)"
Write-Host "  amountRaw               : $($summary.EventAmount)"
Write-Host 'Successful payment balance checks'
Write-Host "  merchant.beforeRaw      : $($summary.MerchantBalanceBefore)"
Write-Host "  merchant.afterRaw       : $($summary.MerchantBalanceAfter)"
Write-Host "  merchant.deltaRaw       : $($summary.MerchantDelta)"
Write-Host "  router.afterRaw         : $($summary.RouterBalanceAfter)"
Write-Host 'Intentionally reverted transaction'
Write-Host "  hash                    : $($summary.FailedHash)"
Write-Host "  from                    : $($summary.FailedFrom)"
Write-Host "  to                      : $($summary.FailedTo)"
Write-Host "  input.selector          : $($summary.FailedSelector)"
Write-Host "  input.bytes             : $($summary.FailedInputBytes)"
Write-Host "  nonce                   : $($summary.FailedTransactionNonce)"
Write-Host "  gasLimit                : $($summary.FailedGasLimit)"
Write-Host "  status                  : $($summary.FailedStatus)"
Write-Host "  blockNumber             : $($summary.FailedBlock)"
Write-Host "  blockHash               : $($summary.FailedBlockHash)"
Write-Host "  gasUsed                 : $($summary.FailedGasUsed)"
Write-Host "  effectiveGasPriceWei    : $($summary.FailedEffectiveGasPrice)"
Write-Host "  gasCostWei              : $($summary.FailedGasCost)"
Write-Host "  revert.selector         : $($summary.FailedRevertSelector)"
Write-Host "  retainedLogCount        : $($summary.FailedLogCount)"
Write-Host "  payerNonce.before       : $($summary.FailureNonceBefore)"
Write-Host "  payerNonce.after        : $($summary.FailureNonceAfter)"
Write-Host "  payer.balanceRaw        : $($summary.FailurePayerBalance)"
Write-Host "  merchant.balanceRaw     : $($summary.FailureMerchantBalance)"
Write-Host "  router.balanceRaw       : $($summary.FailureRouterBalance)"
Write-Host "Assertions              : PASSED"
Write-Host ("ElapsedSeconds          : {0:F3}" -f $stopwatch.Elapsed.TotalSeconds)
