using JobFlow.Infrastructure;

namespace JobFlow.IntegrationTests;

public class CliProviderDiagnosticsTests
{
    [Fact]
    public void OnlyErrorsAreRetainedAndSecretsAreRedacted()
    {
        var output="""
        {"type":"item.completed","item":{"text":"Private candidate dossier"}}
        {"type":"error","message":"401 invalid api_key=super-secret email candidate@example.com Bearer abc123 and sk-abcdefghijklmnop"}
        {"type":"turn.failed","error":{"message":"Authentication failed"}}
        """;
        var result=CliDiagnostics.Extract(output,"user\nPrivate resume with errors\ncodex\nPrivate generated letter\nERROR: Request failed");
        Assert.Contains("401",result);Assert.Contains("Authentication failed",result);Assert.Contains("ERROR: Request failed",result);
        Assert.DoesNotContain("Private",result);Assert.DoesNotContain("super-secret",result);Assert.DoesNotContain("candidate@example.com",result);
        Assert.DoesNotContain("abc123",result);Assert.DoesNotContain("abcdefghijklmnop",result);
    }
    [Theory]
    [InlineData("401 authentication failed","authentication",false)]
    [InlineData("429 quota exceeded","rate_limit",false)]
    [InlineData("invalid response schema","configuration",false)]
    [InlineData("model not supported","configuration",false)]
    [InlineData("network connection reset","transport",true)]
    [InlineData("503 temporarily unavailable","transport",true)]
    [InlineData("input too large","request",false)]
    [InlineData("Unclassified failure","cli_exit",false)]
    [InlineData("Failed to read prompt from stdin: input is not valid UTF-8 (invalid byte at offset 0)","encoding",false)]
    public void RetryRequiresAnExplicitlyTransientError(string error,string code,bool retryable)
    { Assert.Equal((code,retryable),CliDiagnostics.Classify(error)); }
    [Fact]
    public void MissingErrorDoesNotFallBackToPromptOrModelText()
    { Assert.DoesNotContain("Private",CliDiagnostics.Extract("null\n[]\n{\"type\":\"item.completed\",\"text\":\"Private\"}","Private dossier")); }
    [Fact]
    public void WindowsEncodingFailureIsRetainedWithoutAJsonEvent()
    { Assert.Contains("not valid UTF-8",CliDiagnostics.Extract("","Failed to read prompt from stdin: input is not valid UTF-8 (invalid byte at offset 0).")); }
}
