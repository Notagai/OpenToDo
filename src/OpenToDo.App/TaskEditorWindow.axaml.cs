using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenToDo.Core;

namespace OpenToDo.App;

public partial class TaskEditorWindow : Window
{
    private readonly TaskItem _original;
    public TaskItem? Result { get; private set; }

    public TaskEditorWindow(TaskItem task, bool isNew = false)
    {
        _original = task;
        Title = isNew ? "Make event" : "Edit event";
        InitializeComponent();
        TitleBox.Text = task.Title;
        DescriptionBox.Text = task.Description ?? string.Empty;
        DueDateBox.Text = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return;

        DateTimeOffset? dueDate = null;
        var dueText = DueDateBox.Text?.Trim();

        if (!string.IsNullOrWhiteSpace(dueText))
        {
            if (!DateTime.TryParseExact(
                    dueText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
                return;

            dueDate = new DateTimeOffset(parsed.Date, TimeZoneInfo.Local.GetUtcOffset(parsed.Date));
        }

        Result = _original with
        {
            Title = title,
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            DueDate = dueDate
        };

        Close(Result);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(null);
}
