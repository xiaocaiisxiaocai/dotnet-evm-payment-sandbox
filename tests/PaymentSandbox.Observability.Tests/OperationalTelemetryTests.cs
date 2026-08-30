using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using PaymentSandbox.Observability;

namespace PaymentSandbox.Observability.Tests;

public sealed class OperationalTelemetryTests
{
    [Fact]
    public void AbandonedScope_FailsClosedAndBalancesActiveGauge()
    {
        using var capture = new TelemetryCapture();

        using (OperationalTelemetry.Shared.BeginOperation(
            OperationalComponent.TransactionLifecycle,
            OperationalAction.TransactionRefreshReceipt))
        {
            // Simulate orchestration returning without Complete or Fail. The
            // scope must not leave a permanently positive active measurement.
        }

        Activity activity = Assert.Single(capture.StoppedActivities);
        Assert.Equal("failed", activity.GetTagItem("payment_sandbox.outcome"));
        Assert.Equal("unexpected", activity.GetTagItem("payment_sandbox.failure_code"));
        Assert.Equal(
            [1d, -1d],
            capture.Measurements
                .Where(measurement => measurement.InstrumentName == "payment_sandbox.operation.active")
                .Select(measurement => measurement.Value));
    }

    [Fact]
    public async Task Success_EmitsOneBoundedTraceAndBalancedMetrics()
    {
        using var capture = new TelemetryCapture();

        string result = await OperationalExecution.ObserveAsync(
            OperationalTelemetry.Shared,
            OperationalComponent.PermitWorkflow,
            OperationalAction.PermitReserve,
            _ => Task.FromResult("result-is-not-a-tag"),
            _ => OperationalOutcome.Created,
            _ => OperationalFailureCode.Unexpected,
            TestContext.Current.CancellationToken);

        Assert.Equal("result-is-not-a-tag", result);
        Activity activity = Assert.Single(capture.StoppedActivities);
        Assert.Equal("payment_sandbox.permit.reserve", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["payment_sandbox.component"] = "permit_workflow",
                ["payment_sandbox.action"] = "permit.reserve",
                ["payment_sandbox.outcome"] = "created",
                ["payment_sandbox.failure_code"] = "none",
            },
            activity.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value));
        Assert.Empty(activity.Events);

        MetricMeasurement completed = Assert.Single(
            capture.Measurements,
            measurement => measurement.InstrumentName == "payment_sandbox.operation.completed");
        Assert.Equal(1d, completed.Value);
        Assert.Equal("created", completed.Tags["payment_sandbox.outcome"]);
        Assert.Equal("none", completed.Tags["payment_sandbox.failure_code"]);
        Assert.Single(
            capture.Measurements,
            measurement => measurement.InstrumentName == "payment_sandbox.operation.duration");
        Assert.Equal(
            [1d, -1d],
            capture.Measurements
                .Where(measurement => measurement.InstrumentName == "payment_sandbox.operation.active")
                .Select(measurement => measurement.Value));
    }

    [Fact]
    public async Task Failure_RecordsOnlyClassifierOutputAndNeverExceptionDetails()
    {
        const string secret = "https://rpc.invalid/?api-key=secret signed=0xdeadbeef";
        using var capture = new TelemetryCapture();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OperationalExecution.ObserveAsync<string>(
                OperationalTelemetry.Shared,
                OperationalComponent.TransactionLifecycle,
                OperationalAction.TransactionBroadcast,
                _ => throw new InvalidOperationException(secret),
                _ => OperationalOutcome.Applied,
                _ => OperationalFailureCode.DependencyFailure,
                TestContext.Current.CancellationToken));

        Assert.Equal(secret, exception.Message);
        Activity activity = Assert.Single(capture.StoppedActivities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Empty(activity.Events);
        Assert.Equal("failed", activity.GetTagItem("payment_sandbox.outcome"));
        Assert.Equal("dependency_failure", activity.GetTagItem("payment_sandbox.failure_code"));

        string exportedShape = string.Join(
            "|",
            capture.StoppedActivities.SelectMany(item =>
                item.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
                .Concat(capture.Measurements.SelectMany(item =>
                    item.Tags.Select(tag => $"{tag.Key}={tag.Value}"))));
        Assert.DoesNotContain(secret, exportedShape, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), exportedShape, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_HasStableCategoryAndStillBalancesActiveGauge()
    {
        using var capture = new TelemetryCapture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OperationalExecution.ObserveAsync(
                OperationalTelemetry.Shared,
                OperationalComponent.PermitWorkflow,
                OperationalAction.PermitPrepare,
                token => Task.FromCanceled<int>(token),
                _ => OperationalOutcome.Applied,
                _ => OperationalFailureCode.Unexpected,
                cancellation.Token));

        Activity activity = Assert.Single(capture.StoppedActivities);
        Assert.Equal("cancelled", activity.GetTagItem("payment_sandbox.outcome"));
        Assert.Equal("cancelled", activity.GetTagItem("payment_sandbox.failure_code"));
        Assert.Equal(
            [1d, -1d],
            capture.Measurements
                .Where(measurement => measurement.InstrumentName == "payment_sandbox.operation.active")
                .Select(measurement => measurement.Value));
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source =>
                    source.Name == OperationalTelemetry.InstrumentationName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => StoppedActivities.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == OperationalTelemetry.InstrumentationName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) => AddMeasurement(
                    instrument, measurement, tags));
            _meterListener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) => AddMeasurement(
                    instrument, measurement, tags));
            _meterListener.Start();
        }

        public ConcurrentQueue<Activity> StoppedActivities { get; } = new();

        public ConcurrentQueue<MetricMeasurement> Measurements { get; } = new();

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }

        private void AddMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            var copiedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copiedTags.Add(tag.Key, tag.Value);
            }

            Measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                Convert.ToDouble(measurement, System.Globalization.CultureInfo.InvariantCulture),
                copiedTags));
        }
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
