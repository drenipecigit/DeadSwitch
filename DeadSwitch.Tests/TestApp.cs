using System.Net.Http.Json;
using DeadSwitch.Api.Monitoring;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Domain;
using DeadSwitch.Infrastructure.Data;
using DeadSwitch.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DeadSwitch.Tests;

// Starts the real API in memory, with its own empty database, a fake probe and a fake clock.
internal sealed class TestApp : WebApplicationFactory<Program>
{
    // An in-memory SQLite database is deleted when its last connection closes.
    // This connection stays open so the data survives between the API's own connections.
    private readonly SqliteConnection _keeper;
    private readonly string _environmentName;

    public TestProbe Probe { get; } = new TestProbe();

    // Controls both the clock and the timer, so a test can move the time without really waiting.
    public FakeTimeProvider Clock { get; } = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    public TestRepositoryControl RepositoryControl { get; } = new TestRepositoryControl();

    public TestNotifier Notifier { get; } = new TestNotifier();

    public CapturedLogs Logs { get; } = new CapturedLogs();

    // When set, written to the configuration as SecurityWarnings:CertificateWarningDays.
    public string? CertificateWarningDaysSetting { get; set; }

    // When set, the test server claims to listen on this address. No port is opened.
    public string? PretendedAddress { get; set; }

    // Finished when the application starts to stop. Only used together with PretendedAddress.
    public TaskCompletionSource ApplicationStopping { get; } = new TaskCompletionSource();

    // When true, the test host waits until Program.cs has finished before it starts the application.
    // Only for tests where the application must fail at startup, because otherwise Program never ends.
    public bool WaitForProgramBeforeStart { get; set; }

    // The thread that runs Program.cs. The test host starts Program on a thread of its own.
    public Thread? ProgramThread { get; private set; }

    // The address the application reads. It is empty unless a test asks for the real notifier.
    private readonly string _webhookUrl = "";

    // Only set when a test wants the real notifier. It then answers instead of Discord.
    private readonly StubDiscordHandler? _discordStub;

    public TestApp() : this("Development")
    {
    }

    // The framework treats a bad request differently in Development and Production, so the tests
    // of error responses run in both. The unique name gives every test its own database.
    public TestApp(string environmentName) : this(environmentName, "test-" + Guid.NewGuid())
    {
    }

    // Two apps with the same database name share one database. A test can then start a second app
    // and stop the first one, which is how a restart of the application looks to the database.
    public TestApp(string environmentName, string databaseName)
    {
        _environmentName = environmentName;

        _keeper = new SqliteConnection($"Data Source={databaseName};Mode=Memory;Cache=Shared");
        _keeper.Open();
    }

    // Runs the real notifier through the real registration in Program.cs, against a stub. No request
    // can leave the machine, as long as AssertDiscordClientUsesTheStub passes.
    public TestApp(string environmentName, string webhookUrl, StubDiscordHandler discordStub) : this(environmentName)
    {
        _webhookUrl = webhookUrl;
        _discordStub = discordStub;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environmentName);

        // The tests run worker passes by hand, so the background loop must not run at the same time.
        builder.UseSetting("Monitoring:Enabled", "false");

        builder.ConfigureAppConfiguration(AddTestSettings);
        builder.ConfigureLogging(AddCapturedLogs);

