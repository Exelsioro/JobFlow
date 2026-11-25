namespace JobFlow.Infrastructure;

// One browser stage only: never share candidate or form state between runs.
public sealed class BrowserAgentMemory
{
    private readonly List<string> states = [];
    private readonly List<Entry> actions = [];
    public IReadOnlyList<Entry> RecentActions => actions;
    public int SameStateVisits { get; private set; }
    public bool SubmissionAttempted { get; set; }
    public int VisualAttempts { get; set; }
    public string Warning => SameStateVisits >= 3 ? "This page state has already been visited repeatedly. Use the history to avoid reopening filters or repeating failed actions. Collect observed vacancies when the search is ready, or request manual attention." : "";

    public void Observe(string fingerprint)
    {
        if (actions.Count > 0 && states.Count > 0)
            actions[^1] = actions[^1] with { PageChanged = states[^1] != fingerprint };
        states.Add(fingerprint);
        if (states.Count > 16) states.RemoveAt(0);
        SameStateVisits = states.Count(x => x == fingerprint);
        if (SameStateVisits >= 8)
            throw new InvalidOperationException("Browser needs attention: repeated page states without progress. Search stopped to avoid an action loop; inspect browser action history.");
    }

    public void Record(string phase, string action, string targetLabel, string url, string outcome, string reason, string error = "")
    {
        actions.Add(new(phase, action, Limit(targetLabel, 180), Limit(url, 1000), outcome, Limit(reason, 240), Limit(error, 600)));
        if (actions.Count > 12) actions.RemoveAt(0);
    }
    private static string Limit(string value, int length) => value[..Math.Min(length, value.Length)];
    public sealed record Entry(string Phase, string Action, string TargetLabel, string Url, string Outcome, string Reason, string Error, bool? PageChanged = null);
}
