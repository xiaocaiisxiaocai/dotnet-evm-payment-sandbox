using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Authentication.BrowserSessions;

/// <summary>One active login identity; it grants no role or payment authority.</summary>
public sealed record SiweBrowserSession(
    EvmAddress Address,
    EvmChainId ChainId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpirationTimeUtc);

public sealed record SiweSessionLookup(
    SiweSessionLookupResult Result,
    SiweBrowserSession? Session);
