using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;

namespace DeadSwitch.Tests;

// One service in the real API with a real database, a fake probe and a fake notifier.
internal sealed class SecurityWarningScenario : IAsyncDisposable
{
    public const string ServiceName = "Shop";
    public const string ServiceUrl = "https://example.com";
    public const string OtherUrl = "https://example.org";

    private readonly TimeSpan _oneCheckLater = TimeSpan.FromSeconds(1);

    // Longer than the default interval, so that the service is due again.
    private readonly TimeSpan _onePassLater = TimeSpan.FromSeconds(61);

    public TestApp App { get; }
    public HttpClient Client { get; private set; } = null!;
    public MonitoredService Service { get; private set; } = null!;

    private SecurityWarningScenario(TestApp app)
    {
        App = app;
    }

    public static async Task<SecurityWarningScenario> StartAsync(string? certificateWarningDays = null)
    {
        TestApp app = new TestApp();
        app.CertificateWarningDaysSetting = certificateWarningDays;

        SecurityWarningScenario scenario = new SecurityWarningScenario(app);
        await app.MigrateAsync();
        scenario.Client = app.CreateClient();
        scenario.Service = await app.RegisterServiceAsync(scenario.Client, new ServiceConfiguration(ServiceName, ServiceUrl));
        return scenario;
    }

    // The clock moves first: two checks at the same moment would have no defined order.
    public async Task CheckAsync(ProbeResult result)
    {
        App.Clock.Advance(_oneCheckLater);
        App.Probe.Result = result;
        HttpResponseMessage response = await Client.PostAsync($"/services/{Service.Id}/check", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task RunWorkerPassAsync(MonitoringWorker worker, ProbeResult result)
    {
        App.Clock.Advance(_onePassLater);
        App.Probe.Result = result;
        await worker.RunPassAsync(CancellationToken.None);
    }

    public List<SecurityWarning> GetWarnings()
    {
        return App.Notifier.GetSecurityWarnings();
    }

    // Twelve extra hours, so that the seconds a test moves the clock do not change the number of whole days.
    public DateTime DateWithDaysLeft(int days)
    {
        return App.Clock.GetUtcNow().UtcDateTime.AddDays(days).AddHours(12);
    }

    public ProbeResult ValidCertificateWithDaysLeft(int days)
    {
        return TestProbeResults.Response(200, null, TestProbeResults.CertificateValid(DateWithDaysLeft(days)));
    }

    public async Task ChangeUrlAsync(string url)
    {
        await PutAsync(new ServiceConfiguration(ServiceName, url));
    }

    public async Task ChangeNameAsync(string name)
    {
        await PutAsync(new ServiceConfiguration(name, ServiceUrl));
    }

    public async Task<int> CountHistoryAsync()
    {
        List<CheckRecord>? history = await Client.GetFromJsonAsync<List<CheckRecord>>($"/services/{Service.Id}/history?take=200", TestJson.Options);
        return history!.Count;
    }

    private async Task PutAsync(ServiceConfiguration configuration)
    {
        HttpResponseMessage response = await Client.PutAsJsonAsync($"/services/{Service.Id}", configuration);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}
