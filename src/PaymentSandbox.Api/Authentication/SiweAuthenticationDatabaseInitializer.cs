using PaymentSandbox.Authentication.Persistence;

namespace PaymentSandbox.Api.Authentication;

/// <summary>Fails API startup when the authentication schema cannot migrate.</summary>
public sealed class SiweAuthenticationDatabaseInitializer(SiweChallengeDatabase database)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        database.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
