using System.Numerics;

namespace PaymentSandbox.Indexer.Rpc;

/// <summary>Untrusted block fields as reported by an RPC endpoint.</summary>
public sealed record RpcBlockHeader(
    BigInteger Number,
    string? Hash,
    string? ParentHash);
