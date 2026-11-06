using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reklio.Payments;

/// <summary>PayAnyWay (MONETA.Assistant) payment links and Pay URL signature checks.</summary>
public static class PayAnyWay
{
    private const string FormUrl = "https://www.payanyway.ru/assistant.htm";

    public static string Amount(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    public static string PaymentUrl(string accountId, string integrityCode, string transactionId, decimal amount,
        long subscriberId, bool testMode, string description, string returnUrl)
    {
        var total = Amount(amount);
        var subscriber = subscriberId.ToString(CultureInfo.InvariantCulture);
        var flag = testMode ? "1" : "0";
        var signature = Md5Hex(accountId + transactionId + total + "RUB" + subscriber + flag + integrityCode);
        var query = new Dictionary<string, string>
        {
            ["MNT_ID"] = accountId,
            ["MNT_TRANSACTION_ID"] = transactionId,
            ["MNT_AMOUNT"] = total,
            ["MNT_CURRENCY_CODE"] = "RUB",
            ["MNT_SUBSCRIBER_ID"] = subscriber,
            ["MNT_TEST_MODE"] = flag,
            ["MNT_DESCRIPTION"] = description,
            ["MNT_SUCCESS_URL"] = returnUrl,
            ["MNT_FAIL_URL"] = returnUrl,
            ["MNT_SIGNATURE"] = signature
        };
        return FormUrl + "?" + string.Join("&", query.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
    }

    /// <summary>The amount must be passed normalized: the provider signs it with two decimals.</summary>
    public static bool IsValidNotification(string accountId, string integrityCode, string transactionId, string operationId,
        string amount, string currency, string subscriberId, string testMode, string signature)
    {
        var expected = Md5Hex(accountId + transactionId + operationId + amount + currency + subscriberId + testMode + integrityCode);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
