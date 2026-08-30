namespace PaymentSandbox.Orchestrator.Infrastructure;

/// <summary>Bounds the one loopback JSON-RPC endpoint used by Week 14 tests.</summary>
public sealed record LocalAnvilRpcClientOptions
{
    public LocalAnvilRpcClientOptions(string rpcUrl, TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpcUrl);
        if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "The local Anvil RPC URL must be an absolute credential-free loopback HTTP URL.",
                nameof(rpcUrl));
        }

        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (RequestTimeout < TimeSpan.FromSeconds(1) ||
            RequestTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        RpcUri = uri;
    }

    public Uri RpcUri { get; }
    public TimeSpan RequestTimeout { get; }
}
