using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reklio.Payments;

/// <summary>Server-side Tribute Shop API integration. Never expose the API key to a browser.</summary>
public sealed class TributeShopClient(HttpClient http, string apiKey)
{
    private const string BaseUrl = "https://tribute.tg/api/v1/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("TRIBUTE_API_KEY is not configured.");
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("Api-Key", apiKey);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tribute API returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public Task<JsonDocument> GetShopsAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, "shops", null, ct);

    public Task<JsonDocument> GetOrderAsync(Guid orderUuid, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"shop/orders/{orderUuid:D}", null, ct);

    public Task<JsonDocument> CreateOrderAsync(
        long amountKopecks, string title, string description, string customerId,
        string successUrl, string failUrl, CancellationToken ct = default)
    {
        if (amountKopecks <= 0) throw new ArgumentOutOfRangeException(nameof(amountKopecks));
        if (string.IsNullOrWhiteSpace(customerId)) throw new ArgumentException("Customer ID is required.", nameof(customerId));
        if (title.Length > 100 || description.Length > 300)
            throw new ArgumentException("Tribute order title or description exceeds the limit.");
        if (!IsHttps(successUrl) || !IsHttps(failUrl))
            throw new ArgumentException("Redirect URLs must use HTTPS.");
        return SendAsync(HttpMethod.Post, "shop/orders", new
        {
            amount = amountKopecks,
            currency = "rub",
            title,
            description,
            customerId,
            successUrl,
            failUrl,
            period = "onetime"
        }, ct);
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
