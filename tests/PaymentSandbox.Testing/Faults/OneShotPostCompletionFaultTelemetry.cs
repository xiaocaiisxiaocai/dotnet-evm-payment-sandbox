using PaymentSandbox.Observability;

namespace PaymentSandbox.Testing.Faults;

/// <summary>
/// Injects one deterministic failure after a selected workflow operation has
/// completed successfully but before its result can reach the caller.
/// </summary>
/// <remarks>
/// This models a process/transport response-loss window without weakening the
/// production persistence code or using timing-dependent cancellation. The
/// wrapped observer still receives the failure classification emitted by
/// <see cref="OperationalExecution"/> after the injected exception is thrown.
/// </remarks>
public sealed class OneShotPostCompletionFaultTelemetry(
    IOperationalTelemetry inner) : IOperationalTelemetry
{
    private readonly object _gate = new();
    private readonly IOperationalTelemetry _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private OperationalAction? _armedAction;
    private OperationalOutcome? _armedOutcome;
    private int _triggerCount;

    public int TriggerCount
    {
        get
        {
            lock (_gate)
            {
                return _triggerCount;
            }
        }
    }

    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _armedAction is not null;
            }
        }
    }

    /// <summary>
    /// Arms exactly one action/outcome pair. Requiring the expected outcome
    /// prevents a test from accidentally failing at a replay/no-work branch.
    /// </summary>
    public void Arm(OperationalAction action, OperationalOutcome expectedOutcome)
    {
        if (expectedOutcome is OperationalOutcome.Cancelled or OperationalOutcome.Failed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedOutcome), expectedOutcome,
                "Post-completion faults require a successful workflow outcome.");
        }

        lock (_gate)
        {
            if (_armedAction is not null)
            {
                throw new InvalidOperationException(
                    "A deterministic post-completion fault is already armed.");
            }

            _armedAction = action;
            _armedOutcome = expectedOutcome;
        }
    }

    public IOperationalTelemetryOperation BeginOperation(
        OperationalComponent component,
        OperationalAction action) => new Operation(
            this,
            action,
            _inner.BeginOperation(component, action));

    private bool TryTrigger(OperationalAction action, OperationalOutcome outcome)
    {
        lock (_gate)
        {
            if (_armedAction != action || _armedOutcome != outcome)
            {
                return false;
            }

            _armedAction = null;
            _armedOutcome = null;
            _triggerCount++;
            return true;
        }
    }

    private sealed class Operation(
        OneShotPostCompletionFaultTelemetry owner,
        OperationalAction action,
        IOperationalTelemetryOperation inner) : IOperationalTelemetryOperation
    {
        private readonly IOperationalTelemetryOperation _inner = inner;

        public void Complete(OperationalOutcome outcome)
        {
            // OperationalExecution reaches this point only after its async
            // business operation and result classification have succeeded.
            // Throw before completing the inner observer so its subsequent
            // Fail call remains balanced and records the injected loss.
            if (owner.TryTrigger(action, outcome))
            {
                throw new InjectedPostCompletionFaultException(action, outcome);
            }

            _inner.Complete(outcome);
        }

        public void Fail(OperationalFailureCode failureCode) =>
            _inner.Fail(failureCode);

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>A test-only, non-sensitive marker for an injected response loss.</summary>
public sealed class InjectedPostCompletionFaultException(
    OperationalAction action,
    OperationalOutcome completedOutcome)
    : Exception($"Injected response loss after {action} completed as {completedOutcome}.")
{
    public OperationalAction Action { get; } = action;

    public OperationalOutcome CompletedOutcome { get; } = completedOutcome;
}
