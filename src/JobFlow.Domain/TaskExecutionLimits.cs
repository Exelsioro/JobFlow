namespace JobFlow.Domain;

public static class TaskExecutionLimits
{
    public const int DefaultBrowserWorkflowTimeoutSeconds = 7200;
    public static int MaximumTimeoutSeconds(string kind) => kind is "Workflow" or "PrepareApplication" ? 21600 : 900;

    // Retain ownership through the timeout and allow time for browser cleanup.
    public static TimeSpan LeaseDuration(int timeoutSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1200, timeoutSeconds + 300));
}
