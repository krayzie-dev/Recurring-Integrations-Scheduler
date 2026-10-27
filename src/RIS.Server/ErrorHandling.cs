using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RIS.Core.Errors;
using RIS.Persistence;

namespace RIS.Server;

/// <summary>
/// Maps RIS exceptions to problem details: validation → 400 with the list of errors, missing item → 404,
/// other RIS errors → 500 with the error code. Unexpected exceptions are left to the default handler (500, no details).
/// </summary>
public sealed class RisExceptionHandler(IProblemDetailsService problemDetails, ILogger<RisExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            ConfigurationValidationException validation => new HttpValidationProblemDetails(
                new Dictionary<string, string[]> { ["configuration"] = [.. validation.Errors] })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Configuration is invalid."
            },
            ConfigurationNotFoundException notFound => new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = notFound.Message },
            RisException ris => new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = ris.Message },
            _ => null
        };
        if (problem is null) return false;

        if (exception is RisException { ErrorCode: var code })
        {
            problem.Extensions["errorCode"] = code;
        }
        if (problem.Status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Request {Method} {Path} failed", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }
}
