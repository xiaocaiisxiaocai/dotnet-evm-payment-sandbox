using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentSandbox.Api;

namespace PaymentSandbox.Api.Tests.Infrastructure;

internal sealed class ApiTestHost : IAsyncDisposable
{
    private ApiTestHost(WebApplication app, HttpClient client)
    {
        App = app;
        Client = client;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<ApiTestHost> StartAsync(TimeProvider? timeProvider = null)
    {
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
            });

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

        return new ApiTestHost(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync(TestContext.Current.CancellationToken);
        await App.DisposeAsync();
    }
}
