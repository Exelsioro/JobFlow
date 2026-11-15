using JobFlow.Infrastructure;
namespace JobFlow.IntegrationTests;
public class BrowserTests
{
    [Fact] public async Task IsolatedBrowserFormProducesTestReceipt()
    {
        var result = await new PlaywrightBrowser().DryRunAsync(default);
        Assert.True(result.Confirmed); Assert.Equal("TEST-RECEIPT-001", result.Evidence);
    }
    [Theory]
    [InlineData(TestBrowserScenario.UnknownRequiredField, "Blocked")]
    [InlineData(TestBrowserScenario.AuthorizationLost, "Blocked")]
    [InlineData(TestBrowserScenario.ConfirmationMissing, "SubmissionUnknown")]
    public async Task UnsafeBrowserOutcomesNeverReportSuccess(TestBrowserScenario scenario, string expected)
    {
        var result = await new PlaywrightBrowser().RunScenarioAsync(scenario, default);
        Assert.False(result.Confirmed); Assert.StartsWith(expected, result.Evidence);
    }
    [Fact] public async Task TestDiscoveryExtractsVacancyFromRenderedFixture()
    {
        var platform = new JobFlow.Domain.JobPlatform { Adapter = "test-form" };
        var vacancies = await new TestJobDiscoveryProvider().DiscoverAsync(platform, default);
        var vacancy = Assert.Single(vacancies);
        Assert.Equal(platform.Id, vacancy.JobPlatformId); Assert.Equal("synthetic-001", vacancy.ExternalId);
        Assert.StartsWith("TEST", vacancy.Title);
    }
}

