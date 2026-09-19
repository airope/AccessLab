using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AccessLab.Api;

public class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.Headers.CacheControl = "no-store";
        try
        {
            await next(context);
            if (
                !context.Response.HasStarted
                && context.Response.StatusCode >= 400
                && context.Request.Path.StartsWithSegments("/api")
            )
                await Write(
                    context,
                    context.Response.StatusCode,
                    "request_failed",
                    "The request could not be completed."
                );
        }
        catch (ApiError e)
        {
            await Write(context, e.Status, e.Code, e.Message);
        }
        catch (AntiforgeryValidationException)
        {
            await Write(
                context,
                400,
                "csrf_invalid",
                "Refresh the page and retry with a CSRF token."
            );
        }
        catch (BadHttpRequestException)
        {
            await Write(
                context,
                400,
                "invalid_request",
                "The request body or parameters are invalid."
            );
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException p
                && p.SqlState is "23P01" or "23505" or "23503" or "23514"
            )
        {
            await Write(
                context,
                409,
                "concurrent_change",
                "The change conflicts with current state. Refresh impact."
            );
        }
        catch (Exception e)
        {
            logger.LogWarning(
                "Request failed ({ExceptionType}), correlation {CorrelationId}",
                e.GetType().Name,
                context.TraceIdentifier
            );
            var database = e is NpgsqlException || e.InnerException is NpgsqlException;
            await Write(
                context,
                database ? 503 : 500,
                database ? "dependency_unavailable" : "internal_error",
                database
                    ? "The decision store is unavailable. No access has been granted."
                    : "The request could not be completed. Use the correlation ID for support."
            );
        }
    }

    static async Task Write(HttpContext c, int status, string code, string detail)
    {
        if (c.Response.HasStarted)
            return;
        c.Response.StatusCode = status;
        await c.Response.WriteAsJsonAsync(
            new
            {
                status,
                title = code,
                detail,
                code,
                correlationId = c.TraceIdentifier,
            },
            options: Json.Options,
            contentType: "application/problem+json"
        );
    }
}
