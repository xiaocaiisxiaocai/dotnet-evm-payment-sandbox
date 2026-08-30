using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentSandbox.Api;
using PaymentSandbox.Api.Persistence;
using PaymentSandbox.Authentication.Persistence;

namespace PaymentSandbox.Api.Tests.Infrastructure;

internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly TemporarySqliteDatabase? _ownedDatabase;

    private ApiTestHost(
        WebApplication app,
        HttpClient client,
        TemporarySqliteDatabase? ownedDatabase)
    {
        App = app;
        Client = client;
        _ownedDatabase = ownedDatabase;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<ApiTestHost> StartAsync(
        TimeProvider? timeProvider = null,
        string? databasePath = null,
        string? authenticationDatabasePath = null)
    {
        TemporarySqliteDatabase? ownedDatabase =
            databasePath is null || authenticationDatabasePath is null
            ? new TemporarySqliteDatabase()
            : null;
        string effectiveDatabasePath = databasePath ?? ownedDatabase!.DatabasePath;
        string effectiveAuthenticationDatabasePath = authenticationDatabasePath ??
            ownedDatabase!.AuthenticationDatabasePath;

        WebApplication app = PaymentSandboxApi.Build(
            ["--urls", "http://127.0.0.1:0"],
            builder =>
            {
                builder.Logging.ClearProviders();
                if (timeProvider is not null)
                {
                    // DI resolves the last registration for a single service.
                    builder.Services.AddSingleton<TimeProvider>(timeProvider);
                }

                builder.Services.AddSingleton(
                    new PaymentIntentDatabaseOptions(effectiveDatabasePath));
                builder.Services.AddSingleton(
                    new SiweChallengeDatabaseOptions(effectiveAuthenticationDatabasePath));
            });

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);

            IServer server = app.Services.GetRequiredService<IServer>();
            IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
            string address = Assert.Single(addresses.Addresses);
            var client = new HttpClient
            {
                BaseAddress = new Uri(address, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(10),
            };

            return new ApiTestHost(app, client, ownedDatabase);
        }
        catch
        {
            await app.DisposeAsync();
            if (ownedDatabase is not null)
            {
                await ownedDatabase.DisposeAsync();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync(TestContext.Current.CancellationToken);
        await App.DisposeAsync();
        if (_ownedDatabase is not null)
        {
            await _ownedDatabase.DisposeAsync();
        }
    }
}
