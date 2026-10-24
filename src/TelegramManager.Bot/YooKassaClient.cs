using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TelegramManager.Bot;

// The payment provider is authoritative: a return URL or button click never marks an order paid.
public sealed class YooKassaClient : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.yookassa.ru/v3/"), Timeout = TimeSpan.FromSeconds(15) };
    private readonly bool _testMode;
    public bool Enabled { get; }
    public bool TestMode => _testMode;

    public YooKassaClient()
    {
        var shop = Environment.GetEnvironmentVariable("YOOKASSA_SHOP_ID");
        var secret = Environment.GetEnvironmentVariable("YOOKASSA_SECRET_KEY");
        _testMode = string.Equals(Environment.GetEnvironmentVariable("YOOKASSA_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase);
        Enabled = !string.IsNullOrWhiteSpace(shop) && !string.IsNullOrWhiteSpace(secret) && _testMode;
        if (Enabled)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{shop}:{secret}")));
    }

    public async Task<(string Id,string Url)> CreateAsync(long orderId, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("YooKassa test checkout is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "payments");
        request.Headers.Add("Idempotence-Key", idempotencyKey);
        request.Content = JsonContent.Create(new
        {
            amount = new { value = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), currency = "RUB" },
            capture = true,
            confirmation = new { type = "redirect", return_url = "https://t.me/" },
            description = $"Реклама, заказ #{orderId}",
            metadata = new { order_id = orderId }
        });
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        return (root.GetProperty("id").GetString()!, root.GetProperty("confirmation").GetProperty("confirmation_url").GetString()!);
    }

    public async Task<(string Status, decimal Amount, string Currency, bool Paid)> GetAsync(string paymentId,CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("YooKassa test checkout is not configured.");
        using var response = await _http.GetAsync("payments/" + Uri.EscapeDataString(paymentId), ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        var amount = root.GetProperty("amount");
        return (root.GetProperty("status").GetString()!,
            decimal.Parse(amount.GetProperty("value").GetString()!,System.Globalization.CultureInfo.InvariantCulture),
            amount.GetProperty("currency").GetString()!,
            root.GetProperty("paid").GetBoolean());
    }

    public void Dispose() => _http.Dispose();
}
