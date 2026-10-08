using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenToDo.Core;
using OpenToDo.Data;

namespace OpenToDo.App;

public partial class MainWindow : Window
{
    private readonly JsonTaskRepository _repository = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));
    private readonly List<TaskItem> _tasks = [];
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadTasksAsync();
    }
    private async Task LoadTasksAsync()
    {
        _tasks.Clear();
        _tasks.AddRange(await _repository.GetAllAsync());
        TaskList.ItemsSource = null;
        TaskList.ItemsSource = _tasks;
    }
    private async void AddTask_OnClick(object? sender, RoutedEventArgs e)
    {
        var title = TaskTitle.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;
        await _repository.SaveAsync(new TaskItem(Guid.NewGuid().ToString("N"), title));
        TaskTitle.Text = string.Empty;
        await LoadTasksAsync();
    }
}
