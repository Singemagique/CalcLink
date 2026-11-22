using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Services;

public sealed record Notification(string Title, string Message, InfoBarSeverity Severity);

/// <summary>Non-modal messages shown in the shell's InfoBar (cancellations, link loss, successes).</summary>
public sealed class NotificationService
{
    public event EventHandler<Notification>? Posted;

    public void Post(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational) =>
        Posted?.Invoke(this, new Notification(title, message, severity));
}
