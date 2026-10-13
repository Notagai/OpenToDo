using Avalonia.Controls;
using Avalonia.Input;
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
        InitializeComponent();

        // Set after InitializeComponent: the XAML would otherwise overwrite these.
        Title = isNew ? "New task" : "Edit task";
        HeadingText.Text = isNew ? "New task" : "Edit task";
        SubheadingText.Text = isNew ? "What needs doing?" : "Update the details below.";
        SaveButton.Content = isNew ? "Add task" : "Save changes";

        TitleBox.Text = task.Title;
        DescriptionBox.Text = task.Description ?? string.Empty;
        DuePicker.SelectedDate = task.DueDate?.Date; // calendar date as originally stored

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close(null);
            }
        };

        Opened += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private void TitleBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Save();
        }
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            ShowError("Give your task a title.");
            TitleBox.Focus();
            return;
        }

        var description = DescriptionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var picked = DuePicker.SelectedDate?.Date;
        DateTimeOffset? due;

        if (picked is null)
            due = null;
        else if (_original.DueDate is { } existing && existing.Date == picked.Value)
            due = existing; // untouched: keep the stored value exactly, no timezone drift
        else
        {
            var local = DateTime.SpecifyKind(picked.Value, DateTimeKind.Unspecified);
            due = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        Result = _original with
        {
            Title = title.Length > TaskLimits.MaxTitleLength ? title[..TaskLimits.MaxTitleLength] : title,
            Description = description is { Length: > TaskLimits.MaxDescriptionLength } d ? d[..TaskLimits.MaxDescriptionLength] : description,
            DueDate = due,
            CreatedAt = _original.CreatedAt ?? DateTimeOffset.Now
        };

        Close(Result);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void ClearDate_OnClick(object? sender, RoutedEventArgs e) => DuePicker.SelectedDate = null;
    private void QuickToday_OnClick(object? sender, RoutedEventArgs e) => DuePicker.SelectedDate = DateTime.Today;
    private void QuickTomorrow_OnClick(object? sender, RoutedEventArgs e) => DuePicker.SelectedDate = DateTime.Today.AddDays(1);
    private void QuickWeek_OnClick(object? sender, RoutedEventArgs e) => DuePicker.SelectedDate = DateTime.Today.AddDays(7);

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(null);
}
