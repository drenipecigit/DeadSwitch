using System.Net;
using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using DeadSwitch.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// UrlSafety must judge the same name that the connection really uses.
// No test here can reach the machine's DNS: the API tests use the fake probe, and the tests of the
// real probe use a fake DNS. Those carry the trait, in case a run removes ConnectCallback.
public class UnicodeHostTests
{
    private const string NameLookupWithoutConnector = "NameLookupWithoutConnector";
    private const string OriginalUrl = "https://example.com";

    [Theory]
    [MemberData(nameof(UnicodeHostUrls.Forbidden), MemberType = typeof(UnicodeHostUrls))]
    [MemberData(nameof(UnicodeHostUrls.CannotBeConverted), MemberType = typeof(UnicodeHostUrls))]
    public async Task AdHocCheckRejectsTheUrl(string url)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/check?url=" + Uri.EscapeDataString(url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.Probe.Calls);
    }

    [Theory]
    [MemberData(nameof(UnicodeHostUrls.Forbidden), MemberType = typeof(UnicodeHostUrls))]
    [MemberData(nameof(UnicodeHostUrls.CannotBeConverted), MemberType = typeof(UnicodeHostUrls))]
    public async Task RegisteringRejectsTheUrlAndSavesNothing(string url)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync("/services", new ServiceConfiguration("Example", url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        List<MonitoredService> services = (await client.GetFromJsonAsync<List<MonitoredService>>("/services", TestJson.Options))!;
        Assert.Empty(services);
    }

    [Theory]
    [MemberData(nameof(UnicodeHostUrls.Forbidden), MemberType = typeof(UnicodeHostUrls))]
    [MemberData(nameof(UnicodeHostUrls.CannotBeConverted), MemberType = typeof(UnicodeHostUrls))]
    public async Task UpdatingRejectsTheUrlAndKeepsTheOldOne(string url)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        MonitoredService service = await app.RegisterServiceAsync(client, new ServiceConfiguration("Example", OriginalUrl));

        HttpResponseMessage response = await client.PutAsJsonAsync($"/services/{service.Id}", new ServiceConfiguration("Example", url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        MonitoredService saved = (await client.GetFromJsonAsync<MonitoredService>($"/services/{service.Id}", TestJson.Options))!;
        Assert.Equal(OriginalUrl, saved.Url);
    }

    [Theory]
    [MemberData(nameof(UnicodeHostUrls.AllowedNames), MemberType = typeof(UnicodeHostUrls))]
    public async Task NamesThatAreReallyPublicAreStillAccepted(string url)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();

        HttpResponseMessage adHoc = await client.GetAsync("/check?url=" + Uri.EscapeDataString(url));
        HttpResponseMessage registered = await client.PostAsJsonAsync("/services", new ServiceConfiguration("Example", url));

        Assert.Equal(HttpStatusCode.OK, adHoc.StatusCode);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
    }

    // A row saved before the fix can still hold one of these URLs, so the probe must handle it too.
    [Theory]
    [Trait("Category", NameLookupWithoutConnector)]
    [MemberData(nameof(UnicodeHostUrls.Forbidden), MemberType = typeof(UnicodeHostUrls))]
    [MemberData(nameof(UnicodeHostUrls.CannotBeConverted), MemberType = typeof(UnicodeHostUrls))]
    public async Task RealProbeRejectsTheUrlBeforeAnyNameLookup(string url)
    {
        CountingDns dns = new CountingDns();
        using HttpClient client = CreateProtectedClient(dns);
        HttpHealthProbe probe = new HttpHealthProbe(client);

        ProbeResult result = await probe.CheckAsync(url, 2, CancellationToken.None);

        Assert.Equal(ServiceStatus.Unknown, result.Status);
        Assert.Equal("URL rejected by destination policy.", result.FailureReason);
        Assert.Equal(0, dns.Lookups);
    }

    // Before the fix, such a service stayed due, was tried again on every pass and got no history.
    [Theory]
    [Trait("Category", NameLookupWithoutConnector)]
    [MemberData(nameof(UnicodeHostUrls.Forbidden), MemberType = typeof(UnicodeHostUrls))]
    [MemberData(nameof(UnicodeHostUrls.CannotBeConverted), MemberType = typeof(UnicodeHostUrls))]
    public async Task WorkerSavesUnknownForAnOldRowAndWaitsForTheInterval(string url)
    {
        await using TestApp app = new TestApp();
        using HttpClient client = app.CreateClient();
        await app.MigrateAsync();
        await InsertOldServiceAsync(app, url);
        CountingDns dns = new CountingDns();
        using HttpClient probeClient = CreateProtectedClient(dns);
        HttpHealthProbe realProbe = new HttpHealthProbe(probeClient);

        Task<ProbeResult> CheckWithRealProbe(CancellationToken cancellationToken)
        {
            return realProbe.CheckAsync(url, 2, cancellationToken);
        }

        app.Probe.Check = CheckWithRealProbe;
        MonitoringWorker worker = app.CreateWorker();

        await worker.RunPassAsync(CancellationToken.None);
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, app.Probe.Calls);
        Assert.Equal(0, dns.Lookups);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        CheckRecord record = await db.CheckRecords.SingleAsync();
        Assert.Equal(ServiceStatus.Unknown, record.Status);
    }

    private static async Task InsertOldServiceAsync(TestApp app, string url)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        Guid id = Guid.NewGuid();
        string name = "Old";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO MonitoredServices (Id, Name, Url, IsEnabled) VALUES ({id}, {name}, {url}, {true})");
    }

    private static HttpClient CreateProtectedClient(CountingDns dns)
    {
        PublicInternetConnector connector = new PublicInternetConnector(dns.ResolveAsync, UrlSafety.IsPublicAddress);
        return new HttpClient(ProtectedHttpHandler.Create(connector));
    }

    // Answers with loopback, so the real address rule stops the connection even if a lookup happens.
    private sealed class CountingDns
    {
        public int Lookups { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Lookups++;
            IPAddress[] addresses = { IPAddress.Loopback };
            return Task.FromResult(addresses);
        }
    }
}
