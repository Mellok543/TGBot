using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegramManager.Api.Payments;

/// <summary>
/// Low-level YooKassa Safe Deal API client. Not wired to public endpoints until
/// the Safe Deal contract, receipt policy and payout onboarding are complete.
/// Never accept the payment amount or deal id directly from an untrusted client.
/// </summary>
public sealed class YooKassaSafeDealClient(HttpClient http, string shopId, string secretKey)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<YooKassaDeal> CreateDealAsync(long orderId, string idempotenceKey, CancellationToken ct)
    {
        if (orderId <= 0 || !Guid.TryParse(idempotenceKey, out _))
            throw new ArgumentException("An order and persisted UUID key are required.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.yookassa.ru/v3/deals");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{shopId}:{secretKey}")));
        request.Headers.Add("Idempotence-Key", idempotenceKey);
        request.Content = JsonContent.Create(new {
            type = "safe_deal",
            fee_moment = "deal_closed",
            description = $"Размещение Reklio #{orderId}",
            metadata = new { order_id = orderId.ToString(CultureInfo.InvariantCulture) }
        }, options: JsonOptions);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<YooKassaDeal>(JsonOptions, ct)
            ?? throw new JsonException("Empty deal response");
    }

    public async Task<YooKassaDeal> GetDealAsync(string dealId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(dealId) || dealId.Length > 100 ||
            dealId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Invalid deal ID.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.yookassa.ru/v3/deals/{dealId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{shopId}:{secretKey}")));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<YooKassaDeal>(JsonOptions, ct)
            ?? throw new JsonException("Empty deal response");
    }

    public async Task<YooKassaPayment> CreatePaymentAsync(
        long orderId, string dealId, decimal totalRub, decimal sellerRub,
        string returnUrl, string idempotenceKey, CancellationToken ct)
    {
        if (orderId <= 0 || string.IsNullOrWhiteSpace(dealId))
            throw new ArgumentException("A persisted order and provider deal are required.");
        if (totalRub <= 0 || sellerRub <= 0 || sellerRub > totalRub)
            throw new ArgumentOutOfRangeException(nameof(sellerRub));
        if (decimal.Round(totalRub, 2) != totalRub || decimal.Round(sellerRub, 2) != sellerRub)
            throw new ArgumentException("Amounts must have at most two decimal places.");
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri) || returnUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("HTTPS return URL required.");
        if (!Guid.TryParse(idempotenceKey, out _))
            throw new ArgumentException("A persisted UUID idempotency key is required.");

        static object Amount(decimal value) => new
        {
            value = value.ToString("F2", CultureInfo.InvariantCulture),
            currency = "RUB"
        };
        var payload = new
        {
            amount = Amount(totalRub),
            capture = true,
            confirmation = new { type = "redirect", return_url = returnUrl },
            description = $"Рекламное размещение Reklio #{orderId}",
            metadata = new { order_id = orderId.ToString(CultureInfo.InvariantCulture) },
            deal = new
            {
                id = dealId,
                settlements = new[] { new { type = "payout", amount = Amount(sellerRub) } }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.yookassa.ru/v3/payments");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{shopId}:{secretKey}")));
        request.Headers.Add("Idempotence-Key", idempotenceKey);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<YooKassaPayment>(JsonOptions, ct)
            ?? throw new JsonException("Empty payment response");
    }

    public async Task<YooKassaPayment> GetPaymentAsync(string paymentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || paymentId.Length > 100 ||
            paymentId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Invalid provider payment ID.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.yookassa.ru/v3/payments/{paymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{shopId}:{secretKey}")));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<YooKassaPayment>(JsonOptions, ct)
            ?? throw new JsonException("Empty payment response");
    }
}

public sealed record YooKassaPayment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("paid")] bool Paid,
    [property: JsonPropertyName("amount")] YooKassaAmount Amount,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("confirmation")] YooKassaConfirmation? Confirmation);

public sealed record YooKassaAmount(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("currency")] string Currency);

public sealed record YooKassaConfirmation(
    [property: JsonPropertyName("confirmation_url")] string? ConfirmationUrl);

public sealed record YooKassaDeal(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);
