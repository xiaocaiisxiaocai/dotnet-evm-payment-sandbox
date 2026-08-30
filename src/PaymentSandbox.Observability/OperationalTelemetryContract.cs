namespace PaymentSandbox.Observability;

/// <summary>
/// A deliberately small list of workflow families. These values are safe for
/// metric labels because the list is fixed at compile time and cannot grow
/// with customers, wallets, transactions, or requests.
/// </summary>
public enum OperationalComponent
{
    PermitWorkflow,
    TransactionLifecycle,
}

/// <summary>
/// Stable operation names emitted by the two payload-release boundaries.
/// Never add an identifier, address, URL, or other runtime value here.
/// </summary>
public enum OperationalAction
{
    PermitReserve,
    PermitPrepare,
    PermitBeginSubmission,
    PermitRetrySubmission,
    PermitRecordOutcome,
    PermitRefreshUsability,
    TransactionCreate,
    TransactionBroadcast,
    TransactionReplace,
    TransactionRefreshReceipt,
}

/// <summary>
/// Bounded results that describe control flow without exposing business data.
/// A completed method may still report NoWork; that is useful capacity and
/// retry information, not an error.
/// </summary>
public enum OperationalOutcome
{
    Created,
    Applied,
    Replayed,
    NoWork,
    Cancelled,
    Failed,
}

/// <summary>
/// Coarse failure categories. Detailed exception messages stay inside the
/// process because adapters can include RPC credentials or signed payloads.
/// </summary>
public enum OperationalFailureCode
{
    None,
    InvalidInput,
    PolicyRejected,
    WorkflowRejected,
    StateConflict,
    PersistenceFailure,
    DependencyFailure,
    Cancelled,
    Unexpected,
}

/// <summary>
/// Starts one operational scope using only compile-time-bounded labels.
/// The observer never receives the business operation, its generic result, or
/// an exception object. This type-level separation prevents an injected
/// exporter from inspecting replay-sensitive workflow objects.
/// </summary>
public interface IOperationalTelemetry
{
    IOperationalTelemetryOperation BeginOperation(
        OperationalComponent component,
        OperationalAction action);
}

/// <summary>
/// Receives exactly one bounded completion. Dispose records an unexpected
/// failure if orchestration exits without an explicit completion.
/// </summary>
public interface IOperationalTelemetryOperation : IDisposable
{
    void Complete(OperationalOutcome outcome);

    void Fail(OperationalFailureCode failureCode);
}
