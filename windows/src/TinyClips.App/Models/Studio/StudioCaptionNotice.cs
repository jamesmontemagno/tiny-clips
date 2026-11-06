namespace TinyClips.App.Models.Studio;

public enum StudioCaptionNoticeKind
{
    /// <summary>Something went as asked. It goes after a while.</summary>
    Done,

    /// <summary>Something was stopped as asked. It goes after a while.</summary>
    Info,

    /// <summary>Generating heard nobody speak. It stays, with a way to try another model.</summary>
    NoSpeech,

    /// <summary>Something went wrong. It stays until it is closed.</summary>
    Problem,
}

/// <summary>What the Captions panel says about the last thing done to the captions.</summary>
public sealed record StudioCaptionNotice(StudioCaptionNoticeKind Kind, string Message, string? Title = null)
{
    /// <summary>How long one that goes by itself stays.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(6);

    public bool GoesByItself => Kind is StudioCaptionNoticeKind.Done or StudioCaptionNoticeKind.Info;

    /// <summary>What a screen reader says for it.</summary>
    public string SpokenText => Title is null ? Message : $"{Title}. {Message}";
}

/// <summary>A notification about a Studio project, which opens the project when it is clicked.</summary>
public sealed class StudioProjectNotificationEventArgs(string projectId, string title, string message) : EventArgs
{
    public string ProjectId { get; } = projectId;

    public string Title { get; } = title;

    public string Message { get; } = message;
}
