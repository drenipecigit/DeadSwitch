using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// The threshold of an earlier check is counted from the time read back from the database. A time
// that was not marked as UTC would be read as local time, and the same warning would then come again
// for as long as the time zone differs from UTC. The two Kind checks catch this on a machine in UTC too.
public class CertificateWarningTimeTests
{
    [Fact]
    public async Task PassingTheFourteenDayLimitFollowedBySeveralChecksWithinAnHourWarnsOnce()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();

        // Thirty minutes before the number of whole days goes from 15 to 14.
        DateTime notAfter = scenario.App.Clock.GetUtcNow().UtcDateTime.AddDays(15).AddMinutes(30);
        ProbeResult sameCertificate = TestProbeResults.Response(200, null, TestProbeResults.CertificateValid(notAfter));
        await scenario.CheckAsync(sameCertificate);
        Assert.Empty(scenario.GetWarnings());

        scenario.App.Clock.Advance(TimeSpan.FromMinutes(31));
        await scenario.CheckAsync(sameCertificate);
        Assert.Single(scenario.GetWarnings());

        // Eleven checks five minutes apart, which is just under an hour after the limit was passed.
        for (int check = 0; check < 11; check++)
        {
            scenario.App.Clock.Advance(TimeSpan.FromMinutes(5));
            await scenario.CheckAsync(sameCertificate);
        }

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(14, warning.ThresholdDays);

        await using AsyncServiceScope scope = scenario.App.Services.CreateAsyncScope();
        IMonitoredServiceRepository repository = scope.ServiceProvider.GetRequiredService<IMonitoredServiceRepository>();
        CheckRecord? baseline = await repository.GetCertificateBaselineAsync(scenario.Service.Id, SecurityWarningScenario.ServiceUrl);

        Assert.NotNull(baseline);
        Assert.Equal(DateTimeKind.Utc, baseline.CheckedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, baseline.CertificateNotAfterUtc!.Value.Kind);
    }
}
