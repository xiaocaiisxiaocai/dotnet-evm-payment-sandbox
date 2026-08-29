using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Primitives;
using PaymentSandbox.Domain.PaymentIntents;
using PaymentSandbox.Domain.Payments;

namespace PaymentSandbox.Api.PaymentIntents;

public static class PaymentIntentEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string ReplayHeader = "Idempotency-Replayed";

    public static IEndpointRouteBuilder MapPaymentIntentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

        RouteGroupBuilder group = endpoints.MapGroup("/v1/payment-intents");
        group.MapPost(string.Empty, CreateAsync);
        group.MapGet("/{paymentId}", GetByIdAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        CreatePaymentIntentRequest request,
        PaymentIntentService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!TryReadIdempotencyKey(context.Request.Headers, out IdempotencyKey? key))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [IdempotencyHeader] =
                [$"{IdempotencyHeader} must appear once and contain 1-{IdempotencyKey.MaxLength} visible ASCII characters."],
            });
        }

        if (!request.TryCreateTerms(
                out PaymentIntentTerms? terms,
                out Dictionary<string, string[]> errors))
        {
            return Results.ValidationProblem(errors);
        }

        PaymentIntentCreateResult result = await service
            .CreateAsync(key, terms, cancellationToken)
            .ConfigureAwait(false);

        if (result.Disposition == PaymentIntentCreateDisposition.Conflict)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency key conflict",
                detail: "This Idempotency-Key was already used with different payment terms.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "idempotency_key_reused",
                });
        }

        PaymentIntent intent = result.Intent
            ?? throw new InvalidOperationException("A successful store result must contain an intent.");
        PaymentIntentResponse response = PaymentIntentResponse.FromDomain(intent);
        string location = $"/v1/payment-intents/{intent.Id.Value}";

        bool replayed = result.Disposition == PaymentIntentCreateDisposition.Replayed;
        context.Response.Headers[ReplayHeader] = replayed ? "true" : "false";

        return replayed
            ? Results.Ok(response)
            : Results.Created(location, response);
    }

    private static async Task<IResult> GetByIdAsync(
        HttpContext context,
        string paymentId,
        PaymentIntentService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!PaymentId.TryParse(paymentId, out PaymentId? parsedId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(paymentId)] =
                ["paymentId must be a non-zero 32-byte hexadecimal value with a 0x prefix."],
            });
        }

        PaymentIntent? intent = await service
            .FindByIdAsync(parsedId, cancellationToken)
            .ConfigureAwait(false);

        return intent is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Payment intent not found",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "payment_intent_not_found",
                })
            : Results.Ok(PaymentIntentResponse.FromDomain(intent));
    }

    private static bool TryReadIdempotencyKey(
        IHeaderDictionary headers,
        [NotNullWhen(true)] out IdempotencyKey? idempotencyKey)
    {
        StringValues values = headers[IdempotencyHeader];
        if (values.Count != 1)
        {
            idempotencyKey = null;
            return false;
        }

        return IdempotencyKey.TryParse(values[0], out idempotencyKey);
    }
}
