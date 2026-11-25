using JobFlow.Application;

namespace JobFlow.UnitTests;

public class PublicationDatesTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 0, 5, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Вакансия опубликована 15 ноября 2026 в Минске", "2026-11-16T08:13:40.361+03:00", true)]
    [InlineData("Вакансия опубликована 16 ноября 2026 в Минске", "2026-11-16T00:13:40+03:00", false)]
    [InlineData("Published November 16, 2026", "2026-11-16T23:55:00-08:00", false)]
    [InlineData("Posted on 2026-11-18", "2026-11-19T08:00:00Z", true)]
    [InlineData("Published today", "2026-11-18T12:00:00-08:00", false)]
    [InlineData("Updated 2026-11-15. Copyright 2025", "2026-11-16T08:13:40+03:00", false)]
    public void ComparesPublicationLabelsToJobPostingCalendarDate(string text, string posted, bool conflict)
    {
        var sources = PublicationDates.Sources(text, ["{\"@type\":\"JobPosting\",\"datePosted\":\"" + posted + "\"}"], "https://board.example/jobs/1", Now);
        Assert.Equal(conflict, PublicationDates.Conflicting(sources));
        Assert.Contains(sources, x => x.Source == "JobPosting.datePosted");
        if (conflict) Assert.Contains(sources, x => x.Source == "Visible publication label");
    }

    [Fact]
    public void KeepsPreciseStructuredTimestampWithoutDependingOnModelDateOrQuoteFormatting()
    {
        var source = Assert.Single(PublicationDates.Sources("Job description", ["""{"@type":"JobPosting","datePosted":"2026-11-17T19:12:30.796859"}"""], "https://board.example/jobs/1", Now));
        Assert.Equal(DateTimeKind.Utc, source.PublishedAtUtc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 11, 17, 19, 12, 30, DateTimeKind.Utc).AddTicks(7968590), source.PublishedAtUtc);
    }

    [Fact]
    public void IgnoresOtherJobsAndUnrelatedOrMalformedMetadata()
    {
        var sources = PublicationDates.Sources("Published 2026-11-18", [
            "not json", "{\"@type\":\"Organization\",\"datePosted\":\"2020-01-01\"}",
            "{\"@graph\":[{\"@type\":\"JobPosting\",\"url\":\"https://board.example/jobs/2\",\"datePosted\":\"2020-01-01\"},{\"@type\":[\"JobPosting\"],\"url\":\"https://board.example/jobs/1\",\"datePosted\":\"2026-11-18\"}]}"
        ], "https://board.example/jobs/1?query=net", Now);
        Assert.Equal(2, sources.Length);
        Assert.False(PublicationDates.Conflicting(sources));
    }
}
