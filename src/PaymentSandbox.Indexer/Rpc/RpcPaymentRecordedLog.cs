using System.Numerics;

namespace PaymentSandbox.Indexer.Rpc;

/// <summary>Untrusted decoded event fields as reported by an RPC endpoint.</summary>
public sealed record RpcPaymentRecordedLog(
    string? ContractAddress,
    BigInteger BlockNumber,
    string? BlockHash,
    string? TransactionHash,
    BigInteger LogIndex,
    bool Removed,
    byte[]? PaymentId,
    string? Payer,
    string? Token,
    string? Merchant,
    BigInteger Amount);
