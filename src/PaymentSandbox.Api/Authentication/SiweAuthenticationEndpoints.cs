using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Primitives;
using PaymentSandbox.Authentication.BrowserSessions;
using PaymentSandbox.Authentication.Siwe;
using PaymentSandbox.Domain.Evm;

namespace PaymentSandbox.Api.Authentication;

/// <summary>Loopback-only browser boundary for SIWE login and opaque sessions.</summary>
public static class SiweAuthenticationEndpoints
{
    public const string FlowCookieName = "__Host-PaymentSandbox-Siwe-Flow";
    public const string SessionCookieName = "__Host-PaymentSandbox-Session";
    public const string CsrfCookieName = "__Host-PaymentSandbox-Csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";

    public static IEndpointRouteBuilder MapSiweAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/v1/auth");
        group.MapPost("/siwe/challenge", IssueChallengeAsync);
        group.MapPost("/siwe/verify", VerifyAsync);
        group.MapGet("/session", GetSessionAsync);
        group.MapPost("/logout", LogoutAsync);
        return endpoints;
    }

    private static async Task<IResult> IssueChallengeAsync(
        HttpContext context,
        IssueSiweChallengeRequest? request,
        SiweAuthenticationPolicy authenticationPolicy,
        SiweBrowserSessionService sessions,
        CancellationToken cancellationToken)
    {
        PrepareSensitiveResponse(context);
        if (!IsLoopback(context) || !HasExpectedOrigin(context, authenticationPolicy))
        {
            return Forbidden();
        }

        if (!EvmAddress.TryParse(request?.Address, out EvmAddress? address) || address.IsZero)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(IssueSiweChallengeRequest.Address)] =
                ["address must be a non-zero 20-byte hexadecimal address."],
            });
        }

        try
        {
            SiweLoginChallenge challenge = await sessions.IssueAsync(
                address,
                cancellationToken).ConfigureAwait(false);
            AppendCookie(
                context,
                FlowCookieName,
                challenge.BrowserBindingToken,
                challenge.ExpirationTimeUtc,
                httpOnly: true);
            return Results.Ok(new SiweChallengeResponse(
                challenge.Message,
                challenge.ExpirationTimeUtc));
        }
        catch (SiweAuthenticationException exception) when (
            exception.Code == SiweAuthenticationErrorCode.ChallengeCapacityExceeded)
        {
            return TemporarilyUnavailable();
        }
        catch (SiweBrowserSessionException exception) when (
            exception.Code == SiweBrowserSessionErrorCode.SessionCapacityExceeded)
        {
            return TemporarilyUnavailable();
        }
    }

    private static async Task<IResult> VerifyAsync(
        HttpContext context,
        VerifySiweChallengeRequest? request,
        SiweAuthenticationPolicy authenticationPolicy,
        SiweBrowserSessionService sessions,
        CancellationToken cancellationToken)
    {
        PrepareSensitiveResponse(context);
        if (!IsLoopback(context) || !HasExpectedOrigin(context, authenticationPolicy))
        {
            return Forbidden();
        }

        if (string.IsNullOrEmpty(request?.Message) || string.IsNullOrEmpty(request.Signature) ||
            !TryReadSingleCookie(context.Request, FlowCookieName, out string? flowToken))
        {
            return Unauthorized();
        }

        // Absence means this is a first login. A present but malformed or
        // duplicate cookie is rejected, otherwise rotation could silently
        // leave an ambiguous prior bearer session active.
        if (!TryReadOptionalSingleCookie(
                context.Request,
                SessionCookieName,
                out string? previousSession))
        {
            return Unauthorized();
        }

        try
        {
            SiweSessionCredentials credentials = await sessions.VerifyAsync(
                request.Message,
                request.Signature,
                flowToken,
                previousSession,
                cancellationToken).ConfigureAwait(false);
            DeleteCookie(context, FlowCookieName, httpOnly: true);
            AppendCookie(
                context,
                SessionCookieName,
                credentials.SessionToken,
                credentials.Session.ExpirationTimeUtc,
                httpOnly: true);
            // The readable token is not sufficient alone: logout requires the
            // same value in this cookie and an explicit request header.
            AppendCookie(
                context,
                CsrfCookieName,
                credentials.CsrfToken,
                credentials.Session.ExpirationTimeUtc,
                httpOnly: false);
            return Results.Ok(ToResponse(credentials.Session));
        }
        catch (Exception exception) when (
            exception is SiweAuthenticationException or
            SiweBrowserSessionException or
            FormatException or
            ArgumentException)
        {
            return exception is SiweBrowserSessionException sessionException &&
                sessionException.Code == SiweBrowserSessionErrorCode.SessionCapacityExceeded
                    ? TemporarilyUnavailable()
                    : Unauthorized();
        }
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        SiweBrowserSessionService sessions,
        CancellationToken cancellationToken)
    {
        PrepareSensitiveResponse(context);
        if (!IsLoopback(context) ||
            !TryReadSingleCookie(context.Request, SessionCookieName, out string? sessionToken))
        {
            return Unauthorized();
        }

        try
        {
            SiweBrowserSession session = await sessions.GetSessionAsync(
                sessionToken,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToResponse(session));
        }
        catch (SiweBrowserSessionException)
        {
            return Unauthorized();
        }
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        SiweAuthenticationPolicy authenticationPolicy,
        SiweBrowserSessionService sessions,
        CancellationToken cancellationToken)
    {
        PrepareSensitiveResponse(context);
        if (!IsLoopback(context) || !HasExpectedOrigin(context, authenticationPolicy))
        {
            return Forbidden();
        }

        if (!TryReadSingleCookie(context.Request, SessionCookieName, out string? sessionToken) ||
            !TryReadSingleCookie(context.Request, CsrfCookieName, out string? csrfCookie) ||
            !TryReadSingleHeader(context.Request.Headers, CsrfHeaderName, out string? csrfHeader))
        {
            return Forbidden();
        }

        try
        {
            await sessions.LogoutAsync(
                sessionToken,
                csrfCookie,
                csrfHeader,
                cancellationToken).ConfigureAwait(false);
            DeleteCookie(context, SessionCookieName, httpOnly: true);
            DeleteCookie(context, CsrfCookieName, httpOnly: false);
            return Results.NoContent();
        }
        catch (SiweBrowserSessionException exception) when (
            exception.Code == SiweBrowserSessionErrorCode.CsrfMismatch)
        {
            return Forbidden();
        }
        catch (SiweBrowserSessionException)
        {
            return Unauthorized();
        }
    }

    private static SiweSessionResponse ToResponse(SiweBrowserSession session) =>
        new(
            session.Address.Value,
            session.ChainId.ToString(),
            session.CreatedAtUtc,
            session.ExpirationTimeUtc);

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is IPAddress address && IPAddress.IsLoopback(address);

    private static bool HasExpectedOrigin(
        HttpContext context,
        SiweAuthenticationPolicy policy)
    {
        string expected = policy.Origin.GetLeftPart(UriPartial.Authority);
        return TryReadSingleHeader(context.Request.Headers, "Origin", out string? origin) &&
            string.Equals(origin, expected, StringComparison.Ordinal);
    }

    private static bool TryReadSingleHeader(
        IHeaderDictionary headers,
        string name,
        [NotNullWhen(true)] out string? value)
    {
        StringValues values = headers[name];
        string? candidate = values.Count == 1 ? values[0] : null;
        if (!string.IsNullOrEmpty(candidate))
        {
            value = candidate;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryReadSingleCookie(
        HttpRequest request,
        string expectedName,
        [NotNullWhen(true)] out string? value)
    {
        int matches = ReadCookie(request, expectedName, out value);
        return matches == 1 && SiweBrowserSessionService.IsCanonicalOpaqueToken(value);
    }

    private static bool TryReadOptionalSingleCookie(
        HttpRequest request,
        string expectedName,
        out string? value)
    {
        int matches = ReadCookie(request, expectedName, out value);
        return matches == 0 ||
            matches == 1 && SiweBrowserSessionService.IsCanonicalOpaqueToken(value);
    }

    private static int ReadCookie(
        HttpRequest request,
        string expectedName,
        out string? value)
    {
        value = null;
        int matches = 0;
        foreach (string? header in request.Headers.Cookie)
        {
            if (header is null)
            {
                continue;
            }

            foreach (string segment in header.Split(';', StringSplitOptions.TrimEntries))
            {
                int equals = segment.IndexOf('=');
                if (equals <= 0 || !string.Equals(
                        segment[..equals],
                        expectedName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                matches++;
                value = segment[(equals + 1)..];
            }
        }

        // Return the count so callers can distinguish absence from ambiguity.
        // Duplicate names have user-agent-dependent precedence and are never
        // accepted by either the required or optional-cookie policy above.
        return matches;
    }

    private static void PrepareSensitiveResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    private static void AppendCookie(
        HttpContext context,
        string name,
        string value,
        DateTimeOffset expiresAtUtc,
        bool httpOnly) =>
        context.Response.Cookies.Append(name, value, CookieOptions(expiresAtUtc, httpOnly));

    private static void DeleteCookie(HttpContext context, string name, bool httpOnly) =>
        context.Response.Cookies.Delete(name, CookieOptions(DateTimeOffset.UnixEpoch, httpOnly));

    private static CookieOptions CookieOptions(DateTimeOffset expiresAtUtc, bool httpOnly) =>
        new()
        {
            Secure = true,
            HttpOnly = httpOnly,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAtUtc,
            IsEssential = true,
        };

    private static IResult Unauthorized() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication failed",
        extensions: new Dictionary<string, object?> { ["code"] = "authentication_failed" });

    private static IResult Forbidden() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Request origin rejected",
        extensions: new Dictionary<string, object?> { ["code"] = "request_rejected" });

    private static IResult TemporarilyUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Authentication temporarily unavailable",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "authentication_temporarily_unavailable",
        });
}
