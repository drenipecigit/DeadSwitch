namespace DeadSwitch.Api.Security;

// Security-critical: stops a page from another site from using the owner's browser to call the API.
// There are no cookies to steal, but there is also no login: being on this machine is the only
// permission, and a page in the owner's browser is on this machine. An image tag is enough to start a
// GET, and a form is enough to start a POST, even though the page can never read the answer.
//
// This is not a login. Programs on the same machine send neither header and are let through.
public static class CrossSiteRequests
{
    public static async Task BlockAsync(HttpContext context, RequestDelegate next)
    {
        if (ComesFromAnotherSite(context.Request))
        {
            IResult problem = Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Request blocked",
                detail: "Requests from other sites are not allowed.");
            await problem.ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    // Two checks that overlap, kept on purpose: not every browser sends Sec-Fetch-Site to localhost,
    // and the protection must not depend on one header.
    private static bool ComesFromAnotherSite(HttpRequest request)
    {
        // An allowlist, so that a value this code has never heard of is refused. same-site is refused
        // as well, because localhost:3000 counts as the same site as this application.
        string fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite != "" && fetchSite != "same-origin" && fetchSite != "none")
        {
            return true;
        }

        // Compared as a whole string, so that localhost.evil.example or another port never matches.
        string origin = request.Headers.Origin.ToString();
        string ownOrigin = request.Scheme + "://" + request.Host;
        if (origin != "" && !string.Equals(origin, ownOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
