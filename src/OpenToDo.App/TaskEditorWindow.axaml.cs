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
        var titleInvalid = string.IsNullOrWhiteSpace(title);
        TitleBox.Classes.Set("invalid", titleInvalid);
        if (titleInvalid)
        {
            TitleBox.Focus();
            return;
        }

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
            {
                DueDateBox.Classes.Set("invalid", true);
                DueDateBox.Focus();
                return;
            }

            DueDateBox.Classes.Set("invalid", false);

            dueDate = new DateTimeOffset(parsed.Date, TimeZoneInfo.Local.GetUtcOffset(parsed.Date));
        }

        TitleBox.Classes.Set("invalid", false);
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
