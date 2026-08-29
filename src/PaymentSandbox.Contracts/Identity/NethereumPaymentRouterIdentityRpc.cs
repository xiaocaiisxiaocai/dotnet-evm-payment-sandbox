using System.Numerics;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace PaymentSandbox.Contracts.Identity;

/// <summary>Read-only Nethereum adapter for Router identity observations.</summary>
public sealed class NethereumPaymentRouterIdentityRpc : IPaymentRouterIdentityRpc
{
    private readonly IWeb3 _web3;

    /// <summary>Creates an adapter for an HTTP(S) JSON-RPC endpoint.</summary>
    /// <remarks>
    /// Constructing this type does not contact the endpoint. The public surface
    /// deliberately exposes only eth_chainId and eth_getCode even though Web3 has
    /// broader capabilities internally.
    /// </remarks>
    public NethereumPaymentRouterIdentityRpc(string rpcUrl)
        : this(CreateWeb3(rpcUrl))
    {
    }

    internal NethereumPaymentRouterIdentityRpc(IWeb3 web3)
    {
        _web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
    }

    public async Task<BigInteger> GetChainIdAsync(
        CancellationToken cancellationToken = default)
    {
        // Nethereum 6.1.0's request does not accept a CancellationToken, so
        // WaitAsync bounds our wait and preserves cancellation for callers.
        var chainId = await _web3.Eth.ChainId
            .SendRequestAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return chainId.Value;
    }

    public async Task<string> GetCodeAsync(
        string contractAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractAddress);

        return await _web3.Eth.GetCode
            .SendRequestAsync(contractAddress, BlockParameter.CreateLatest())
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static IWeb3 CreateWeb3(string rpcUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpcUrl);

        if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The RPC URL must be an absolute HTTP or HTTPS URL.",
                nameof(rpcUrl));
        }

        return new Web3(uri.AbsoluteUri);
    }
}
