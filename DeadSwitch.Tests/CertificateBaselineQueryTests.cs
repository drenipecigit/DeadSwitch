using DeadSwitch.Application.Interfaces;
using DeadSwitch.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DeadSwitch.Tests;

// The baseline query runs as SQL and cannot call Classify, so the same rule exists in two places.
// This test is what keeps them together: the query must find a row exactly when Classify calls it a verdict.
public class CertificateBaselineQueryTests
{
    [Theory]
    [InlineData(true, null, CertificateFinding.Valid)]
    [InlineData(false, CertificateReasons.NotValid, CertificateFinding.Rejected)]
    [InlineData(false, CertificateReasons.NoNewHandshake, CertificateFinding.NotSeen)]
    [InlineData(false, null, CertificateFinding.NotLookedAt)]
    [InlineData(false, "Not an HTTPS address.", CertificateFinding.NotLookedAt)]
    [InlineData(false, TestProbeResults.UnknownCertificateReason, CertificateFinding.NotLookedAt)]
    [InlineData(false, "the certificate was not valid.", CertificateFinding.NotLookedAt)]
    [InlineData(false, "The certificate was not valid", CertificateFinding.NotLookedAt)]
    [InlineData(false, "The certificate was not valid. Extra", CertificateFinding.NotLookedAt)]
    [InlineData(true, CertificateReasons.NotValid, CertificateFinding.Valid)]
    [InlineData(true, CertificateReasons.NoNewHandshake, CertificateFinding.Valid)]
    [InlineData(true, TestProbeResults.UnknownCertificateReason, CertificateFinding.Valid)]
    public async Task CertificateBaselineQueryAgreesWithClassify(bool hasDate, string? reason, CertificateFinding expectedFinding)
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        DateTime? notAfter = null;
        if (hasDate)
        {
            notAfter = scenario.DateWithDaysLeft(90);
        }

        await scenario.CheckAsync(TestProbeResults.Response(200, null, new CertificateCheck(notAfter, reason)));

        CertificateFinding finding = CertificateWarningRule.Classify(notAfter, reason);
        Assert.Equal(expectedFinding, finding);

        bool shouldBeABaseline = finding == CertificateFinding.Valid || finding == CertificateFinding.Rejected;
        CheckRecord? baseline = await ReadCertificateBaselineAsync(scenario);
        Assert.Equal(shouldBeABaseline, baseline != null);
    }

    [Fact]
    public async Task HeaderBaselineIsTheNewestAssessedCheck()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(TestSecurityHeaders.ReferrerPolicy));
        await scenario.CheckAsync(TestProbeResults.Assessed(TestSecurityHeaders.FrameOptions));
        await scenario.CheckAsync(TestProbeResults.Timeout());

        await using AsyncServiceScope scope = scenario.App.Services.CreateAsyncScope();
        IMonitoredServiceRepository repository = scope.ServiceProvider.GetRequiredService<IMonitoredServiceRepository>();
        CheckRecord? baseline = await repository.GetHeaderBaselineAsync(scenario.Service.Id, SecurityWarningScenario.ServiceUrl);

        Assert.NotNull(baseline);
        Assert.Equal(TestSecurityHeaders.FrameOptions, baseline.MissingSecurityHeaders);
    }

    [Fact]
    public async Task HeaderBaselineSkipsARowThatWasNotAssessed()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.HeadersNotAssessedBy("redirect"));

        await using AsyncServiceScope scope = scenario.App.Services.CreateAsyncScope();
        IMonitoredServiceRepository repository = scope.ServiceProvider.GetRequiredService<IMonitoredServiceRepository>();
        CheckRecord? baseline = await repository.GetHeaderBaselineAsync(scenario.Service.Id, SecurityWarningScenario.ServiceUrl);

        Assert.Null(baseline);
    }

    [Fact]
    public async Task AnEmptyTextIsAnAssessmentAndNotAMissingValue()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(TestProbeResults.Assessed(TestProbeResults.NothingMissing));

        await using AsyncServiceScope scope = scenario.App.Services.CreateAsyncScope();
        IMonitoredServiceRepository repository = scope.ServiceProvider.GetRequiredService<IMonitoredServiceRepository>();
        CheckRecord? baseline = await repository.GetHeaderBaselineAsync(scenario.Service.Id, SecurityWarningScenario.ServiceUrl);

        Assert.NotNull(baseline);
        Assert.Equal("", baseline.MissingSecurityHeaders);
    }

    [Fact]
    public async Task ABaselineIsOnlyForItsOwnService()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync();
        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(90));

        await using AsyncServiceScope scope = scenario.App.Services.CreateAsyncScope();
        IMonitoredServiceRepository repository = scope.ServiceProvider.GetRequiredService<IMonitoredServiceRepository>();
        CheckRecord? baseline = await repository.GetCertificateBaselineAsync(Guid.NewGuid(), SecurityWarningScenario.ServiceUrl);

        Assert.Null(baseline);
    }

    private static async Task<CheckRecord?> ReadCertificateBaselineAsync(SecurityWarningScenario scenario)
    {
        await using AsyncServiceScope scope = scenario.App.Services.CreateAsyncScope();
        IMonitoredServiceRepository repository = scope.ServiceProvider.GetRequiredService<IMonitoredServiceRepository>();
        return await repository.GetCertificateBaselineAsync(scenario.Service.Id, SecurityWarningScenario.ServiceUrl);
    }
}
