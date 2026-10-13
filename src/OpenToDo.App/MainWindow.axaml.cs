using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OpenToDo.Core;
using OpenToDo.Data;
using OpenToDo.Plugins.Ai;

namespace OpenToDo.App;

public partial class MainWindow : Window
{
    private const string CheckIcon = "M5 12.5 L10 17.5 L19 7";
    private const string AlertIcon = "M12 4 L22 20 H2 Z M12 10 V14 M12 17 V17.5";
    private const int ChartDays = 14;
    private const double ChartMaxBarHeight = 112;

    private readonly JsonTaskRepository _repository =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));
    private readonly AiSettingsStore _settingsStore = new();
    private readonly AiClient _aiClient = new();
    private readonly List<TaskItem> _tasks = [];
    private readonly Dictionary<AiProvider, string[]> _modelCache = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _toastTimer = new();

    private AiSettings _aiSettings = new();
    private AiProvider _formProvider = AiProvider.OpenAI;
    private CancellationTokenSource? _aiCts;
    private string _currentView = "home";
    private DateTime _renderedDay = DateTime.Today;
    private bool _mutationInProgress;
    private bool _aiBusy;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        _ready = true;

        TemperatureBox.Value = 0.7m;
        MaxOutputTokensBox.Value = 1024m;
        UpdateTemperatureHint();

        // TextBox.TextChanged isn't available on every version; PropertyChanged always is.
        SearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                RefreshAll();
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastHost.IsVisible = false;
        };

        // Re-render when the date rolls over while the app is left open (stale "Today", streak and chart otherwise).
        _clock.Tick += (_, _) =>
        {
            if (DateTime.Today != _renderedDay)
                RefreshAll();
        };

        KeyDown += OnWindowKeyDown;
        Opened += async (_, _) => await StartupAsync();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _aiCts?.Cancel();
        };
    }

    // ------------------------------------------------------------------ startup

    private async Task StartupAsync()
    {
        // Settings and tasks load independently: a damaged settings file must never hide the user's tasks.
        try
        {
            _aiSettings = await _settingsStore.LoadAsync();
            if (_settingsStore.LoadWarning is { } warning)
                ShowToast(warning, isError: true);
        }
        catch (Exception ex)
        {
            _aiSettings = new AiSettings();
            ShowToast($"Couldn't load settings: {ex.Message}", isError: true);
        }

        _formProvider = SelectedProvider;
        LoadProviderSettings();

        try
        {
            await LoadTasksAsync();
            if (_repository.LastSkippedCount > 0)
                ShowToast($"{_repository.LastSkippedCount} task file(s) couldn't be read and were skipped.", isError: true);
        }
        catch (Exception ex)
        {
            RefreshAll();
            ShowToast($"Couldn't load tasks: {ex.Message}", isError: true);
        }

        _clock.Start();
    }

    private async Task LoadTasksAsync()
    {
        var loaded = await _repository.GetAllAsync();
        _tasks.Clear();
        _tasks.AddRange(loaded);
        RefreshAll();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.N && !e.Handled)
        {
            e.Handled = true;
            NewTask_OnClick(this, new RoutedEventArgs());
        }
    }

    // ------------------------------------------------------------------ rendering

    private void RefreshAll()
    {
        if (!_ready)
            return;

        var today = DateTime.Today;
        _renderedDay = today;

        PageTitle.Text = _currentView switch
        {
            "todo" => "To-do",
            "completed" => "Completed",
            "settings" => "Settings",
            _ => "Home"
        };

        PageSubtitle.Text = _currentView switch
        {
            "todo" => "Everything that still needs doing.",
            "completed" => "A look back at what you've finished.",
            "settings" => "AI providers and application configuration.",
            _ => today.ToString("dddd, MMMM d")
        };

        HomePanel.IsVisible = _currentView == "home";
        TodoPanel.IsVisible = _currentView == "todo";
        CompletedPanel.IsVisible = _currentView == "completed";
        SettingsPanel.IsVisible = _currentView == "settings";
        NewTaskButton.IsVisible = _currentView != "settings";

        SetClass(NavHome, "active", _currentView == "home");
        SetClass(NavTodo, "active", _currentView == "todo");
        SetClass(NavCompleted, "active", _currentView == "completed");
        SetClass(NavSettings, "active", _currentView == "settings");

        var sorted = TaskQueries.Sort(_tasks).ToArray();
        var active = sorted.Where(t => !t.IsCompleted).ToArray();
        var done = sorted
            .Where(t => t.IsCompleted)
            .OrderByDescending(t => TaskQueries.LastCompletion(t) ?? DateTime.MinValue)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        TodoBadgeText.Text = active.Length > 99 ? "99+" : active.Length.ToString();
        TodoBadge.IsVisible = active.Length > 0;
        DoneBadgeText.Text = done.Length > 99 ? "99+" : done.Length.ToString();
        DoneBadge.IsVisible = done.Length > 0;

        // Home
        var upNext = active.Take(5).Select(t => TaskRow.From(t, today)).ToArray();
        TaskList.ItemsSource = upNext;
        HomeEmpty.IsVisible = upNext.Length == 0;

        // To-do (with search)
        var query = SearchBox.Text?.Trim();
        var searching = !string.IsNullOrEmpty(query);
        var filtered = active.Where(t => TaskQueries.Matches(t, query)).Select(t => TaskRow.From(t, today)).ToArray();
        TodoList.ItemsSource = filtered;
        TodoEmpty.IsVisible = filtered.Length == 0;
        TodoEmptyTitle.Text = searching ? "No matches" : "Nothing to do";
        TodoEmptyText.Text = searching
            ? $"No open task matches “{query}”."
            : "Use quick add above or press Ctrl+N.";
        TodoCountLabel.Text = searching
            ? $"{filtered.Length} of {active.Length} open task{(active.Length == 1 ? "" : "s")}"
            : $"{active.Length} open task{(active.Length == 1 ? "" : "s")}";

        // Completed
        CompletedList.ItemsSource = done.Select(t => TaskRow.From(t, today)).ToArray();
        CompletedEmpty.IsVisible = done.Length == 0;
        DoneCountLabel.Text = $"{done.Length} completed task{(done.Length == 1 ? "" : "s")}";

        RefreshHome(active, today);
    }

    private void RefreshHome(TaskItem[] active, DateTime today)
    {
        var summary = TaskAnalytics.Summarize(_tasks, today);
        var overdue = active.Count(t => TaskQueries.IsOverdue(t, today));
        var dueToday = active.Count(t => TaskQueries.IsDueToday(t, today));

        var hour = DateTime.Now.Hour;
        HeroGreeting.Text = hour switch
        {
            < 5 => "Still up?",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };

        if (active.Length == 0)
        {
            HeroSummary.Text = summary.Today > 0
                ? $"Inbox zero. You wrapped up {summary.Today} task{(summary.Today == 1 ? "" : "s")} today."
                : "Nothing on your plate. Add a task to get started.";
        }
        else
        {
            var parts = new List<string> { $"{active.Length} open task{(active.Length == 1 ? "" : "s")}" };
            if (overdue > 0) parts.Add($"{overdue} overdue");
            if (dueToday > 0) parts.Add($"{dueToday} due today");
            HeroSummary.Text = string.Join("  ·  ", parts);
        }

        StreakText.Text = summary.Streak switch
        {
            0 => "No streak yet",
            1 => "1 day streak",
            _ => $"{summary.Streak} day streak"
        };
        StreakHint.Text = summary.Streak > 0 && summary.Today == 0
            ? "Finish a task today to keep it alive"
            : $"{summary.Total} completed all-time";

        CompletedDay.Text = summary.Today.ToString();
        CompletedWeek.Text = summary.Week.ToString();
        CompletedMonth.Text = summary.Month.ToString();
        CompletedYear.Text = summary.Year.ToString();

        var counts = TaskAnalytics.DailyCounts(_tasks, today, ChartDays);
        var max = Math.Max(1, counts.Max());
        var bars = counts.Select((count, i) =>
        {
            var date = today.AddDays(i - (ChartDays - 1));
            var height = count == 0 ? 3 : Math.Max(10, ChartMaxBarHeight * count / max);
            return new AnalyticsBar(
                $"{date:ddd}\n{date.Day}",
                date.ToString("dddd, MMM d"),
                count,
                height,
                date == today);
        }).ToArray();

        DailyChart.ItemsSource = bars;
        DailyNumbers.ItemsSource = Enumerable.Reverse(bars).ToArray();
    }

    private static void SetClass(StyledElement element, string name, bool on)
    {
        if (on)
        {
            if (!element.Classes.Contains(name))
                element.Classes.Add(name);
        }
        else
        {
            element.Classes.Remove(name);
        }
    }

    private void ShowToast(string message, bool isError = false)
    {
        ToastText.Text = message;
        ToastIcon.Data = Geometry.Parse(isError ? AlertIcon : CheckIcon);
        SetClass(ToastHost, "error", isError);
        ToastHost.IsVisible = true;

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(isError ? 7 : 3);
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ chart mode

    private void GraphMode_OnClick(object? sender, RoutedEventArgs e) => SetChartMode(graph: true);
    private void NumberMode_OnClick(object? sender, RoutedEventArgs e) => SetChartMode(graph: false);

    private void SetChartMode(bool graph)
    {
        DailyChart.IsVisible = graph;
        DailyNumbers.IsVisible = !graph;
        SetClass(GraphModeButton, "active", graph);
        SetClass(NumberModeButton, "active", !graph);
        ChartSubtitle.Text = graph ? $"Last {ChartDays} days" : $"Last {ChartDays} days · exact counts";
    }

    // ------------------------------------------------------------------ navigation

    private void Home_OnClick(object? sender, RoutedEventArgs e) => Navigate("home");
    private void Todo_OnClick(object? sender, RoutedEventArgs e) => Navigate("todo");
    private void Completed_OnClick(object? sender, RoutedEventArgs e) => Navigate("completed");
    private void Settings_OnClick(object? sender, RoutedEventArgs e) => Navigate("settings");

    private void Navigate(string view)
    {
        _currentView = view;
        RefreshAll();
    }

    // ------------------------------------------------------------------ task actions

    private async void NewTask_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_mutationInProgress)
                return;

            var blank = new TaskItem(Guid.NewGuid().ToString("N"), string.Empty, CreatedAt: DateTimeOffset.Now);
            var result = await new TaskEditorWindow(blank, isNew: true).ShowDialog<TaskItem?>(this);
            if (result is null)
                return;

            await RunMutationAsync(async () =>
            {
                await _repository.SaveAsync(result);
                _currentView = "todo";
                SearchBox.Text = string.Empty; // otherwise a stale filter could hide the task that was just added
            }, "Task added");
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't open the editor: {ex.Message}", isError: true);
        }
    }

    private async void QuickAdd_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var title = QuickAddBox.Text?.Trim();
        if (string.IsNullOrEmpty(title) || _mutationInProgress)
            return;

        if (title.Length > TaskLimits.MaxTitleLength)
            title = title[..TaskLimits.MaxTitleLength];

        var task = new TaskItem(Guid.NewGuid().ToString("N"), title, CreatedAt: DateTimeOffset.Now);
        var saved = await RunMutationAsync(() => _repository.SaveAsync(task), "Task added");
        if (saved)
        {
            QuickAddBox.Text = string.Empty;
            SearchBox.Text = string.Empty;
        }
        QuickAddBox.Focus();
    }

    private async void Edit_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_mutationInProgress || sender is not Button { DataContext: TaskRow row })
                return;

            var result = await new TaskEditorWindow(row.Task).ShowDialog<TaskItem?>(this);
            if (result is null || result == row.Task)
                return;

            await RunMutationAsync(() => _repository.SaveAsync(result), "Changes saved");
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't open the editor: {ex.Message}", isError: true);
        }
    }

    private async void ToggleCompleted_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskRow row })
            return;

        var updated = TaskQueries.Complete(row.Task, DateTimeOffset.Now);
        await RunMutationAsync(() => _repository.SaveAsync(updated), "Task completed");
    }

    private async void Restore_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskRow row } || !row.Task.IsCompleted)
            return;

        var updated = TaskQueries.Restore(row.Task);
        await RunMutationAsync(() => _repository.SaveAsync(updated), "Moved back to to-do");
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_mutationInProgress || sender is not Button { DataContext: TaskRow row })
                return;

            var confirmed = await new ConfirmDialog(
                "Delete task?",
                $"“{row.Title}” will be permanently removed, along with its completion history. This can't be undone.",
                "Delete").ShowDialog<bool>(this);

            if (!confirmed)
                return;

            await RunMutationAsync(() => _repository.DeleteAsync(row.Task.Id), "Task deleted");
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't delete: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Runs a disk mutation, then always reloads from disk so the screen reflects what is really stored,
    /// even when the mutation failed. Failures surface as a toast instead of disappearing.
    /// </summary>
    private async Task<bool> RunMutationAsync(Func<Task> mutation, string successMessage)
    {
        if (_mutationInProgress)
            return false;

        _mutationInProgress = true;
        var success = false;
        try
        {
            await mutation();
            success = true;
        }
        catch (Exception ex)
        {
            ShowToast($"Operation failed: {ex.Message}", isError: true);
        }
        finally
        {
            try
            {
                await LoadTasksAsync();
            }
            catch (Exception ex)
            {
                ShowToast($"Couldn't refresh tasks: {ex.Message}", isError: true);
            }

            _mutationInProgress = false;
        }

        if (success)
            ShowToast(successMessage);

        return success;
    }

    // ------------------------------------------------------------------ AI settings

    private AiProvider SelectedProvider => ProviderCombo.SelectedIndex switch
    {
        1 => AiProvider.Groq,
        2 => AiProvider.OpenRouter,
        _ => AiProvider.OpenAI
    };

    private void Provider_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ApiKeyBox is null)
            return;

        // Keep whatever was typed for the provider we're leaving; switching must never discard edits.
        CommitFormToSettings(_formProvider);
        _formProvider = SelectedProvider;
        LoadProviderSettings();
    }

    private void CommitFormToSettings(AiProvider provider)
    {
        var key = ApiKeyBox.Text?.Trim() ?? string.Empty;
        var model = ModelComboBox.SelectedItem?.ToString() ?? GetStoredModel(provider);

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
        _aiSettings.Normalize();
    }

    private void LoadProviderSettings()
    {
        var provider = SelectedProvider;
        ApiKeyBox.Text = provider switch
        {
            AiProvider.Groq => _aiSettings.GroqApiKey,
            AiProvider.OpenRouter => _aiSettings.OpenRouterApiKey,
            _ => _aiSettings.OpenAiApiKey
        };

        var model = GetStoredModel(provider);
        var items = new List<string>(_modelCache.TryGetValue(provider, out var cached) ? cached : Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(model) && !items.Contains(model, StringComparer.OrdinalIgnoreCase))
            items.Insert(0, model);

        ModelComboBox.ItemsSource = items;
        ModelComboBox.SelectedItem = string.IsNullOrWhiteSpace(model)
            ? null
            : items.First(i => string.Equals(i, model, StringComparison.OrdinalIgnoreCase));

        TemperatureBox.Value = (decimal)Math.Clamp(_aiSettings.Temperature, 0, 2);
        MaxOutputTokensBox.Value = Math.Clamp(_aiSettings.MaxOutputTokens, 1, AiSettings.MaxTokenLimit);
        UpdateTemperatureHint();
        SetStatus($"{provider}: fetch models to validate the key and load the current model list.");
    }

    private void UpdateTemperatureHint()
    {
        var openAi = SelectedProvider == AiProvider.OpenAI;
        TemperatureBox.IsEnabled = !openAi;
        TemperatureHint.Text = openAi ? "Not used by OpenAI's Responses API." : string.Empty;
    }

    private void ToggleKey_OnClick(object? sender, RoutedEventArgs e) =>
        ApiKeyBox.PasswordChar = ApiKeyBox.PasswordChar == '\0' ? '●' : '\0';

    private string GetStoredModel(AiProvider provider) => provider switch
    {
        AiProvider.Groq => _aiSettings.GroqModel,
        AiProvider.OpenRouter => _aiSettings.OpenRouterModel,
        _ => _aiSettings.OpenAiModel
    };

    private void SetStatus(string text, string kind = "info")
    {
        AiStatus.Text = text;
        SetClass(AiStatus, "ok", kind == "ok");
        SetClass(AiStatus, "error", kind == "error");
    }

    private void SetAiBusy(bool busy)
    {
        _aiBusy = busy;
        FetchButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        ProviderCombo.IsEnabled = !busy;
        CancelAiButton.IsVisible = busy;
    }

    private void CancelAi_OnClick(object? sender, RoutedEventArgs e) => _aiCts?.Cancel();

    /// <summary>Runs a network call with busy-state, cancellation and readable errors. Only one runs at a time.</summary>
    private async Task RunAiAsync(string startMessage, Func<CancellationToken, Task> work)
    {
        if (_aiBusy)
            return;

        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        SetAiBusy(true);
        SetStatus(startMessage);

        try
        {
            await work(_aiCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, "error");
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private async void FetchModels_OnClick(object? sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        var key = ApiKeyBox.Text?.Trim() ?? string.Empty;

        await RunAiAsync($"Fetching {provider} models…", async ct =>
        {
            var models = await _aiClient.GetModelsAsync(provider, key, ct);
            var ids = models.Select(m => m.Id).ToArray();
            _modelCache[provider] = ids;

            if (ids.Length == 0)
            {
                SetStatus($"{provider} returned no models for this key.", "error");
                return;
            }

            // The user may have switched provider while this was in flight; only touch the form if still relevant.
            if (_formProvider == provider)
            {
                var current = ModelComboBox.SelectedItem?.ToString() ?? GetStoredModel(provider);
                ModelComboBox.ItemsSource = ids;
                ModelComboBox.SelectedItem = ids.FirstOrDefault(i => string.Equals(i, current, StringComparison.OrdinalIgnoreCase))
                                             ?? ids[0];
            }

            SetStatus($"Loaded {ids.Length} models from {provider}.", "ok");
        });
    }

    private async void SaveSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        CommitFormToSettings(_formProvider);

        if (_aiBusy)
            return;

        SetAiBusy(true);
        try
        {
            var result = await _settingsStore.SaveAsync(_aiSettings);
            if (result.SecretsStored)
            {
                SetStatus($"Saved {_formProvider} settings. Keys are in the OS credential store.", "ok");
                ShowToast("Settings saved");
            }
            else
            {
                SetStatus(result.Warning ?? "Settings saved, but keys could not be stored securely.", "error");
                ShowToast("Saved, but keys weren't stored securely", isError: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}", "error");
        }
        finally
        {
            SetAiBusy(false);
        }
    }

    private async void TestAi_OnClick(object? sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        var key = ApiKeyBox.Text?.Trim() ?? string.Empty;
        var model = ModelComboBox.SelectedItem?.ToString() ?? GetStoredModel(provider);
        var prompt = AiPromptBox.Text?.Trim() ?? string.Empty;
        var temperature = (double)(TemperatureBox.Value ?? 0.7m);
        var tokens = (int)(MaxOutputTokensBox.Value ?? 1024m);

        await RunAiAsync($"Running {provider} test…", async ct =>
        {
            AiOutput.Text = "Thinking…";
            try
            {
                var output = await _aiClient.GenerateAsync(provider, key, model, prompt, temperature, tokens, ct);
                AiOutput.Text = output;
                SetStatus($"Test completed using {model}.", "ok");
            }
            catch
            {
                AiOutput.Text = "No output.";
                throw;
            }
        });
    }
}
