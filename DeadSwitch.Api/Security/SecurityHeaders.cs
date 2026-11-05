namespace DeadSwitch.Api.Security;

// Security-critical: runs first in the pipeline and covers every answer, so that no path can be
// forgotten. The API's JSON answers get the same headers as the page, which does them no harm.
public static class SecurityHeaders
{
    // Everything is forbidden except our own files. No 'unsafe-inline' and no 'unsafe-eval': if a name
    // from a user ever reached the page as HTML, the browser would still refuse to run it.
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static Task AddAsync(HttpContext context, RequestDelegate next)
    {
        // Set just before the answer is sent, and not here. The exception handler empties the headers
        // before it writes a 500, so headers set here would be missing from that answer.
        context.Response.OnStarting(() =>
        {
            WriteHeaders(context.Response);
            return Task.CompletedTask;
        });

        return next(context);
    }

    private static void WriteHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;

        // The browser must not guess the file type, so JSON or text can never run as a script.
        response.Headers.XContentTypeOptions = "nosniff";

        // Means the same as frame-ancestors above, for browsers that do not read that part of the policy.
        response.Headers.XFrameOptions = "DENY";

        // No address from this application is passed on to another site.
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
