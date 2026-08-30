using System.Numerics;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.RPC.Web3;
using Nethereum.Web3;
using PaymentSandbox.Contracts.Identity;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Infrastructure;

/// <summary>
/// Loopback-only Anvil adapter for Router identity, nonce, raw broadcast, and receipt reads.
/// </summary>
/// <remarks>
/// Construction is asynchronous because the adapter is not exposed until both
/// `web3_clientVersion` and `eth_chainId` identify local Anvil. Every operation
/// rechecks chain identity before trusting a response or causing a side effect.
/// </remarks>
public sealed class LocalAnvilRpcClient :
    IPaymentRouterIdentityRpc,
    IAccountNonceReader,
    IRawTransactionBroadcaster,
    ITransactionReceiptReader,
    IAsyncDisposable
{
    private readonly IWeb3 _web3;
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    private LocalAnvilRpcClient(
        IWeb3 web3,
        string clientVersion,
        TimeSpan requestTimeout)
    {
        _web3 = web3;
        ClientVersion = clientVersion;
        _requestTimeout = requestTimeout;
    }

    public string ClientVersion { get; }

    public static async Task<LocalAnvilRpcClient> ConnectAsync(
        LocalAnvilRpcClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var web3 = new Web3(options.RpcUri.AbsoluteUri);

        try
        {
            string clientVersion = await new Web3ClientVersion(web3.Client)
                .SendRequestAsync()
                .WaitAsync(options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(clientVersion) ||
                !clientVersion.StartsWith("anvil/", StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalAnvilRpcException(
                    "The loopback endpoint did not identify itself as Anvil.");
            }

            BigInteger chainId = (await web3.Eth.ChainId
                .SendRequestAsync()
                .WaitAsync(options.RequestTimeout, cancellationToken)
                .ConfigureAwait(false)).Value;
            if (chainId != TransactionLifecyclePolicy.LocalAnvilChainId)
            {
                throw new LocalAnvilRpcException(
                    "The local Anvil adapter requires chain ID 31337.");
            }

            return new LocalAnvilRpcClient(
                web3, clientVersion, options.RequestTimeout);
        }
        catch
        {
            (web3.Client as IDisposable)?.Dispose();
            throw;
        }
    }

    public async Task<BigInteger> GetChainIdAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadAndValidateChainIdAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetCodeAsync(
        string contractAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);
        await ReadAndValidateChainIdAsync(cancellationToken).ConfigureAwait(false);
        return await _web3.Eth.GetCode
            .SendRequestAsync(contractAddress, BlockParameter.CreateLatest())
            .WaitAsync(_requestTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> GetPendingNonceAsync(
        EvmChainId chainId,
        EvmAddress account,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestedChain(chainId);
        ArgumentNullException.ThrowIfNull(account);
        if (account.IsZero)
        {
            throw new ArgumentException("The nonce account cannot be zero.", nameof(account));
        }

        await ReadAndValidateChainIdAsync(cancellationToken).ConfigureAwait(false);
        BigInteger nonce = (await _web3.Eth.Transactions.GetTransactionCount
            .SendRequestAsync(account.Value, BlockParameter.CreatePending())
            .WaitAsync(_requestTimeout, cancellationToken)
            .ConfigureAwait(false)).Value;
        try
        {
            return checked((long)nonce);
        }
        catch (OverflowException)
        {
            throw new LocalAnvilRpcException("Anvil returned a pending nonce outside Int64.");
        }
    }

    public async Task<TransactionBroadcastOutcome> BroadcastAsync(
        EvmChainId chainId,
        SignedTransactionPayload payload,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestedChain(chainId);
        ArgumentNullException.ThrowIfNull(payload);
        await ReadAndValidateChainIdAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string returnedHash = await _web3.Eth.Transactions.SendRawTransaction
                .SendRequestAsync(payload.RawTransaction)
                .WaitAsync(_requestTimeout, cancellationToken)
                .ConfigureAwait(false);
            TransactionHash observed = TransactionHash.Parse(returnedHash);
            if (observed != payload.TransactionHash)
            {
                // The node may already have accepted the bytes, so a hash
                // mismatch is deliberately ambiguous rather than rejected.
                throw new LocalAnvilRpcException(
                    "Anvil returned a different hash for the submitted transaction.");
            }

            return new TransactionBroadcastOutcome(
                TransactionBroadcastOutcomeKind.Accepted, "accepted");
        }
        catch (RpcResponseException exception)
        {
            return ClassifyRpcRejection(exception.RpcError?.Message);
        }
    }

    public async Task<TransactionReceiptObservation?> GetReceiptAsync(
        EvmChainId chainId,
        TransactionHash transactionHash,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestedChain(chainId);
        ArgumentNullException.ThrowIfNull(transactionHash);
        await ReadAndValidateChainIdAsync(cancellationToken).ConfigureAwait(false);
        TransactionReceipt? receipt = await _web3.Eth.Transactions.GetTransactionReceipt
            .SendRequestAsync(transactionHash.Value)
            .WaitAsync(_requestTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        if (receipt.Status is null || receipt.BlockNumber is null ||
            string.IsNullOrWhiteSpace(receipt.BlockHash) || receipt.GasUsed is null ||
            receipt.EffectiveGasPrice is null || string.IsNullOrWhiteSpace(receipt.TransactionHash))
        {
            throw new LocalAnvilRpcException("Anvil returned an incomplete mined receipt.");
        }

        TransactionHash observedHash = TransactionHash.Parse(receipt.TransactionHash);
        if (observedHash != transactionHash)
        {
            throw new LocalAnvilRpcException("Anvil returned a receipt for a different hash.");
        }

        TransactionExecutionStatus execution = receipt.Status.Value switch
        {
            var value when value.IsZero => TransactionExecutionStatus.Reverted,
            var value when value.IsOne => TransactionExecutionStatus.Succeeded,
            _ => throw new LocalAnvilRpcException("Anvil returned a receipt status other than zero or one."),
        };

        try
        {
            return new TransactionReceiptObservation(
                observedHash,
                execution,
                checked((long)receipt.BlockNumber.Value),
                TransactionHash.Parse(receipt.BlockHash),
                checked((long)receipt.GasUsed.Value),
                receipt.EffectiveGasPrice.Value);
        }
        catch (OverflowException)
        {
            throw new LocalAnvilRpcException("Anvil returned receipt quantities outside Int64.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            (_web3.Client as IDisposable)?.Dispose();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"Local Anvil RPC ({ClientVersion}; endpoint redacted)";

    private async Task<BigInteger> ReadAndValidateChainIdAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        BigInteger observed = (await _web3.Eth.ChainId
            .SendRequestAsync()
            .WaitAsync(_requestTimeout, cancellationToken)
            .ConfigureAwait(false)).Value;
        if (observed != TransactionLifecyclePolicy.LocalAnvilChainId)
        {
            throw new LocalAnvilRpcException("The connected endpoint no longer reports Anvil chain ID 31337.");
        }

        return observed;
    }

    private void ValidateRequestedChain(EvmChainId chainId)
    {
        ArgumentNullException.ThrowIfNull(chainId);
        ThrowIfDisposed();
        if (chainId.Value != TransactionLifecyclePolicy.LocalAnvilChainId)
        {
            throw new ArgumentException(
                "The local Anvil RPC adapter only accepts chain ID 31337.",
                nameof(chainId));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static TransactionBroadcastOutcome ClassifyRpcRejection(string? message)
    {
        string value = message ?? string.Empty;
        if (value.Contains("already known", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("known transaction", StringComparison.OrdinalIgnoreCase) ||
            // Anvil v1.7 uses this wording when the exact EIP-1559 bytes are
            // replayed after the caller lost the first accepted response.
            value.Contains("already imported", StringComparison.OrdinalIgnoreCase))
        {
            return new TransactionBroadcastOutcome(
                TransactionBroadcastOutcomeKind.AlreadyKnown, "already_known");
        }

        if (value.Contains("nonce too low", StringComparison.OrdinalIgnoreCase))
        {
            // The exact bytes may already have been mined, so receipt polling
            // must decide; signing a new payment here would be unsafe.
            return new TransactionBroadcastOutcome(
                TransactionBroadcastOutcomeKind.Unknown, "nonce_too_low");
        }

        if (value.Contains("underpriced", StringComparison.OrdinalIgnoreCase))
        {
            return new TransactionBroadcastOutcome(
                TransactionBroadcastOutcomeKind.Rejected, "replacement_underpriced");
        }

        if (value.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase))
        {
            return new TransactionBroadcastOutcome(
                TransactionBroadcastOutcomeKind.Rejected, "insufficient_funds");
        }

        return new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Unknown, "rpc_error");
    }
}
