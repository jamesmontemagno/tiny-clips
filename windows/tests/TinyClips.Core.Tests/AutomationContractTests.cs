using System.Reflection;
using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public sealed class AutomationContractTests
{
    [Fact]
    public void EmbeddedContract_Loads()
    {
        var contract = AutomationContract.Default;
        Assert.Equal(1, contract.ProtocolVersion);
        Assert.NotEmpty(contract.Commands);
        Assert.Equal(
            [AutomationSessionCommands.Hello, AutomationSessionCommands.Pair, AutomationSessionCommands.PairStatus],
            contract.SessionCommands);
    }

    [Fact]
    public void SensitiveScopes_AreDeleteUploadAndDiscard()
    {
        var contract = AutomationContract.Default;
        Assert.Equal(["delete", "discard", "upload"], contract.SensitiveScopes.Order());
        Assert.All(contract.SensitiveScopes, scope => Assert.True(contract.Scopes.ContainsKey(scope)));
    }

    [Fact]
    public void EveryCommand_UsesAKnownScope_AndHasAUniqueName()
    {
        var contract = AutomationContract.Default;
        Assert.All(contract.Commands, command => Assert.True(contract.Scopes.ContainsKey(command.Scope), command.Name));
        Assert.Equal(contract.Commands.Count, contract.Commands.Select(c => c.Name).Distinct().Count());
        Assert.DoesNotContain(contract.Commands, c => contract.IsSessionCommand(c.Name));
    }

    [Fact]
    public void DestructiveCommands_NeedSensitiveScopes()
    {
        var contract = AutomationContract.Default;
        foreach (var name in new[] { "library.delete", "library.upload", "recording.discard", "recording.restart" })
        {
            Assert.True(contract.TryGetCommand(name, out var command), name);
            Assert.True(contract.IsSensitiveScope(command.Scope), name);
        }
    }

    [Fact]
    public void TargetedRecordingAndScrollingCommands_HaveExpectedScopesAndTargets()
    {
        var contract = AutomationContract.Default;
        foreach (var name in new[] { "recording.startVideo", "recording.startGif", "capture.scrolling.start" })
        {
            Assert.True(contract.TryGetCommand(name, out var command), name);
            Assert.Contains(command.Scope, new[] { "record", "capture" });
            Assert.Contains("screen", command.Parameters["mode"].AllowedValues!);
            Assert.Contains("region", command.Parameters["mode"].AllowedValues!);
            Assert.Contains("window", command.Parameters["mode"].AllowedValues!);
            Assert.Contains("displayId", command.Parameters.Keys);
            Assert.Contains("windowId", command.Parameters.Keys);
            Assert.Contains("region", command.Parameters.Keys);
        }

        Assert.True(contract.TryGetCommand("capture.scrolling.cancel", out var cancel));
        Assert.Equal("discard", cancel.Scope);
        Assert.True(contract.IsSensitiveScope(cancel.Scope));
    }

    [Fact]
    public void StudioCommands_HaveExpectedScopesAndOptimisticEditParameters()
    {
        var contract = AutomationContract.Default;
        Assert.True(contract.TryGetCommand("studio.projects.list", out var list));
        Assert.Equal("read", list.Scope);
        Assert.True(contract.TryGetCommand("studio.project.get", out var get));
        Assert.Equal("read", get.Scope);
        Assert.True(contract.TryGetCommand("studio.project.edit", out var edit));
        Assert.Equal("edit", edit.Scope);
        Assert.True(edit.Parameters["projectId"].Required);
        Assert.True(edit.Parameters["expectedModifiedAt"].Required);
        Assert.True(edit.Parameters["expectedRevision"].Required);
        Assert.True(edit.Parameters["patch"].Required);
        Assert.True(contract.TryGetCommand("studio.zoom.suggest", out var suggest));
        Assert.Equal("read", suggest.Scope);
        Assert.True(contract.TryGetCommand("studio.export", out var export));
        Assert.Equal("export", export.Scope);
        Assert.True(export.Parameters["outputPath"].Required);
    }

    [Fact]
    public void MediaEditCommands_UseEditScopeAndRequiredRange()
    {
        var contract = AutomationContract.Default;
        Assert.True(contract.TryGetCommand("editor.crop", out var crop));
        Assert.Equal("edit", crop.Scope);
        Assert.True(crop.Parameters["path"].Required);
        Assert.True(crop.Parameters["region"].Required);
        Assert.True(contract.TryGetCommand("editor.trim", out var trim));
        Assert.Equal("edit", trim.Scope);
        Assert.True(trim.Parameters["path"].Required);
        Assert.True(trim.Parameters["start"].Required);
        Assert.True(trim.Parameters["end"].Required);
        Assert.False(trim.Parameters["removeAudio"].Required);
        Assert.True(contract.TryGetCommand("editor.saveFrame", out var saveFrame));
        Assert.Equal("edit", saveFrame.Scope);
        Assert.True(saveFrame.Parameters["path"].Required);
        Assert.False(saveFrame.Parameters["time"].Required);
        Assert.False(saveFrame.Parameters["frame"].Required);
        Assert.True(contract.Errors.ContainsKey(AutomationErrorCodes.Conflict));
    }

    [Fact]
    public void ErrorCodes_ContainEveryConstant()
    {
        var contract = AutomationContract.Default;
        var constants = typeof(AutomationErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!);
        Assert.All(constants, code => Assert.True(contract.Errors.ContainsKey(code), code));
    }

    [Fact]
    public void Parameters_CarryTypesEnumsAndDefaults()
    {
        var contract = AutomationContract.Default;
        Assert.True(contract.TryGetCommand("capture.screenshot", out var screenshot));
        var mode = screenshot.Parameters["mode"];
        Assert.Equal("string", mode.Type);
        Assert.Contains("screen", mode.AllowedValues!);
        Assert.Equal("screen", mode.DefaultValue!.Value.GetString());

        Assert.True(contract.TryGetCommand("editor.open", out var editor));
        Assert.True(editor.Parameters["path"].Required);
    }

    [Fact]
    public void ValidateParams_FillsDefaults_AndRejectsBadValues()
    {
        var contract = AutomationContract.Default;
        Assert.True(contract.TryGetCommand("library.list", out var list));

        Assert.True(AutomationHost.TryValidateParams(list, null, out var filled, out _));
        Assert.Equal(20, filled["limit"]!.GetValue<int>());
        Assert.Equal("all", filled["kind"]!.GetValue<string>());

        Assert.False(AutomationHost.TryValidateParams(
            list, new System.Text.Json.Nodes.JsonObject { ["kind"] = "audio" }, out _, out var error));
        Assert.Contains("kind", error);

        Assert.False(AutomationHost.TryValidateParams(
            list, new System.Text.Json.Nodes.JsonObject { ["limit"] = "ten" }, out _, out _));

        Assert.True(contract.TryGetCommand("library.delete", out var delete));
        Assert.False(AutomationHost.TryValidateParams(delete, new System.Text.Json.Nodes.JsonObject(), out _, out error));
        Assert.Contains("path", error);
    }
}
