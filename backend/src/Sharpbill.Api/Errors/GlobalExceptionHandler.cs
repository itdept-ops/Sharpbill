using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using MySqlConnector;
using Sharpbill.Api.Diagnostics;
using Sharpbill.Api.Middleware;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Api.Errors;

public sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IPrivilegedDenialRecorder denialRecorder,
    BoundaryRejectionLogger boundaryRejectionLogger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        (int status, string code, string message, LogLevel level) = exception switch
        {
            ApiException applicationError => (
                applicationError.StatusCode,
                applicationError.Code,
                applicationError.Message,
                applicationError.StatusCode >= 500 ? LogLevel.Error : LogLevel.Information),
            RequestBodyTooLargeException => (
                StatusCodes.Status413PayloadTooLarge,
                "REQUEST_TOO_LARGE",
                "Request body exceeds the allowed size",
                LogLevel.Warning),
            JsonException => (
                StatusCodes.Status422UnprocessableEntity,
                "VALIDATION_ERROR",
                "Invalid request",
                LogLevel.Information),
            BadHttpRequestException badRequest when badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge => (
                StatusCodes.Status413PayloadTooLarge,
                "REQUEST_TOO_LARGE",
                "Request body exceeds the allowed size",
                LogLevel.Warning),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (
                StatusCodes.Status499ClientClosedRequest,
                "REQUEST_CANCELLED",
                "Request was cancelled.",
                LogLevel.Debug),
            MySqlException => (
                StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE",
                "The database is temporarily unavailable.",
                LogLevel.Error),
            DatabaseConnectionTimeoutException => (
                StatusCodes.Status503ServiceUnavailable,
                "DATABASE_UNAVAILABLE",
                "The database is temporarily unavailable.",
                LogLevel.Error),
            _ => (
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                "Internal server error",
                LogLevel.Error),
        };

        LogFailure(logger, level, exception, code, status);
        if (status == StatusCodes.Status413PayloadTooLarge)
        {
            boundaryRejectionLogger.Record(httpContext, "request_body", code, status);
        }

        await denialRecorder.RecordAsync(httpContext, status, code).ConfigureAwait(false);

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        if (exception is ApiException apiException)
        {
            foreach ((string header, string value) in apiException.Headers)
            {
                httpContext.Response.Headers[header] = value;
            }
        }

        await ApiErrorWriter.WriteAsync(
            httpContext,
            status,
            code,
            message,
            exception is ApiException { ValidationFailures: { } validationFailures }
                ? validationFailures.Select(static failure => new
                {
                    type = failure.Code,
                    loc = new object[] { "body", failure.Field },
                    msg = failure.Message,
                }).ToArray()
                : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(EventId = 1000, Message = "Request failed with code {ErrorCode} and status {StatusCode}")]
    private static partial void LogFailure(
        ILogger logger,
        LogLevel level,
        Exception exception,
        string errorCode,
        int statusCode);
}
