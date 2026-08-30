using PaymentSandbox.Api.Authentication;
using PaymentSandbox.Api.PaymentIntents;
using PaymentSandbox.Api.Persistence;
using PaymentSandbox.Authentication.BrowserSessions;
using PaymentSandbox.Authentication.Persistence;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Api;

/// <summary>Builds the runnable Payment Intent HTTP and persistence boundary.</summary>
public static class PaymentSandboxApi
{
    public static WebApplication Build(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Payment-intent JSON is tiny. A hard request limit makes accidental or
        // hostile oversized bodies fail before model binding allocates freely.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(TimeProvider.System);

        // The SIWE policy is server-owned. In particular, neither Host nor
        // Origin from an untrusted request is copied into the signed message.
        builder.Services.AddSingleton(serviceProvider =>
        {
            IConfigurationSection section = builder.Configuration.GetSection("Authentication");
            return new SiweAuthenticationPolicy(
                new Uri(section["Origin"] ?? "https://auth.example", UriKind.Absolute),
                new Uri(
                    section["RequestUri"] ?? "https://auth.example/login",
                    UriKind.Absolute),
                EvmChainId.Parse(section["ChainId"] ?? "31337"),
                section["Statement"] ?? "Sign in to the dotnet EVM payment sandbox.",
                TimeSpan.FromSeconds(section.GetValue("ChallengeLifetimeSeconds", 300)),
                TimeSpan.FromSeconds(section.GetValue("AllowedClockSkewSeconds", 30)));
        });
        builder.Services.AddSingleton(serviceProvider =>
        {
            IConfigurationSection section = builder.Configuration.GetSection("Authentication");
            string configuredPath = section["DatabasePath"] ?? "data/authentication.db";
            string absolutePath = Path.IsPathFullyQualified(configuredPath)
                ? configuredPath
                : Path.Combine(builder.Environment.ContentRootPath, configuredPath);
            return new SiweChallengeDatabaseOptions(
                absolutePath,
                section.GetValue("ChallengeCapacity", 1_024),
                section.GetValue("SessionCapacity", 1_024));
        });
        builder.Services.AddSingleton(serviceProvider => new SiweBrowserSessionPolicy(
            TimeSpan.FromSeconds(builder.Configuration.GetSection("Authentication")
                .GetValue("SessionLifetimeSeconds", 1_800))));
        builder.Services.AddSingleton<SiweChallengeDatabase>();
        builder.Services.AddHostedService<SiweAuthenticationDatabaseInitializer>();
        builder.Services.AddSingleton<ISiweChallengeStore, SqliteSiweChallengeStore>();
        builder.Services.AddSingleton<SiweAuthenticationService>();
        builder.Services.AddSingleton<ISiweBrowserSessionStore, SqliteSiweBrowserSessionStore>();
        builder.Services.AddSingleton<SiweBrowserSessionService>();

        builder.Services.AddSingleton(serviceProvider =>
        {
            string configuredPath = builder.Configuration["PaymentIntents:DatabasePath"]
                ?? "data/payment-intents.db";
            string absolutePath = Path.IsPathFullyQualified(configuredPath)
                ? configuredPath
                : Path.Combine(builder.Environment.ContentRootPath, configuredPath);

            return new PaymentIntentDatabaseOptions(absolutePath);
        });
        builder.Services.AddSingleton<PaymentIntentDatabase>();
        builder.Services.AddHostedService<PaymentIntentDatabaseInitializer>();
        builder.Services.AddSingleton<IPaymentIntentStore, SqlitePaymentIntentStore>();
        builder.Services.AddSingleton<PaymentIntentService>();

        // Tests and a future composition root may replace configuration while
        // exercising the exact same middleware and endpoint map.
        configureBuilder?.Invoke(builder);

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.MapSiweAuthenticationEndpoints();
        app.MapPaymentIntentEndpoints();
        return app;
    }
}
