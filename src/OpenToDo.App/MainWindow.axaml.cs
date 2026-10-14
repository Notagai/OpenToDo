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

    /// <summary>Slides shown after the username step of the first-run tour.</summary>
    private static readonly (string Icon, string Title, string Body)[] TutorialPages =
    {
        ("M3 11 L12 3 L21 11 M5 9.5 V20 H10 V14 H14 V20 H19 V9.5",
            "Your dashboard",
            "Home shows your streak, completions for today, this week, this month and this year, a 14-day activity chart, and the five tasks due soonest."),
        ("M9 6 H20 M9 12 H20 M9 18 H20 M4 6 L5 7 L7 5 M4 12 L5 13 L7 11 M4 18 L5 19 L7 17",
            "Capture tasks fast",
            "Use the quick-add bar on To-do, or press Ctrl+N anywhere for the full editor. Add notes and due dates, then search to find anything instantly."),
        ("M12 3 L14 10 L21 12 L14 14 L12 21 L10 14 L3 12 L10 10 Z",
            "AI at your side",
            "Connect a provider in Settings, then open the Assistant. Ask it to add tasks, mark them done or reschedule them — and get a progress report right on Home."),
        ("M5 20 V12 M12 20 V5 M19 20 V9",
            "See your momentum",
            "The activity chart toggles between a graph and exact numbers. Finish at least one task a day to grow your streak — the flame lights up as it climbs."),
        ("M4 7 H20 M4 17 H20 M9 4 V10 M15 14 V20",
            "Make it yours",
            "Change your username, connect AI providers and replay this tour any time from Settings."),
        ("M5 12.5 L10 17.5 L19 7",
            "You're all set",
            "Everything is stored locally on this device as plain files — no account, no cloud. Add your first task and get going."),
    };

    private readonly JsonTaskRepository _repository =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));
    private readonly AiSettingsStore _settingsStore = new();
    private readonly AiClient _aiClient = new();
    private readonly AiAssistant _assistant;
    private readonly List<TaskItem> _tasks = [];
    private readonly List<AiPromptMessage> _chatHistory = [];
    private readonly Dictionary<AiProvider, string[]> _modelCache = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _toastTimer = new();

    private AiSettings _aiSettings = new();
    private AiProvider _formProvider = AiProvider.OpenAI;
    private CancellationTokenSource? _aiCts;
    private CancellationTokenSource? _chatCts;
    private CancellationTokenSource? _insightCts;
    private string _currentView = "home";
    private DateTime _renderedDay = DateTime.Today;
    private bool _mutationInProgress;
    private bool _aiBusy;
    private bool _chatBusy;
    private bool _insightBusy;
    private bool _ready;
    private bool _onboardReplay;
    private int _onboardStep;
    private string _pendingUsername = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _assistant = new AiAssistant(_aiClient);
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

        // Bounds is a plain AvaloniaProperty, so observing it works on every Avalonia version.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                ApplyResponsiveLayout(Bounds.Width);
        };

        KeyDown += OnWindowKeyDown;
        Opened += async (_, _) => await StartupAsync();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _aiCts?.Cancel();
            _chatCts?.Cancel();
            _insightCts?.Cancel();
        };
    }

    // ------------------------------------------------------------------ startup

    private async Task StartupAsync()
    {
        var settingsExisted = _settingsStore.Exists;

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

        UsernameBox.Text = _aiSettings.Username;
        _formProvider = SelectedProvider;
        LoadProviderSettings();

        // First run: create settings.json straight away so it exists even if the app is closed mid-onboarding.
        if (!settingsExisted)
        {
            try
            {
                await _settingsStore.SaveAsync(_aiSettings);
            }
            catch (Exception ex)
            {
                ShowToast($"Couldn't create settings.json: {ex.Message}", isError: true);
            }
        }

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

        UpdateAssistantStatus();

        if (!_aiSettings.OnboardingCompleted || string.IsNullOrWhiteSpace(_aiSettings.Username))
            ShowOnboarding(replay: false);

        ApplyResponsiveLayout(Bounds.Width);
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

    // ------------------------------------------------------------------ responsive layout

    /// <summary>Keeps the dashboard coherent everywhere from 720p up to 8K: fluid margins, a capped content
    /// width that stays centred, and a stat grid that drops to two columns on narrow windows.</summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0)
            return;

        var compact = width < 1120;
        var margin = compact ? 22.0 : 44.0;

        HeaderBar.Margin = new Thickness(margin, compact ? 20 : 34, margin, 0);
        ContentRoot.Margin = new Thickness(margin, compact ? 18 : 26, margin, compact ? 26 : 44);

        AiSidebar.Width = width < 1240 ? 320 : 400;

        // Explicit width + centered alignment keeps the dashboard column centred on 8K displays and
        // still filling the window at 720p, instead of leaving a lopsided gap on the right.
        var reserved = 252 + (AiSidebar.IsVisible ? AiSidebar.Width : 0) + (2 * margin);
        var contentWidth = Math.Clamp(width - reserved, 200, 1600);
        ContentRoot.Width = contentWidth;
        HeaderBar.Width = contentWidth; // keep the page header aligned with the content column

        var columns = width < 1040 ? 2 : 4;
        if (StatGrid.Columns != columns)
            StatGrid.Columns = columns;
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
            "settings" => "Your profile, AI providers and application configuration.",
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
        var greeting = hour switch
        {
            < 5 => "Still up?",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };

        var name = _aiSettings.Username?.Trim();
        var hasName = !string.IsNullOrEmpty(name);
        HeroGreeting.Text = hasName ? $"{greeting}, {name}" : greeting;
        Title = hasName ? $"OpenToDo — {name}" : "OpenToDo";

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

        var streak = summary.Streak;
        StreakText.Text = streak switch
        {
            0 => "No streak yet",
            1 => "1 day streak",
            _ => $"{streak} day streak"
        };
        SetClass(StreakChip, "lit", streak > 0);
        SetClass(StreakFlame, "lit", streak > 0);
        StreakHint.Text = streak > 0 && summary.Today == 0
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

    // ------------------------------------------------------------------ onboarding

    private void ShowOnboarding(bool replay)
    {
        _onboardReplay = replay;
        _onboardStep = replay ? 1 : 0;
        _pendingUsername = _aiSettings.Username;
        OnboardUsernameBox.Text = _aiSettings.Username;
        OnboardError.IsVisible = false;
        OnboardingOverlay.IsVisible = true;
        RenderOnboarding();

        if (!replay)
            OnboardUsernameBox.Focus();
    }

    private void RenderOnboarding()
    {
        var total = TutorialPages.Length + 1;
        var onUsername = _onboardStep == 0;

        OnboardUsernameStep.IsVisible = onUsername;
        OnboardTutorialStep.IsVisible = !onUsername;
        OnboardStepLabel.Text = $"Step {_onboardStep + 1} of {total}";

        if (!onUsername)
        {
            var page = TutorialPages[_onboardStep - 1];
            OnboardIcon.Data = Geometry.Parse(page.Icon);
            OnboardTitle.Text = page.Title;
            OnboardBody.Text = page.Body;
        }

        OnboardBackButton.IsVisible = _onboardStep > 0;
        OnboardSkipButton.IsVisible = !_onboardReplay;
        OnboardNextButton.Content = _onboardStep >= total - 1 ? "Get started" : "Continue";
        OnboardError.IsVisible = false;
    }

    private void OnboardNext_OnClick(object? sender, RoutedEventArgs e) => AdvanceOnboarding();

    private void OnboardUsername_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        AdvanceOnboarding();
    }

    private void AdvanceOnboarding()
    {
        if (_onboardStep == 0)
        {
            var name = OnboardUsernameBox.Text?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                OnboardError.Text = "Please enter a name so we know what to call you.";
                OnboardError.IsVisible = true;
                OnboardUsernameBox.Focus();
                return;
            }

            _pendingUsername = name.Length > AiSettings.MaxUsernameLength ? name[..AiSettings.MaxUsernameLength] : name;
        }

        var total = TutorialPages.Length + 1;
        if (_onboardStep >= total - 1)
        {
            FinishOnboarding();
            return;
        }

        _onboardStep++;
        RenderOnboarding();
    }

    private void OnboardBack_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_onboardStep == 0)
            return;

        _onboardStep--;
        RenderOnboarding();
    }

    private void OnboardSkip_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_onboardStep == 0)
        {
            var name = OnboardUsernameBox.Text?.Trim() ?? string.Empty;
            if (name.Length > 0)
                _pendingUsername = name.Length > AiSettings.MaxUsernameLength ? name[..AiSettings.MaxUsernameLength] : name;
        }

        FinishOnboarding();
    }

    private async void FinishOnboarding()
    {
        OnboardingOverlay.IsVisible = false;

        _aiSettings.Username = string.IsNullOrWhiteSpace(_pendingUsername) ? _aiSettings.Username : _pendingUsername;
        _aiSettings.OnboardingCompleted = true;
        UsernameBox.Text = _aiSettings.Username;

        try
        {
            await _settingsStore.SaveAsync(_aiSettings);
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't save your settings: {ex.Message}", isError: true);
        }

        RefreshAll();
        UpdateAssistantStatus();

        if (!string.IsNullOrWhiteSpace(_aiSettings.Username))
            ShowToast($"You're all set, {_aiSettings.Username}!");
    }

    private void ReplayTutorial_OnClick(object? sender, RoutedEventArgs e) => ShowOnboarding(replay: true);

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

    private string KeyFor(AiProvider provider) => provider switch
    {
        AiProvider.Groq => _aiSettings.GroqApiKey,
        AiProvider.OpenRouter => _aiSettings.OpenRouterApiKey,
        _ => _aiSettings.OpenAiApiKey
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
        ApiKeyBox.Text = KeyFor(provider);

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
        UpdateAssistantStatus();
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
            UpdateAssistantStatus();
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
            UpdateAssistantStatus();
        }
    }

    private async void SaveProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = UsernameBox.Text?.Trim() ?? string.Empty;
        if (name.Length > AiSettings.MaxUsernameLength)
            name = name[..AiSettings.MaxUsernameLength];

        _aiSettings.Username = name;

        try
        {
            await _settingsStore.SaveAsync(_aiSettings);
            UsernameBox.Text = name;
            RefreshAll();
            UpdateAssistantStatus();
            ShowToast(string.IsNullOrEmpty(name) ? "Profile saved" : $"Profile saved — hello, {name}!");
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't save your profile: {ex.Message}", isError: true);
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

    // ------------------------------------------------------------------ AI assistant sidebar

    private void ToggleAssistant_OnClick(object? sender, RoutedEventArgs e)
    {
        if (AiSidebar.IsVisible)
            CloseAssistant();
        else
            OpenAssistant();
    }

    private void CloseAssistant_OnClick(object? sender, RoutedEventArgs e) => CloseAssistant();

    private void OpenAssistant()
    {
        AiSidebar.IsVisible = true;
        SetClass(NavAssistant, "active", true);
        ApplyResponsiveLayout(Bounds.Width);
        UpdateAssistantStatus();
        RefreshChatHint();

        if (ChatList.Children.Count == 0)
            AddChatBubble(AiChatMessage.Assistant(
                "Hi! I can add tasks, mark them done, reschedule or clean them up. Try “add pay rent tomorrow” or “mark the report as done”."));

        ChatInput.Focus();
    }

    private void CloseAssistant()
    {
        AiSidebar.IsVisible = false;
        SetClass(NavAssistant, "active", false);
        ApplyResponsiveLayout(Bounds.Width);
    }

    private void UpdateAssistantStatus()
    {
        var provider = SelectedProvider;
        var model = GetStoredModel(provider);
        var configured = !string.IsNullOrWhiteSpace(KeyFor(provider)) && !string.IsNullOrWhiteSpace(model);
        AiSidebarStatus.Text = configured ? $"{provider} · {model}" : "Not configured";
        if (ChatHint is not null)
            RefreshChatHint();
    }

    private void RefreshChatHint() =>
        ChatHint.Text = TryGetAiConfig(out _, out _, out _, out var error) ? "Enter to send" : error;

    private bool TryGetAiConfig(out AiProvider provider, out string key, out string model, out string error)
    {
        CommitFormToSettings(_formProvider);
        provider = SelectedProvider;
        key = KeyFor(provider);
        model = GetStoredModel(provider);

        if (string.IsNullOrWhiteSpace(key))
        {
            error = $"Add a {provider} API key in Settings to use the assistant.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            error = $"Pick a {provider} model in Settings (use “Fetch models”).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ChatInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            _ = SendChatAsync();
        }
    }

    private void ChatSend_OnClick(object? sender, RoutedEventArgs e) => _ = SendChatAsync();

    private async Task SendChatAsync()
    {
        if (_chatBusy)
            return;

        var text = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        if (!TryGetAiConfig(out var provider, out var key, out var model, out var error))
        {
            AddChatBubble(AiChatMessage.Note(error));
            return;
        }

        var history = _chatHistory.ToArray();
        ChatInput.Text = string.Empty;
        AddChatBubble(AiChatMessage.User(text));
        _chatHistory.Add(new AiPromptMessage("user", text));

        _chatBusy = true;
        ChatSendButton.IsEnabled = false;
        ChatInput.IsEnabled = false;
        AiSidebarStatus.Text = "Thinking…";

        _chatCts?.Dispose();
        _chatCts = new CancellationTokenSource();

        try
        {
            var context = TaskInsights.BuildContext(_tasks, DateTime.Today, _aiSettings.Username);
            var tokens = Math.Clamp(_aiSettings.MaxOutputTokens, 128, 1500);
            var response = await _assistant.SendAsync(
                provider, key, model, _aiSettings.Temperature, tokens,
                context, history, text, _chatCts.Token);

            AddChatBubble(AiChatMessage.Assistant(response.Message));
            _chatHistory.Add(new AiPromptMessage("assistant", response.Message));

            await ApplyAssistantCommandsAsync(response.Commands);
        }
        catch (OperationCanceledException)
        {
            AddChatBubble(AiChatMessage.Note("Request cancelled."));
        }
        catch (Exception ex)
        {
            AddChatBubble(AiChatMessage.Note($"Couldn't reach the AI: {ex.Message}"));
        }
        finally
        {
            _chatBusy = false;
            ChatSendButton.IsEnabled = true;
            ChatInput.IsEnabled = true;
            UpdateAssistantStatus();
            ChatInput.Focus();
        }
    }

    /// <summary>Applies the task changes the assistant asked for, persisting each one and reporting back in the chat.</summary>
    private async Task ApplyAssistantCommandsAsync(IReadOnlyList<TaskCommand> commands)
    {
        if (commands is null || commands.Count == 0)
            return;

        var today = DateTime.Today;
        var now = DateTimeOffset.Now;
        var working = _tasks.ToList();
        var applied = 0;

        foreach (var command in commands)
        {
            var outcome = TaskCommandPlanner.Apply(command, working, today, now);

            if (outcome.Item is not null)
            {
                await _repository.SaveAsync(outcome.Item);
                var index = working.FindIndex(t => t.Id == outcome.Item.Id);
                if (index >= 0)
                    working[index] = outcome.Item;
                else
                    working.Add(outcome.Item);

                applied++;
                AddChatBubble(AiChatMessage.Note($"✓ {outcome.Message}"));
            }
            else if (outcome.DeleteId is not null)
            {
                await _repository.DeleteAsync(outcome.DeleteId);
                working.RemoveAll(t => t.Id == outcome.DeleteId);
                applied++;
                AddChatBubble(AiChatMessage.Note($"✓ {outcome.Message}"));
            }
            else
            {
                AddChatBubble(AiChatMessage.Note(outcome.Message));
            }
        }

        if (applied > 0)
        {
            await LoadTasksAsync();
            ShowToast($"{applied} task{(applied == 1 ? "" : "s")} updated by the assistant");
        }
    }

    private void AddChatBubble(AiChatMessage message)
    {
        ChatList.Children.Add(BuildChatBubble(message));

        // Scroll to the newest bubble once layout has settled (the setter clamps to the real extent).
        Dispatcher.UIThread.Post(
            () => ChatScroll.Offset = new Vector(ChatScroll.Offset.X, ChatScroll.Extent.Height),
            DispatcherPriority.Background);
    }

    private Control BuildChatBubble(AiChatMessage message)
    {
        var border = new Border
        {
            HorizontalAlignment = message.Alignment,
            MaxWidth = 300,
            Child = new SelectableTextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = message.IsNote ? 12 : 14,
                Foreground = Resolve(message.IsNote ? "MutedBrush" : "TextBrush")
            }
        };

        border.Classes.Add("chat");
        if (message.IsUser)
            border.Classes.Add("user");
        if (message.IsNote)
            border.Classes.Add("note");

        return border;
    }

    private static IBrush Resolve(string key)
    {
        if (Application.Current is { } app &&
            app.Resources.TryGetResource(key, null, out var value) &&
            value is IBrush brush)
            return brush;

        return Brushes.White;
    }

    // ------------------------------------------------------------------ home AI insight

    private async void HomeInsight_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_insightBusy)
            return;

        if (!TryGetAiConfig(out var provider, out var key, out var model, out var error))
        {
            HomeInsightText.Text = error;
            return;
        }

        _insightBusy = true;
        HomeInsightButton.IsEnabled = false;
        HomeInsightText.Text = "Looking over your tasks…";

        _insightCts?.Dispose();
        _insightCts = new CancellationTokenSource();

        try
        {
            var context = TaskInsights.BuildContext(_tasks, DateTime.Today, _aiSettings.Username);
            var messages = new[]
            {
                new AiPromptMessage("system", context),
                new AiPromptMessage("user",
                    "In two or three short sentences, summarize how this person is doing with their tasks. " +
                    "Mention their streak and any overdue items, be encouraging but honest, and suggest one concrete next step. " +
                    "Reply in plain sentences — no lists, headings or JSON.")
            };

            var raw = await _aiClient.GenerateChatAsync(
                provider, key, model, messages, _aiSettings.Temperature,
                Math.Clamp(_aiSettings.MaxOutputTokens, 128, 400), _insightCts.Token);

            HomeInsightText.Text = string.IsNullOrWhiteSpace(raw)
                ? "No summary came back — give it another try."
                : raw.Trim();
        }
        catch (OperationCanceledException)
        {
            HomeInsightText.Text = "Summary cancelled.";
        }
        catch (Exception ex)
        {
            HomeInsightText.Text = $"Couldn't reach the AI: {ex.Message}";
        }
        finally
        {
            _insightBusy = false;
            HomeInsightButton.IsEnabled = true;
        }
    }
}
