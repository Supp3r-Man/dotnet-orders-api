using OrdersApi.Domain;

namespace OrdersApi.Application;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    PreconditionFailed,
    PreconditionRequired,
    Unprocessable,
    PayloadTooLarge,
    UnsupportedMediaType,
    Unavailable,
}

/// <summary>One field-level problem. <paramref name="Field"/> is a body path (lines[0].quantity), a query name or a header name.</summary>
public sealed record FieldError(string Field, string Code, string Message);

/// <summary>
/// Every expected failure in the application layer. The API layer turns it into one RFC 9457 problem document;
/// nothing else is allowed to decide an error response.
/// </summary>
public sealed class AppException(ErrorKind kind, string code, string message, IReadOnlyList<FieldError>? errors = null)
    : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
    public IReadOnlyList<FieldError> Errors { get; } = errors ?? [];

    public static AppException Validation(IReadOnlyList<FieldError> errors) =>
        new(ErrorKind.Validation, "validation-failed", "One or more fields are invalid.", errors);

    public static AppException Validation(string field, string code, string message) =>
        Validation([new FieldError(field, code, message)]);

    public static AppException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static AppException From(DomainException ex) => ex.Code switch
    {
        "invalid-transition" => new AppException(ErrorKind.Conflict, ex.Code, ex.Message),
        _ => new AppException(ErrorKind.Unprocessable, ex.Code, ex.Message),
    };
}
