namespace TinyClips.App;

internal static class GlobalHotKeyFailureFormatter
{
    public static string FormatApply(
        IReadOnlyList<GlobalHotKeyRegistrationFailure> failures)
    {
        var messages = ServiceMessages(failures);
        var rejectedNames = BindingNames(failures);

        if (rejectedNames.Length > 0)
        {
            messages.Add(
                $"Windows could not register {string.Join(", ", rejectedNames)}. " +
                "Another app may already use this shortcut. Choose a different combination.");
        }

        return string.Join(" ", messages);
    }

    public static string FormatRollback(
        IReadOnlyList<GlobalHotKeyRegistrationFailure> failures)
    {
        var messages = ServiceMessages(failures);
        var rejectedNames = BindingNames(failures);

        if (rejectedNames.Length > 0)
        {
            messages.Add(
                $"Windows could not reactivate {string.Join(", ", rejectedNames)}. " +
                "Close the competing app or restart TinyClips.");
        }

        return
            $" The previous shortcut was restored in Settings, but TinyClips could not reactivate it. " +
            string.Join(" ", messages);
    }

    private static List<string> ServiceMessages(
        IReadOnlyList<GlobalHotKeyRegistrationFailure> failures)
        => failures
            .Where(failure => failure.Action is null)
            .Select(failure => failure.Message)
            .Distinct()
            .ToList();

    private static string[] BindingNames(
        IReadOnlyList<GlobalHotKeyRegistrationFailure> failures)
        => failures
            .Where(failure => failure.Action is not null)
            .Select(failure => failure.Name)
            .ToArray();
}
