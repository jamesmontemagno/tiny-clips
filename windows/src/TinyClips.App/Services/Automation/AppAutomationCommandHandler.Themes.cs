using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TinyClips.Core.Automation;
using TinyClips.Core.Editing.Themes;

namespace TinyClips.App;

/// <summary>
/// Saved screenshot editor themes. Values are the shared theme JSON, so <c>editor.themes.get</c>
/// is export and <c>editor.themes.save</c> is import. Open editor windows keep their current look.
/// </summary>
internal sealed partial class AppAutomationCommandHandler
{
    private static EditorThemeStore ThemeStore => App.Services.GetRequiredService<EditorThemeStore>();

    private static JsonNode ListThemes()
    {
        var store = ThemeStore;
        var themes = new JsonArray();
        foreach (var theme in store.Themes)
        {
            themes.Add(new JsonObject { ["id"] = theme.Id, ["name"] = theme.Name });
        }

        return new JsonObject { ["activeThemeId"] = store.ActiveThemeId, ["themes"] = themes };
    }

    private static JsonNode GetTheme(AutomationCommandContext context) =>
        new JsonObject { ["theme"] = RequireTheme(RequiredThemeId(context, "id")).ToJson() };

    private static JsonNode SaveTheme(AutomationCommandContext context)
    {
        if (context.Params["theme"] is not JsonObject json)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "theme must be an editor theme object.");
        }

        try
        {
            return new JsonObject { ["theme"] = ThemeStore.Save(EditorTheme.Parse(json)).ToJson() };
        }
        catch (EditorThemeFormatException ex)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, ex.Message);
        }
        catch (EditorThemeLimitException ex)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, ex.Message);
        }
    }

    private static JsonNode SetActiveTheme(AutomationCommandContext context)
    {
        var id = context.GetString("id");
        if (!ThemeStore.SetActive(id))
        {
            throw ThemeNotFound();
        }

        return new JsonObject { ["activeThemeId"] = ThemeStore.ActiveThemeId };
    }

    private static JsonNode DeleteTheme(AutomationCommandContext context)
    {
        if (!ThemeStore.Delete(RequiredThemeId(context, "id")))
        {
            throw ThemeNotFound();
        }

        return new JsonObject { ["deleted"] = true };
    }

    /// <summary>The theme a <c>themeId</c> param names, or null when it has none.</summary>
    private static EditorTheme? OptionalTheme(AutomationCommandContext context)
    {
        if (context.Params["themeId"] is null)
        {
            return null;
        }

        return RequireTheme(RequiredThemeId(context, "themeId"));
    }

    private static EditorTheme RequireTheme(string id) => ThemeStore.Get(id) ?? throw ThemeNotFound();

    private static string RequiredThemeId(AutomationCommandContext context, string name)
    {
        var id = context.GetString(name);
        return string.IsNullOrEmpty(id)
            ? throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{name} is required.")
            : id;
    }

    private static AutomationException ThemeNotFound() =>
        new(AutomationErrorCodes.NotFound, "There is no editor theme with that id.");
}
