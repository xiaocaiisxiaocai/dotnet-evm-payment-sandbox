using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Permits.Preflight;

/// <summary>Read-only JSON-RPC adapter that pins every token call to one block.</summary>
public sealed class JsonRpcErc2612TokenSnapshotRpc : IErc2612TokenSnapshotRpc, IDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
    private const string NameSelector = "0x06fdde03";
    private const string DomainSeparatorSelector = "0x3644e515";
    private const string NoncesSelector = "0x7ecebe00";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private long _nextRequestId;

    public JsonRpcErc2612TokenSnapshotRpc(string rpcUrl)
        : this(CreateClient(rpcUrl), ownsClient: true)
    {
    }

    /// <summary>Uses a caller-owned HTTP client, useful for controlled hosts and tests.</summary>
    public JsonRpcErc2612TokenSnapshotRpc(HttpClient httpClient)
        : this(httpClient, ownsClient: false)
    {
    }

    private JsonRpcErc2612TokenSnapshotRpc(HttpClient httpClient, bool ownsClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = ownsClient;
    }

    public async Task<Erc2612TokenSnapshotObservation> ObserveAsync(
        EvmAddress token,
        EvmAddress owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);

        BigInteger chainId = ParseQuantity(await RequestStringAsync(
            "eth_chainId", [], cancellationToken).ConfigureAwait(false));
        RpcBlock latest = await ReadBlockAsync("latest", cancellationToken).ConfigureAwait(false);
        string blockTag = ToQuantity(latest.Number);

        // Every state-bearing read uses the captured number. Reading its header
        // again afterwards detects a canonical-hash change during the snapshot.
        string code = await RequestStringAsync(
            "eth_getCode", [token.Value, blockTag], cancellationToken).ConfigureAwait(false);
        string nameResult = await CallAsync(
            token, NameSelector, blockTag, cancellationToken).ConfigureAwait(false);
        string domainResult = await CallAsync(
            token, DomainSeparatorSelector, blockTag, cancellationToken).ConfigureAwait(false);
        string nonceData = NoncesSelector + owner.Value.AsSpan(2).ToString().PadLeft(64, '0');
        string nonceResult = await CallAsync(
            token, nonceData, blockTag, cancellationToken).ConfigureAwait(false);
        RpcBlock after = await ReadBlockAsync(blockTag, cancellationToken).ConfigureAwait(false);
        if (after.Number != latest.Number ||
            !string.Equals(after.Hash, latest.Hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The observed block changed during token preflight.");
        }

        return new Erc2612TokenSnapshotObservation(
            chainId,
            token,
            owner,
            checked((long)latest.Number),
            latest.Hash,
            code,
            DecodeAbiString(nameResult),
            DecodeBytes32(domainResult),
            DecodeUint256(nonceResult));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<RpcBlock> ReadBlockAsync(
        string blockTag,
        CancellationToken cancellationToken)
    {
        using JsonDocument response = await RequestAsync(
            "eth_getBlockByNumber", [blockTag, false], cancellationToken).ConfigureAwait(false);
        JsonElement result = RequireResult(response);
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("number", out JsonElement number) ||
            !result.TryGetProperty("hash", out JsonElement hash))
        {
            throw new InvalidOperationException("RPC returned an invalid block header.");
        }

        string hashValue = RequireCanonicalBytes32(hash.GetString());
        BigInteger numberValue = ParseQuantity(number.GetString());
        if (numberValue > long.MaxValue)
        {
            throw new InvalidOperationException("RPC block number exceeds the supported range.");
        }

        return new RpcBlock(numberValue, hashValue);
    }

    private async Task<string> CallAsync(
        EvmAddress token,
        string data,
        string blockTag,
        CancellationToken cancellationToken) =>
        await RequestStringAsync(
            "eth_call",
            [new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["to"] = token.Value,
                ["data"] = data,
            }, blockTag],
            cancellationToken).ConfigureAwait(false);

    private async Task<string> RequestStringAsync(
        string method,
        object[] parameters,
        CancellationToken cancellationToken)
    {
        using JsonDocument response = await RequestAsync(
            method, parameters, cancellationToken).ConfigureAwait(false);
        JsonElement result = RequireResult(response);
        return result.ValueKind == JsonValueKind.String && result.GetString() is string value
            ? value
            : throw new InvalidOperationException("RPC returned a non-string result.");
    }

    private async Task<JsonDocument> RequestAsync(
        string method,
        object[] parameters,
        CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextRequestId);
        var request = new JsonRpcRequest("2.0", id, method, parameters);
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            string.Empty, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                throw new InvalidOperationException("RPC response exceeded the preflight limit.");
            }

            limited.Write(buffer, 0, read);
        }

        limited.Position = 0;
        JsonDocument document = await JsonDocument.ParseAsync(
            limited, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        bool idMatches = root.TryGetProperty("id", out JsonElement responseId) &&
            responseId.TryGetInt64(out long returnedId) && returnedId == id;
        if (!idMatches || !root.TryGetProperty("jsonrpc", out JsonElement version) ||
            version.GetString() != "2.0" || root.TryGetProperty("error", out _))
        {
            document.Dispose();
            throw new InvalidOperationException("RPC returned an invalid or error response.");
        }

        return document;
    }

    private static JsonElement RequireResult(JsonDocument response) =>
        response.RootElement.TryGetProperty("result", out JsonElement result) &&
        result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? result
            : throw new InvalidOperationException("RPC response has no result.");

    private static BigInteger ParseQuantity(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("0x", StringComparison.Ordinal) ||
            value.Length < 3 || (value.Length > 3 && value[2] == '0') ||
            !value.AsSpan(2).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("RPC returned a non-canonical quantity.");
        }

        return BigInteger.Parse(
            "0" + value[2..],
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
    }

    private static string ToQuantity(BigInteger value) =>
        $"0x{value.ToString("x", CultureInfo.InvariantCulture)}";

    private static string DecodeBytes32(string value)
    {
        byte[] bytes = DecodeHex(value, expectedBytes: 32);
        return $"0x{Convert.ToHexStringLower(bytes)}";
    }

    private static BigInteger DecodeUint256(string value) =>
        new(DecodeHex(value, expectedBytes: 32), isUnsigned: true, isBigEndian: true);

    private static string DecodeAbiString(string value)
    {
        byte[] bytes = DecodeHex(value, expectedBytes: null);
        if (bytes.Length < 64 || bytes.Length % 32 != 0 ||
            new BigInteger(bytes.AsSpan(0, 32), isUnsigned: true, isBigEndian: true) != 32)
        {
            throw new InvalidOperationException("Token name() returned invalid ABI data.");
        }

        BigInteger lengthValue = new(
            bytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (lengthValue < 1 || lengthValue > 64)
        {
            throw new InvalidOperationException("Token name() exceeds the supported bound.");
        }

        int length = (int)lengthValue;
        int paddedLength = ((length + 31) / 32) * 32;
        if (bytes.Length != 64 + paddedLength ||
            bytes.AsSpan(64 + length).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidOperationException("Token name() returned non-canonical ABI padding.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, 64, length);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("Token name() returned invalid UTF-8.");
        }
    }

    private static byte[] DecodeHex(string value, int? expectedBytes)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("0x", StringComparison.Ordinal) ||
            value.Length % 2 != 0 || !value.AsSpan(2).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("RPC returned invalid hexadecimal data.");
        }

        byte[] bytes = Convert.FromHexString(value.AsSpan(2));
        if (expectedBytes.HasValue && bytes.Length != expectedBytes.Value)
        {
            throw new InvalidOperationException("RPC returned an unexpected ABI word length.");
        }

        return bytes;
    }

    private static string RequireCanonicalBytes32(string? value)
    {
        if (value is null || value.Length != 66 ||
            !value.StartsWith("0x", StringComparison.Ordinal) ||
            !value.AsSpan(2).ToString().All(Uri.IsHexDigit) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RPC returned an invalid bytes32 value.");
        }

        return value;
    }

    private static HttpClient CreateClient(string rpcUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpcUrl);
        if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The RPC URL must be an absolute HTTP or HTTPS URL.",
                nameof(rpcUrl));
        }

        return new HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private sealed record JsonRpcRequest(
        string Jsonrpc,
        long Id,
        string Method,
        object[] Params);

    private sealed record RpcBlock(BigInteger Number, string Hash);
}
