using PaymentSandbox.Contracts.PaymentRouter;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Domain.Payments;
using PaymentSandbox.Observability;
using PaymentSandbox.Permits.Tests.Infrastructure;
using PaymentSandbox.Permits.Workflow;
using PaymentSandbox.Testing.Faults;

namespace PaymentSandbox.Permits.Tests.Workflow;

public sealed class Erc2612PermitWorkflowTests
{
    [Fact]
    public async Task BoundaryTelemetry_ClassifiesReservationReplayWithoutPermitMaterial()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var telemetry = new RecordingOperationalTelemetry();
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(
                temporary, telemetry: telemetry);

        await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(42), TestContext.Current.CancellationToken);
        await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(42), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new OperationalObservation(
                    OperationalComponent.PermitWorkflow,
                    OperationalAction.PermitReserve,
                    OperationalOutcome.Created,
                    OperationalFailureCode.None),
                new OperationalObservation(
                    OperationalComponent.PermitWorkflow,
                    OperationalAction.PermitReserve,
                    OperationalOutcome.Replayed,
                    OperationalFailureCode.None),
            ],
            telemetry.Observations);
    }

    [Fact]
    public async Task ReserveCommitResponseLoss_IdenticalRetryReplaysDurableOperation()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var fault = new OneShotPostCompletionFaultTelemetry(
            new RecordingOperationalTelemetry());
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(
                temporary, telemetry: fault);
        fault.Arm(OperationalAction.PermitReserve, OperationalOutcome.Created);

        await Assert.ThrowsAsync<InjectedPostCompletionFaultException>(() =>
            components.Workflow.ReserveAsync(
                wallet.Address,
                new RawTokenAmount(42),
                TestContext.Current.CancellationToken));

        PermitWorkflowCommitResult retry = await components.Workflow.ReserveAsync(
            wallet.Address,
            new RawTokenAmount(42),
            TestContext.Current.CancellationToken);

        Assert.Equal(PermitWorkflowCommitDisposition.Replayed, retry.Disposition);
        Assert.Equal(PermitWorkflowState.Reserved, retry.Snapshot.State);
        Assert.Equal(2, components.Rpc.Calls);
        Assert.Equal(1, fault.TriggerCount);
    }

    [Fact]
    public async Task PrepareCommitResponseLoss_IdenticalRetryReplaysPreparation()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var fault = new OneShotPostCompletionFaultTelemetry(
            new RecordingOperationalTelemetry());
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(
                temporary, telemetry: fault);
        PermitWorkflowCommitResult reserved = await components.Workflow.ReserveAsync(
            wallet.Address,
            new RawTokenAmount(1_250_000),
            TestContext.Current.CancellationToken);
        string signature = wallet.Sign(reserved.Snapshot.Draft);
        VerifiedPaymentRouterClient router = await PermitWorkflowTestData.RouterAsync();
        PaymentId paymentId = PaymentId.New();
        EvmAddress merchant = EvmAddress.Parse(PermitWorkflowTestData.MerchantAddress);
        fault.Arm(OperationalAction.PermitPrepare, OperationalOutcome.Applied);

        await Assert.ThrowsAsync<InjectedPostCompletionFaultException>(() =>
            components.Workflow.VerifyAndPrepareAsync(
                reserved.Snapshot.OperationId,
                signature,
                router,
                paymentId,
                merchant,
                TestContext.Current.CancellationToken));

        PermitWorkflowSnapshot durable = Assert.IsType<PermitWorkflowSnapshot>(
            await components.Store.GetAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));
        Assert.Equal(PermitWorkflowState.Prepared, durable.State);
        string durableCalldata = Assert.IsType<PermitPaymentPreparation>(
            durable.Preparation).Calldata;

        PermitWorkflowCommitResult retry =
            await components.Workflow.VerifyAndPrepareAsync(
                reserved.Snapshot.OperationId,
                signature,
                router,
                paymentId,
                merchant,
                TestContext.Current.CancellationToken);

        Assert.Equal(PermitWorkflowCommitDisposition.Replayed, retry.Disposition);
        Assert.Equal(durableCalldata, retry.Snapshot.Preparation?.Calldata);
    }

    [Fact]
    public async Task AuthorizationCommitResponseLoss_RetryReleasesExactPersistedBytes()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var fault = new OneShotPostCompletionFaultTelemetry(
            new RecordingOperationalTelemetry());
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) =
            await CreatePreparedAsync(temporary, fault);
        fault.Arm(
            OperationalAction.PermitBeginSubmission,
            OperationalOutcome.Applied);

        await Assert.ThrowsAsync<InjectedPostCompletionFaultException>(() =>
            components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));

        PermitWorkflowSnapshot durable = Assert.IsType<PermitWorkflowSnapshot>(
            await components.Store.GetAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));
        Assert.Equal(PermitWorkflowState.SubmissionUnknown, durable.State);
        PermitPaymentPreparation preparation =
            Assert.IsType<PermitPaymentPreparation>(durable.Preparation);

        PermitSubmissionAuthorization retry = Assert.IsType<PermitSubmissionAuthorization>(
            await components.Workflow.RetryUnknownAsync(
                reserved.Snapshot.OperationId,
                durable.LatestTransitionId,
                TestContext.Current.CancellationToken));

        Assert.Equal(preparation.Calldata, retry.Calldata);
        Assert.Equal(2, retry.AuthorizationSequence);
        Assert.Equal(PermitWorkflowState.SubmissionUnknown,
            (await components.Store.GetAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken))?.State);
    }

    [Theory]
    [InlineData(PermitSubmissionOutcome.Accepted, PermitWorkflowState.SubmissionAccepted)]
    [InlineData(PermitSubmissionOutcome.Rejected, PermitWorkflowState.SubmissionRejected)]
    public async Task OutcomeCommitResponseLoss_ExactRetryIsReplayed(
        PermitSubmissionOutcome outcome,
        PermitWorkflowState expectedState)
    {
        await using var temporary = new TemporaryPermitDatabase();
        var fault = new OneShotPostCompletionFaultTelemetry(
            new RecordingOperationalTelemetry());
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) =
            await CreatePreparedAsync(temporary, fault);
        PermitSubmissionAuthorization authorization =
            Assert.IsType<PermitSubmissionAuthorization>(
                await components.Workflow.BeginSubmissionAsync(
                    reserved.Snapshot.OperationId,
                    TestContext.Current.CancellationToken));
        fault.Arm(OperationalAction.PermitRecordOutcome, OperationalOutcome.Applied);

        await Assert.ThrowsAsync<InjectedPostCompletionFaultException>(async () =>
            await components.Workflow.RecordSubmissionOutcomeAsync(
                authorization,
                outcome,
                TestContext.Current.CancellationToken));

        PermitWorkflowSnapshot durable = Assert.IsType<PermitWorkflowSnapshot>(
            await components.Store.GetAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));
        Assert.Equal(expectedState, durable.State);
        long durableTransitionId = durable.LatestTransitionId;

        PermitWorkflowCommitResult retry =
            await components.Workflow.RecordSubmissionOutcomeAsync(
                authorization,
                outcome,
                TestContext.Current.CancellationToken);

        Assert.Equal(PermitWorkflowCommitDisposition.Replayed, retry.Disposition);
        Assert.Equal(durableTransitionId, retry.Snapshot.LatestTransitionId);
        Assert.Equal(expectedState, retry.Snapshot.State);

        PermitSubmissionOutcome opposite = outcome == PermitSubmissionOutcome.Accepted
            ? PermitSubmissionOutcome.Rejected
            : PermitSubmissionOutcome.Accepted;
        await Assert.ThrowsAsync<PermitWorkflowException>(async () =>
            await components.Workflow.RecordSubmissionOutcomeAsync(
                authorization,
                opposite,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TerminalCommitResponseLoss_RetryReturnsNoWorkWithoutAnotherRpcRead()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var fault = new OneShotPostCompletionFaultTelemetry(
            new RecordingOperationalTelemetry());
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) =
            await CreatePreparedAsync(temporary, fault);
        int callsBefore = components.Rpc.Calls;
        components.Clock.Advance(TimeSpan.FromMinutes(10));
        fault.Arm(
            OperationalAction.PermitRefreshUsability,
            OperationalOutcome.Applied);

        await Assert.ThrowsAsync<InjectedPostCompletionFaultException>(() =>
            components.Workflow.RefreshUsabilityAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));

        PermitWorkflowSnapshot durable = Assert.IsType<PermitWorkflowSnapshot>(
            await components.Store.GetAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken));
        Assert.Equal(PermitWorkflowState.Expired, durable.State);

        PermitWorkflowCommitResult retry =
            await components.Workflow.RefreshUsabilityAsync(
                reserved.Snapshot.OperationId,
                TestContext.Current.CancellationToken);

        Assert.Equal(PermitWorkflowCommitDisposition.NoWork, retry.Disposition);
        Assert.Equal(callsBefore, components.Rpc.Calls);
    }

    [Fact]
    public async Task Reserve_UsesObservedNonceAndSurvivesRestart()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture first =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary);

        PermitWorkflowCommitResult reserved = await first.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(1_250_000),
            TestContext.Current.CancellationToken);
        PermitWorkflowTestData.WorkflowFixture restarted =
            await PermitWorkflowTestData.CreateWorkflowAsync(
                temporary, first.Rpc, first.Clock);
        PermitWorkflowSnapshot? loaded = await restarted.Store.GetAsync(
            reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);

        Assert.Equal(PermitWorkflowCommitDisposition.Created, reserved.Disposition);
        Assert.Equal(7, reserved.Snapshot.Draft.Nonce);
        Assert.Equal(PermitWorkflowState.Reserved, loaded?.State);
        Assert.Equal(reserved.Snapshot.Draft.Digest, loaded?.Draft.Digest);
        Assert.DoesNotContain(
            reserved.Snapshot.Draft.TypedDataJson,
            loaded!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSameNonceReservation_ReplaysOneOperation()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary);

        PermitWorkflowCommitResult[] results = await Task.WhenAll(
            components.Workflow.ReserveAsync(
                wallet.Address, new RawTokenAmount(99), TestContext.Current.CancellationToken),
            components.Workflow.ReserveAsync(
                wallet.Address, new RawTokenAmount(99), TestContext.Current.CancellationToken));

        Assert.Single(results, result =>
            result.Disposition == PermitWorkflowCommitDisposition.Created);
        Assert.Single(results, result =>
            result.Disposition == PermitWorkflowCommitDisposition.Replayed);
        Assert.Single(results.Select(result => result.Snapshot.OperationId).Distinct());
    }

    [Fact]
    public async Task SameObservedNonceWithDifferentAmount_Conflicts()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary);
        await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(10), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PermitWorkflowException>(() =>
            components.Workflow.ReserveAsync(
                wallet.Address, new RawTokenAmount(11), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrepareAndBegin_PersistsUnknownBeforeReleasingRedactedCalldata()
    {
        await using var temporary = new TemporaryPermitDatabase();
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(temporary);
        PermitWorkflowCommitResult reserved = await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(1_250_000),
            TestContext.Current.CancellationToken);
        await components.Workflow.VerifyAndPrepareAsync(
            reserved.Snapshot.OperationId,
            wallet.Sign(reserved.Snapshot.Draft),
            await PermitWorkflowTestData.RouterAsync(),
            PaymentId.New(),
            EvmAddress.Parse(PermitWorkflowTestData.MerchantAddress),
            TestContext.Current.CancellationToken);

        PermitSubmissionAuthorization? authorization =
            await components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);
        PermitWorkflowSnapshot? durable = await components.Store.GetAsync(
            reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);

        Assert.NotNull(authorization);
        Assert.Equal(PermitWorkflowState.SubmissionUnknown, durable?.State);
        Assert.Equal(authorization!.AuthorizationTransitionId, durable?.LatestTransitionId);
        Assert.Equal(wallet.Address, authorization.RequiredSender);
        Assert.StartsWith("0x1f2b568e", authorization.Calldata);
        Assert.DoesNotContain(
            authorization.Calldata, authorization.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            authorization.Calldata, durable!.Preparation!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentBegin_OnlyOneCallerReceivesCalldata()
    {
        await using var temporary = new TemporaryPermitDatabase();
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) = await CreatePreparedAsync(temporary);

        PermitSubmissionAuthorization?[] results = await Task.WhenAll(
            components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken),
            components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken));

        Assert.Single(results, result => result is not null);
    }

    [Fact]
    public async Task ConcurrentUnknownRetry_ReleasesExactBytesOncePerObservedTransition()
    {
        await using var temporary = new TemporaryPermitDatabase();
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) = await CreatePreparedAsync(temporary);
        PermitSubmissionAuthorization first = (await components.Workflow.BeginSubmissionAsync(
            reserved.Snapshot.OperationId, TestContext.Current.CancellationToken))!;

        PermitSubmissionAuthorization?[] retries = await Task.WhenAll(
            components.Workflow.RetryUnknownAsync(
                reserved.Snapshot.OperationId,
                first.AuthorizationTransitionId,
                TestContext.Current.CancellationToken),
            components.Workflow.RetryUnknownAsync(
                reserved.Snapshot.OperationId,
                first.AuthorizationTransitionId,
                TestContext.Current.CancellationToken));
        PermitSubmissionAuthorization retry = Assert.Single(retries, item => item is not null)!;

        Assert.Equal(first.Calldata, retry.Calldata);
        Assert.Equal(2, retry.AuthorizationSequence);
        await Assert.ThrowsAsync<PermitWorkflowException>(async () =>
            await components.Workflow.RecordSubmissionOutcomeAsync(
                first, PermitSubmissionOutcome.Accepted,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonceChangeBeforeSubmission_IsRecordedWithoutClaimingConsumption()
    {
        await using var temporary = new TemporaryPermitDatabase();
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) = await CreatePreparedAsync(temporary);
        components.Rpc.Nonce = 8;

        PermitSubmissionAuthorization? authorization =
            await components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);
        PermitWorkflowSnapshot? durable = await components.Store.GetAsync(
            reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);

        Assert.Null(authorization);
        Assert.Equal(PermitWorkflowState.NonceChanged, durable?.State);
        Assert.DoesNotContain("consumed", durable!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExactDeadline_ExpiresWithoutAnotherRpcRead()
    {
        await using var temporary = new TemporaryPermitDatabase();
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) = await CreatePreparedAsync(temporary);
        int callsBefore = components.Rpc.Calls;
        components.Clock.Advance(TimeSpan.FromMinutes(10));

        PermitSubmissionAuthorization? authorization =
            await components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);
        PermitWorkflowSnapshot? durable = await components.Store.GetAsync(
            reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);

        Assert.Null(authorization);
        Assert.Equal(PermitWorkflowState.Expired, durable?.State);
        Assert.Equal(callsBefore, components.Rpc.Calls);
    }

    [Fact]
    public async Task AcceptedOutcome_IsTransportEvidenceAndLaterNonceChangeRemainsAmbiguous()
    {
        await using var temporary = new TemporaryPermitDatabase();
        (PermitWorkflowTestData.WorkflowFixture components,
            PermitWorkflowCommitResult reserved) = await CreatePreparedAsync(temporary);
        PermitSubmissionAuthorization authorization =
            (await components.Workflow.BeginSubmissionAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken))!;

        PermitWorkflowCommitResult accepted =
            await components.Workflow.RecordSubmissionOutcomeAsync(
                authorization, PermitSubmissionOutcome.Accepted,
                TestContext.Current.CancellationToken);
        components.Rpc.Nonce = 8;
        PermitWorkflowCommitResult refreshed =
            await components.Workflow.RefreshUsabilityAsync(
                reserved.Snapshot.OperationId, TestContext.Current.CancellationToken);

        Assert.Equal(PermitWorkflowState.SubmissionAccepted, accepted.Snapshot.State);
        Assert.Equal(PermitWorkflowState.NonceChanged, refreshed.Snapshot.State);
    }

    private static async Task<(PermitWorkflowTestData.WorkflowFixture Components,
        PermitWorkflowCommitResult Reserved)> CreatePreparedAsync(
            TemporaryPermitDatabase temporary,
            IOperationalTelemetry? telemetry = null)
    {
        var wallet = new PermitWorkflowTestData.TestWallet();
        PermitWorkflowTestData.WorkflowFixture components =
            await PermitWorkflowTestData.CreateWorkflowAsync(
                temporary, telemetry: telemetry);
        PermitWorkflowCommitResult reserved = await components.Workflow.ReserveAsync(
            wallet.Address, new RawTokenAmount(1_250_000),
            TestContext.Current.CancellationToken);
        await components.Workflow.VerifyAndPrepareAsync(
            reserved.Snapshot.OperationId,
            wallet.Sign(reserved.Snapshot.Draft),
            await PermitWorkflowTestData.RouterAsync(),
            PaymentId.New(),
            EvmAddress.Parse(PermitWorkflowTestData.MerchantAddress),
            TestContext.Current.CancellationToken);
        return (components, reserved);
    }
}
