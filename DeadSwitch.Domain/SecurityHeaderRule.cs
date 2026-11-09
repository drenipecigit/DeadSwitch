namespace DeadSwitch.Domain;

public static class SecurityHeaderRule
{
    // Written between the names of the headers that are missing.
    public const string Separator = ", ";

    public static readonly IReadOnlyList<string> CheckedHeaders = new string[]
    {
        "Strict-Transport-Security",
        "Content-Security-Policy",
        "X-Content-Type-Options",
        "X-Frame-Options",
        "Referrer-Policy",
    };

    // Every header counts the same, so that a grade is easy to explain. A grade only says how many
    // headers are present, and not whether their values are any good.
    public static string GradeFor(int missingCount)
    {
        if (missingCount == 0)
        {
            return "A";
        }

        if (missingCount == 1)
        {
            return "B";
        }

        if (missingCount == 2)
        {
            return "C";
        }

        if (missingCount == 3)
        {
            return "D";
        }

        return "F";
    }
}
