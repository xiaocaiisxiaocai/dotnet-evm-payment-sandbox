using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Observability;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Persistence;
using PaymentSandbox.Orchestrator.Policy;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Lifecycle;

/// <summary>Coordinates one test transaction without owning a key or an RPC client.</summary>
public sealed class TransactionLifecycleProcessor
{
    private readonly TransactionLifecyclePolicy _policy;
    private readonly VerifiedPaymentRouterClient _router;
    private readonly IAccountNonceReader _nonceReader;
    private readonly ITestTransactionSigner _signer;
    private readonly IRawTransactionBroadcaster _broadcaster;
    private readonly ITransactionReceiptReader _receiptReader;
    private readonly ITransactionLifecycleStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IOperationalTelemetry _telemetry;

    public TransactionLifecycleProcessor(
        TransactionLifecyclePolicy policy,
        VerifiedPaymentRouterClient router,
        IAccountNonceReader nonceReader,
        ITestTransactionSigner signer,
        IRawTransactionBroadcaster broadcaster,
        ITransactionReceiptReader receiptReader,
        ITransactionLifecycleStore store,
        TimeProvider timeProvider,
        IOperationalTelemetry? telemetry = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _nonceReader = nonceReader ?? throw new ArgumentNullException(nameof(nonceReader));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _receiptReader = receiptReader ?? throw new ArgumentNullException(nameof(receiptReader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        // The default emits only provider-neutral .NET diagnostics. Tests and
        // hosts may inject an implementation, but callers can never attach
        // arbitrary labels through the workflow API.
        _telemetry = telemetry ?? OperationalTelemetry.Shared;

        // Identity verification happened before the client existed. Refuse to
        // compose it with a policy for a different chain or Router.
        if (router.Identity.ChainId != policy.ChainId.Value ||
            !string.Equals(router.Identity.ContractAddress, policy.Router.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("The verified Router identity does not match the lifecycle policy.", nameof(router));
        }
    }

    /// <summary>Reserves a nonce and persists the initial signed attempt, but does not broadcast.</summary>
    public Task<LifecycleCommitResult> CreateAsync(
        PaymentTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        OperationalExecution.ObserveAsync(
            _telemetry,
            OperationalComponent.TransactionLifecycle,
            OperationalAction.TransactionCreate,
            token => CreateCoreAsync(request, token),
            ClassifyResult,
            ClassifyFailure,
            cancellationToken);

    private async Task<LifecycleCommitResult> CreateCoreAsync(
        PaymentTransactionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _policy.ValidateInitialRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        EncodedPaymentRouterCall call = Encode(request);
        TransactionLifecycleSnapshot? durable = await _store.GetAsync(
            request.OperationId, cancellationToken);
        // Once reserved, the database is the nonce authority for this
        // operation. A restart retry must not depend on a healthy RPC or select
        // a newer pending nonce before it verifies the original request.
        long pendingNonce = durable?.Nonce ?? await ReadPendingNonceAsync(cancellationToken);
        var prepared = new PreparedPaymentOperation(
            request, _policy, call.Data, pendingNonce,
            TransactionLifecycleFingerprint.ForRequest(_policy, request, call.Data),
            _timeProvider.GetUtcNow());
        LifecycleCommitResult reservation = await _store.ReserveAsync(prepared, cancellationToken);
        if (reservation.Snapshot.AttemptCount > 0)
        {
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, reservation.Snapshot);
        }

        UnsignedPaymentTransaction unsigned = BuildUnsigned(
            reservation.Snapshot, request.InitialFee, call.Data);
        SignedTransactionPayload payload = await SignAsync(unsigned, cancellationToken);
        var attempt = new PreparedTransactionAttempt(
            request.OperationId, ExpectedPreviousAttemptCount: 0, request.InitialFee,
            payload, TransactionLifecycleFingerprint.ForUnsigned(unsigned),
            _timeProvider.GetUtcNow());
        return await _store.CommitAttemptAsync(attempt, cancellationToken);
    }

    /// <summary>
    /// Broadcasts the current persisted payload. After an unknown outcome, a
    /// retry loads and sends the same bytes instead of signing another payment.
    /// </summary>
    public Task<LifecycleCommitResult> BroadcastAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default) =>
        OperationalExecution.ObserveAsync(
            _telemetry,
            OperationalComponent.TransactionLifecycle,
            OperationalAction.TransactionBroadcast,
            token => BroadcastCoreAsync(operationId, token),
            ClassifyResult,
            ClassifyFailure,
            cancellationToken);

    private async Task<LifecycleCommitResult> BroadcastCoreAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        TransactionLifecycleSnapshot snapshot = await RequireSnapshotAsync(operationId, cancellationToken);
        if (snapshot.State is TransactionLifecycleState.Submitted or
            TransactionLifecycleState.MinedSucceeded or TransactionLifecycleState.MinedReverted)
        {
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, snapshot);
        }

        if (snapshot.State is TransactionLifecycleState.Reserved or TransactionLifecycleState.Rejected)
        {
            throw new TransactionLifecycleException(
                $"Operation '{operationId.Value}' has no broadcastable current attempt.");
        }

        TransactionAttemptPayload current = await _store.GetCurrentPayloadAsync(operationId, cancellationToken)
            ?? throw new TransactionLifecycleException("The signed current attempt is missing.");
        TransactionBroadcastOutcome outcome = await BroadcastAsync(current.Payload, cancellationToken);
        var observation = new BroadcastObservationCommand(
            operationId, current.Summary.AttemptId, current.Summary.TransactionHash,
            outcome, _timeProvider.GetUtcNow());
        return await _store.AppendBroadcastAsync(observation, cancellationToken);
    }

