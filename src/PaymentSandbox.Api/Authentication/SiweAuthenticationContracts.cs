namespace PaymentSandbox.Api.Authentication;

public sealed record IssueSiweChallengeRequest(string? Address);

public sealed record VerifySiweChallengeRequest(string? Message, string? Signature);

public sealed record SiweChallengeResponse(
    string Message,
    DateTimeOffset ExpirationTimeUtc);

public sealed record SiweSessionResponse(
    string Address,
    string ChainId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpirationTimeUtc);
