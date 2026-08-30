using PaymentSandbox.Observability;

namespace PaymentSandbox.Permits.Tests.Infrastructure;

/// <summary>
/// A payload-blind test observer: it retains classifications only, never the
/// permit result, signature, calldata, exception, or exception message.
/// </summary>
internal sealed class RecordingOperationalTelemetry : IOperationalTelemetry
{
    internal List<OperationalObservation> Observations { get; } = [];

    public IOperationalTelemetryOperation BeginOperation(
        OperationalComponent component,
        OperationalAction action) => new Operation(this, component, action);

    private sealed class Operation(
        RecordingOperationalTelemetry owner,
        OperationalComponent component,
        OperationalAction action) : IOperationalTelemetryOperation
    {
        private bool _finished;

        public void Complete(OperationalOutcome outcome)
        {
            Add(outcome, OperationalFailureCode.None);
        }

        public void Fail(OperationalFailureCode failureCode)
        {
            Add(
                failureCode == OperationalFailureCode.Cancelled
                    ? OperationalOutcome.Cancelled
                    : OperationalOutcome.Failed,
                failureCode);
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Add(OperationalOutcome.Failed, OperationalFailureCode.Unexpected);
            }
        }

        private void Add(OperationalOutcome outcome, OperationalFailureCode failureCode)
        {
            Assert.False(_finished);
            _finished = true;
            owner.Observations.Add(new OperationalObservation(
                component, action, outcome, failureCode));
        }
    }
}

internal sealed record OperationalObservation(
    OperationalComponent Component,
    OperationalAction Action,
    OperationalOutcome Outcome,
    OperationalFailureCode FailureCode);
