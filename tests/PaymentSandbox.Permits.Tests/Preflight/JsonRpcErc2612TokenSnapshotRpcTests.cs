using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using PaymentSandbox.Domain.Evm;
using PaymentSandbox.Permits.Erc2612;
using PaymentSandbox.Permits.Preflight;
using PaymentSandbox.Permits.Tests.Infrastructure;

namespace PaymentSandbox.Permits.Tests.Preflight;

public sealed class JsonRpcErc2612TokenSnapshotRpcTests
{
    [Fact]
    public async Task Adapter_PinsCodeAndCallsToOneStableBlockAndDecodesStrictAbi()
    {
        var handler = new SnapshotRpcHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:18545"),
        };
        using var rpc = new JsonRpcErc2612TokenSnapshotRpc(client);
        EvmAddress token = EvmAddress.Parse(PermitWorkflowTestData.TokenAddress);
        EvmAddress owner = EvmAddress.Parse("0x5555555555555555555555555555555555555555");

        Erc2612TokenSnapshotObservation observed = await rpc.ObserveAsync(
            token, owner, TestContext.Current.CancellationToken);

        Assert.Equal(31_337, observed.ChainId);
        Assert.Equal(100, observed.BlockNumber);
        Assert.Equal("Test USDC", observed.TokenName);
        Assert.Equal(7, observed.Nonce);
        Assert.Equal(PermitWorkflowTestData.RuntimeCode, observed.RuntimeCode);
        Assert.Equal(4, handler.PinnedStateReads);
        Assert.All(handler.StateBlockTags, tag => Assert.Equal("0x64", tag));
    }

    [Fact]
    public async Task Adapter_RejectsBlockHashChangeAcrossSnapshot()
    {
        var handler = new SnapshotRpcHandler { ReorgOnFinalHeader = true };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:18545"),
        };
        using var rpc = new JsonRpcErc2612TokenSnapshotRpc(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.ObserveAsync(
            EvmAddress.Parse(PermitWorkflowTestData.TokenAddress),
            EvmAddress.Parse("0x5555555555555555555555555555555555555555"),
            TestContext.Current.CancellationToken));
    }

    private sealed class SnapshotRpcHandler : HttpMessageHandler
    {
        private int _blockReads;
        internal bool ReorgOnFinalHeader { get; init; }
        internal int PinnedStateReads { get; private set; }
        internal List<string> StateBlockTags { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                await request.Content!.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;
            long id = root.GetProperty("id").GetInt64();
            string method = root.GetProperty("method").GetString()!;
            JsonElement parameters = root.GetProperty("params");
            object result = method switch
            {
                "eth_chainId" => "0x7a69",
                "eth_getBlockByNumber" => Block(parameters),
                "eth_getCode" => StateResult(parameters, PermitWorkflowTestData.RuntimeCode),
                "eth_call" => Call(parameters),
                _ => throw new InvalidOperationException($"Unexpected method {method}."),
            };
            string json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private object Block(JsonElement parameters)
        {
            _blockReads++;
            string requested = parameters[0].GetString()!;
            string hash = ReorgOnFinalHeader && _blockReads > 1 && requested == "0x64"
                ? $"0x{new string('b', 64)}"
                : $"0x{new string('a', 64)}";
            return new { number = "0x64", hash };
        }

        private string Call(JsonElement parameters)
        {
            string data = parameters[0].GetProperty("data").GetString()!;
            return data[..10] switch
            {
                "0x06fdde03" => StateResult(parameters, EncodeString("Test USDC")),
                "0x3644e515" => StateResult(parameters,
                    Erc2612PermitService.CalculateDomainSeparator(
                        PermitWorkflowTestData.PermitPolicy())),
                "0x7ecebe00" => StateResult(parameters, $"0x{7:x64}"),
                _ => throw new InvalidOperationException("Unexpected eth_call selector."),
            };
        }

        private string StateResult(JsonElement parameters, string value)
        {
            string tag = parameters[parameters.GetArrayLength() - 1].GetString()!;
            StateBlockTags.Add(tag);
            PinnedStateReads++;
            return value;
        }

        private static string EncodeString(string value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            byte[] encoded = new byte[96];
            encoded[31] = 32;
            encoded[63] = (byte)text.Length;
            text.CopyTo(encoded, 64);
            return $"0x{Convert.ToHexStringLower(encoded)}";
        }
    }
}
