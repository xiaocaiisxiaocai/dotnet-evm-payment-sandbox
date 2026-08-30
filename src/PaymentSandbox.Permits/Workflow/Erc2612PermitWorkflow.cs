using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Permits.Erc2612;
using PaymentSandbox.Permits.Persistence;
using PaymentSandbox.Permits.Preflight;

namespace PaymentSandbox.Permits.Workflow;

/// <summary>
/// Coordinates exact-block nonce observation, durable reservation, external
/// signature verification, and fail-safe release of Router calldata.
/// </summary>
public sealed class Erc2612PermitWorkflow
{
    private readonly Erc2612PermitService _permitService;
    private readonly Erc2612PermitPreflightService _preflight;
    private readonly SqlitePermitWorkflowStore _store;
    private readonly TimeProvider _timeProvider;

    public Erc2612PermitWorkflow(
        Erc2612PermitService permitService,
        Erc2612PermitPreflightService preflight,
        SqlitePermitWorkflowStore store,
        TimeProvider timeProvider)
    {
        _permitService = permitService ?? throw new ArgumentNullException(nameof(permitService));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Observes the token nonce and atomically reserves it before returning
    /// wallet-readable typed data. Concurrent callers sharing the database can
    /// create only one operation for the same chain/token/owner/nonce tuple.
    /// </summary>
    public async Task<PermitWorkflowCommitResult> ReserveAsync(
        EvmAddress owner,
        RawTokenAmount value,
        CancellationToken cancellationToken = default)
    {
        VerifiedErc2612TokenSnapshot observation = await _preflight.ObserveAsync(
            owner, cancellationToken).ConfigureAwait(false);
        Erc2612PermitDraft draft = _permitService.CreateDraft(
            owner, value, observation.Nonce);
        var command = new PermitReservationCommand(
            PermitOperationId.New(), draft, observation, _timeProvider.GetUtcNow());
        return await _store.ReserveAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the external signature, constructs exact Router calldata, and
    /// persists the signature-bearing bytes for restart-safe identical retry.
    /// </summary>
    public async Task<PermitWorkflowCommitResult> VerifyAndPrepareAsync(
        PermitOperationId operationId,
        string signature,
        VerifiedPaymentRouterClient router,
        PaymentId paymentId,
        EvmAddress merchant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        PermitWorkflowSnapshot snapshot = await RequireAsync(operationId, cancellationToken);
        if (snapshot.State is not (PermitWorkflowState.Reserved or PermitWorkflowState.Prepared))
        {
            throw new PermitWorkflowException(
                "Only a reserved or identically prepared permit can be prepared.");
        }

        VerifiedErc2612Permit verified = _permitService.Verify(snapshot.Draft, signature);
        PreparedErc2612Payment payment = _permitService.PreparePayment(
            router, paymentId, merchant, verified);
        var command = new PermitPreparationCommand(
            operationId, snapshot.LatestTransitionId, payment, _timeProvider.GetUtcNow());
        return await _store.PrepareAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rechecks expiry and current token nonce, then persists an unknown marker
    /// before releasing calldata. A crash after return therefore resumes as an
    /// ambiguous submission and cannot silently create a new permit.
    /// </summary>
    public async Task<PermitSubmissionAuthorization?> BeginSubmissionAsync(
        PermitOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        PermitWorkflowSnapshot snapshot = await RequireAsync(operationId, cancellationToken);
        if (snapshot.State != PermitWorkflowState.Prepared)
        {
            return null;
        }

        return await AuthorizeAfterPreflightAsync(
            snapshot, PermitWorkflowState.Prepared, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly retries an ambiguous submission with the exact persisted
    /// calldata. The caller must pass the transition it actually observed;
    /// competing callers that saw that same transition cannot both win the
    /// database compare-and-append operation.
    /// </summary>
    public async Task<PermitSubmissionAuthorization?> RetryUnknownAsync(
        PermitOperationId operationId,
        long observedTransitionId,
        CancellationToken cancellationToken = default)
    {
        if (observedTransitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedTransitionId));
        }

        PermitWorkflowSnapshot snapshot = await RequireAsync(operationId, cancellationToken);
        if (snapshot.State != PermitWorkflowState.SubmissionUnknown ||
            snapshot.LatestTransitionId != observedTransitionId)
        {
            return null;
        }

        return await AuthorizeAfterPreflightAsync(
            snapshot, PermitWorkflowState.SubmissionUnknown, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records only the transport result belonging to this exact authorization.
    /// Accepted is not a receipt, nonce-consumption proof, finality, or payment.
    /// Unknown needs no write because it was persisted before calldata escaped.
    /// </summary>
    public ValueTask<PermitWorkflowCommitResult> RecordSubmissionOutcomeAsync(
        PermitSubmissionAuthorization authorization,
        PermitSubmissionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return _store.RecordOutcomeAsync(
            authorization.OperationId,
            authorization.AuthorizationTransitionId,
            outcome,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    /// <summary>
    /// Reobserves usability without inferring which transaction advanced a
    /// token nonce. A changed nonce is recorded as changed, never as consumed.
    /// </summary>
    public async Task<PermitWorkflowCommitResult> RefreshUsabilityAsync(
        PermitOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        PermitWorkflowSnapshot snapshot = await RequireAsync(operationId, cancellationToken);
        if (snapshot.State is PermitWorkflowState.Expired or
            PermitWorkflowState.NonceChanged or
            PermitWorkflowState.SubmissionRejected)
        {
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.NoWork, snapshot);
        }

        if (IsExpired(snapshot))
        {
            return await _store.RecordTerminalAsync(
                operationId,
                snapshot.LatestTransitionId,
                PermitWorkflowState.Expired,
                observation: null,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        VerifiedErc2612TokenSnapshot current = await _preflight.ObserveAsync(
            snapshot.Draft.Owner, cancellationToken).ConfigureAwait(false);
        if (current.Nonce == snapshot.Draft.Nonce)
        {
            return new PermitWorkflowCommitResult(
                PermitWorkflowCommitDisposition.NoWork, snapshot);
        }

        return await _store.RecordTerminalAsync(
            operationId,
            snapshot.LatestTransitionId,
            PermitWorkflowState.NonceChanged,
            current,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PermitSubmissionAuthorization?> AuthorizeAfterPreflightAsync(
        PermitWorkflowSnapshot snapshot,
        PermitWorkflowState expectedState,
        CancellationToken cancellationToken)
    {
        if (IsExpired(snapshot))
        {
            await _store.RecordTerminalAsync(
                snapshot.OperationId,
                snapshot.LatestTransitionId,
                PermitWorkflowState.Expired,
                observation: null,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        VerifiedErc2612TokenSnapshot current = await _preflight.ObserveAsync(
            snapshot.Draft.Owner, cancellationToken).ConfigureAwait(false);
        if (current.Nonce != snapshot.Draft.Nonce)
        {
            await _store.RecordTerminalAsync(
                snapshot.OperationId,
                snapshot.LatestTransitionId,
                PermitWorkflowState.NonceChanged,
                current,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        (_, PermitSubmissionAuthorization? authorization) = await _store.AuthorizeAsync(
            snapshot.OperationId,
            snapshot.LatestTransitionId,
            expectedState,
            current,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return authorization;
    }

    private bool IsExpired(PermitWorkflowSnapshot snapshot) =>
        _timeProvider.GetUtcNow().ToUniversalTime() >= snapshot.Draft.DeadlineUtc;

    private async Task<PermitWorkflowSnapshot> RequireAsync(
        PermitOperationId operationId,
        CancellationToken cancellationToken) =>
        await _store.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException(
            $"Permit operation '{operationId.Value}' was not found.");
}
