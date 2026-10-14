using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace OpenToDo.App;

/// <summary>Small modal confirmation. Returns true only when the user explicitly confirms.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel" };
        var confirm = new Button { Content = confirmText, Classes = { "danger" } };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);

        Content = new Border
        {
            Padding = new Thickness(26),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = title, Classes = { "h2" } },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { cancel, confirm }
                    }
                }
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(false);
            }
        };

        // Default focus on the safe choice.
        Opened += (_, _) => cancel.Focus();
    }
}
