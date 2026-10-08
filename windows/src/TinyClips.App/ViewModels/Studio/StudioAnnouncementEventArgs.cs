namespace TinyClips.App.ViewModels.Studio;

/// <summary>What a sentence for screen readers is about, which decides how it is delivered.</summary>
public enum StudioAnnouncementKind
{
    /// <summary>Something started or changed.</summary>
    Information,

    /// <summary>Something the user asked for is done.</summary>
    Completed,

    /// <summary>Something the user asked for was stopped before it was done.</summary>
    Stopped,
}

/// <summary>A sentence the Studio window hands to screen readers, such as "Export started."</summary>
/// <param name="message">What is said.</param>
/// <param name="activityId">Groups notices of one kind, so a newer one replaces an older one still waiting.</param>
/// <param name="kind">What the sentence is about.</param>
public sealed class StudioAnnouncementEventArgs(string message, string activityId, StudioAnnouncementKind kind) : EventArgs
{
    public string Message { get; } = message;

    public string ActivityId { get; } = activityId;

    public StudioAnnouncementKind Kind { get; } = kind;
}
