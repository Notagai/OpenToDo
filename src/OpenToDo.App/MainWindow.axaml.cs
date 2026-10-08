using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenToDo.Core;
using OpenToDo.Data;
using OpenToDo.Plugins.Ai;

namespace OpenToDo.App;

public partial class MainWindow : Window
{
    private readonly JsonTaskRepository _repository = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));
    private readonly AiSettingsStore _settingsStore = new();
    private readonly AiClient _aiClient = new();
    private readonly List<TaskItem> _tasks = [];
    private AiSettings _aiSettings = new();
    private string _currentView = "home";

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            _aiSettings = await _settingsStore.LoadAsync();
            LoadProviderSettings();
            await LoadTasksAsync();
        };
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
            "todo" => "Todo",
            "completed" => "Completed",
            "settings" => "Settings",
            _ => "Home"
        };
        PageSubtitle.Text = _currentView switch
        {
            "todo" => "Tasks that still need doing.",
            "completed" => "Completed tasks, kept as an archive.",
            "settings" => "AI providers and application configuration.",
            _ => "Your productivity overview."
        };

        HomePanel.IsVisible = _currentView == "home";
        TodoPanel.IsVisible = _currentView == "todo";
        CompletedPanel.IsVisible = _currentView == "completed";
        SettingsPanel.IsVisible = _currentView == "settings";
        AddTaskPanel.IsVisible = _currentView == "todo";

        TodoList.ItemsSource = _tasks.Where(t => !t.IsCompleted).ToArray();
        CompletedList.ItemsSource = _tasks.Where(t => t.IsCompleted).ToArray();
        TaskList.ItemsSource = _tasks.Where(t => !t.IsCompleted).Take(5).ToArray();

        if (_currentView == "home")
            RefreshAnalytics();
    }

    private void RefreshAnalytics()
    {
        var history = _tasks
            .SelectMany(t => t.CompletionHistory ?? Array.Empty<DateTimeOffset>())
            .Select(d => d.LocalDateTime.Date)
            .ToArray();

        var now = DateTime.Now;
        var today = now.Date;
        var weekStart = today.AddDays(-(int)((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        CompletedTotal.Text = history.Length.ToString();
        CompletedDay.Text = history.Count(d => d == today).ToString();
        CompletedWeek.Text = history.Count(d => d >= weekStart).ToString();
        CompletedMonth.Text = history.Count(d => d >= monthStart).ToString();
        CompletedYear.Text = history.Count(d => d >= yearStart).ToString();

        var daily = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(-13 + i))
            .Select(date => new { date, count = history.Count(d => d == date) })
            .ToArray();
        var monthly = Enumerable.Range(0, 6)
            .Select(i => monthStart.AddMonths(-5 + i))
            .Select(month => new { month, count = history.Count(d => d >= month && d < month.AddMonths(1)) })
            .ToArray();

        var dailyMax = Math.Max(1, daily.Max(x => x.count));
        var monthlyMax = Math.Max(1, monthly.Max(x => x.count));

        DailyChart.ItemsSource = daily.Select(x => new AnalyticsBar(
            x.date.ToString("ddd"),
            x.count,
            220d * x.count / dailyMax)).ToArray();

        MonthlyChart.ItemsSource = monthly.Select(x => new AnalyticsBar(
            x.month.ToString("MMM"),
            x.count,
            220d * x.count / monthlyMax)).ToArray();
    }

    private async void AddTask_OnClick(object? sender, RoutedEventArgs e)
    {
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

    private void Home_OnClick(object? sender, RoutedEventArgs e) { _currentView = "home"; RefreshTaskList(); }
    private void Todo_OnClick(object? sender, RoutedEventArgs e) { _currentView = "todo"; RefreshTaskList(); }
    private void Completed_OnClick(object? sender, RoutedEventArgs e) { _currentView = "completed"; RefreshTaskList(); }
    private void Settings_OnClick(object? sender, RoutedEventArgs e) { _currentView = "settings"; LoadProviderSettings(); RefreshTaskList(); }

    private AiProvider SelectedProvider => ProviderCombo.SelectedIndex switch
    {
        1 => AiProvider.Groq,
        2 => AiProvider.OpenRouter,
        _ => AiProvider.OpenAI
    };

    private void Provider_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) LoadProviderSettings();
    }

    private void LoadProviderSettings()
    {
        if (ProviderCombo is null || ApiKeyBox is null) return;

        var provider = SelectedProvider;
        ApiKeyBox.Text = provider switch
        {
            AiProvider.Groq => _aiSettings.GroqApiKey,
            AiProvider.OpenRouter => _aiSettings.OpenRouterApiKey,
            _ => _aiSettings.OpenAiApiKey
        };

        var model = GetStoredModel(provider);
        ModelComboBox.ItemsSource = string.IsNullOrWhiteSpace(model) ? Array.Empty<string>() : new[] { model };
        ModelComboBox.SelectedItem = string.IsNullOrWhiteSpace(model) ? null : model;
        TemperatureBox.Value = (decimal)_aiSettings.Temperature;
        TemperatureBox.IsEnabled = provider != AiProvider.OpenAI;
        MaxOutputTokensBox.Value = _aiSettings.MaxOutputTokens;
        AiStatus.Text = $"Provider: {provider}. Fetch models to validate the key and load the current model catalog.";
    }

    private async void FetchModels_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var provider = SelectedProvider;
            var key = ApiKeyBox.Text?.Trim() ?? string.Empty;
            AiStatus.Text = $"Fetching {provider} models...";
            var models = await _aiClient.GetModelsAsync(provider, key);
            var ids = models.Select(m => m.Id).ToArray();
            ModelComboBox.ItemsSource = ids;

            var current = GetStoredModel(provider);
            ModelComboBox.SelectedItem = ids.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? current
                : ids.FirstOrDefault();

            AiStatus.Text = $"Loaded {ids.Length} models from {provider}.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = $"Model fetch failed: {ex.Message}";
        }
    }

    private async void SaveSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        var key = ApiKeyBox.Text?.Trim() ?? string.Empty;
        var model = ModelComboBox.SelectedItem?.ToString() ?? string.Empty;

        switch (provider)
        {
            case AiProvider.Groq:
                _aiSettings.GroqApiKey = key;
                _aiSettings.GroqModel = model;
                break;
            case AiProvider.OpenRouter:
                _aiSettings.OpenRouterApiKey = key;
                _aiSettings.OpenRouterModel = model;
                break;
            default:
                _aiSettings.OpenAiApiKey = key;
                _aiSettings.OpenAiModel = model;
                break;
        }

        _aiSettings.Temperature = (double)(TemperatureBox.Value ?? 0.7m);
        _aiSettings.MaxOutputTokens = (int)(MaxOutputTokensBox.Value ?? 1024m);
        await _settingsStore.SaveAsync(_aiSettings);
        AiStatus.Text = $"Saved {provider} settings.";
    }

    private string GetStoredModel(AiProvider provider) => provider switch
    {
        AiProvider.Groq => _aiSettings.GroqModel,
        AiProvider.OpenRouter => _aiSettings.OpenRouterModel,
        _ => _aiSettings.OpenAiModel
    };
}