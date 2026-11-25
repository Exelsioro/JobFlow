using JobFlow.Domain;

namespace JobFlow.Infrastructure;

public sealed record AgentTaskRecommendation(string Operation, string TaskType, string Title, string PreferredModel, string Reasoning, string Why);
public sealed record ResolvedAgentTask(string Operation, string TaskType, string Title, string Model, string Reasoning, string Why);

public static class AgentTaskPolicy
{
    public static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>
    {
        ["ru"] = "русский", ["en"] = "English", ["pl"] = "polski", ["be"] = "беларуская",
        ["uk"] = "українська", ["de"] = "Deutsch", ["es"] = "español", ["fr"] = "français"
    };
    public static readonly AgentTaskRecommendation[] Recommendations =
    [
        new("VacancyFreshness", "BrowserAutomation", "Дата и открытый статус", "gpt-6-luna", "high", "Извлечение конкретных фактов с проверкой цитат в коде."),
        new("BrowserAutomation", "BrowserAutomation", "Действия в браузере", "gpt-6.1-terra", "medium", "Выбор элементов и работа с интерфейсом; важнее точность действий, чем длинное рассуждение."),
        new("Evaluation", "Evaluation", "Соответствие вакансии", "gpt-6.2-sol", "medium", "Сопоставление нескольких источников, переносимых навыков и обязательных требований."),
        new("CoverLetter", "CoverLetter", "Сопроводительное письмо", "gpt-6.1-terra", "low", "Краткое письмо по готовой оценке и каталогу фактов."),
        new("CoverLetterVerification", "CoverLetter", "Проверка письма", "gpt-6.2-sol", "medium", "Независимая проверка смысла и отсутствия выдуманных утверждений."),
        new("ResumeSelection", "Resume", "Выбор исходного PDF", "gpt-6-luna", "high", "Ограниченный выбор из предоставленных документов."),
        new("ResumeAdaptation", "Resume", "Адаптация резюме", "gpt-6.2-sol", "medium", "Связный документ с точными доказательствами без усиления фактов."),
        new("ResumeVerification", "Resume", "Проверка резюме", "gpt-6.2-sol", "medium", "Проверка фактических утверждений и исправлений."),
        new("Dossier", "Dossier", "Извлечение досье", "gpt-6-luna", "high", "Структурированное извлечение без домысливания."),
        new("MailAnalysis", "MailAnalysis", "Разбор писем", "gpt-6-luna", "high", "Классификация и сопоставление; сомнительное совпадение остаётся для человека.")
    ];

    public static ResolvedAgentTask Resolve(string operation, CliModels catalog)
    {
        var recommendation = Recommendations.FirstOrDefault(x => x.Operation == operation) ??
            Recommendations.FirstOrDefault(x => x.Operation == operation.Split('/')[0]);
        if (recommendation == null) throw new ArgumentException("Для этой операции нет экономичного профиля. Выберите модель вручную.");
        var candidates = new[] { recommendation.PreferredModel, "gpt-6.2-sol", "gpt-6.1-sol", "gpt-6-sol" }.Distinct();
        var model = candidates.Select(id => catalog.Models.FirstOrDefault(m => m.Id == id)).FirstOrDefault(m => m != null)
            ?? throw new InvalidOperationException("Подходящая модель отсутствует в каталоге CLI. Выберите доступную модель вручную.");
        var effort = model.SupportedReasoningEfforts.Contains(recommendation.Reasoning) ? recommendation.Reasoning : model.DefaultReasoningEffort;
        return new(operation, recommendation.TaskType, recommendation.Title, model.Id, effort, recommendation.Why);
    }

    public static string LanguageInstruction(string language) =>
        "Language policy for this operation: write explanations intended for the candidate (rationale, missingSkills, unverifiedRequirements, clarifications, reasons, issues and summaries) in " +
        (Languages.GetValueOrDefault(language) ?? throw new ArgumentException("Выберите поддерживаемый язык объяснений.")) + ". " +
        "This preference NEVER selects the language of an employer-facing document. For a cover letter or adapted resume, follow an explicit application-language requirement in the vacancy; otherwise use the language of the actual vacancy description, ignoring translated job-board navigation and recommendations. " +
        "Use that document language consistently in title, headings and body. Do not append internal review notes or unresolved eligibility questions to a resume. " +
        "Keep JSON property names, enums, identifiers, URLs, names, dates, technical names and exact evidence quotations unchanged. Browser form values follow the form and supplied candidate data. Never translate an evidence quote.";
}
