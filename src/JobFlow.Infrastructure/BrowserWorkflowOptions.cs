namespace JobFlow.Infrastructure;

public sealed class BrowserWorkflowOptions
{
    public int NavigationTimeoutSeconds { get; set; } = 60;
    public int NavigationRetries { get; set; } = 1;
    public int ActionTimeoutSeconds { get; set; } = 8;
    public int ActionRecoveryLimit { get; set; } = 3;
    public int VisualRecoveryLimit { get; set; } = 3;

    public void Validate()
    {
        if (NavigationTimeoutSeconds is < 1 or > 180 || NavigationRetries is < 0 or > 2 ||
            ActionTimeoutSeconds is < 1 or > 60 || ActionRecoveryLimit is < 0 or > 5 || VisualRecoveryLimit is < 0 or > 5)
            throw new ArgumentException("Browser settings: navigation timeout 1–180s, retries 0–2, action timeout 1–60s, recovery limits 0–5.");
    }
}
