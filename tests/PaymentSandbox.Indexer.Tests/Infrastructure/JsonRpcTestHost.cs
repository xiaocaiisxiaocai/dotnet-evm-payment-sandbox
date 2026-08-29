using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PaymentSandbox.Indexer.Tests.Infrastructure;

/// <summary>Loopback JSON-RPC fixture that exposes raw request/response behavior.</summary>
internal sealed class JsonRpcTestHost(WebApplication app, Uri endpoint)
    : IAsyncDisposable
{
    private const string EventTopic =
        "0xa3c98d2a8a41cf6c27fd990afbd1c1b88bae461cdd447ca141d7934084b1cc04";
    private readonly WebApplication _app = app;

    internal Uri Endpoint { get; } = endpoint;

    internal ConcurrentQueue<JsonRpcRequest> Requests { get; } = new();

    internal static async Task<JsonRpcTestHost> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { Args = ["--urls", "http://127.0.0.1:0"] });
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        JsonRpcTestHost? host = null;
        app.MapPost("/", async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted);
            JsonElement root = document.RootElement;
            string method = root.GetProperty("method").GetString()
                ?? throw new InvalidOperationException("JSON-RPC method was null.");
            string parameters = root.TryGetProperty("params", out JsonElement value)
                ? value.GetRawText()
                : "[]";
            host!.Requests.Enqueue(new JsonRpcRequest(method, parameters));
            string result = method switch
            {
                "eth_chainId" => "\"0x7a69\"",
                "eth_getBlockByNumber" => BlockResult(),
                "eth_getLogs" => LogResult(),
                _ => throw new InvalidOperationException($"Unexpected JSON-RPC method '{method}'."),
            };
            string id = root.GetProperty("id").GetRawText();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}",
                context.RequestAborted);
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not publish a listening address.");
        host = new JsonRpcTestHost(app, new Uri(Assert.Single(addresses.Addresses)));
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }

    private static string BlockResult() =>
        $$"""
        {
          "number":"0x64",
          "hash":"{{IndexerTestData.Hash('1').Value}}",
          "parentHash":"{{IndexerTestData.Hash('0').Value}}",
          "transactions":[]
        }
        """;

    private static string LogResult()
    {
        string payerTopic = AddressWord('4');
        string merchantTopic = AddressWord('3');
        string tokenWord = AddressWord('2')[2..];
        string amountWord = new System.Numerics.BigInteger(1_250_000).ToString("x64");
        return $$"""
        [{
          "address":"{{IndexerTestData.Router.Value}}",
          "topics":[
            "{{EventTopic}}",
            "0x{{new string('a', 64)}}",
            "{{payerTopic}}",
            "{{merchantTopic}}"
          ],
          "data":"0x{{tokenWord}}{{amountWord}}",
          "blockNumber":"0x64",
          "transactionHash":"{{IndexerTestData.Hash('c').Value}}",
          "transactionIndex":"0x0",
          "blockHash":"{{IndexerTestData.Hash('1').Value}}",
          "logIndex":"0x3",
          "removed":false
        }]
        """;
    }

    private static string AddressWord(char digit) =>
        $"0x{new string('0', 24)}{new string(digit, 40)}";
}

internal sealed record JsonRpcRequest(string Method, string Parameters);
