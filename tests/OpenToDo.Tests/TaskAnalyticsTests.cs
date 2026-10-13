using OpenToDo.Core;
using Xunit;

namespace OpenToDo.Tests;

public class TaskAnalyticsTests
{
    private static readonly DateTime Today = new(2026, 10, 14); // Wednesday

    private static TaskItem Done(params DateTime[] localDays) =>
        new("t" + localDays.Length, "x", IsCompleted: true,
            CompletionHistory: localDays.Select(d => new DateTimeOffset(d.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(d.AddHours(12)))).ToArray());

    [Fact]
    public void Summarize_CountsEachWindow_AndWeekStartsMonday()
    {
        var task = Done(Today, Today.AddDays(-2), new DateTime(2026, 10, 1), new DateTime(2026, 3, 3), new DateTime(2025, 12, 31));
        var s = TaskAnalytics.Summarize(new[] { task }, Today);

        Assert.Equal(5, s.Total);
        Assert.Equal(1, s.Today);
        Assert.Equal(2, s.Week);   // Mon 12th .. Wed 14th: today + Monday(12th)
        Assert.Equal(3, s.Month);
        Assert.Equal(4, s.Year);
    }

    [Fact]
    public void Summarize_IgnoresFutureCompletions()
    {
        var s = TaskAnalytics.Summarize(new[] { Done(Today.AddDays(3)) }, Today);
        Assert.Equal(0, s.Total);
    }

    [Fact]
    public void Streak_SurvivesUntilEndOfDay_ThenBreaks()
    {
        var yesterdayRun = Done(Today.AddDays(-1), Today.AddDays(-2), Today.AddDays(-3));
        Assert.Equal(3, TaskAnalytics.Summarize(new[] { yesterdayRun }, Today).Streak);

        var broken = Done(Today.AddDays(-2), Today.AddDays(-3));
        Assert.Equal(0, TaskAnalytics.Summarize(new[] { broken }, Today).Streak);

        var withToday = Done(Today, Today.AddDays(-1));
        Assert.Equal(2, TaskAnalytics.Summarize(new[] { withToday }, Today).Streak);
    }

    [Fact]
    public void DailyCounts_ReturnsOldestFirst_WithZeros()
    {
        var counts = TaskAnalytics.DailyCounts(new[] { Done(Today, Today, Today.AddDays(-13)) }, Today, 14);
        Assert.Equal(14, counts.Length);
        Assert.Equal(1, counts[0]);
        Assert.Equal(2, counts[13]);
        Assert.Equal(0, counts[5]);
    }
}
