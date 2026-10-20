using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OrdersApi.Application;

namespace OrdersApi.Api;

/// <summary>
/// The single error shape (RFC 9457 problem details): type, title, status, detail, instance, plus the extension
/// members code (machine readable), errors (field level, possibly empty) and traceId. Our own errors and the
/// framework's (unknown route, wrong method) are both normalised here, so a client parses one document.
/// </summary>
public static class Problems
{
    public const string TypePrefix = "urn:orders-api:problem:";

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorKind.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorKind.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TitleFor(int status) => status switch
    {
        400 => "Bad request",
        404 => "Not found",
        405 => "Method not allowed",
        409 => "Conflict",
        412 => "Precondition failed",
        413 => "Payload too large",
        415 => "Unsupported media type",
        422 => "Unprocessable request",
        428 => "Precondition required",
        503 => "Service unavailable",
        500 => "Internal server error",
        _ => "Error",
    };

    /// <summary>Fallback machine-readable code for errors that did not come from our code.</summary>
    public static string CodeForStatus(int status) => status switch
    {
        400 => "bad-request",
        404 => "not-found",
        405 => "method-not-allowed",
        406 => "not-acceptable",
        415 => "unsupported-media-type",
        500 => "internal-error",
        _ => $"http-{status}",
    };

    public static ProblemDetails Create(int status, string code, string? detail, IReadOnlyList<FieldError>? errors = null)
    {
        var problem = new ProblemDetails { Status = status, Title = TitleFor(status), Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["errors"] = errors ?? [];
        return problem;
    }

    public static ProblemDetails From(AppException ex) =>
        Create(StatusFor(ex.Kind), ex.Code, ex.Message, ex.Errors);

    /// <summary>Applied to every problem document written through <see cref="IProblemDetailsService"/>.</summary>
    public static void Normalize(ProblemDetailsContext context)
    {
        var http = context.HttpContext;
        var problem = context.ProblemDetails;
        problem.Status ??= http.Response.StatusCode;
        if (!problem.Extensions.ContainsKey("code"))
        {
            problem.Extensions["code"] = CodeForStatus(problem.Status.Value);
        }

        if (!problem.Extensions.ContainsKey("errors"))
        {
            problem.Extensions["errors"] = Array.Empty<FieldError>();
        }

        problem.Type = TypePrefix + problem.Extensions["code"];
        problem.Title = TitleFor(problem.Status.Value);
        problem.Detail ??= problem.Title;
        problem.Instance ??= http.Request.Path.Value;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
    }
}

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case AppException app:
                problem = Problems.From(app);
                break;
            case BadHttpRequestException bad:
                problem = Problems.Create(bad.StatusCode, "bad-request", "The request could not be read.");
                break;
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                    httpContext.Request.Method, httpContext.Request.Path);
                problem = Problems.Create(StatusCodes.Status500InternalServerError, "internal-error",
                    "An unexpected error occurred.");
                break;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
