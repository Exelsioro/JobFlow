using System.Text.Json;
using JobFlow.Application;
using JobFlow.Infrastructure;

namespace JobFlow.IntegrationTests;

public class FreshnessEvidenceRegressionTests
{
    [Theory]
    [InlineData("\"Отклик в Telegram\"")]
    [InlineData("«Отклик в Telegram»")]
    [InlineData("“Отклик в Telegram”")]
    public void Paired_quotation_marks_do_not_invalidate_exact_source_text(string quote)
    {
        Assert.True(EvidenceText.Contains("Описание\nОтклик в Telegram\nКонтакты", quote));
    }

    [Theory]
    [InlineData("\"Откликнуться; вакансия открыта\"")]
    [InlineData("«Отклик в Telegram\"")]
    [InlineData("\"\"")]
    [InlineData("\"8 лет опыта\"")]
    public void Quote_formatting_does_not_allow_fabricated_or_joined_evidence(string quote)
    {
        Assert.False(EvidenceText.Contains("Откликнуться\nДругой текст\nвакансия открыта\n7 лет опыта", quote));
    }

    [Fact]
    public void Freshness_schema_limits_status_evidence_to_observed_source_choices()
    {
        const string evidence = "Опубликовано сегодня\nОтклик в Telegram\nВакансия закрыта\n{\"validThrough\":\"2026-12-01\"}";
        var schema = BrowserWorkflowDriver.FreshnessSchemaFor(evidence, [null!, "", " ", "Отклик в Telegram", "Не существующая кнопка"]);
        using var doc = JsonDocument.Parse(schema);
        var values = doc.RootElement.GetProperty("properties").GetProperty("availabilityEvidence")
            .GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("", values);
        var choices = BrowserWorkflowDriver.FreshnessEvidenceChoices(evidence, ["Отклик в Telegram", "Не существующая кнопка"]);
        Assert.Contains("Отклик в Telegram", choices.Values);
        Assert.Contains("Вакансия закрыта", choices.Values);
        Assert.DoesNotContain("Не существующая кнопка", choices.Values);
        Assert.DoesNotContain(choices.Values, x => x.Contains("validThrough"));
        Assert.All(values.Where(x => x != ""), x => Assert.Matches("^e[0-9]+$", x!));
        Assert.Contains("publicationEvidence", schema);
    }

    [Fact]
    public void Quotes_newlines_and_directApply_metadata_cannot_enter_schema_literals()
    {
        const string evidence = "Apply for \"C# developer\"\nОткликнуться\n\"directApply\": false,\n{\"validThrough\":\"2026-12-01\"}";
        var choices = BrowserWorkflowDriver.FreshnessEvidenceChoices(evidence, ["Apply for \"C# developer\"", "Откликнуться"]);
        var schema = BrowserWorkflowDriver.FreshnessSchemaFor(evidence, ["Apply for \"C# developer\"", "Откликнуться"]);
        Assert.Contains("Apply for \"C# developer\"", choices.Values);
        Assert.DoesNotContain(choices.Values, x => x.Contains("directApply") || x.Contains("validThrough"));
        Assert.DoesNotContain("directApply", schema);
        Assert.DoesNotContain("developer", schema);
        Assert.Equal("Apply for \"C# developer\"", choices["e1"]);
    }

    [Fact]
    public async Task Offline_pdf_preserves_unicode_and_treats_markup_as_text()
    {
        var bytes = await ResumeAgentService.RenderAsync("Резюме кандидата", [("Опыт", "Разработчик C# & SQL\n<script>literal</script>")], default);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var text = string.Join(" ", pdf.GetPages().Select(x => x.Text));
        Assert.Contains("Резюме", text);
        Assert.Contains("Разработчик", text);
        Assert.Contains("<script>literal</script>", text);
    }

    [Fact]
    public async Task Cancelled_pdf_render_does_not_start_or_retry_work()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResumeAgentService.RenderAsync("Title", [], cancel.Token));
    }
}
