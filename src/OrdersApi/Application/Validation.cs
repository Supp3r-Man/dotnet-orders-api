using System.Globalization;
using System.Text.RegularExpressions;
using OrdersApi.Domain;

namespace OrdersApi.Application;

/// <summary>
/// All input rules in one place. Each method collects every problem it can find and throws one
/// <see cref="AppException"/> with field-level errors, so a client fixes a request in one round trip.
/// </summary>
public static partial class Validation
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const int MaxKeyLength = 128;
    public const int MaxCustomerLength = 64;
    public const int MaxSkuLength = 64;
    public const int MaxNameLength = 100;
    public const int MaxLines = 50;
    public const int MaxQuantity = 1000;
    public const long MaxUnitPriceMinor = 1_000_000_000;
    public const int MaxStock = 1_000_000;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SkuPattern();

    public static string NormalizeSku(string sku) => sku.Trim().ToUpperInvariant();

    public static NormalizedOrder ValidateOrder(string? idempotencyKey, CreateOrderInput? input)
    {
        var errors = new List<FieldError>();
        ValidateKey(idempotencyKey, errors);

        string customer = "";
        var merged = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (input is null)
        {
            errors.Add(new FieldError("body", "required", "A JSON request body is required."));
        }
        else
        {
            customer = (input.CustomerRef ?? "").Trim();
            if (customer.Length == 0)
            {
                errors.Add(new FieldError("customerRef", "required", "customerRef is required."));
            }
            else if (customer.Length > MaxCustomerLength)
            {
                errors.Add(new FieldError("customerRef", "too-long",
                    $"customerRef must be at most {MaxCustomerLength} characters."));
            }

            ValidateLines(input.Lines, errors, merged);
        }

        if (errors.Count > 0)
        {
            throw AppException.Validation(errors);
        }

        var lines = merged.Select(kv => new NormalizedLine(kv.Key, (int)kv.Value)).ToList();
        return new NormalizedOrder(idempotencyKey!, customer, lines);
    }

    private static void ValidateKey(string? key, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add(new FieldError(IdempotencyKeyHeader, "required",
                $"The {IdempotencyKeyHeader} header is required to create an order."));
            return;
        }

        if (key.Length > MaxKeyLength)
        {
            errors.Add(new FieldError(IdempotencyKeyHeader, "too-long",
                $"The {IdempotencyKeyHeader} header must be at most {MaxKeyLength} characters."));
        }
        else if (key.Any(c => c < '!' || c > '~'))
        {
            errors.Add(new FieldError(IdempotencyKeyHeader, "invalid-format",
                $"The {IdempotencyKeyHeader} header may only contain printable ASCII without spaces."));
        }
    }

    private static void ValidateLines(IReadOnlyList<OrderLineInput?>? lines, List<FieldError> errors,
        SortedDictionary<string, long> merged)
    {
        if (lines is null || lines.Count == 0)
        {
            errors.Add(new FieldError("lines", "required", "lines must contain at least one line."));
            return;
        }

        if (lines.Count > MaxLines)
        {
            errors.Add(new FieldError("lines", "too-many", $"lines must contain at most {MaxLines} entries."));
            return;
        }

        var firstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line is null)
            {
                errors.Add(new FieldError($"lines[{i}]", "required", "A line must be an object."));
                continue;
            }

            var lineOk = ValidateSku(line.Sku, $"lines[{i}].sku", errors, out var sku);
            if (line.Quantity is null)
            {
                errors.Add(new FieldError($"lines[{i}].quantity", "required", "quantity is required."));
                continue;
            }

            if (line.Quantity < 1 || line.Quantity > MaxQuantity)
            {
                errors.Add(new FieldError($"lines[{i}].quantity", "out-of-range",
                    $"quantity must be between 1 and {MaxQuantity}."));
                continue;
            }

            if (!lineOk)
            {
                continue;
            }

            merged[sku] = merged.GetValueOrDefault(sku) + line.Quantity.Value;
            firstIndex.TryAdd(sku, i);
        }

        foreach (var (sku, total) in merged)
        {
            if (total > MaxQuantity)
            {
                errors.Add(new FieldError($"lines[{firstIndex[sku]}].quantity", "out-of-range",
                    $"The combined quantity for SKU {sku} must be at most {MaxQuantity}."));
            }
        }
    }

    private static bool ValidateSku(string? raw, string field, List<FieldError> errors, out string sku)
    {
        sku = "";
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            errors.Add(new FieldError(field, "required", "sku is required."));
            return false;
        }

        if (trimmed.Length > MaxSkuLength)
        {
            errors.Add(new FieldError(field, "too-long", $"sku must be at most {MaxSkuLength} characters."));
            return false;
        }

        if (!SkuPattern().IsMatch(trimmed))
        {
            errors.Add(new FieldError(field, "invalid-format", "sku may only contain letters, digits, '.', '_' and '-'."));
            return false;
        }

        sku = NormalizeSku(trimmed);
        return true;
    }

    public static CreateItemInput ValidateItem(CreateItemInput? input)
    {
        var errors = new List<FieldError>();
        if (input is null)
        {
            throw AppException.Validation("body", "required", "A JSON request body is required.");
        }

        var skuOk = ValidateSku(input.Sku, "sku", errors, out var sku);

        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            errors.Add(new FieldError("name", "required", "name is required."));
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add(new FieldError("name", "too-long", $"name must be at most {MaxNameLength} characters."));
        }

        if (input.UnitPriceMinor is null)
        {
            errors.Add(new FieldError("unitPriceMinor", "required", "unitPriceMinor is required (integer minor units)."));
        }
        else if (input.UnitPriceMinor < 1 || input.UnitPriceMinor > MaxUnitPriceMinor)
        {
            errors.Add(new FieldError("unitPriceMinor", "out-of-range",
                $"unitPriceMinor must be between 1 and {MaxUnitPriceMinor}."));
        }

        if (input.Stock is null)
        {
            errors.Add(new FieldError("stock", "required", "stock is required."));
        }
        else if (input.Stock < 0 || input.Stock > MaxStock)
        {
            errors.Add(new FieldError("stock", "out-of-range", $"stock must be between 0 and {MaxStock}."));
        }

        if (errors.Count > 0 || !skuOk)
        {
            throw AppException.Validation(errors);
        }

        return new CreateItemInput(sku, name, input.UnitPriceMinor, input.Stock);
    }

    public static OrderStatus ValidateStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw AppException.Validation("status", "required", "status is required.");
        }

        if (!OrderStatusRules.TryParse(status.Trim(), out var parsed))
        {
            throw AppException.Validation("status", "invalid-value",
                "status must be one of NEW, PAID, SHIPPED, CANCELLED.");
        }

        return parsed;
    }

    public static OrderListQuery ValidateListQuery(string? status, string? customerRef, string? createdAfter,
        string? page, string? size)
    {
        var errors = new List<FieldError>();

        OrderStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (OrderStatusRules.TryParse(status.Trim(), out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add(new FieldError("status", "invalid-value", "status must be one of NEW, PAID, SHIPPED, CANCELLED."));
            }
        }

        var customer = customerRef?.Trim();
        if (string.IsNullOrEmpty(customer))
        {
            customer = null;
        }
        else if (customer.Length > MaxCustomerLength)
        {
            errors.Add(new FieldError("customerRef", "too-long", $"customerRef must be at most {MaxCustomerLength} characters."));
        }

        DateTimeOffset? after = null;
        if (!string.IsNullOrWhiteSpace(createdAfter))
        {
            if (DateTimeOffset.TryParse(createdAfter.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                after = parsed;
            }
            else
            {
                errors.Add(new FieldError("createdAfter", "invalid-value", "createdAfter must be an ISO 8601 timestamp."));
            }
        }

        var pageNumber = ParseInt(page, "page", 0, MaxPage, 0, errors);
        var pageSize = ParseInt(size, "size", 1, MaxPageSize, DefaultPageSize, errors);

        if (errors.Count > 0)
        {
            throw AppException.Validation(errors);
        }

        return new OrderListQuery(statusFilter, customer, after, pageNumber, pageSize);
    }

    private static int ParseInt(string? text, string field, int min, int max, int fallback, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add(new FieldError(field, "invalid-value", $"{field} must be a whole number."));
            return fallback;
        }

        if (value < min || value > max)
        {
            errors.Add(new FieldError(field, "out-of-range", $"{field} must be between {min} and {max}."));
            return fallback;
        }

        return value;
    }
}
