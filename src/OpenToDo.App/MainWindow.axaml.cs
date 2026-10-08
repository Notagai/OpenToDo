using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenToDo.Core;
using OpenToDo.Data;
using OpenToDo.Plugins.Ai;

namespace OpenToDo.App;

public partial class MainWindow : Window
{
    private readonly JsonTaskRepository _repository = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));
    private readonly AiSettingsStore _settingsStore = new();
    private readonly AiClient _aiClient = new();
    private readonly List<TaskItem> _tasks = [];
    private AiSettings _aiSettings = new();
    private string _currentView = "home";

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => { try { _aiSettings = await _settingsStore.LoadAsync(); LoadProviderSettings(); await LoadTasksAsync(); } catch (Exception ex) { AiStatus.Text = $"Startup warning: {ex.Message}"; } };
    }

    private async Task LoadTasksAsync() { _tasks.Clear(); _tasks.AddRange(await _repository.GetAllAsync()); RefreshTaskList(); }

    private void RefreshTaskList()
    {
        PageTitle.Text = _currentView switch { "todo" => "Todo", "completed" => "Completed", "settings" => "Settings", _ => "Home" };
        PageSubtitle.Text = _currentView switch { "todo" => "Tasks that still need doing.", "completed" => "Completed tasks, kept as an archive.", "settings" => "AI providers and application configuration.", _ => "Your productivity overview." };
        HomePanel.IsVisible = _currentView == "home"; TodoPanel.IsVisible = _currentView == "todo"; CompletedPanel.IsVisible = _currentView == "completed"; SettingsPanel.IsVisible = _currentView == "settings";
        TodoList.ItemsSource = _tasks.Where(t => !t.IsCompleted).ToArray(); CompletedList.ItemsSource = _tasks.Where(t => t.IsCompleted).ToArray(); TaskList.ItemsSource = _tasks.Where(t => !t.IsCompleted).Take(5).ToArray();
        if (_currentView == "home") RefreshAnalytics();
    }

    private void RefreshAnalytics()
    {
        var history = _tasks.SelectMany(t => t.CompletionHistory ?? Array.Empty<DateTimeOffset>()).Select(d => d.LocalDateTime.Date).ToArray();
        var today = DateTime.Now.Date; var weekStart = today.AddDays(-(int)((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7)); var monthStart = new DateTime(today.Year, today.Month, 1); var yearStart = new DateTime(today.Year, 1, 1);
        CompletedTotal.Text = history.Length.ToString(); CompletedDay.Text = history.Count(d => d == today).ToString(); CompletedWeek.Text = history.Count(d => d >= weekStart).ToString(); CompletedMonth.Text = history.Count(d => d >= monthStart).ToString(); CompletedYear.Text = history.Count(d => d >= yearStart).ToString();
        var daily = Enumerable.Range(0, 14).Select(i => today.AddDays(-13 + i)).Select(date => new AnalyticsBar(date.ToString("ddd"), history.Count(d => d == date), 0)).ToArray(); var max = Math.Max(1, daily.Max(x => x.Count)); var bars = daily.Select(x => x with { Width = 760d * x.Count / max }).ToArray();
        DailyChart.ItemsSource = bars; DailyNumbers.ItemsSource = bars;
    }

    private void GraphMode_OnClick(object? sender, RoutedEventArgs e) { GraphPanel.IsVisible = true; NumberPanel.IsVisible = false; ChartSubtitle.Text = "Last 14 days"; }
    private void NumberMode_OnClick(object? sender, RoutedEventArgs e) { GraphPanel.IsVisible = false; NumberPanel.IsVisible = true; ChartSubtitle.Text = "Last 14 days · exact counts"; }
    private void NewTask_OnClick(object? sender, RoutedEventArgs e) { _currentView = "todo"; RefreshTaskList(); TaskTitle.Focus(); }

    private async void AddTask_OnClick(object? sender, RoutedEventArgs e) { var title = TaskTitle.Text?.Trim(); if (string.IsNullOrWhiteSpace(title)) return; await _repository.SaveAsync(new TaskItem(Guid.NewGuid().ToString("N"), title)); TaskTitle.Text = string.Empty; await LoadTasksAsync(); }

    private async void Edit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem task }) return;
        var editor = new TaskEditorWindow(task); var result = await editor.ShowDialog<TaskItem?>(this); if (result is not null) await _repository.SaveAsync(result); await LoadTasksAsync();
    }

    private async void ToggleCompleted_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskItem task }) return;
        if (task.IsCompleted) await _repository.SaveAsync(task with { IsCompleted = false });
        else { var history = (task.CompletionHistory ?? Array.Empty<DateTimeOffset>()).ToList(); history.Add(DateTimeOffset.Now); await _repository.SaveAsync(task with { IsCompleted = true, CompletionHistory = history }); }
        await LoadTasksAsync();
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e) { if (sender is Button { DataContext: TaskItem task }) { await _repository.DeleteAsync(task.Id); await LoadTasksAsync(); } }
    private void Home_OnClick(object? sender, RoutedEventArgs e) { _currentView = "home"; RefreshTaskList(); }
    private void Todo_OnClick(object? sender, RoutedEventArgs e) { _currentView = "todo"; RefreshTaskList(); }
    private void Completed_OnClick(object? sender, RoutedEventArgs e) { _currentView = "completed"; RefreshTaskList(); }
    private void Settings_OnClick(object? sender, RoutedEventArgs e) { _currentView = "settings"; LoadProviderSettings(); RefreshTaskList(); }

    private AiProvider SelectedProvider => ProviderCombo.SelectedIndex switch { 1 => AiProvider.Groq, 2 => AiProvider.OpenRouter, _ => AiProvider.OpenAI };
    private void Provider_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) { if (IsInitialized) LoadProviderSettings(); }

    private void LoadProviderSettings()
    {
        if (ProviderCombo is null || ApiKeyBox is null) return;
        var provider = SelectedProvider; ApiKeyBox.Text = provider switch { AiProvider.Groq => _aiSettings.GroqApiKey, AiProvider.OpenRouter => _aiSettings.OpenRouterApiKey, _ => _aiSettings.OpenAiApiKey };
        var model = GetStoredModel(provider); ModelComboBox.ItemsSource = string.IsNullOrWhiteSpace(model) ? Array.Empty<string>() : new[] { model }; ModelComboBox.SelectedItem = string.IsNullOrWhiteSpace(model) ? null : model;
        TemperatureBox.Value = (decimal)_aiSettings.Temperature; TemperatureBox.IsEnabled = provider != AiProvider.OpenAI; MaxOutputTokensBox.Value = _aiSettings.MaxOutputTokens; AiStatus.Text = $"Provider: {provider}. Fetch models to validate the key and load the current model catalog.";
    }

    private async void FetchModels_OnClick(object? sender, RoutedEventArgs e)
    {
        try { var provider = SelectedProvider; var key = ApiKeyBox.Text?.Trim() ?? string.Empty; AiStatus.Text = $"Fetching {provider} models..."; var models = await _aiClient.GetModelsAsync(provider, key); var ids = models.Select(m => m.Id).ToArray(); ModelComboBox.ItemsSource = ids; var current = GetStoredModel(provider); ModelComboBox.SelectedItem = ids.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : ids.FirstOrDefault(); AiStatus.Text = $"Loaded {ids.Length} models from {provider}."; }
        catch (Exception ex) { AiStatus.Text = $"Model fetch failed: {ex.Message}"; }
    }

    private async void SaveSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider; var key = ApiKeyBox.Text?.Trim() ?? string.Empty; var model = ModelComboBox.SelectedItem?.ToString() ?? string.Empty;
        switch (provider) { case AiProvider.Groq: _aiSettings.GroqApiKey = key; _aiSettings.GroqModel = model; break; case AiProvider.OpenRouter: _aiSettings.OpenRouterApiKey = key; _aiSettings.OpenRouterModel = model; break; default: _aiSettings.OpenAiApiKey = key; _aiSettings.OpenAiModel = model; break; }
        _aiSettings.Temperature = (double)(TemperatureBox.Value ?? 0.7m); _aiSettings.MaxOutputTokens = (int)(MaxOutputTokensBox.Value ?? 1024m);
        try { await _settingsStore.SaveAsync(_aiSettings); AiStatus.Text = $"Saved {provider} settings securely."; } catch (Exception ex) { AiStatus.Text = $"Save failed: {ex.Message}"; }
    }

    private async void TestAi_OnClick(object? sender, RoutedEventArgs e)
    {
        try { var provider = SelectedProvider; var key = ApiKeyBox.Text?.Trim() ?? string.Empty; var model = ModelComboBox.SelectedItem?.ToString() ?? GetStoredModel(provider); AiStatus.Text = $"Running {provider} test..."; AiOutput.Text = await _aiClient.GenerateAsync(provider, key, model, AiPromptBox.Text?.Trim() ?? string.Empty, (double)(TemperatureBox.Value ?? 0.7m), (int)(MaxOutputTokensBox.Value ?? 1024m)); AiStatus.Text = $"AI test completed using {model}."; }
        catch (Exception ex) { AiStatus.Text = $"AI test failed: {ex.Message}"; }
    }

    private string GetStoredModel(AiProvider provider) => provider switch { AiProvider.Groq => _aiSettings.GroqModel, AiProvider.OpenRouter => _aiSettings.OpenRouterModel, _ => _aiSettings.OpenAiModel };
}