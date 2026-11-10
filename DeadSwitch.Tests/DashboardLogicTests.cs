using System.Net.Http.Json;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;
using Jint;
using Jint.Native;

namespace DeadSwitch.Tests;

// Runs the dashboard's decisions in Jint, a JavaScript engine written in .NET. Only dashboard-logic.js
// is run here: it has no page, so nothing in it needs a browser. These tests prove the decisions, not
// how the page looks or how a real browser reads the times. Only the owner's run in a browser shows that.
public class DashboardLogicTests
{
    // A fixed zone without summer time, so that a test gives the same answer on every machine.
    // Summer time itself is therefore not tested here.
    private static readonly TimeZoneInfo OneHourAheadOfUtc = TimeZoneInfo.CreateCustomTimeZone(
        "Test UTC+1",
        TimeSpan.FromHours(1),
        "Test UTC+1",
        "Test UTC+1");

    private const string ConfirmedDetail = "Confirmed after 2 failed checks in a row";
    private const string UnconfirmedDetail = "1 failed check, no alarm yet";

    // One row for every combination of current and last known status. Rows marked "cannot happen" are
    // states the server never writes, and they must still show a status with its text.
    [Theory]
    [InlineData("UP", "UP", "UP", "status-up", null)]
    [InlineData("UP", null, "UP", "status-up", null)] // cannot happen
    [InlineData("UP", "DOWN", "UP", "status-up", null)] // cannot happen
    [InlineData("DOWN", "DOWN", "DOWN", "status-down", ConfirmedDetail)]
    [InlineData("DOWN", "UP", "DOWN?", "status-pending", UnconfirmedDetail)]
    [InlineData("DOWN", null, "DOWN?", "status-pending", UnconfirmedDetail)]
    [InlineData("UNKNOWN", null, "UNKNOWN", "status-unknown", null)]
    [InlineData("UNKNOWN", "UP", "UNKNOWN", "status-unknown", "Last confirmed: UP")]
    [InlineData("UNKNOWN", "DOWN", "UNKNOWN", "status-unknown", "Last confirmed: DOWN")]
    [InlineData("UNKNOWN", "UNKNOWN", "UNKNOWN", "status-unknown", null)] // cannot happen
    [InlineData("SOMETHING_NEW", "DOWN", "UNKNOWN", "status-unknown", "Last confirmed: DOWN")] // cannot happen
    [InlineData("down", "down", "UNKNOWN", "status-unknown", null)] // cannot happen
    public async Task EveryStatusCombinationGetsTheRightBadge(string currentStatus, string? lastKnownStatus, string text, string className, string? detail)
    {
        Engine engine = await LoadLogicAsync();

        JsValue badge = engine.Invoke("getStatusBadge", currentStatus, lastKnownStatus);

        Assert.Equal(text, ReadText(badge, "text"));
        Assert.Equal(className, ReadText(badge, "className"));
        Assert.Equal(detail, ReadText(badge, "detail"));
    }

    [Fact]
    public async Task TheMostUrgentServicesComeFirst()
    {
        Engine engine = await LoadLogicAsync();
        JsArray services = CreateList(engine,
            CreateService(engine, "a up", "UP", "UP"),
            CreateService(engine, "b unknown", "UNKNOWN", null),
            CreateService(engine, "c unconfirmed", "DOWN", "UP"),
            CreateService(engine, "d confirmed", "DOWN", "DOWN"),
            CreateService(engine, "e strange", "SOMETHING_NEW", null));

        List<string> names = ReadNames(engine.Invoke("sortServices", services));

        Assert.Equal(new[] { "d confirmed", "c unconfirmed", "b unknown", "e strange", "a up" }, names);
    }

    // Upper and lower case count the same, so that "alpha" does not end up after "Charlie".
    [Fact]
    public async Task InsideAGroupTheServicesAreSortedByName()
    {
        Engine engine = await LoadLogicAsync();
        JsArray services = CreateList(engine,
            CreateService(engine, "Charlie", "UP", "UP"),
            CreateService(engine, "alpha", "UP", "UP"),
            CreateService(engine, "Bravo", "UP", "UP"),
            CreateService(engine, "Zulu", "DOWN", "DOWN"),
            CreateService(engine, "Mike", "DOWN", "DOWN"));

        List<string> names = ReadNames(engine.Invoke("sortServices", services));

        Assert.Equal(new[] { "Mike", "Zulu", "alpha", "Bravo", "Charlie" }, names);
    }

