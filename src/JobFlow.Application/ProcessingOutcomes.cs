using System.Text.RegularExpressions;

namespace JobFlow.Application;

public sealed class VacancyUnavailableException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class HumanReviewRequiredException(string message) : InvalidOperationException(message);

public sealed class ModelProviderException(string code, string message, bool retryable = false, Guid? diagnosticId = null)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public Guid? DiagnosticId { get; } = diagnosticId;
    public static ModelProviderException? Find(Exception? error) => error == null ? null :
        error as ModelProviderException ?? Find(error.InnerException);
}

public static class EvidenceText
{
    public static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    public static bool Contains(string source, string quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return false;
        var text = Normalize(source); var value = Normalize(quote);
        if (text.Contains(value, StringComparison.Ordinal)) return true;
        if (value.Length < 3) return false;
        var wrapped = (value[0], value[^1]) switch
            { ('"','"') or ('\'','\'') or ('«','»') or ('“','”') or ('„','“') => true, _ => false };
        if (!wrapped) return false;
        var inner = value[1..^1].Trim();
        return inner.Length > 0 && text.Contains(inner, StringComparison.Ordinal);
    }
}
