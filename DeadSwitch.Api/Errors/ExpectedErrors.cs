using DeadSwitch.Application.Services;

namespace DeadSwitch.Api.Errors;

// Translates the exceptions the Application layer uses for expected errors into status codes. Each
// answer uses the standard Problem Details format, so that every error looks the same to a client.
public static class ExpectedErrors
{
    private const string UnreadableRequestTitle = "Invalid request";
    private const string UnreadableRequestDetail = "The request could not be read.";

    public static async Task TranslateAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);

            if (IsEmpty415(context))
            {
                await WriteProblemAsync(context, StatusCodes.Status415UnsupportedMediaType, UnreadableRequestTitle, UnreadableRequestDetail);
            }
        }
        catch (ServiceValidationException ex)
        {
            // Safe to show: this exception only ever holds fixed texts that we wrote ourselves.
            await WriteProblemAsync(context, 400, "Invalid configuration", ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            // Fixed text on purpose: ex.Message names the parameter and the class, and the inner exception
            // holds the JSON path and position. The status code is kept because it is not always 400:
            // a body that is too large gives 413.
            await WriteProblemAsync(context, ex.StatusCode, UnreadableRequestTitle, UnreadableRequestDetail);
        }
        catch (CheckInProgressException ex)
        {
            await WriteProblemAsync(context, 409, "Check in progress", ex.Message);
        }
    }

    // A wrong content type throws nothing: the framework only sets 415 and leaves the body empty.
    // It gets the same fixed answer here, so that every error has the same format.
    private static bool IsEmpty415(HttpContext context)
    {
        return context.Response.StatusCode == StatusCodes.Status415UnsupportedMediaType && !context.Response.HasStarted;
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        IResult problem = Results.Problem(statusCode: statusCode, title: title, detail: detail);
        await problem.ExecuteAsync(context);
    }
}
