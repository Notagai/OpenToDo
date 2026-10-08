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
        var isHome = _currentView == "home";

        PageTitle.Text = _currentView switch
        {
            "home" => "Home",
            "completed" => "Completed",
            _ => "Todo"
        };

        PageSubtitle.Text = _currentView switch
        {
            "home" => "Your productivity overview.",
            "completed" => "Completed tasks, kept as an archive.",
            _ => "Tasks that still need doing."
        };

        AnalyticsPanel.IsVisible = isHome;

        TaskList.ItemsSource = _currentView switch
        {
            "completed" => _tasks.Where(t => t.IsCompleted).ToArray(),
            "home" => _tasks.Where(t => !t.IsCompleted).Take(5).ToArray(),
            _ => _tasks.Where(t => !t.IsCompleted).ToArray()
        };

        if (isHome)
            RefreshAnalytics();
    }

    private void RefreshAnalytics()
    {
        var history = _tasks
            .SelectMany(t => t.CompletionHistory ?? Array.Empty<DateTimeOffset>())
            .ToArray();

        var now = DateTimeOffset.Now;
        var today = now.Date;
        var weekStart = today.AddDays(-(int)today.DayOfWeek + (int)DayOfWeek.Monday);
        if (today.DayOfWeek == DayOfWeek.Sunday)
            weekStart = today.AddDays(-6);

        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        CompletedTotal.Text = history.Length.ToString();
        CompletedDay.Text = history.Count(d => d.LocalDateTime.Date == today).ToString();
        CompletedWeek.Text = history.Count(d => d.LocalDateTime.Date >= weekStart).ToString();
        CompletedMonth.Text = history.Count(d => d.LocalDateTime.Date >= monthStart).ToString();
        CompletedYear.Text = history.Count(d => d.LocalDateTime.Date >= yearStart).ToString();
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

        if (task.IsCompleted)
        {
            await _repository.SaveAsync(task with { IsCompleted = false });
        }
        else
        {
            var history = (task.CompletionHistory ?? Array.Empty<DateTimeOffset>()).ToList();
            history.Add(DateTimeOffset.Now);
            await _repository.SaveAsync(task with { IsCompleted = true, CompletionHistory = history });
        }

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