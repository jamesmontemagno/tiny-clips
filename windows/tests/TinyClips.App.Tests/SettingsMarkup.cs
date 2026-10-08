using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TinyClips.App.Tests;

/// <summary>
/// The markup of the Settings window and of its pages, and the code of the window and of two
/// pages, as text. The project file puts them into the test assembly: a page cannot be made
/// here, because a XAML page needs the running app, so what can be read off it is read.
/// </summary>
internal static partial class SettingsMarkup
{
    private const string Prefix = "SettingsMarkup.";

    /// <summary>The markup files of the Settings pages, by name: every .xaml file next to the Studio page's.</summary>
    public static IReadOnlyList<string> Pages { get; } =
    [
        .. typeof(SettingsMarkup).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith("SettingsSection.xaml", StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The text of one file, by its name, such as <c>StudioSettingsSection.xaml</c>.</summary>
    public static string Read(string name)
    {
        using var stream = typeof(SettingsMarkup).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"{name} is not in the test assembly. The project file puts it there.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The elements of a markup file, in the order they are written.</summary>
    public static IReadOnlyList<XElement> Elements(string name) => [.. XDocument.Parse(Read(name)).Descendants()];

    /// <summary>The value of an attribute such as <c>AutomationProperties.AutomationId</c>, or null.</summary>
    public static string? Attr(this XElement element, string attribute) =>
        element.Attributes().FirstOrDefault(candidate => candidate.Name.LocalName == attribute)?.Value;

    /// <summary>Every <c>AutomationProperties.AutomationId</c> of a markup file, in order.</summary>
    public static IReadOnlyList<string> AutomationIds(string name) =>
        [.. Elements(name).Select(element => element.Attr("AutomationProperties.AutomationId")).OfType<string>()];

    /// <summary>
    /// The values of the Settings view model a markup file binds both ways
    /// (<c>{x:Bind ViewModel.X, Mode=TwoWay}</c>), in order. Fails for a two-way binding that
    /// is written another way, so that none is missed.
    /// </summary>
    public static IReadOnlyList<string> TwoWayViewModelProperties(string name)
    {
        var text = Read(name);
        var properties = new List<string>();
        foreach (var attribute in XDocument.Parse(text).Descendants().SelectMany(element => element.Attributes()))
        {
            var match = Bind().Match(attribute.Value.Trim());
            if (!match.Success || !TwoWayMode().IsMatch(match.Groups["rest"].Value))
            {
                continue;
            }

            var path = match.Groups["path"].Value;
            const string Root = "ViewModel.";
            Assert.True(
                path.StartsWith(Root, StringComparison.Ordinal) && path.IndexOf('.', Root.Length) < 0,
                $"{name} binds {attribute.Name.LocalName} both ways to {path}, which is not a value of the Settings view model.");
            properties.Add(path[Root.Length..]);
        }

        Assert.Equal(TwoWayMode().Count(text), properties.Count);
        return properties;
    }

    [GeneratedRegex(@"^\{x:Bind\s+(?<path>[^,}\s]+)\s*,(?<rest>[^}]*)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex Bind();

    [GeneratedRegex(@"\bMode\s*=\s*TwoWay\b", RegexOptions.CultureInvariant)]
    private static partial Regex TwoWayMode();
}
