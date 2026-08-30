namespace PaymentSandbox.Observability;

/// <summary>
/// Executes and classifies a workflow call while keeping the observer isolated
/// from its generic result and exception. Only bounded enum values cross into
/// <see cref="IOperationalTelemetry"/>.
/// </summary>
public static class OperationalExecution
{
    public static async Task<T> ObserveAsync<T>(
        IOperationalTelemetry telemetry,
        OperationalComponent component,
        OperationalAction action,
        Func<CancellationToken, Task<T>> operation,
        Func<T, OperationalOutcome> classifyResult,
        Func<Exception, OperationalFailureCode> classifyFailure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(classifyResult);
        ArgumentNullException.ThrowIfNull(classifyFailure);

        using IOperationalTelemetryOperation observation =
            telemetry.BeginOperation(component, action);
        try
        {
            T result = await operation(cancellationToken).ConfigureAwait(false);
            observation.Complete(classifyResult(result));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observation.Fail(OperationalFailureCode.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            // The exception stays here, outside the injected observer. Only a
            // stable category crosses the instrumentation contract.
            observation.Fail(classifyFailure(exception));
            throw;
        }
    }
}
