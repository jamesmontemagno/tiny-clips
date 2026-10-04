using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TinyClips.Core.Tests")]

// The Studio check tools (windows\tools) read counters that pixels cannot show, force the preview's
// players mute, and simulate a lost device. None of that belongs in the public surface.
[assembly: InternalsVisibleTo("StudioRenderCheck")]
[assembly: InternalsVisibleTo("StudioPreviewCheck")]
