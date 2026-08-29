using PaymentSandbox.Api;

WebApplication app = PaymentSandboxApi.Build(args);
await app.RunAsync();