    // The server gives A, B, C, D or F. E never comes, but it is in the list the owner asked for.
    [Theory]
    [InlineData("A", "grade-good")]
    [InlineData("B", "grade-good")]
    [InlineData("C", "grade-warn")]
    [InlineData("D", "grade-warn")]
    [InlineData("E", "grade-bad")]
    [InlineData("F", "grade-bad")]
    public async Task AGradeGetsItsColourFromTheFixedList(string grade, string className)
    {
        Engine engine = await LoadLogicAsync();

        JsValue box = engine.Invoke("getGradeBox", grade);

        Assert.Equal(grade, ReadText(box, "text"));
        Assert.Equal(className, ReadText(box, "className"));
    }

    // Everything else is grey with a dash. The last rows show that text from the server can never
    // become a class name or reach the page as the grade.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("G")]
    [InlineData("AB")]
    [InlineData("A ")]
    [InlineData("grade-good")]
    [InlineData("A onclick=alert(1)")]
    public async Task AnythingElseIsAGreyDash(string? grade)
    {
        Engine engine = await LoadLogicAsync();

        JsValue box = engine.Invoke("getGradeBox", grade);

        Assert.Equal("-", ReadText(box, "text"));
        Assert.Equal("grade-none", ReadText(box, "className"));
    }

