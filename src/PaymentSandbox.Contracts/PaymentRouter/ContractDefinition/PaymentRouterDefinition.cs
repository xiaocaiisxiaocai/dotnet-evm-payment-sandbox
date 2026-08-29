using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace PaymentSandbox.Contracts.PaymentRouter.ContractDefinition;

// This file is the typed C# projection of contracts/abi/PaymentRouter.json.
// Keep it mechanical: business rules belong in Domain, while RPC trust checks
// and safer construction of these messages belong in the adapter classes.

[Function("pay")]
public sealed class PayFunction : FunctionMessage
{
    [Parameter("bytes32", "paymentId", 1)]
    public byte[] PaymentId { get; set; } = [];

    [Parameter("address", "token", 2)]
    public string Token { get; set; } = string.Empty;

    [Parameter("address", "merchant", 3)]
    public string Merchant { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 4)]
    public BigInteger Amount { get; set; }
}

[Function("payWithPermit")]
public sealed class PayWithPermitFunction : FunctionMessage
{
    [Parameter("bytes32", "paymentId", 1)]
    public byte[] PaymentId { get; set; } = [];

    [Parameter("address", "token", 2)]
    public string Token { get; set; } = string.Empty;

    [Parameter("address", "merchant", 3)]
    public string Merchant { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 4)]
    public BigInteger Amount { get; set; }

    [Parameter("uint256", "permitDeadline", 5)]
    public BigInteger PermitDeadline { get; set; }

    [Parameter("uint8", "v", 6)]
    public byte V { get; set; }

    [Parameter("bytes32", "r", 7)]
    public byte[] R { get; set; } = [];

    [Parameter("bytes32", "s", 8)]
    public byte[] S { get; set; } = [];
}

[Event("PaymentRecorded")]
public sealed class PaymentRecordedEventDto : IEventDTO
{
    [Parameter("bytes32", "paymentId", 1, true)]
    public byte[] PaymentId { get; set; } = [];

    [Parameter("address", "payer", 2, true)]
    public string Payer { get; set; } = string.Empty;

    [Parameter("address", "token", 3, false)]
    public string Token { get; set; } = string.Empty;

    [Parameter("address", "merchant", 4, true)]
    public string Merchant { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 5, false)]
    public BigInteger Amount { get; set; }
}

[Error("InvalidAmount")]
public sealed class InvalidAmountError : IErrorDTO
{
}

[Error("InvalidMerchant")]
public sealed class InvalidMerchantError : IErrorDTO
{
    [Parameter("address", "merchant", 1)]
    public string Merchant { get; set; } = string.Empty;
}

[Error("InvalidPaymentId")]
public sealed class InvalidPaymentIdError : IErrorDTO
{
}

[Error("InvalidToken")]
public sealed class InvalidTokenError : IErrorDTO
{
    [Parameter("address", "token", 1)]
    public string Token { get; set; } = string.Empty;
}

// SafeERC20FailedOperation is declared by OpenZeppelin and appears in the
// Router ABI because SafeERC20 may surface it from pay/payWithPermit.
[Error("SafeERC20FailedOperation")]
public sealed class SafeErc20FailedOperationError : IErrorDTO
{
    [Parameter("address", "token", 1)]
    public string Token { get; set; } = string.Empty;
}
