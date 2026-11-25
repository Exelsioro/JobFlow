namespace JobFlow.Application;

public static class VacancyAssessmentPolicy
{
    public const string Search = """
        Search is candidate discovery, not the final eligibility decision. Favour recall for plausible matches to the candidate's target work, core stack and transferable experience.
        Collect a potentially relevant vacancy even when some requirements are missing or unverified. Missing dossier evidence is not evidence that the candidate cannot do the work.
        Do not exclude a plausible match solely for a senior title, an exact years/months threshold, a language level needing confirmation, timezone overlap needing confirmation, a missing secondary tool, or a preferred/nice-to-have qualification.
        Consider supplied personal projects and transferable experience without upgrading them into commercial experience, tenure or proficiency. Leave unresolved requirements for the dedicated evaluator and human review; never invent a match.
        Exclude clearly unrelated work, confirmed conflicts with explicit candidate exclusions or hard search rules, closed/stale/already-applied vacancies and disallowed hosts. Keep freshness, host and application-duplication restrictions.
        When relevance is plausible but eligibility is uncertain, collect the observed vacancy link for full evaluation instead of silently discarding it. Record the uncertainty in the description/reason.
        """;

    public const string Evaluation = """
        Assess overall suitability for the work, not perfect keyword or checklist coverage. Separate core responsibilities and genuinely mandatory requirements from preferred/nice-to-have qualifications.
        Weigh confirmed core skills, supplied personal projects and transferable experience. A senior title or exact tenure threshold alone is not proof of a poor fit; do not turn project work into commercial experience or infer missing duration/proficiency.
        Reject for a clear substantive mismatch or a confirmed conflict with hard candidate constraints; explain that conflict with supplied evidence. Absence of a fact in the dossier is uncertainty, not proof that the candidate lacks the ability.
        For a plausible fit with unresolved mandatory skills, experience duration, language level, work authorization or timezone availability, return NeedsReview, preserving the opportunity and listing concrete questions for the candidate. Never invent answers.
        Check the supplied candidate profile and dossier before asking anything: experience, skills, languages, pay expectations, geography, work preferences and profile rules are evidence when explicitly stated. Do not ask the candidate to repeat a supplied fact unless there is a specific material contradiction. Distinguish candidate preferences (for example expected pay) from employer terms (for example the actual salary offered).
        Put missing or unverified mandatory requirements in missingSkills/unverifiedRequirements. Use clarifications only for concrete questions the candidate can answer and that affect this particular vacancy. Explain missing employer-side information (such as undisclosed salary or publication date), optional gaps and learning opportunities in the rationale instead. Never return a generic confirmation checklist.
        Before returning a question, identify the exact vacancy requirement or decision that depends on the answer and check all supplied profile fields and vacancy-specific confirmations for it. Ask only for the missing part; do not ask for a whole skill history or exact tenure unless that specificity is required. For a contradiction, state what conflicts. Write one direct question per item, ending with a question mark, or return an empty clarifications array. Do not put duplicate checks, submission approval, or source verification in candidate questions.
        Explain the strongest reasons to apply, material gaps, and which requirements actually need confirmation. Proceed only when mandatory eligibility is confirmed; NeedsReview must not authorize automatic submission.
        Respect explicit exclusions and publication-age limits. FreshnessDays is the active workflow age limit; older profile instructions cannot relax it.
        matches must contain only skill names copied from candidateSkills, never sentences, inferred proficiency or years of experience. Discuss other dossier evidence in rationale. An unconfirmed mandatory skill belongs in unverifiedRequirements, never in matches.
        """;
}