        builder.ConfigureServices(services =>
        {
            // This runs inside builder.Build() in Program.cs, so it runs on the thread of Program.
            ProgramThread = Thread.CurrentThread;

            // Remove the real database registration, so no test can touch the real database file.
            services.RemoveAll<DbContextOptions<DeadSwitchDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<DeadSwitchDbContext>>();
            services.AddDbContext<DeadSwitchDbContext>(options => options.UseSqlite(_keeper.ConnectionString));

            services.RemoveAll<IMonitoredServiceRepository>();
            services.AddScoped<IMonitoredServiceRepository>(provider => CreateTestRepository(provider));

            services.RemoveAll<IHealthProbe>();
            services.AddSingleton<IHealthProbe>(Probe);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            if (_discordStub == null)
            {
                services.RemoveAll<INotifier>();
                services.AddSingleton<INotifier>(Notifier);
            }
            else
            {
                services.AddHttpClient(nameof(INotifier)).ConfigurePrimaryHttpMessageHandler(CreateDiscordStub);
            }

            if (PretendedAddress != null)
            {
                services.AddHostedService(CreatePretendedAddress);
            }
        });
    }

    // Program.cs keeps running on its own thread after the host is built, at the same time as the test
    // host starts it. Waiting for that thread to end forces one fixed order: the application has done
    // everything it does on a failed start before the test host goes on. A test then gets the same
    // result every time instead of the result of a race.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (!WaitForProgramBeforeStart)
        {
            return base.CreateHost(builder);
        }

        IHost host = builder.Build();

        // If the thread is missing or is the test's own thread, Join waits for nothing and proves nothing.
        if (ProgramThread == null || ProgramThread == Thread.CurrentThread)
        {
            throw new InvalidOperationException("ProgramThread was not captured on the Program thread, so Join proves nothing.");
        }

        // The limit is only there so that a bug gives a red test instead of a test that hangs.
        bool finished = ProgramThread.Join(TimeSpan.FromSeconds(10));
        if (!finished)
        {
            throw new InvalidOperationException("Program did not finish within 10 seconds.");
        }

        host.Start();
        return host;
    }

    private PretendedServerAddress CreatePretendedAddress(IServiceProvider provider)
    {
        IServer server = provider.GetRequiredService<IServer>();
        IHostApplicationLifetime lifetime = provider.GetRequiredService<IHostApplicationLifetime>();
        return new PretendedServerAddress(server, lifetime, PretendedAddress!, ApplicationStopping);
    }

    // Development reads user secrets, so the tests would otherwise see the owner's real webhook address.
    // This source is added last and therefore wins over user secrets and environment variables.
    private void AddTestSettings(WebHostBuilderContext context, IConfigurationBuilder configuration)
    {
        Dictionary<string, string?> settings = new Dictionary<string, string?>();
        settings["Discord:WebhookUrl"] = _webhookUrl;

        if (CertificateWarningDaysSetting != null)
        {
            settings["SecurityWarnings:CertificateWarningDays"] = CertificateWarningDaysSetting;
        }

        // With the real notifier everything is logged, so a test can show what the log never holds.
        if (_discordStub != null)
        {
            settings["Logging:LogLevel:Default"] = "Trace";
            settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
        }

        configuration.AddInMemoryCollection(settings);
    }

    private void AddCapturedLogs(ILoggingBuilder logging)
    {
        logging.AddProvider(Logs);
    }

    private HttpMessageHandler CreateDiscordStub()
    {
        return _discordStub!;
    }

    // A wrong client name would leave the real handler in place, and the next send would really go
    // out. Every test that sends through the real notifier therefore calls this first.
    public void AssertDiscordClientUsesTheStub()
    {
        IHttpMessageHandlerFactory factory = Services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler current = factory.CreateHandler(nameof(INotifier));
        while (current is DelegatingHandler wrapper)
        {
            current = wrapper.InnerHandler!;
        }

        Assert.Same(_discordStub, current);
    }

    public async Task MigrateAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task<MonitoredService> RegisterServiceAsync(HttpClient client, ServiceConfiguration configuration)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/services", configuration);
        response.EnsureSuccessStatusCode();

        // The ! is safe here: the API returned success, so the body contains a service.
        MonitoredService service = (await response.Content.ReadFromJsonAsync<MonitoredService>(TestJson.Options))!;
        return service;
    }

    // Read straight from the database, so a test does not depend on what the API chooses to show.
    public async Task<MonitoredService> ReadSavedServiceAsync(Guid id)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        DeadSwitchDbContext db = scope.ServiceProvider.GetRequiredService<DeadSwitchDbContext>();
        return await db.MonitoredServices.AsNoTracking().SingleAsync(saved => saved.Id == id);
    }

    public MonitoringWorker CreateWorker()
    {
        return CreateWorker(new MonitoringOptions(), NullLogger<MonitoringWorker>.Instance);
    }

    public MonitoringWorker CreateWorker(MonitoringOptions monitoringOptions, ILogger<MonitoringWorker> logger)
    {
        IServiceScopeFactory scopes = Services.GetRequiredService<IServiceScopeFactory>();
        IOptions<MonitoringOptions> options = Options.Create(monitoringOptions);
        return new MonitoringWorker(scopes, options, logger, Clock);
    }

    private TestRepository CreateTestRepository(IServiceProvider provider)
    {
        DeadSwitchDbContext db = provider.GetRequiredService<DeadSwitchDbContext>();
        ServiceLocks locks = provider.GetRequiredService<ServiceLocks>();
        MonitoredServiceRepository realRepository = new MonitoredServiceRepository(db);
        return new TestRepository(realRepository, RepositoryControl, locks);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _keeper.DisposeAsync();
    }
}
