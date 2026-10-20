using System.Text.Json;
using OrdersApi.Application;

namespace OrdersApi.Api;

/// <summary>
/// Reads a JSON body by hand instead of using minimal-API parameter binding, because binding failures bypass
/// our error shape and give no field path. Here a bad body becomes the same problem document as everything else.
/// </summary>
public static class RequestBody
{
    public const int MaxBytes = 64 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task<T?> ReadJsonAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType())
        {
            throw new AppException(ErrorKind.UnsupportedMediaType, "unsupported-media-type",
                "Content-Type must be application/json.");
        }

        if (request.ContentLength > MaxBytes)
        {
            throw TooLarge();
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                throw TooLarge();
            }
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        // System.Text.Json rejects a UTF-8 byte order mark that Windows tools (PowerShell, Notepad) like to add.
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options);
        }
        catch (JsonException ex)
        {
            throw new AppException(ErrorKind.Validation, "invalid-json", "The request body is not valid JSON for this endpoint.",
                [new FieldError(ex.Path ?? "$", "invalid-json", "This value is missing, malformed or has the wrong type.")]);
        }
    }

    private static AppException TooLarge() => new(ErrorKind.PayloadTooLarge, "payload-too-large",
        $"The request body must be at most {MaxBytes} bytes.");
}
