using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PaymentSandbox.Api.PaymentIntents;
using PaymentSandbox.Api.Tests.Infrastructure;

namespace PaymentSandbox.Api.Tests.PaymentIntents;

public sealed class PaymentIntentHttpTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task PostThenGet_CreatesCanonicalOffChainResource()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(new FixedTimeProvider(Now));

        using HttpResponseMessage created = await PostAsync(
            host.Client,
            "checkout-http-1",
            ValidRequest(
                chainId: "00031337",
                token: "0X2222222222222222222222222222222222222222",
                amount: "0001250000"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("false", Assert.Single(created.Headers.GetValues("Idempotency-Replayed")));
        PaymentIntentResponse body = await ReadIntentAsync(created);
        Assert.Equal("created", body.Status);
        Assert.Equal("31337", body.ChainId);
        Assert.Equal("0x2222222222222222222222222222222222222222", body.TokenAddress);
        Assert.Equal("1250000", body.AmountRaw);
        Assert.Equal(Now, body.CreatedAtUtc);
        Assert.Equal($"/v1/payment-intents/{body.PaymentId}", created.Headers.Location?.OriginalString);

        using HttpResponseMessage queried = await host.Client.GetAsync(
            created.Headers.Location,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, queried.StatusCode);
        Assert.Equal("no-store", queried.Headers.CacheControl?.ToString());
        Assert.Equal(body, await ReadIntentAsync(queried));
    }

    [Fact]
    public async Task Post_SameKeyAndNormalizedTermsReplaysOriginalResource()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(new FixedTimeProvider(Now));

        using HttpResponseMessage first = await PostAsync(
            host.Client,
            "checkout-replay",
            ValidRequest());
        PaymentIntentResponse firstBody = await ReadIntentAsync(first);

        using HttpResponseMessage replay = await PostAsync(
            host.Client,
            "checkout-replay",
            ValidRequest(chainId: "031337", token: "0X2222222222222222222222222222222222222222"));
        PaymentIntentResponse replayBody = await ReadIntentAsync(replay);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(firstBody, replayBody);
    }

    [Fact]
    public async Task Post_SameKeyWithDifferentTermsReturnsNonLeakingConflict()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage first = await PostAsync(
            host.Client,
            "checkout-conflict",
            ValidRequest());
        PaymentIntentResponse created = await ReadIntentAsync(first);

        using HttpResponseMessage conflict = await PostAsync(
            host.Client,
            "checkout-conflict",
            ValidRequest(amount: "2"));
        string json = await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_key_reused", json, StringComparison.Ordinal);
        Assert.DoesNotContain(created.PaymentId, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentPosts_PublishExactlyOneResource()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(new FixedTimeProvider(Now));

        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 20)
            .Select(_ => PostAsync(host.Client, "checkout-race", ValidRequest()))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.OK));

            PaymentIntentResponse[] bodies = await Task.WhenAll(
                responses.Select(ReadIntentAsync));
            Assert.Single(bodies.Select(body => body.PaymentId).Distinct(StringComparer.Ordinal));
            Assert.Single(bodies.Distinct());
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Restart_PreservesLookupAndIdempotentReplay()
    {
        await using var database = new TemporarySqliteDatabase();
        PaymentIntentResponse createdBody;

        await using (ApiTestHost firstHost = await ApiTestHost.StartAsync(
            new FixedTimeProvider(Now),
            database.DatabasePath))
        {
            using HttpResponseMessage created = await PostAsync(
                firstHost.Client,
                "restart-safe-key",
                ValidRequest());
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            createdBody = await ReadIntentAsync(created);
        }

        await using (ApiTestHost restartedHost = await ApiTestHost.StartAsync(
            new FixedTimeProvider(Now.AddDays(1)),
            database.DatabasePath))
        {
            using HttpResponseMessage queried = await restartedHost.Client.GetAsync(
                $"/v1/payment-intents/{createdBody.PaymentId}",
                TestContext.Current.CancellationToken);
            using HttpResponseMessage replayed = await PostAsync(
                restartedHost.Client,
                "restart-safe-key",
                ValidRequest());

            Assert.Equal(HttpStatusCode.OK, queried.StatusCode);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            Assert.Equal(createdBody, await ReadIntentAsync(queried));
            Assert.Equal(createdBody, await ReadIntentAsync(replayed));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contains space")]
    public async Task Post_InvalidOrMissingIdempotencyKeyReturns400(string? key)
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await PostAsync(host.Client, key, ValidRequest());
        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Idempotency-Key", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_RepeatedIdempotencyHeaderReturns400()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payment-intents")
        {
            Content = JsonContent.Create(ValidRequest()),
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            ["first-key", "second-key"]);

        using HttpResponseMessage response = await host.Client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0", "0x2222222222222222222222222222222222222222", "0x3333333333333333333333333333333333333333", "1", "ChainId")]
    [InlineData("31337", "0x0", "0x3333333333333333333333333333333333333333", "1", "TokenAddress")]
    [InlineData("31337", "0x2222222222222222222222222222222222222222", "0x0000000000000000000000000000000000000000", "1", "MerchantAddress")]
    [InlineData("31337", "0x2222222222222222222222222222222222222222", "0x3333333333333333333333333333333333333333", "0", "AmountRaw")]
    [InlineData("31337", "0x2222222222222222222222222222222222222222", "0x3333333333333333333333333333333333333333", "1.5", "AmountRaw")]
    public async Task Post_InvalidTermsReturnFieldSpecific400(
        string chainId,
        string token,
        string merchant,
        string amount,
        string expectedField)
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await PostAsync(
            host.Client,
            "invalid-terms",
            ValidRequest(chainId, token, merchant, amount));
        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedField, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_MalformedAndUnknownIdsHaveDifferentFailureSemantics()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage malformed = await host.Client.GetAsync(
            "/v1/payment-intents/not-a-payment-id",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage unknown = await host.Client.GetAsync(
            $"/v1/payment-intents/0x{new string('f', 64)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        string unknownJson = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("payment_intent_not_found", unknownJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_MalformedAndOversizedJsonFailBeforeApplicationMutation()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/payment-intents")
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json"),
        };
        malformedRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "malformed-json");
        using HttpResponseMessage malformed = await host.Client.SendAsync(
            malformedRequest,
            TestContext.Current.CancellationToken);

        string oversizedJson = $"{{\"chainId\":\"31337\",\"unused\":\"{new string('x', 20_000)}\"}}";
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/payment-intents")
        {
            Content = new StringContent(oversizedJson, Encoding.UTF8, "application/json"),
        };
        oversizedRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "oversized-json");
        using HttpResponseMessage oversized = await host.Client.SendAsync(
            oversizedRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_DoesNotRequirePaymentHeaders()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.Client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? idempotencyKey,
        CreatePaymentIntentRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/payment-intents")
        {
            Content = JsonContent.Create(request),
        };

        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        HttpResponseMessage response = await client.SendAsync(
            message,
            TestContext.Current.CancellationToken);
        message.Dispose();
        return response;
    }

    private static async Task<PaymentIntentResponse> ReadIntentAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<PaymentIntentResponse>(
            cancellationToken: TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty intent response.");

    private static CreatePaymentIntentRequest ValidRequest(
        string chainId = "31337",
        string token = "0x2222222222222222222222222222222222222222",
        string merchant = "0x3333333333333333333333333333333333333333",
        string amount = "1250000") =>
        new(chainId, token, merchant, amount);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
