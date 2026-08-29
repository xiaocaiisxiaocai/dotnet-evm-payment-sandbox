namespace PaymentSandbox.Api.Persistence;

/// <summary>Fails application startup if the configured database cannot migrate.</summary>
public sealed class PaymentIntentDatabaseInitializer(PaymentIntentDatabase database)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        database.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
