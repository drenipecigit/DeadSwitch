using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Tests;

public class SecurityWarningSettingsTests
{
    [Fact]
    public void DefaultIsFourteenDays()
    {
        SecurityWarningSettings settings = new SecurityWarningSettings();

        Assert.Equal(14, settings.CertificateWarningDays);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(14, true)]
    [InlineData(365, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(366, false)]
    public void OnlyOneToThreeHundredSixtyFiveIsValid(int days, bool expected)
    {
        SecurityWarningSettings settings = new SecurityWarningSettings();
        settings.CertificateWarningDays = days;

        Assert.Equal(expected, SecurityWarningSettings.HasValidWarningDays(settings));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("366")]
    public async Task InvalidSettingStopsTheApplicationWithAFixedText(string value)
    {
        await using TestApp app = new TestApp();
        app.CertificateWarningDaysSetting = value;
        app.WaitForProgramBeforeStart = true;

        // Reading Services is what starts the application.
        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() => app.Services);

        Assert.Equal("CertificateWarningDays must be between 1 and 365.", exception.Message);
    }

    [Fact]
    public async Task WithoutASettingFifteenDaysAreQuietAndFourteenWarn()
    {
        await using SecurityWarningScenario quiet = await SecurityWarningScenario.StartAsync();
        await quiet.CheckAsync(quiet.ValidCertificateWithDaysLeft(15));
        Assert.Empty(quiet.GetWarnings());

        await using SecurityWarningScenario warned = await SecurityWarningScenario.StartAsync();
        await warned.CheckAsync(warned.ValidCertificateWithDaysLeft(14));
        SecurityWarning warning = Assert.Single(warned.GetWarnings());
        Assert.Equal(14, warning.ThresholdDays);
    }

    [Fact]
    public async Task AHigherSettingIsUsed()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync("30");

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(20));

        SecurityWarning warning = Assert.Single(scenario.GetWarnings());
        Assert.Equal(30, warning.ThresholdDays);
    }

    [Fact]
    public async Task ALowerSettingIsUsed()
    {
        await using SecurityWarningScenario scenario = await SecurityWarningScenario.StartAsync("5");

        await scenario.CheckAsync(scenario.ValidCertificateWithDaysLeft(6));

        Assert.Empty(scenario.GetWarnings());
    }

    [Fact]
    public async Task TheRegisteredSettingsAreTheOnesFromTheConfiguration()
    {
        await using TestApp app = new TestApp();
        app.CertificateWarningDaysSetting = "21";

        SecurityWarningSettings settings = app.Services.GetRequiredService<SecurityWarningSettings>();

        Assert.Equal(21, settings.CertificateWarningDays);
    }
}
