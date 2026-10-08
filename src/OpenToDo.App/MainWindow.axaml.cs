using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenToDo.Core;
using OpenToDo.Data;

namespace OpenToDo.App;

public partial class MainWindow : Window
{
    private readonly JsonTaskRepository _repository = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));

    private readonly List<TaskItem> _tasks = [];
    private string _currentView = "todo";

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadTasksAsync();
    }

    private async Task LoadTasksAsync()
    {
        _tasks.Clear();
        _tasks.AddRange(await _repository.GetAllAsync());
        RefreshTaskList();
    }

    private void RefreshTaskList()
    {
        PageTitle.Text = _currentView switch
        {
            "home" => "Home",
            "completed" => "Completed",
            _ => "Todo"
        };

        PageSubtitle.Text = _currentView switch
        {
            "home" => "Your OpenToDo overview.",
            "completed" => "Completed tasks, kept as an archive.",
            _ => "Tasks that still need doing."
        };

        TaskList.ItemsSource = _currentView switch
        {
            "completed" => _tasks.Where(t => t.IsCompleted).ToArray(),
            "home" => _tasks.Where(t => !t.IsCompleted).Take(5).ToArray(),
            _ => _tasks.Where(t => !t.IsCompleted).ToArray()
        };

    }

    private async void AddTask_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_currentView != "todo") return;

        var title = TaskTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        await _repository.SaveAsync(new TaskItem(Guid.NewGuid().ToString("N"), title));
        TaskTitle.Text = string.Empty;
        await LoadTasksAsync();
    }

    private async void ToggleCompleted_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem task }) return;
        await _repository.SaveAsync(task with { IsCompleted = !task.IsCompleted });
        await LoadTasksAsync();
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem task }) return;
        await _repository.DeleteAsync(task.Id);
        await LoadTasksAsync();
    }

    private void Home_OnClick(object? sender, RoutedEventArgs e)
    {
        _currentView = "home";
        RefreshTaskList();
    }

    private void Todo_OnClick(object? sender, RoutedEventArgs e)
    {
        _currentView = "todo";
        RefreshTaskList();
    }

    private void Completed_OnClick(object? sender, RoutedEventArgs e)
    {
        _currentView = "completed";
        RefreshTaskList();
    }
}