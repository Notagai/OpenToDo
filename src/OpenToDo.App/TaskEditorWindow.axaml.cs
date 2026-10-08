using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenToDo.Core;

namespace OpenToDo.App;

public partial class TaskEditorWindow : Window
{
    private readonly TaskItem _original;
    public TaskItem? Result { get; private set; }

    public TaskEditorWindow(TaskItem task)
    {
        _original = task;
        InitializeComponent();
        TitleBox.Text = task.Title;
        DescriptionBox.Text = task.Description ?? string.Empty;
        DueDateBox.Text = task.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty;
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;
        DateTimeOffset? dueDate = null;
        var dueText = DueDateBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(dueText) && DateTime.TryParse(dueText, out var parsed))
            dueDate = new DateTimeOffset(parsed.Date);
        Result = _original with { Title = title, Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(), DueDate = dueDate };
        Close(Result);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(null);
}