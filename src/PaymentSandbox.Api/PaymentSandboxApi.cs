using PaymentSandbox.Api.PaymentIntents;
using PaymentSandbox.Api.Persistence;

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
        app.MapPaymentIntentEndpoints();
        return app;
    }
}
