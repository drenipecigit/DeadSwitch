using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Monitoring;

namespace DeadSwitch.Tests;

// The answers are read as raw JSON, because the dashboard depends on the exact names.
public class CertificateStorageTests
{
    private const string Url = "https://monitored.test/";
    private const string Reason = "A reason.";
    private const string NotAfterText = "2027-01-02T03:04:05Z";
    private const string HostileMarker = "hostile-certificate-marker";
    private const string HostileSubject = "CN=\"<img src=x onerror=alert(1)> hostile-certificate-marker\"";
    private static readonly DateTime NotAfter = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // The address the real probe uses in the next call. Set by each test.
    private string _realProbeUrl = Url;
    private HttpHealthProbe? _realProbe;

    [Fact]
    public async Task ADateIsSavedAndShownWithZ()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", Url));
        app.Probe.Result = ResultWith(new CertificateCheck(NotAfter, null));

        JsonElement checkAnswer = await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertCertificateFields(checkAnswer, NotAfterText, null);
        AssertCertificateFields(historyRow, NotAfterText, null);
    }

    [Fact]
    public async Task AReasonIsSavedAndThereIsNoDate()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", Url));
        app.Probe.Result = ResultWith(new CertificateCheck(null, Reason));

        await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertCertificateFields(historyRow, null, Reason);
    }

    [Fact]
    public async Task ACheckWithoutCertificateResultLeavesBothFieldsEmpty()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", Url));
        app.Probe.Result = new ProbeResult(ServiceStatus.Down, null, 10, "Check timed out.");

        await CheckAsync(client, service.Id);
        JsonElement historyRow = await GetNewestHistoryRowAsync(client, service.Id);

        AssertCertificateFields(historyRow, null, null);
    }

    // Nothing written by whoever made the certificate may reach an answer, the log or a notification.
    [Fact]
    public async Task NothingFromTheCertificateReachesAnyAnswerLogOrNotification()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", Url));
        using X509Certificate2 hostile = TestCertificates.CreateForMonitoredHosts(HostileSubject);
        using X509Certificate2 somethingElse = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer server = await LocalTestServer.StartHttpsAsync(hostile);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(TimeProvider.System);
        _realProbeUrl = $"{Url}ok";
        app.Probe.Check = CheckWithTheRealProbeAsync;

        using HttpClient trusting = TlsTestClient.Create(new LocalServerConnectStep(server.Port), recorder, hostile);
        _realProbe = new HttpHealthProbe(trusting, recorder);
        string validAnswer = await CheckAndReadTextAsync(client, service.Id);

        using HttpClient notTrusting = TlsTestClient.Create(new LocalServerConnectStep(server.Port), recorder, somethingElse);
        _realProbe = new HttpHealthProbe(notTrusting, recorder);
        string rejectedAnswer = await CheckAndReadTextAsync(client, service.Id);

        // A second rejected check, because only the second failed check in a row gives a message.
        string secondRejectedAnswer = await CheckAndReadTextAsync(client, service.Id);

        string history = await client.GetStringAsync($"/services/{service.Id}/history");
        string overview = await client.GetStringAsync("/services/overview");

        Assert.Contains("\"UP\"", validAnswer);
        Assert.Contains("\"DOWN\"", rejectedAnswer);
        Assert.Contains("\"DOWN\"", secondRejectedAnswer);
        Assert.DoesNotContain(HostileMarker, validAnswer);
        Assert.DoesNotContain(HostileMarker, rejectedAnswer);
        Assert.DoesNotContain(HostileMarker, secondRejectedAnswer);
        Assert.DoesNotContain(HostileMarker, history);
        Assert.DoesNotContain(HostileMarker, overview);
        app.Logs.AssertNothingContains(HostileMarker);

        List<StatusChange> changes = app.Notifier.GetChanges();
        Assert.Single(changes);
        Assert.DoesNotContain(HostileMarker, changes[0].ToString());
    }

    // GET /check shares the recorder with the saved checks, through the API.
    [Fact]
    public async Task ARejectedCertificateOnGetCheckDoesNotAffectALaterCheck()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", Url));
        using X509Certificate2 trusted = TestCertificates.CreateForMonitoredHosts();
        using X509Certificate2 notTrusted = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer goodServer = await LocalTestServer.StartHttpsAsync(trusted);
        await using LocalTestServer badServer = await LocalTestServer.StartHttpsAsync(notTrusted);
        LocalServerConnectStep connectStep = new LocalServerConnectStep(badServer.Port);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(TimeProvider.System);
        using HttpClient probeClient = TlsTestClient.Create(connectStep, recorder, trusted);
        _realProbe = new HttpHealthProbe(probeClient, recorder);
        _realProbeUrl = $"{Url}ok";
        app.Probe.Check = CheckWithTheRealProbeAsync;

        JsonElement getCheck = await client.GetFromJsonAsync<JsonElement>($"/check?url={Url}");
        connectStep.Port = goodServer.Port;
        JsonElement savedCheck = await CheckAsync(client, service.Id);

        Assert.Equal("DOWN", getCheck.GetProperty("status").GetString());
        Assert.Equal("UP", savedCheck.GetProperty("status").GetString());
        AssertCertificateFields(savedCheck, FormatTime(trusted.NotAfter), null);
    }

    // The saved check has its answer on the way when GET /check is refused. The entry placed by the
    // test takes the place of what a handshake at the wrong moment would leave behind.
    [Fact]
    public async Task ARejectedCertificateOnGetCheckDuringAnotherCheckNeverMakesItInvalid()
    {
        await using TestApp app = await CreateAppAsync();
        using HttpClient client = app.CreateClient();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Shop", Url));
        using X509Certificate2 trusted = TestCertificates.CreateForMonitoredHosts();
        using X509Certificate2 notTrusted = TestCertificates.CreateForMonitoredHosts();
        await using LocalTestServer goodServer = await LocalTestServer.StartHttpsAsync(trusted);
        await using LocalTestServer badServer = await LocalTestServer.StartHttpsAsync(notTrusted);
        LocalServerConnectStep connectStep = new LocalServerConnectStep(goodServer.Port);
        TlsHandshakeRecorder recorder = new TlsHandshakeRecorder(TimeProvider.System);
        using HttpClient probeClient = TlsTestClient.Create(connectStep, recorder, trusted);
        _realProbe = new HttpHealthProbe(probeClient, recorder);
        app.Probe.Check = CheckWithTheRealProbeAsync;

        _realProbeUrl = $"{Url}held-first";
        Task<JsonElement> savedCheck = CheckAsync(client, service.Id);
        await WaitForRequestAsync(goodServer, "/held-first");

        connectStep.Port = badServer.Port;
        _realProbeUrl = $"{Url}ok";
        JsonElement getCheck = await client.GetFromJsonAsync<JsonElement>($"/check?url={Url}");

        recorder.RecordHandshake(TestCertificates.MonitoredHost, null, false);
        await ReleaseHeldRequestAsync(goodServer, trusted);
        JsonElement savedAnswer = await savedCheck;

        Assert.Equal("DOWN", getCheck.GetProperty("status").GetString());
        Assert.Equal("UP", savedAnswer.GetProperty("status").GetString());
        AssertCertificateFields(savedAnswer, null, HttpHealthProbe.NoNewHandshakeReason);
    }

    // Uses a client and a recorder of its own, so that it does not disturb the entry under test.
    private static async Task ReleaseHeldRequestAsync(LocalTestServer server, X509Certificate2 trusted)
    {
        TlsHandshakeRecorder ownRecorder = new TlsHandshakeRecorder(TimeProvider.System);
        using HttpClient client = TlsTestClient.Create(new LocalServerConnectStep(server.Port), ownRecorder, trusted);

        using HttpResponseMessage response = await client.GetAsync($"{Url}held-second");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task WaitForRequestAsync(LocalTestServer server, string path)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (server.CountRequestsTo(path) > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The request never reached the server.");
    }

    private Task<ProbeResult> CheckWithTheRealProbeAsync(CancellationToken cancellationToken)
    {
        return _realProbe!.CheckAsync(_realProbeUrl, 10, cancellationToken);
    }

    private static string FormatTime(DateTime localTime)
    {
        return localTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static ProbeResult ResultWith(CertificateCheck check)
    {
        return new ProbeResult(ServiceStatus.Up, 200, 12, null, null, check);
    }

    private static void AssertCertificateFields(JsonElement element, string? notAfter, string? reason)
    {
        Assert.Equal(notAfter, element.GetProperty("certificateNotAfterUtc").GetString());
        Assert.Equal(reason, element.GetProperty("certificateNotAssessedReason").GetString());
    }

    private static async Task<TestApp> CreateAppAsync()
    {
        TestApp app = new TestApp();
        await app.MigrateAsync();
        return app;
    }

    private static async Task<JsonElement> CheckAsync(HttpClient client, Guid id)
    {
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> CheckAndReadTextAsync(HttpClient client, Guid id)
    {
        HttpResponseMessage response = await client.PostAsync($"/services/{id}/check", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonElement> GetNewestHistoryRowAsync(HttpClient client, Guid id)
    {
        JsonElement history = await client.GetFromJsonAsync<JsonElement>($"/services/{id}/history");
        return history[0];
    }
}
