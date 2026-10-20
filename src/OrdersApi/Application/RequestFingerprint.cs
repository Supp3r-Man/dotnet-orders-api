using System.Security.Cryptography;
using System.Text;

namespace OrdersApi.Application;

/// <summary>
/// A stable hash of the normalised request. Two bodies that mean the same order (different line order, different
/// case or padding on SKUs, split quantities for one SKU) hash the same, so a retry is a replay. Every string is
/// length-prefixed so that ("ab","c") and ("a","bc") cannot collide by concatenation.
/// </summary>
public static class RequestFingerprint
{
    public static string Compute(NormalizedOrder order)
    {
        var canonical = new StringBuilder("v1|customer=")
            .Append(order.CustomerRef.Length).Append(':').Append(order.CustomerRef);
        foreach (var line in order.Lines)
        {
            canonical.Append("|sku=").Append(line.Sku.Length).Append(':').Append(line.Sku)
                .Append("|qty=").Append(line.Quantity);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
