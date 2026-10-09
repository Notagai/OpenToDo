using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenToDo.Core;
using OpenToDo.Data;
using OpenToDo.Plugins.Ai;

namespace OpenToDo.App;

public partial class MainWindow : Window
{
    private readonly JsonTaskRepository _repository = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "tasks"));
    private readonly AiSettingsStore _settingsStore = new();
    private readonly string _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenToDo", "config.json");
    private string _username = string.Empty;
    private readonly AiClient _aiClient = new();
    private readonly List<TaskItem> _tasks = [];
    private AiSettings _aiSettings = new();
    private string _currentView = "home";
    private bool _mutationInProgress;
    private string _chatTranscript = string.Empty;
    private TaskItem? _draggedTask;
    private double _dragStartY;
    private int _dragTargetIndex = -1;
    private Border? _draggedRow;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            try
            {
                await LoadProfileAsync();
                _aiSettings = await _settingsStore.LoadAsync();
                LoadProviderSettings();
                await LoadTasksAsync();
            }
            catch (Exception ex)
            {
                AiStatus.Text = $"Startup warning: {ex.Message}";
            }
        };
    }

    private async Task LoadProfileAsync()
    {
        if (!File.Exists(_configPath))
        {
            MainContent.IsVisible = false;
            SetupPanel.IsVisible = true;
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));
            if (document.RootElement.TryGetProperty("username", out var name))
                _username = name.GetString()?.Trim() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            _username = string.Empty;
        }

        var hasProfile = !string.IsNullOrWhiteSpace(_username);
        MainContent.IsVisible = hasProfile;
        SetupPanel.IsVisible = !hasProfile;
        if (hasProfile)
            UsernameBox.Text = _username;
    }

    private async void SaveProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        _username = UsernameBox.Text?.Trim() ?? string.Empty;
        UsernameBox.Classes.Set("invalid", string.IsNullOrWhiteSpace(_username));
        UsernameError.IsVisible = string.IsNullOrWhiteSpace(_username);
        if (string.IsNullOrWhiteSpace(_username))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            await File.WriteAllTextAsync(_configPath, System.Text.Json.JsonSerializer.Serialize(new { username = _username }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            SetupPanel.IsVisible = false;
            MainContent.IsVisible = true;
            RefreshTaskList();
        }
        catch (Exception ex)
        {
            UsernameError.Text = $"Couldn't save your profile: {ex.Message}";
            UsernameError.IsVisible = true;
        }
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
            _ => string.IsNullOrWhiteSpace(_username) ? "Your productivity overview." : $"Welcome back, {_username}."
        };

        HomePanel.IsVisible = _currentView == "home";
        TodoPanel.IsVisible = _currentView == "todo";
        CompletedPanel.IsVisible = _currentView == "completed";
        SettingsPanel.IsVisible = _currentView == "settings";

        TodoList.ItemsSource = _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.SortOrder).ThenBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        CompletedList.ItemsSource = _tasks.Where(t => t.IsCompleted).ToArray();
        TaskList.ItemsSource = _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.SortOrder).ThenBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).Take(5).ToArray();

        if (_currentView == "home")
            RefreshAnalytics();
    }

    private void RefreshAnalytics()
    {
        var today = DateTime.Now.Date;
        var history = _tasks
            .SelectMany(t => t.CompletionHistory ?? Array.Empty<DateTimeOffset>())
            .Select(d => d.LocalDateTime.Date)
            .Where(d => d <= today)
            .ToArray();

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
            .Select(date => new AnalyticsBar(date.ToString("ddd"), history.Count(d => d == date), 0))
            .ToArray();

        var max = Math.Max(1, daily.Max(x => x.Count));
        var bars = daily.Select(x => x with { Width = 760d * x.Count / max }).ToArray();
        DailyChart.ItemsSource = bars;
        DailyNumbers.ItemsSource = bars;
    }

    private void GraphMode_OnClick(object? sender, RoutedEventArgs e)
    {
        GraphPanel.IsVisible = true;
        NumberPanel.IsVisible = false;
        ChartSubtitle.Text = "Last 14 days";
    }

    private void NumberMode_OnClick(object? sender, RoutedEventArgs e)
    {
        GraphPanel.IsVisible = false;
        NumberPanel.IsVisible = true;
        ChartSubtitle.Text = "Last 14 days · exact counts";
    }

    private async void NewTask_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress)
            return;

        var editor = new TaskEditorWindow(new TaskItem(Guid.NewGuid().ToString("N"), string.Empty), true);
        var result = await editor.ShowDialog<TaskItem?>(this);

        if (result is null)
            return;

        await RunMutationAsync(async () =>
        {
            var nextOrder = _tasks.Where(t => !t.IsCompleted).Select(t => t.SortOrder).DefaultIfEmpty(-1).Max() + 1;
            await _repository.SaveAsync(result with { SortOrder = nextOrder });
            _currentView = "todo";
            await LoadTasksAsync();
        });
    }

    private async void Edit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskItem task })
            return;

        var editor = new TaskEditorWindow(task);
        var result = await editor.ShowDialog<TaskItem?>(this);

        if (result is null)
            return;

        await RunMutationAsync(async () =>
        {
            await _repository.SaveAsync(result);
            await LoadTasksAsync();
        });
    }

    private async void ToggleCompleted_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskItem task })
            return;

        await RunMutationAsync(async () =>
        {
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
        });
    }

    private async void Restore_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskItem task } || !task.IsCompleted)
            return;

        await RunMutationAsync(async () =>
        {
            await _repository.SaveAsync(task with { IsCompleted = false });
            await LoadTasksAsync();
        });
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button button || button.DataContext is not TaskItem task)
            return;

        if (!string.Equals(button.Content?.ToString(), "Are you sure?", StringComparison.Ordinal))
        {
            button.Content = "Are you sure?";
            button.MinWidth = 104;
            return;
        }

        var row = button.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(candidate => candidate.Classes.Contains("task-row"));
        if (row is not null)
        {
            row.Classes.Set("deleting", true);
            row.RenderTransform = new TranslateTransform(520, 0);
            row.Opacity = 0;
            await Task.Delay(240);
        }

        await RunMutationAsync(async () =>
        {
            await _repository.DeleteAsync(task.Id);
            await LoadTasksAsync();
        });
    }

    private async Task RunMutationAsync(Func<Task> mutation)
    {
        if (_mutationInProgress)
            return;

        _mutationInProgress = true;
        try
        {
            await mutation();
        }
        catch (Exception ex)
        {
            AiStatus.Text = $"Operation failed: {ex.Message}";
        }
        finally
        {
            _mutationInProgress = false;
        }
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

    private void Settings_OnClick(object? sender, RoutedEventArgs e)
    {
        _currentView = "settings";
        LoadProviderSettings();
        RefreshTaskList();
    }

    private AiProvider SelectedProvider => ProviderCombo.SelectedIndex switch
    {
        1 => AiProvider.Groq,
        2 => AiProvider.OpenRouter,
        _ => AiProvider.OpenAI
    };

    private void Provider_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
            LoadProviderSettings();
    }

    private void LoadProviderSettings()
    {
        if (ProviderCombo is null || ApiKeyBox is null)
            return;

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
        TemperatureBox.Value = (decimal)Math.Clamp(_aiSettings.Temperature, 0, 2);
        TemperatureBox.IsEnabled = provider != AiProvider.OpenAI;
        MaxOutputTokensBox.Value = Math.Clamp(_aiSettings.MaxOutputTokens, 1, 131072);
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

        try
        {
            await _settingsStore.SaveAsync(_aiSettings);
            AiStatus.Text = $"Saved {provider} settings securely.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = $"Save failed: {ex.Message}";
        }
    }

    private async void TestAi_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var provider = SelectedProvider;
            var key = ApiKeyBox.Text?.Trim() ?? string.Empty;
            var model = ModelComboBox.SelectedItem?.ToString() ?? GetStoredModel(provider);
            AiStatus.Text = $"Running {provider} test...";
            AiOutput.Text = await _aiClient.GenerateAsync(
                provider,
                key,
                model,
                AiPromptBox.Text?.Trim() ?? string.Empty,
                (double)(TemperatureBox.Value ?? 0.7m),
                (int)(MaxOutputTokensBox.Value ?? 1024m));
            AiStatus.Text = $"AI test completed using {model}.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = $"AI test failed: {ex.Message}";
        }
    }


    private async void MoveTaskUp_OnClick(object? sender, RoutedEventArgs e) =>
        await MoveTaskAsync(sender, -1);

    private async void MoveTaskDown_OnClick(object? sender, RoutedEventArgs e) =>
        await MoveTaskAsync(sender, 1);

    private async Task MoveTaskAsync(object? sender, int offset)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskItem task })
            return;
        var active = _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.SortOrder)
            .ThenBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var index = active.FindIndex(t => t.Id == task.Id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= active.Count) return;
        (active[index], active[target]) = (active[target], active[index]);
        await RunMutationAsync(async () =>
        {
            for (var i = 0; i < active.Count; i++) await _repository.SaveAsync(active[i] with { SortOrder = i });
            await LoadTasksAsync();
        });
    }

    private async void SendAi_OnClick(object? sender, RoutedEventArgs e)
    {
        var userMessage = AiChatInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userMessage)) { AiChatStatus.Text = "Enter a message first."; return; }
        var provider = SelectedProvider;
        var apiKey = GetStoredApiKey(provider);
        var model = GetStoredModel(provider);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            AiChatStatus.Text = "Configure an API key and model in Settings, then save them.";
            return;
        }

        AiChatInput.Text = string.Empty;
        AppendChat("You", userMessage);
        AiChatStatus.Text = $"Thinking with {provider}…";
        try
        {
            var taskSnapshot = _tasks.OrderBy(t => t.IsCompleted).ThenBy(t => t.SortOrder).ThenBy(t => t.DueDate)
                .Select(t => new { id = t.Id, title = t.Title, description = t.Description, completed = t.IsCompleted, dueDate = t.DueDate?.ToString("O"), sortOrder = t.SortOrder }).ToArray();
            var systemPrompt = """
You are the task assistant inside OpenToDo. The task list is supplied as JSON context.
Respond with exactly one JSON object, no markdown, using this schema:
{"message":"short user-facing explanation","actions":[ ... ]}
Supported actions:
- {"type":"add","title":"...","description":"optional","dueDate":"optional ISO-8601 date/time"}
- {"type":"update","taskId":"existing exact id","title":"optional new title","description":"optional new description","dueDate":"optional ISO-8601 date/time"}
- {"type":"complete","taskId":"existing exact id","completed":true}
- {"type":"reorder","taskIds":["existing-id-1","existing-id-2"]}
For a read-only question, use an empty actions array and answer in message.
Never invent task IDs. For update/complete/reorder, use IDs from the supplied task list.
For reorder, include all active task IDs in the requested order. Do not delete tasks.
Only perform actions the user clearly requested. If the intent is ambiguous, ask a question in message and return no actions.
""";
            var prompt = systemPrompt + "\n\nCurrent task list JSON:\n" + System.Text.Json.JsonSerializer.Serialize(taskSnapshot) +
                          "\n\nConversation so far:\n" + _chatTranscript +
                          "\n\nLatest user request:\n" + userMessage;
            var raw = await _aiClient.GenerateAsync(provider, apiKey, model, prompt, _aiSettings.Temperature, _aiSettings.MaxOutputTokens);
            using var document = System.Text.Json.JsonDocument.Parse(ExtractJsonObject(raw));
            var root = document.RootElement;
            var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? messageElement.GetString() ?? string.Empty : "Done.";
            AppendChat("AI", message);
            var actionCount = 0;
            if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var action in actions.EnumerateArray())
                {
                    await ApplyAiActionAsync(action);
                    await LoadTasksAsync();
                    actionCount++;
                }
            }
            AiChatStatus.Text = actionCount == 0 ? $"Response received from {provider}." : $"Applied {actionCount} task action(s).";
        }
        catch (Exception ex)
        {
            AiChatStatus.Text = $"AI request failed: {ex.Message}";
            AppendChat("System", "I couldn't apply that request. Check provider/model settings and try again.");
        }
    }

    private string GetStoredApiKey(AiProvider provider) => provider switch
    {
        AiProvider.Groq => _aiSettings.GroqApiKey,
        AiProvider.OpenRouter => _aiSettings.OpenRouterApiKey,
        _ => _aiSettings.OpenAiApiKey
    };

    private void AppendChat(string speaker, string message)
    {
        _chatTranscript = string.IsNullOrWhiteSpace(_chatTranscript)
            ? $"{speaker}: {message}"
            : _chatTranscript + Environment.NewLine + Environment.NewLine + $"{speaker}: {message}";

        var isUser = string.Equals(speaker, "You", StringComparison.Ordinal);
        var bubble = new Border
        {
            MaxWidth = 255,
            Margin = new Avalonia.Thickness(isUser ? 12 : 0, 0, isUser ? 0 : 12, 0),
            HorizontalAlignment = isUser ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left,
            Background = new SolidColorBrush(isUser ? Color.Parse("#46516B") : Color.Parse("#252932")),
            CornerRadius = new Avalonia.CornerRadius(12),
            Padding = new Avalonia.Thickness(12, 9)
        };
        bubble.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = speaker == "You" ? "You" : speaker == "System" ? "Notice" : "OpenToDo",
                    FontSize = 10,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(isUser ? "#DDE3F4" : "#AAB4D0"))
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#ECEEF1"))
                }
            }
        };
        ChatMessages.Children.Add(bubble);
        ChatScroll.Offset = new Avalonia.Vector(ChatScroll.Offset.X, ChatScroll.Extent.Height);
    }

    private void TaskDrag_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_mutationInProgress || sender is not Button { DataContext: TaskItem task } handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        _draggedTask = task;
        _dragStartY = e.GetPosition(TodoList).Y;
        _dragTargetIndex = -1;
        _draggedRow = handle.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(candidate => candidate.Classes.Contains("task-row"));
        if (_draggedRow is not null)
        {
            _draggedRow.Classes.Set("dragging", true);
            _draggedRow.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _draggedRow.RenderTransform = new ScaleTransform(1.025, 1.025);
            _draggedRow.Opacity = 0.88;
        }
        handle.Classes.Set("dragging", true);
        e.Pointer.Capture(handle);
        e.Handled = true;
        UpdateDropIndicator(_dragStartY);
    }

    private void TaskDrag_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedTask is null || sender is not Button handle)
            return;

        var y = e.GetPosition(TodoList).Y;
        UpdateDropIndicator(y);
        // A picked-up row follows the pointer slightly, while the blue insertion line
        // previews the exact slot that will receive the task on release.
        if (_draggedRow is not null)
            _draggedRow.RenderTransform = new TranslateTransform(0, Math.Clamp(y - _dragStartY, -80, 80) * 0.18);
        e.Handled = true;
    }

    private async void TaskDrag_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedTask is null || sender is not Button handle)
            return;

        var task = _draggedTask;
        var targetIndex = _dragTargetIndex;
        _draggedTask = null;
        _dragTargetIndex = -1;
        e.Pointer.Capture(null);
        handle.Classes.Set("dragging", false);
        handle.Opacity = 1;
        if (_draggedRow is not null)
        {
            _draggedRow.Classes.Set("dragging", false);
            _draggedRow.RenderTransform = new TranslateTransform(0, 0);
            _draggedRow.Opacity = 1;
            _draggedRow = null;
        }
        ClearDropIndicators();
        e.Handled = true;

        if (targetIndex >= 0)
            await ReorderTaskToIndexAsync(task, targetIndex);
    }

    private void UpdateDropIndicator(double pointerY)
    {
        var rows = TodoList.GetVisualDescendants().OfType<Border>()
            .Where(row => row.Classes.Contains("task-row") && row.DataContext is TaskItem)
            .OrderBy(row => row.TranslatePoint(new Point(0, 0), TodoList)?.Y ?? double.MaxValue)
            .ToList();
        ClearDropIndicators();
        if (rows.Count == 0)
        {
            _dragTargetIndex = -1;
            return;
        }

        var draggedIndex = rows.FindIndex(row => row.DataContext is TaskItem item && item.Id == _draggedTask?.Id);
        var targetIndex = rows.Count;
        for (var i = 0; i < rows.Count; i++)
        {
            var top = rows[i].TranslatePoint(new Point(0, 0), TodoList)?.Y ?? 0;
            var middle = top + rows[i].Bounds.Height / 2;
            if (pointerY < middle)
            {
                targetIndex = i;
                break;
            }
        }

        // Convert the insertion slot to the final index after removing the dragged item.
        if (draggedIndex >= 0 && targetIndex > draggedIndex)
            targetIndex--;
        _dragTargetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, rows.Count - 1));

        var indicatorIndex = targetIndex >= rows.Count ? rows.Count - 1 : targetIndex;
        var indicator = rows[indicatorIndex].GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(candidate => candidate.Name == "DropIndicator");
        if (indicator is not null)
        {
            indicator.VerticalAlignment = targetIndex >= rows.Count
                ? Avalonia.Layout.VerticalAlignment.Bottom
                : Avalonia.Layout.VerticalAlignment.Top;
            indicator.IsVisible = true;
        }
    }

    private void ClearDropIndicators()
    {
        foreach (var indicator in TodoList.GetVisualDescendants().OfType<Border>()
                     .Where(candidate => candidate.Name == "DropIndicator"))
            indicator.IsVisible = false;
    }

    private async Task ReorderTaskToIndexAsync(TaskItem task, int targetIndex)
    {
        var active = _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.SortOrder)
            .ThenBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var currentIndex = active.FindIndex(item => item.Id == task.Id);
        if (currentIndex < 0 || active.Count < 2)
            return;
        targetIndex = Math.Clamp(targetIndex, 0, active.Count - 1);
        if (targetIndex == currentIndex)
        {
            await LoadTasksAsync();
            return;
        }

        active.RemoveAt(currentIndex);
        active.Insert(targetIndex, task);
        await RunMutationAsync(async () =>
        {
            for (var i = 0; i < active.Count; i++)
                await _repository.SaveAsync(active[i] with { SortOrder = i });
            await LoadTasksAsync();
        });
    }

    private async Task ReorderTaskByOffsetAsync(TaskItem task, int offset)
    {
        if (_mutationInProgress) return;
        var active = _tasks.Where(t => !t.IsCompleted).OrderBy(t => t.SortOrder)
            .ThenBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var index = active.FindIndex(t => t.Id == task.Id);
        if (index < 0) return;
        var target = Math.Clamp(index + offset, 0, active.Count - 1);
        if (target == index) return;
        active.RemoveAt(index);
        active.Insert(target, task);
        await RunMutationAsync(async () =>
        {
            for (var i = 0; i < active.Count; i++)
                await _repository.SaveAsync(active[i] with { SortOrder = i });
            await LoadTasksAsync();
        });
    }

    private static string ExtractJsonObject(string response)
    {
        var text = response.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("The AI response did not contain a valid task-action JSON object.");
        return text[start..(end + 1)];
    }

    private async Task ApplyAiActionAsync(System.Text.Json.JsonElement action)
    {
        if (!action.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString();
        string ReadString(string name) => action.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

        switch (type)
        {
            case "add":
            {
                var title = ReadString("title").Trim();
                if (title.Length == 0 || title.Length > 300) throw new InvalidOperationException("AI task title was empty or too long.");
                DateTimeOffset? due = null;
                var dueText = ReadString("dueDate");
                if (!string.IsNullOrWhiteSpace(dueText))
                {
                    if (!DateTimeOffset.TryParse(dueText, out var parsedDue)) throw new InvalidOperationException($"Invalid due date for task '{title}'.");
                    due = parsedDue;
                }
                var nextOrder = _tasks.Where(t => !t.IsCompleted).Select(t => t.SortOrder).DefaultIfEmpty(-1).Max() + 1;
                await _repository.SaveAsync(new TaskItem(Guid.NewGuid().ToString("N"), title, EmptyToNull(ReadString("description")), false, due, null, nextOrder));
                break;
            }
            case "update":
            {
                var task = FindAiTask(ReadString("taskId"));
                var title = ReadString("title");
                var description = ReadString("description");
                var dueText = ReadString("dueDate");
                DateTimeOffset? due = task.DueDate;
                if (!string.IsNullOrWhiteSpace(dueText))
                {
                    if (!DateTimeOffset.TryParse(dueText, out var parsedDue)) throw new InvalidOperationException("AI supplied an invalid due date.");
                    due = parsedDue;
                }
                if (!string.IsNullOrWhiteSpace(title) && title.Trim().Length > 300) throw new InvalidOperationException("Task title is too long.");
                await _repository.SaveAsync(task with { Title = string.IsNullOrWhiteSpace(title) ? task.Title : title.Trim(),
                    Description = string.IsNullOrWhiteSpace(description) ? task.Description : EmptyToNull(description), DueDate = due });
                break;
            }
            case "complete":
            {
                var task = FindAiTask(ReadString("taskId"));
                var completed = action.TryGetProperty("completed", out var completedValue) && completedValue.ValueKind == System.Text.Json.JsonValueKind.True;
                var history = (task.CompletionHistory ?? Array.Empty<DateTimeOffset>()).ToList();
                if (completed && !task.IsCompleted) history.Add(DateTimeOffset.Now);
                await _repository.SaveAsync(task with { IsCompleted = completed, CompletionHistory = history });
                break;
            }
            case "reorder":
            {
                if (!action.TryGetProperty("taskIds", out var ids) || ids.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new InvalidOperationException("AI reorder action did not contain task IDs.");
                var requested = ids.EnumerateArray().Where(v => v.ValueKind == System.Text.Json.JsonValueKind.String).Select(v => v.GetString() ?? string.Empty).ToList();
                var active = _tasks.Where(t => !t.IsCompleted).ToDictionary(t => t.Id, StringComparer.Ordinal);
                var ordered = requested.Where(active.ContainsKey).Distinct(StringComparer.Ordinal).Select(id => active[id]).ToList();
                ordered.AddRange(_tasks.Where(t => !t.IsCompleted && !requested.Contains(t.Id, StringComparer.Ordinal)).OrderBy(t => t.SortOrder));
                for (var i = 0; i < ordered.Count; i++) await _repository.SaveAsync(ordered[i] with { SortOrder = i });
                break;
            }
        }
    }

    private TaskItem FindAiTask(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("AI task action is missing a task ID.");
        return _tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("AI referenced a task that no longer exists. Please retry.");
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string GetStoredModel(AiProvider provider) => provider switch
    {
        AiProvider.Groq => _aiSettings.GroqModel,
        AiProvider.OpenRouter => _aiSettings.OpenRouterModel,
        _ => _aiSettings.OpenAiModel
    };
}