    // The date is shown in local time, which is why 23:30 UTC is already the next day.
    [Fact]
    public async Task ACertificateWithADateShowsTheServersTextAndTheDateAsATooltip()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "certificateExpiryText", "14 days left");
        SetField(engine, service, "certificateNotAfterUtc", "2026-11-30T23:30:00Z");

        JsValue certificate = engine.Invoke("getCertificate", service);

        Assert.Equal("Certificate: 14 days left", ReadText(certificate, "text"));
        Assert.Equal("Not valid after 2026-12-01", ReadText(certificate, "title"));
    }

    [Fact]
    public async Task ACertificateDateThatCannotBeReadIsNotShownAsNumbers()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "certificateExpiryText", "14 days left");
        SetField(engine, service, "certificateNotAfterUtc", "not a time");

        JsValue certificate = engine.Invoke("getCertificate", service);

        Assert.Equal("Not valid after unknown date", ReadText(certificate, "title"));
    }

    [Fact]
    public async Task ACertificateThatFailedValidationIsShownAsInvalid()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "DOWN", "DOWN");
        SetField(engine, service, "certificateNotAssessedReason", CertificateReasons.NotValid);

        JsValue certificate = engine.Invoke("getCertificate", service);

        Assert.Equal("Certificate: Invalid", ReadText(certificate, "text"));
        Assert.Equal(CertificateReasons.NotValid, ReadText(certificate, "title"));
    }

    // Only the exact reason for a failed validation means Invalid. A missing handshake, an http address
    // or a reason no version knows says nothing bad about the certificate.
    [Theory]
    [InlineData(CertificateReasons.NoNewHandshake)]
    [InlineData(SecurityHeaderReader.NotHttpsReason)]
    [InlineData(TestProbeResults.UnknownCertificateReason)]
    [InlineData("The certificate was not valid")]
    [InlineData("the certificate was not valid.")]
    [InlineData("The certificate was not valid. ")]
    public async Task AnyOtherCertificateReasonIsNotAssessed(string reason)
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "certificateNotAssessedReason", reason);

        JsValue certificate = engine.Invoke("getCertificate", service);

        Assert.Equal("Certificate: Not assessed", ReadText(certificate, "text"));
        Assert.Equal(reason, ReadText(certificate, "title"));
    }

    // No check yet, a timeout, a blocked address or a failure that was not about TLS.
    [Fact]
    public async Task WithoutAnythingKnownAboutTheCertificateTheFieldIsHidden()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "DOWN", "DOWN");

        JsValue certificate = engine.Invoke("getCertificate", service);

        Assert.True(certificate.IsNull());
    }

    [Fact]
    public async Task NoMissingHeadersHidesTheField()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "securityGrade", "A");
        SetField(engine, service, "missingSecurityHeaders", TestProbeResults.NothingMissing);

        JsValue details = engine.Invoke("getHeaderDetails", service);

        Assert.True(details.IsNull());
    }

    [Fact]
    public async Task WithoutAHeaderCheckTheFieldIsHidden()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "DOWN", "DOWN");

        JsValue details = engine.Invoke("getHeaderDetails", service);

        Assert.True(details.IsNull());
    }

    [Fact]
    public async Task MissingHeadersAreListed()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "securityGrade", "C");
        SetField(engine, service, "missingSecurityHeaders", "X-Frame-Options, Referrer-Policy");

        JsValue details = engine.Invoke("getHeaderDetails", service);

        Assert.Equal("Missing headers: X-Frame-Options, Referrer-Policy", details.AsString());
    }

    [Fact]
    public async Task TheReasonForNoGradeIsShown()
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "securityNotAssessedReason", SecurityHeaderReader.RedirectReason);

        JsValue details = engine.Invoke("getHeaderDetails", service);

        Assert.Equal("Headers not assessed: " + SecurityHeaderReader.RedirectReason, details.AsString());
    }

    [Theory]
    [InlineData("NO_REDIRECT")]
    [InlineData(null)]
    [InlineData("SOMETHING_NEW")]
    public async Task NoRedirectHidesTheField(string? finding)
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "redirectFinding", finding);

        JsValue text = engine.Invoke("getRedirectText", service);

        Assert.True(text.IsNull());
    }

    [Theory]
    [InlineData("OTHER_HOST", "other.example.com", "Redirects to other.example.com")]
    [InlineData("SAME_HOST", null, "Redirects to the same host")]
    [InlineData("INTERNAL_TARGET", null, "Redirects to an internal address (hidden)")]
    [InlineData("UNREADABLE", null, "Redirect target unreadable")]
    public async Task ARedirectIsDescribed(string finding, string? host, string expected)
    {
        Engine engine = await LoadLogicAsync();
        JsObject service = CreateService(engine, "Shop", "UP", "UP");
        SetField(engine, service, "redirectFinding", finding);
        SetField(engine, service, "redirectTargetHost", host);

        JsValue text = engine.Invoke("getRedirectText", service);

        Assert.Equal(expected, text.AsString());
    }

    [Fact]
    public async Task AResponseTimeIsShownInMilliseconds()
    {
        Engine engine = await LoadLogicAsync();

        JsValue text = engine.Invoke("formatResponseTime", 123);

        Assert.Equal("123 ms", text.AsString());
    }

    [Fact]
    public async Task WithoutAResponseTimeTheFieldIsHidden()
    {
        Engine engine = await LoadLogicAsync();

        JsValue text = engine.Invoke("formatResponseTime", JsValue.Null);

        Assert.True(text.IsNull());
    }

    // The formats the API really sends: UTC with Z, and between none and seven decimals.
    [Theory]
    [InlineData("2026-11-10T12:08:59Z", "2026-11-10 13:08:59")]
    [InlineData("2026-11-03T12:00:00.5Z", "2026-11-03 13:00:00")]
    [InlineData("2026-01-01T12:00:00.25Z", "2026-01-01 13:00:00")]
    [InlineData("2026-11-10T12:08:59.1234567Z", "2026-11-10 13:08:59")]
    [InlineData("2026-01-02T03:04:05Z", "2026-01-02 04:04:05")]
    [InlineData("2026-11-10T22:30:00Z", "2026-11-10 23:30:00")]
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01 00:30:00")]
    public async Task ATimeIsShownInLocalTimeWithTwentyFourHours(string time, string expected)
    {
        Engine engine = await LoadLogicAsync();

        JsValue text = engine.Invoke("formatLocalTime", time);

        Assert.Equal(expected, text.AsString());
    }

    [Fact]
    public async Task ATimeThatCannotBeReadIsNotShownAsNumbers()
    {
        Engine engine = await LoadLogicAsync();

        JsValue text = engine.Invoke("formatLocalTime", "not a time");

        Assert.Equal("Unknown time", text.AsString());
    }

    [Fact]
    public async Task AServiceThatWasNeverCheckedSaysNever()
    {
        Engine engine = await LoadLogicAsync();

        JsValue text = engine.Invoke("formatLastCheck", JsValue.Null);

        Assert.Equal("Last check: Never", text.AsString());
    }

    // The exact text from the API, taken from a real check with a clock that has seven decimals.
    [Fact]
    public async Task TheTimeFromTheOverviewIsShownInLocalTime()
    {
        await using TestApp app = new TestApp();
        await app.MigrateAsync();
        using HttpClient client = app.CreateClient();
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 10, 12, 8, 59, TimeSpan.Zero).AddTicks(1234567));
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", "https://shop.example.com"));
        app.Probe.Result = new ProbeResult(ServiceStatus.Up, 200, 12, null);
        HttpResponseMessage check = await client.PostAsync($"/services/{service.Id}/check", null);
        check.EnsureSuccessStatusCode();
        JsonElement overview = await client.GetFromJsonAsync<JsonElement>("/services/overview");
        string lastCheckedAt = overview[0].GetProperty("lastCheckedAt").GetString()!;
        Engine engine = CreateEngine(await ReadFileAsync(app, "/dashboard-logic.js"));

        JsValue text = engine.Invoke("formatLastCheck", lastCheckedAt);

        Assert.Equal("2026-11-10T12:08:59.1234567Z", lastCheckedAt);
        Assert.Equal("Last check: 2026-11-10 13:08:59", text.AsString());
    }

    [Fact]
    public async Task TheSummaryCountsEveryGroup()
    {
        Engine engine = await LoadLogicAsync();
        JsArray services = CreateList(engine,
            CreateService(engine, "a", "UP", "UP"),
            CreateService(engine, "b", "UP", "UP"),
            CreateService(engine, "c", "DOWN", "DOWN"),
            CreateService(engine, "d", "DOWN", null),
            CreateService(engine, "e", "UNKNOWN", "UP"));

        JsValue summary = engine.Invoke("formatSummary", services);

        Assert.Equal("2 UP | 1 DOWN | 1 waiting for confirmation | 1 UNKNOWN", summary.AsString());
    }

    [Fact]
    public async Task TheSummaryLeavesOutGroupsWithoutServices()
    {
        Engine engine = await LoadLogicAsync();
        JsArray services = CreateList(engine,
            CreateService(engine, "a", "UP", "UP"),
            CreateService(engine, "b", "DOWN", "UP"));

        JsValue summary = engine.Invoke("formatSummary", services);

        Assert.Equal("1 UP | 1 waiting for confirmation", summary.AsString());
    }

    [Fact]
    public async Task WithoutServicesTheSummaryIsEmpty()
    {
        Engine engine = await LoadLogicAsync();
        JsArray services = CreateList(engine);

        JsValue summary = engine.Invoke("formatSummary", services);

        Assert.Equal("", summary.AsString());
    }

    // The script compares with the server's own text. If the text in Domain changed, an invalid
    // certificate would quietly become "Not assessed" on the page.
    [Fact]
    public async Task TheScriptUsesTheServersExactReasonForAnInvalidCertificate()
    {
        string script = await ReadLogicAsync();

        Assert.Contains("\"" + CertificateReasons.NotValid + "\"", script);
    }

    [Fact]
    public async Task TheConfirmedTextMatchesTheNumberOfChecksTheServerWaitsFor()
    {
        string script = await ReadLogicAsync();

        Assert.Contains("\"Confirmed after " + DownConfirmationRule.FailedChecksBeforeDown + " failed checks in a row\"", script);
    }

    private static async Task<Engine> LoadLogicAsync()
    {
        string script = await ReadLogicAsync();
        return CreateEngine(script);
    }

    private static Engine CreateEngine(string script)
    {
        Options options = new Options();
        options.LocalTimeZone(OneHourAheadOfUtc);
        Engine engine = new Engine(options);
        engine.Execute(script);
        return engine;
    }

    private static async Task<string> ReadLogicAsync()
    {
        await using TestApp app = new TestApp();
        return await ReadFileAsync(app, "/dashboard-logic.js");
    }

    private static async Task<string> ReadFileAsync(TestApp app, string path)
    {
        using HttpClient client = app.CreateClient();
        return await client.GetStringAsync(path);
    }

    // A service as the overview sends it: every field is there, and those not set here are null.
    private static JsObject CreateService(Engine engine, string name, string currentStatus, string? lastKnownStatus)
    {
        JsObject service = new JsObject(engine);
        SetField(engine, service, "name", name);
        SetField(engine, service, "currentStatus", currentStatus);
        SetField(engine, service, "lastKnownStatus", lastKnownStatus);

        string[] emptyFields =
        {
            "lastCheckedAt", "responseTimeMs", "failureReason", "securityGrade", "missingSecurityHeaders",
            "securityNotAssessedReason", "certificateNotAfterUtc", "certificateDaysLeft", "certificateExpiryText",
            "certificateNotAssessedReason", "redirectFinding", "redirectTargetHost",
        };

        foreach (string field in emptyFields)
        {
            SetField(engine, service, field, null);
        }

        return service;
    }

    // JsValue.FromObject turns a C# null into JavaScript null, the same as null in the API's JSON.
    private static void SetField(Engine engine, JsObject service, string field, string? value)
    {
        service.Set(field, JsValue.FromObject(engine, value));
    }

    private static JsArray CreateList(Engine engine, params JsObject[] services)
    {
        return new JsArray(engine, services);
    }

    private static List<string> ReadNames(JsValue sorted)
    {
        JsArray list = (JsArray)sorted;
        List<string> names = new List<string>();

        for (uint i = 0; i < list.Length; i++)
        {
            names.Add(list[i].AsObject().Get("name").AsString());
        }

        return names;
    }

    // Null when the field is JavaScript null, so that a test can compare with null directly.
    private static string? ReadText(JsValue result, string field)
    {
        JsValue value = result.AsObject().Get(field);

        if (value.IsNull())
        {
            return null;
        }

        return value.AsString();
    }
}
