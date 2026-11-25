using System.Net.Http.Json;
using JobFlow.Domain;
using JobFlow.Application;
using System.Text.Json;

namespace JobFlow.Infrastructure;

public static class BrowserWorkflowClient
{
    public static async Task<WorkflowRun> RunAsync(Guid id, CancellationToken ct, Guid? vacancyId = null, bool preparationOnly = false, bool outsideAgeLimit = false)
    {
        var baseUrl = Environment.GetEnvironmentVariable("JOBFLOW_API_URL") ?? "http://127.0.0.1:5271";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("JOBFLOW_API_URL must point to the local API browser host.");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
        var suffix = preparationOnly ? $"?vacancyId={vacancyId:D}&preparationOnly=true" + (outsideAgeLimit ? "&outsideAgeLimit=true" : "") : "";
        using var response = await http.PostAsJsonAsync($"/api/internal/workflows/{id}/browser-run{suffix}", new { }, ct);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (error.TryGetProperty("providerCode", out var code) && code.ValueKind == JsonValueKind.String)
                    throw new ModelProviderException(code.GetString()!, error.GetProperty("error").GetString()!,
                        error.TryGetProperty("retryable", out var retryable) && retryable.ValueKind == JsonValueKind.True,
                        error.TryGetProperty("diagnosticId", out var idValue) && idValue.ValueKind == JsonValueKind.String && idValue.TryGetGuid(out var diagnosticId) ? diagnosticId : null);
            }
            catch (JsonException) { }
            throw new InvalidOperationException($"Browser host returned HTTP {(int)response.StatusCode}. Check workflow history and the local API process.");
        }
        return await response.Content.ReadFromJsonAsync<WorkflowRun>(cancellationToken: ct) ?? throw new InvalidOperationException("Browser host returned no workflow result.");
    }
}