    /// <summary>Signs a fee-only replacement that preserves nonce and payment calldata.</summary>
    public Task<LifecycleCommitResult> ReplaceAsync(
        TransactionOperationId operationId,
        TransactionFeeQuote replacementFee,
        CancellationToken cancellationToken = default) =>
        OperationalExecution.ObserveAsync(
            _telemetry,
            OperationalComponent.TransactionLifecycle,
            OperationalAction.TransactionReplace,
            token => ReplaceCoreAsync(operationId, replacementFee, token),
            ClassifyResult,
            ClassifyFailure,
            cancellationToken);

    private async Task<LifecycleCommitResult> ReplaceCoreAsync(
        TransactionOperationId operationId,
        TransactionFeeQuote replacementFee,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(replacementFee);
        TransactionLifecycleSnapshot snapshot = await RequireSnapshotAsync(operationId, cancellationToken);
        if (snapshot.State is TransactionLifecycleState.MinedSucceeded or TransactionLifecycleState.MinedReverted)
        {
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, snapshot);
        }

        if (snapshot.State is TransactionLifecycleState.Reserved or TransactionLifecycleState.Signed)
        {
            throw new TransactionLifecycleException(
                "A replacement requires an earlier broadcast observation.");
        }

        if (snapshot.AttemptCount >= _policy.MaxAttemptsPerOperation)
        {
            throw new TransactionLifecycleException(
                "The operation already reached the lifecycle attempt limit.");
        }

