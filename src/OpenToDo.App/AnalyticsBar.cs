namespace OpenToDo.App;

/// <summary>One day in the completion chart.</summary>
public sealed record AnalyticsBar(string Label, string FullLabel, int Count, double BarHeight, bool IsToday)
{
    public bool IsEmpty => Count == 0;
    public string CountText => Count == 0 ? string.Empty : Count.ToString();
}
