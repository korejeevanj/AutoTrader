using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Trading.Api;

public sealed class RequestBodyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest) return false;
        httpContext.Response.StatusCode = badRequest.StatusCode;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "Invalid request body",
                Detail = (httpContext.Request.Path.StartsWithSegments("/api/replay")
                    ? "Supply a JSON object with the required date or command properties; dates use yyyy-MM-dd. "
                    : "Supply a JSON object with every required contract property. lots must be a positive integer within the 32-bit range; strings cannot be null. ")
                    + badRequest.Message
            }
        });
        return true;
    }
}