        TransactionAttemptPayload current = await _store.GetCurrentPayloadAsync(operationId, cancellationToken)
            ?? throw new TransactionLifecycleException("The current attempt is missing.");
        _policy.ValidateReplacement(current.Summary.Fee, replacementFee);
        string calldata = Encode(snapshot).Data;
        UnsignedPaymentTransaction unsigned = BuildUnsigned(snapshot, replacementFee, calldata);
        SignedTransactionPayload payload = await SignAsync(unsigned, cancellationToken);
        var replacement = new PreparedTransactionAttempt(
            operationId, snapshot.AttemptCount, replacementFee, payload,
            TransactionLifecycleFingerprint.ForUnsigned(unsigned), _timeProvider.GetUtcNow());
        return await _store.CommitAttemptAsync(replacement, cancellationToken);
    }

    /// <summary>Queries every possibly submitted same-nonce attempt and records at most one receipt.</summary>
    public Task<LifecycleCommitResult> RefreshReceiptAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken = default) =>
        OperationalExecution.ObserveAsync(
            _telemetry,
            OperationalComponent.TransactionLifecycle,
            OperationalAction.TransactionRefreshReceipt,
            token => RefreshReceiptCoreAsync(operationId, token),
            ClassifyResult,
            ClassifyFailure,
            cancellationToken);

    private async Task<LifecycleCommitResult> RefreshReceiptCoreAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        TransactionLifecycleSnapshot snapshot = await RequireSnapshotAsync(operationId, cancellationToken);
        if (snapshot.State is TransactionLifecycleState.MinedSucceeded or TransactionLifecycleState.MinedReverted)
        {
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, snapshot);
        }

        IReadOnlyList<TransactionAttemptPayload> attempts = await _store.GetPayloadsAsync(operationId, cancellationToken);
        var observed = new List<(TransactionAttemptPayload Attempt, TransactionReceiptObservation Receipt)>();
        foreach (TransactionAttemptPayload attempt in attempts.Where(item =>
            item.Summary.LatestBroadcastOutcome is TransactionBroadcastOutcomeKind.Accepted or
                TransactionBroadcastOutcomeKind.AlreadyKnown or TransactionBroadcastOutcomeKind.Unknown))
        {
            TransactionReceiptObservation? receipt = await ReadReceiptAsync(
                attempt.Summary.TransactionHash, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.TransactionHash != attempt.Summary.TransactionHash)
                {
                    throw new TransactionLifecycleException(
                        "The receipt reader returned a receipt for another transaction hash.");
                }

                observed.Add((attempt, receipt));
            }
        }

        if (observed.Count == 0)
        {
            return new LifecycleCommitResult(LifecycleCommitDisposition.NoWork, snapshot);
        }

        if (observed.Count > 1)
        {
            throw new TransactionLifecycleException(
                "RPC reported multiple mined transactions for one signer nonce.");
        }

        (TransactionAttemptPayload minedAttempt, TransactionReceiptObservation minedReceipt) = observed[0];
        var command = new ReceiptObservationCommand(
            operationId, minedAttempt.Summary.AttemptId, minedReceipt, _timeProvider.GetUtcNow());
        return await _store.AppendReceiptAsync(command, cancellationToken);
    }

    private EncodedPaymentRouterCall Encode(PaymentTransactionRequest request) =>
        _router.EncodePay(
            request.PaymentId, request.Token.Value, request.Merchant.Value, request.Amount);

    private EncodedPaymentRouterCall Encode(TransactionLifecycleSnapshot snapshot) =>
        _router.EncodePay(
            snapshot.PaymentId, snapshot.Token.Value, snapshot.Merchant.Value, snapshot.Amount);

    private UnsignedPaymentTransaction BuildUnsigned(
        TransactionLifecycleSnapshot snapshot,
        TransactionFeeQuote fee,
        string calldata) =>
        new(_policy.ChainId, _policy.Signer, _policy.Router, snapshot.Nonce,
            snapshot.GasLimit, fee.MaxFeePerGasWei, fee.MaxPriorityFeePerGasWei, calldata);

    private async Task<long> ReadPendingNonceAsync(CancellationToken cancellationToken)
    {
        try
        {
            long nonce = await _nonceReader.GetPendingNonceAsync(
                _policy.ChainId, _policy.Signer, cancellationToken);
            return nonce < 0
                ? throw new TransactionLifecycleException("RPC returned a negative pending nonce.")
                : nonce;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TransactionLifecycleException) { throw; }
        catch (Exception)
        {
            // RPC exceptions can contain a credential-bearing endpoint. Keep
            // that detail inside the adapter's private diagnostics boundary.
            throw new TransactionLifecycleException("Pending nonce observation failed.");
        }
    }

    private async Task<SignedTransactionPayload> SignAsync(
        UnsignedPaymentTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _signer.SignAsync(transaction, cancellationToken)
                ?? throw new TransactionLifecycleException("The signer returned no signed payload.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TransactionLifecycleException) { throw; }
        catch (Exception)
        {
            // A signer exception is untrusted: some libraries embed transaction
            // bytes or key-provider details in its message/inner exceptions.
            throw new TransactionLifecycleException("Test transaction signing failed.");
        }
    }

    private async Task<TransactionBroadcastOutcome> BroadcastAsync(
        SignedTransactionPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _broadcaster.BroadcastAsync(_policy.ChainId, payload, cancellationToken)
                ?? throw new TransactionLifecycleException("The broadcaster returned no outcome.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A transport exception can happen after the node accepted the
            // bytes. Persist an unknown observation; never invite a new nonce
            // or a new payment merely because the response was lost.
            return new TransactionBroadcastOutcome(
                TransactionBroadcastOutcomeKind.Unknown, "transport_error");
        }
    }

    private async Task<TransactionReceiptObservation?> ReadReceiptAsync(
        TransactionHash hash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _receiptReader.GetReceiptAsync(_policy.ChainId, hash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // As with nonce RPC failures, do not let a credential-bearing
            // endpoint escape through an inner exception.
            throw new TransactionLifecycleException(
                $"Receipt observation failed for transaction {hash.Value}.");
        }
    }

    private async Task<TransactionLifecycleSnapshot> RequireSnapshotAsync(
        TransactionOperationId operationId,
        CancellationToken cancellationToken) =>
        await _store.GetAsync(operationId, cancellationToken)
        ?? throw new KeyNotFoundException($"Transaction operation '{operationId.Value}' was not found.");

    private static OperationalOutcome ClassifyResult(LifecycleCommitResult result) =>
        result.Disposition switch
        {
            LifecycleCommitDisposition.Applied => OperationalOutcome.Applied,
            LifecycleCommitDisposition.Replayed => OperationalOutcome.Replayed,
            LifecycleCommitDisposition.NoWork => OperationalOutcome.NoWork,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result), result.Disposition, "Unknown lifecycle disposition."),
        };

    private static OperationalFailureCode ClassifyFailure(Exception exception) => exception switch
    {
        ArgumentException or KeyNotFoundException => OperationalFailureCode.InvalidInput,
        TransactionLifecycleConflictException => OperationalFailureCode.StateConflict,
        // This legacy exception intentionally carries sanitized workflow text
        // but no machine-readable subtype. Keep the metric equally coarse and
        // never inspect its message to guess a more detailed category.
        TransactionLifecycleException => OperationalFailureCode.WorkflowRejected,
        Microsoft.Data.Sqlite.SqliteException => OperationalFailureCode.PersistenceFailure,
        _ => OperationalFailureCode.Unexpected,
    };
}
