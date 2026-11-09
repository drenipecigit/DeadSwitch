using System.Net.Http.Headers;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Infrastructure.Monitoring;

// Only hands the value over to the parser. The redirect is never followed and its target is never
// looked up, because nothing here opens a connection.
public static class RedirectTargetReader
{
    private const string LocationHeader = "Location";

    public static RedirectTargetCheck? Read(Uri uri, int statusCode, HttpResponseHeaders headers)
    {
        if (statusCode >= 200 && statusCode <= 299)
        {
            return new RedirectTargetCheck(RedirectFinding.NoRedirect, null);
        }

        // An error page often comes from another layer, so it says nothing about where the site points.
        if (statusCode < 300 || statusCode > 399)
        {
            return null;
        }

        List<string> values = new List<string>();

        // Security: NonValidated shows what the server really sent. The typed Headers.Location property
        // gives null for several values or a broken one, which would look like no redirect at all.
        HeaderStringValues found;
        if (headers.NonValidated.TryGetValues(LocationHeader, out found))
        {
            foreach (string value in found)
            {
                values.Add(value);
            }
        }

        return RedirectTargetParser.Parse(uri, values);
    }
}
