namespace OrdersApi.Application;

public static class ETags
{
    /// <summary>The strong ETag of an order: its concurrency version in quotes, e.g. "3".</summary>
    public static string ForVersion(int version) => $"\"{version}\"";
}

/// <summary>A parsed If-Match or If-None-Match header value.</summary>
public sealed class EntityTagPrecondition
{
    private readonly List<(bool Weak, string Value)> _tags = [];

    private EntityTagPrecondition()
    {
    }

    public bool Any { get; private set; }

    /// <summary>If-Match uses strong comparison: a weak tag (W/"1") never matches.</summary>
    public bool MatchesStrong(string etag) => Any || _tags.Exists(t => !t.Weak && t.Value == etag);

    /// <summary>If-None-Match uses weak comparison: W/"1" and "1" are equal.</summary>
    public bool MatchesWeak(string etag) => Any || _tags.Exists(t => t.Value == etag);

    /// <summary>
    /// Parses "*" or a comma separated list of ETags ("1", W/"2"). Returns false for anything malformed,
    /// including an empty value, so the caller can answer 400 instead of guessing.
    /// </summary>
    public static bool TryParse(string? headerValue, out EntityTagPrecondition? result)
    {
        result = null;
        if (headerValue is null)
        {
            return false;
        }

        var parsed = new EntityTagPrecondition();
        var i = 0;
        var count = 0;
        while (i < headerValue.Length)
        {
            var c = headerValue[i];
            if (c == ' ' || c == '\t' || c == ',')
            {
                i++;
                continue;
            }

            if (c == '*')
            {
                parsed.Any = true;
                i++;
            }
            else
            {
                var weak = false;
                if (string.CompareOrdinal(headerValue, i, "W/", 0, 2) == 0)
                {
                    weak = true;
                    i += 2;
                }

                if (i >= headerValue.Length || headerValue[i] != '"')
                {
                    return false;
                }

                var close = headerValue.IndexOf('"', i + 1);
                if (close < 0)
                {
                    return false;
                }

                parsed._tags.Add((weak, headerValue.Substring(i, close - i + 1)));
                i = close + 1;
            }

            count++;
            if (i < headerValue.Length && headerValue[i] is not (' ' or '\t' or ','))
            {
                return false;
            }
        }

        if (count == 0)
        {
            return false;
        }

        result = parsed;
        return true;
    }
}
