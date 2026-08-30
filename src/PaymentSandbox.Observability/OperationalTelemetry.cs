using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PaymentSandbox.Observability;

/// <summary>
/// Provider-neutral tracing and metrics for high-value workflow boundaries.
/// Consumers can attach OpenTelemetry or another .NET listener without this
/// library depending on a collector, exporter, or logging framework.
/// </summary>
public sealed class OperationalTelemetry : IOperationalTelemetry
{
    public const string InstrumentationName = "PaymentSandbox.OperationalTelemetry";
    public const string InstrumentationVersion = "1.0.0";

    public static OperationalTelemetry Shared { get; } = new();

    private static readonly ActivitySource Activities =
        new(InstrumentationName, InstrumentationVersion);
    private static readonly Meter Metrics =
        new(InstrumentationName, InstrumentationVersion);
    private static readonly Counter<long> CompletedOperations = Metrics.CreateCounter<long>(
        "payment_sandbox.operation.completed",
        unit: "{operation}",
        description: "Completed workflow boundary calls grouped by bounded outcome.");
    private static readonly Histogram<double> OperationDuration = Metrics.CreateHistogram<double>(
        "payment_sandbox.operation.duration",
        unit: "ms",
        description: "Workflow boundary duration in milliseconds.");
    private static readonly UpDownCounter<long> ActiveOperations = Metrics.CreateUpDownCounter<long>(
        "payment_sandbox.operation.active",
        unit: "{operation}",
        description: "Workflow boundary calls currently executing.");

    private OperationalTelemetry()
    {
    }

    public IOperationalTelemetryOperation BeginOperation(
        OperationalComponent component,
        OperationalAction action)
    {
        string componentName = ToTagValue(component);
        string actionName = ToTagValue(action);
        return new Operation(componentName, actionName);
    }

    private sealed class Operation : IOperationalTelemetryOperation
    {
        private readonly string _component;
        private readonly string _action;
        private readonly TagList _activeTags;
        private readonly long _startedAt;
        private readonly Activity? _activity;
        private int _finished;

        internal Operation(string component, string action)
        {
            _component = component;
            _action = action;
            _activeTags = CreateActiveTags(component, action);
            _startedAt = Stopwatch.GetTimestamp();
            _activity = Activities.StartActivity(
                $"payment_sandbox.{action}", ActivityKind.Internal);
            _activity?.SetTag("payment_sandbox.component", component);
            _activity?.SetTag("payment_sandbox.action", action);
            ActiveOperations.Add(1, _activeTags);
        }

        public void Complete(OperationalOutcome outcome)
        {
            if (outcome is OperationalOutcome.Cancelled or OperationalOutcome.Failed)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outcome), outcome, "Use Fail for unsuccessful operations.");
            }

            Finish(outcome, OperationalFailureCode.None, ActivityStatusCode.Ok);
        }

        public void Fail(OperationalFailureCode failureCode)
        {
            if (failureCode == OperationalFailureCode.None)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(failureCode), failureCode, "A failed operation needs a failure code.");
            }

            OperationalOutcome outcome = failureCode == OperationalFailureCode.Cancelled
                ? OperationalOutcome.Cancelled
                : OperationalOutcome.Failed;
            Finish(outcome, failureCode, ActivityStatusCode.Error);
        }

        public void Dispose()
        {
            // A missing Complete/Fail call is an instrumentation bug. Record a
            // bounded failure instead of leaking an active measurement forever.
            if (Interlocked.CompareExchange(ref _finished, 1, 0) == 0)
            {
                RecordCompletion(
                    OperationalOutcome.Failed,
                    OperationalFailureCode.Unexpected,
                    ActivityStatusCode.Error);
            }
        }

        private void Finish(
            OperationalOutcome outcome,
            OperationalFailureCode failureCode,
            ActivityStatusCode activityStatus)
        {
            if (Interlocked.CompareExchange(ref _finished, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "An operational telemetry scope can complete only once.");
            }

            RecordCompletion(outcome, failureCode, activityStatus);
        }

        private void RecordCompletion(
            OperationalOutcome outcome,
            OperationalFailureCode failureCode,
            ActivityStatusCode activityStatus)
        {
            string outcomeName = ToTagValue(outcome);
            string failureName = ToTagValue(failureCode);
            _activity?.SetTag("payment_sandbox.outcome", outcomeName);
            _activity?.SetTag("payment_sandbox.failure_code", failureName);
            // No status description or exception event is attached. Both can
            // accidentally expose RPC credentials or signed payload material.
            _activity?.SetStatus(activityStatus);

            try
            {
                TagList completionTags = CreateCompletionTags(
                    _component, _action, outcomeName, failureName);
                CompletedOperations.Add(1, completionTags);
                OperationDuration.Record(
                    Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds,
                    completionTags);
            }
            finally
            {
                try
                {
                    ActiveOperations.Add(-1, _activeTags);
                }
                finally
                {
                    _activity?.Dispose();
                }
            }
        }
    }

    private static TagList CreateActiveTags(string component, string action) => new()
    {
        { "payment_sandbox.component", component },
        { "payment_sandbox.action", action },
    };

    private static TagList CreateCompletionTags(
        string component,
        string action,
        string outcome,
        string failureCode) => new()
    {
        { "payment_sandbox.component", component },
        { "payment_sandbox.action", action },
        { "payment_sandbox.outcome", outcome },
        { "payment_sandbox.failure_code", failureCode },
    };

    private static string ToTagValue(OperationalComponent value) => value switch
    {
        OperationalComponent.PermitWorkflow => "permit_workflow",
        OperationalComponent.TransactionLifecycle => "transaction_lifecycle",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown component."),
    };

    private static string ToTagValue(OperationalAction value) => value switch
    {
        OperationalAction.PermitReserve => "permit.reserve",
        OperationalAction.PermitPrepare => "permit.prepare",
        OperationalAction.PermitBeginSubmission => "permit.begin_submission",
        OperationalAction.PermitRetrySubmission => "permit.retry_submission",
        OperationalAction.PermitRecordOutcome => "permit.record_outcome",
        OperationalAction.PermitRefreshUsability => "permit.refresh_usability",
        OperationalAction.TransactionCreate => "transaction.create",
        OperationalAction.TransactionBroadcast => "transaction.broadcast",
        OperationalAction.TransactionReplace => "transaction.replace",
        OperationalAction.TransactionRefreshReceipt => "transaction.refresh_receipt",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown action."),
    };

    private static string ToTagValue(OperationalOutcome value) => value switch
    {
        OperationalOutcome.Created => "created",
        OperationalOutcome.Applied => "applied",
        OperationalOutcome.Replayed => "replayed",
        OperationalOutcome.NoWork => "no_work",
        OperationalOutcome.Cancelled => "cancelled",
        OperationalOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown outcome."),
    };

    private static string ToTagValue(OperationalFailureCode value) => value switch
    {
        OperationalFailureCode.None => "none",
        OperationalFailureCode.InvalidInput => "invalid_input",
        OperationalFailureCode.PolicyRejected => "policy_rejected",
        OperationalFailureCode.WorkflowRejected => "workflow_rejected",
        OperationalFailureCode.StateConflict => "state_conflict",
        OperationalFailureCode.PersistenceFailure => "persistence_failure",
        OperationalFailureCode.DependencyFailure => "dependency_failure",
        OperationalFailureCode.Cancelled => "cancelled",
        OperationalFailureCode.Unexpected => "unexpected",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown failure code."),
    };
}
