using PaymentSandbox.Observability;
using PaymentSandbox.Orchestrator.Abstractions;
using PaymentSandbox.Orchestrator.Lifecycle;
using PaymentSandbox.Orchestrator.Tests.Infrastructure;
using PaymentSandbox.Orchestrator.Transactions;

namespace PaymentSandbox.Orchestrator.Tests.Lifecycle;

public sealed class TransactionLifecycleProcessorTests
{
    [Fact]
    public async Task BoundaryTelemetry_ClassifiesMutationAndNoWorkWithoutPayloadLabels()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var telemetry = new RecordingOperationalTelemetry();
        var components = await OrchestratorTestData.CreateProcessorAsync(
            temporary, telemetry: telemetry);
        PaymentTransactionRequest request = OrchestratorTestData.Request();

        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new OperationalObservation(
                    OperationalComponent.TransactionLifecycle,
                    OperationalAction.TransactionCreate,
                    OperationalOutcome.Applied,
                    OperationalFailureCode.None),
                new OperationalObservation(
                    OperationalComponent.TransactionLifecycle,
                    OperationalAction.TransactionCreate,
                    OperationalOutcome.NoWork,
                    OperationalFailureCode.None),
            ],
            telemetry.Observations);
    }

    [Fact]
    public async Task Create_ReservesAndSignsWithoutBroadcasting()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);

        LifecycleCommitResult result = await components.Processor.CreateAsync(
            OrchestratorTestData.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(TransactionLifecycleState.Signed, result.Snapshot.State);
        Assert.Equal(7, result.Snapshot.Nonce);
        Assert.Equal(1, result.Snapshot.AttemptCount);
        Assert.Single(components.Signer.Transactions);
        Assert.StartsWith("0x76bbf425", components.Signer.Transactions[0].Data);
        Assert.Empty(components.Broadcaster.RawTransactions);
    }

    [Fact]
    public async Task CreateRetry_UsesDurableReservationWithoutNonceOrSignerReads()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();
        await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        components.Nonces.Failure = new IOException("RPC is offline");

        LifecycleCommitResult replay = await components.Processor.CreateAsync(
            request, TestContext.Current.CancellationToken);

        Assert.Equal(LifecycleCommitDisposition.NoWork, replay.Disposition);
        Assert.Equal(1, components.Nonces.Calls);
        Assert.Single(components.Signer.Transactions);
    }

    [Fact]
    public async Task TransportFailure_PersistsUnknownAndRetriesExactRawBytes()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new IOException("response lost"));
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Accepted, "accepted"));

        LifecycleCommitResult unknown = await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);
        LifecycleCommitResult accepted = await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(TransactionLifecycleState.BroadcastUnknown, unknown.Snapshot.State);
        Assert.Equal(TransactionLifecycleState.Submitted, accepted.Snapshot.State);
        Assert.Equal(2, accepted.Snapshot.BroadcastObservationCount);
        Assert.Equal(2, components.Broadcaster.RawTransactions.Count);
        Assert.Equal(components.Broadcaster.RawTransactions[0], components.Broadcaster.RawTransactions[1]);
        Assert.Equal(1, accepted.Snapshot.AttemptCount);
    }

    [Fact]
    public async Task AcceptedBroadcast_IsNotSentAgain()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);

        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);
        LifecycleCommitResult duplicate = await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(LifecycleCommitDisposition.NoWork, duplicate.Disposition);
        Assert.Single(components.Broadcaster.RawTransactions);
    }

    [Fact]
    public async Task Replacement_PreservesNonceAndCalldataWhileChangingOnlyFeesAndHash()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Unknown, "timeout"));
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);

        LifecycleCommitResult replacement = await components.Processor.ReplaceAsync(id, new TransactionFeeQuote(110, 11), TestContext.Current.CancellationToken);
        IReadOnlyList<TransactionAttemptSummary> attempts = await components.Store.GetAttemptsAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(TransactionLifecycleState.Signed, replacement.Snapshot.State);
        Assert.Equal(2, replacement.Snapshot.AttemptCount);
        Assert.Equal(attempts[0].Nonce, attempts[1].Nonce);
        Assert.NotEqual(attempts[0].TransactionHash, attempts[1].TransactionHash);
        Assert.Equal(components.Signer.Transactions[0].Data, components.Signer.Transactions[1].Data);
        Assert.Equal(components.Signer.Transactions[0].Destination, components.Signer.Transactions[1].Destination);
    }

    [Fact]
    public async Task ReplacementBelowBump_IsRejectedBeforeSigning()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Unknown, "timeout"));
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => components.Processor.ReplaceAsync(id, new TransactionFeeQuote(109, 11), TestContext.Current.CancellationToken));
        Assert.Single(components.Signer.Transactions);
    }

    [Theory]
    [InlineData(TransactionExecutionStatus.Succeeded, TransactionLifecycleState.MinedSucceeded)]
    [InlineData(TransactionExecutionStatus.Reverted, TransactionLifecycleState.MinedReverted)]
    public async Task ReceiptObservation_DerivesTerminalExecutionState(
        TransactionExecutionStatus execution,
        TransactionLifecycleState expected)
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);
        TransactionAttemptPayload attempt = (await components.Store.GetPayloadsAsync(id, TestContext.Current.CancellationToken)).Single();
        components.Receipts.Values[attempt.Summary.TransactionHash] = new TransactionReceiptObservation(
            attempt.Summary.TransactionHash, execution, 12,
            TransactionHash.Parse($"0x{new string('b', 64)}"), 80_000, 50);

        LifecycleCommitResult result = await components.Processor.RefreshReceiptAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Snapshot.State);
        Assert.Equal(12, result.Snapshot.MinedBlockNumber);
        Assert.Equal(attempt.Summary.TransactionHash, result.Snapshot.MinedTransactionHash);
    }

    [Fact]
    public async Task RejectedAttempt_RequiresReplacementInsteadOfExactRebroadcast()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Rejected, "fee_too_low"));
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TransactionLifecycleException>(
            () => components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken));
        Assert.Single(components.Broadcaster.RawTransactions);
    }

    [Fact]
    public async Task SignerFailure_LeavesReservationRecoverableWithoutAnotherNonceRead()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();
        components.Signer.Failure = new InvalidOperationException("test signer unavailable");

        await Assert.ThrowsAsync<TransactionLifecycleException>(
            () => components.Processor.CreateAsync(request, TestContext.Current.CancellationToken));
        TransactionLifecycleSnapshot reserved = Assert.IsType<TransactionLifecycleSnapshot>(
            await components.Store.GetAsync(request.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(TransactionLifecycleState.Reserved, reserved.State);
        components.Signer.Failure = null;
        components.Nonces.Failure = new IOException("RPC remains unavailable");

        LifecycleCommitResult recovered = await components.Processor.CreateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(TransactionLifecycleState.Signed, recovered.Snapshot.State);
        Assert.Equal(1, components.Nonces.Calls);
    }

    [Fact]
    public async Task AdapterFailures_DoNotExposeUntrustedInnerExceptionDetails()
    {
        const string sensitive = "0xfeedface-secret-or-credential-bearing-rpc-url";
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        components.Signer.Failure = new InvalidOperationException(sensitive);

        TransactionLifecycleException exception = await Assert.ThrowsAsync<TransactionLifecycleException>(
            () => components.Processor.CreateAsync(
                OrchestratorTestData.Request(), TestContext.Current.CancellationToken));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(sensitive, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SignedPayload_ToStringNeverReturnsRawBytes()
    {
        var payload = new SignedTransactionPayload("0x01020304");

        Assert.DoesNotContain(payload.RawTransaction, payload.ToString(), StringComparison.Ordinal);
        Assert.Contains("raw redacted", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSameOperation_LeavesOneIdenticalSignedAttempt()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        PaymentTransactionRequest request = OrchestratorTestData.Request();

        await Task.WhenAll(
            components.Processor.CreateAsync(request, TestContext.Current.CancellationToken),
            components.Processor.CreateAsync(request, TestContext.Current.CancellationToken));

        Assert.Single(await components.Store.GetAttemptsAsync(
            request.OperationId, TestContext.Current.CancellationToken));
        TransactionLifecycleSnapshot snapshot = Assert.IsType<TransactionLifecycleSnapshot>(
            await components.Store.GetAsync(request.OperationId, TestContext.Current.CancellationToken));
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.All(components.Signer.Transactions, value => Assert.Equal(7, value.Nonce));
    }

    [Fact]
    public async Task AttemptLimit_RejectsAnotherReplacementBeforeSigning()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(
            temporary, OrchestratorTestData.Policy(maxAttempts: 2));
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Unknown, "timeout"));
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);
        await components.Processor.ReplaceAsync(id, new TransactionFeeQuote(110, 11), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Unknown, "timeout"));
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TransactionLifecycleException>(() =>
            components.Processor.ReplaceAsync(id, new TransactionFeeQuote(121, 13), TestContext.Current.CancellationToken));
        Assert.Equal(2, components.Signer.Transactions.Count);
    }

    [Fact]
    public async Task MultipleSameNonceReceiptsFromRpc_AreRejectedWithoutCommit()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        components.Broadcaster.Results.Enqueue(new TransactionBroadcastOutcome(
            TransactionBroadcastOutcomeKind.Unknown, "timeout"));
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);
        await components.Processor.ReplaceAsync(id, new TransactionFeeQuote(110, 11), TestContext.Current.CancellationToken);
        await components.Processor.BroadcastAsync(id, TestContext.Current.CancellationToken);
        IReadOnlyList<TransactionAttemptPayload> attempts = await components.Store.GetPayloadsAsync(id, TestContext.Current.CancellationToken);
        foreach (TransactionAttemptPayload attempt in attempts)
        {
            components.Receipts.Values[attempt.Summary.TransactionHash] = new TransactionReceiptObservation(
                attempt.Summary.TransactionHash, TransactionExecutionStatus.Succeeded,
                12, TransactionHash.Parse($"0x{new string((char)('b' + attempt.Summary.Sequence), 64)}"),
                80_000, 50);
        }

        await Assert.ThrowsAsync<TransactionLifecycleException>(
            () => components.Processor.RefreshReceiptAsync(id, TestContext.Current.CancellationToken));
        TransactionLifecycleSnapshot snapshot = Assert.IsType<TransactionLifecycleSnapshot>(
            await components.Store.GetAsync(id, TestContext.Current.CancellationToken));
        Assert.Null(snapshot.MinedTransactionHash);
    }

    [Fact]
    public async Task AcceptedEvidence_DominatesALaterUnknownObservationForSameBytes()
    {
        await using var temporary = new TemporaryTransactionLifecycleDatabase();
        var components = await OrchestratorTestData.CreateProcessorAsync(temporary);
        TransactionOperationId id = OrchestratorTestData.Request().OperationId;
        await components.Processor.CreateAsync(
            OrchestratorTestData.Request(), TestContext.Current.CancellationToken);
        TransactionAttemptPayload attempt = Assert.Single(await components.Store.GetPayloadsAsync(
            id, TestContext.Current.CancellationToken));

        await components.Store.AppendBroadcastAsync(new BroadcastObservationCommand(
            id, attempt.Summary.AttemptId, attempt.Summary.TransactionHash,
            new TransactionBroadcastOutcome(TransactionBroadcastOutcomeKind.Accepted, "accepted"),
            OrchestratorTestData.Now), TestContext.Current.CancellationToken);
        LifecycleCommitResult result = await components.Store.AppendBroadcastAsync(
            new BroadcastObservationCommand(
                id, attempt.Summary.AttemptId, attempt.Summary.TransactionHash,
                new TransactionBroadcastOutcome(TransactionBroadcastOutcomeKind.Unknown, "timeout"),
                OrchestratorTestData.Now.AddSeconds(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionLifecycleState.Submitted, result.Snapshot.State);
        TransactionAttemptSummary summary = Assert.Single(await components.Store.GetAttemptsAsync(
            id, TestContext.Current.CancellationToken));
        Assert.Equal(TransactionBroadcastOutcomeKind.Accepted, summary.LatestBroadcastOutcome);
        Assert.Equal(2, summary.BroadcastObservationCount);
    }
}
