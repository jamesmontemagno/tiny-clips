using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// <c>audio.volume</c>: what it is when the project does not say, what it is used as, how it is
/// written, and how the editor sets it.
/// </summary>
public sealed class StudioSoundTests : StudioEditorSessionTestBase
{
    private const string Minimal = """ "id": "p", "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } } """;

    // The value in use

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.05, 0.05)]
    [InlineData(0, 0)]
    [InlineData(1.0000001, 1)]
    [InlineData(2, 1)]
    [InlineData(-0.25, 0)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(double.NaN, 1)]
    public void AStoredVolume_IsUsedBetweenSilentAndAsRecorded(double stored, double used)
    {
        Assert.Equal(used, StudioSound.Volume(stored));
        Assert.Equal(used, StudioSound.Volume(new StudioAudio { Volume = stored }));
    }

    // The project file

    [Fact]
    public void AProjectThatDoesNotSay_HasTheSoundAsRecorded()
    {
        Assert.Equal(1, new StudioAudio().Volume);
        Assert.Equal(1, new StudioProject().Audio.Volume);
        Assert.Equal(1, Read($$"""{ {{Minimal}} }""").Audio.Volume);
        Assert.Equal(1, Read($$"""{ {{Minimal}}, "audio": null }""").Audio.Volume);
        Assert.Equal(1, Read($$"""{ {{Minimal}}, "audio": {} }""").Audio.Volume);
        Assert.Equal(1, Read($$"""{ {{Minimal}}, "audio": { "muted": true } }""").Audio.Volume);
        Assert.Equal(1, Read($$"""{ {{Minimal}}, "audio": { "volume": null } }""").Audio.Volume);
    }

    [Theory]
    [InlineData("0.35", 0.35)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("0.5e0", 0.5)]
    public void AVolumeInTheFile_IsRead_AndLeavesTheOtherTwoAlone(string written, double expected)
    {
        var audio = Read($$"""{ {{Minimal}}, "audio": { "volume": {{written}}, "systemVolume": 0.25, "microphoneVolume": 0.75 } }""").Audio;

        Assert.Equal(expected, audio.Volume);
        Assert.Equal(0.25, audio.SystemVolume);
        Assert.Equal(0.75, audio.MicrophoneVolume);
        Assert.False(audio.Muted);
    }

    [Theory]
    [InlineData("3", 3, 1)]
    [InlineData("-2", -2, 0)]
    public void AVolumeOutOfRange_IsKeptAsItIsWritten_AndClampedOnlyWhenUsed(string written, double stored, double used)
    {
        var project = Read($$"""{ {{Minimal}}, "audio": { "volume": {{written}} } }""");

        Assert.Equal(stored, project.Audio.Volume);
        Assert.Equal(used, StudioSound.Volume(project.Audio));
        Assert.Equal(stored, AudioOf(StudioProjectJson.WriteProject(project))["volume"]!.GetValue<double>());
    }

    [Fact]
    public void AVolumeThatIsNoNumber_MakesTheProjectInvalid_AsAnyOtherNumberDoes()
    {
        Assert.Throws<StudioProjectInvalidException>(() => Read($$"""{ {{Minimal}}, "audio": { "volume": "loud" } }"""));
        Assert.Throws<StudioProjectInvalidException>(() => Read($$"""{ {{Minimal}}, "audio": { "systemVolume": "loud" } }"""));
    }

    [Fact]
    public void TheVolume_IsWrittenOnce_InAudio_AsANumber_WhateverItIs()
    {
        foreach (var (volume, text) in new[] { (1d, "1"), (0.5, "0.5"), (0d, "0"), (0.35, "0.35") })
        {
            var json = StudioProjectJson.WriteProject(Read($$"""{ {{Minimal}} }""") with { Audio = new StudioAudio { Volume = volume } });

            Assert.Single(Regex.Matches(json, "\"volume\""));
            Assert.Contains($"\"volume\": {text},", json, StringComparison.Ordinal);
            Assert.Equal(["muted", "volume", "systemVolume", "microphoneVolume"], AudioOf(json).Select(property => property.Key));
            Assert.Equal(volume, StudioProjectJson.ReadProject(json).Audio.Volume);
        }
    }

    [Fact]
    public void AVolumeABuildWithoutItCarriedAsAnUnknownProperty_IsReadAsTheVolume_AndWrittenOnce()
    {
        // What a build from before the volume wrote of a project a later build had set to 35%:
        // it kept "volume" with the properties it did not know, which are written after the ones
        // it knew. A property nobody knows travels with it.
        var carried = $$"""{ {{Minimal}}, "audio": { "muted": false, "systemVolume": 1, "microphoneVolume": 1, "volume": 0.35, "limiter": "soft" } }""";

        var project = Read(carried);

        Assert.Equal(0.35, project.Audio.Volume);
        Assert.DoesNotContain("volume", project.Audio.ExtensionData.Keys);
        Assert.Equal(["limiter"], project.Audio.ExtensionData.Keys);

        var edited = project with { Audio = project.Audio with { Volume = 0.8 } };
        var json = StudioProjectJson.WriteProject(edited);

        Assert.Single(Regex.Matches(json, "\"volume\""));
        Assert.Equal(0.8, AudioOf(json)["volume"]!.GetValue<double>());
        Assert.Equal("soft", AudioOf(json)["limiter"]!.GetValue<string>());
        Assert.Equal(0.8, StudioProjectJson.ReadProject(json).Audio.Volume);
    }

    // The editor

    [Fact]
    public void SetVolume_StoresTheVolumeInUse_AndIsOneUndoStep()
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}} }"""));

        model.SetVolume(0.35);
        Assert.Equal(0.35, model.Project.Audio.Volume);
        Assert.True(model.CanUndo);

        model.SetVolume(4);
        Assert.Equal(1, model.Project.Audio.Volume);
        model.SetVolume(-4);
        Assert.Equal(0, model.Project.Audio.Volume);

        model.Undo();
        model.Undo();
        model.Undo();
        Assert.Equal(1, model.Project.Audio.Volume);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void SetVolume_StoresAPlainNumber_WhereASliderHandsOverStepsAddedUp()
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}} }"""));

        // Seven steps of 5%, as the slider's row counts them.
        model.SetVolume(7 * 0.05);

        Assert.NotEqual(0.35, 7 * 0.05);
        Assert.Equal(0.35, model.Project.Audio.Volume);
        Assert.Contains("\"volume\": 0.35,", StudioProjectJson.WriteProject(model.Project), StringComparison.Ordinal);

        // The same value again, with or without the noise, is no edit.
        model.SetVolume(0.35);
        model.SetVolume(7 * 0.05);
        model.Undo();
        Assert.Equal(1, model.Project.Audio.Volume);
        Assert.False(model.CanUndo);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetVolume_WithWhatIsNoNumber_ChangesNothing(double value)
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}}, "audio": { "volume": 0.6 } }"""));

        model.SetVolume(value);

        Assert.Equal(0.6, model.Project.Audio.Volume);
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void SetVolume_ToTheVolumeItHas_LeavesNoUndoStep()
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}} }"""));

        model.SetVolume(1);
        model.SetVolume(7);

        Assert.False(model.CanUndo);
    }

    [Fact]
    public void ADragOfTheVolume_IsOneUndoStep()
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}} }"""));

        model.BeginEditingGroup();
        model.SetVolume(0.9);
        model.SetVolume(0.6);
        model.SetVolume(0.45);
        model.CommitEditingGroup();

        Assert.Equal(0.45, model.Project.Audio.Volume);
        model.Undo();
        Assert.Equal(1, model.Project.Audio.Volume);
        Assert.False(model.CanUndo);
        model.Redo();
        Assert.Equal(0.45, model.Project.Audio.Volume);
    }

    [Fact]
    public void MuteAndTheVolume_LeaveEachOtherAlone()
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}} }"""));

        model.SetVolume(0.4);
        model.SetMuted(true);
        Assert.Equal((true, 0.4), (model.Project.Audio.Muted, model.Project.Audio.Volume));

        model.SetVolume(0.7);
        Assert.Equal((true, 0.7), (model.Project.Audio.Muted, model.Project.Audio.Volume));

        model.SetMuted(false);
        Assert.Equal((false, 0.7), (model.Project.Audio.Muted, model.Project.Audio.Volume));
    }

    [Fact]
    public void TheVolume_IsNotPartOfALook()
    {
        var model = new StudioEditorModel(Read($$"""{ {{Minimal}} }"""));
        model.SetVolume(0.4);

        model.ApplyLook(new StudioLook(new StudioCanvas { Padding = 0.2 }, new StudioScreenStyle(), new StudioCameraStyle()));

        Assert.Equal(0.2, model.Project.Canvas.Padding);
        Assert.Equal(0.4, model.Project.Audio.Volume);
    }

    [Fact]
    public async Task ADragOfTheVolumeInTheSession_ReachesThePreviewAtEveryStep_AndTheDisk_AndIsOneUndoStep()
    {
        var id = CreateProject();
        var session = await OpenAsync(id);

        session.BeginGesture();
        session.SetVolume(0.8);
        Assert.Equal(0.8, Preview.LastProject!.Audio.Volume);
        session.SetVolume(0.5);
        Assert.Equal(0.5, Preview.LastProject!.Audio.Volume);
        session.EndGesture();

        Advance(600);
        Assert.Equal(0.5, Projects.Load(id).Audio.Volume);
        Assert.True(session.CanUndo);

        session.Undo();
        Assert.Equal(1, session.Project!.Audio.Volume);
        Assert.Equal(1, Preview.LastProject!.Audio.Volume);
        Assert.False(session.CanUndo);
    }

    private static StudioProject Read(string json) => StudioProjectJson.ReadProject(json);

    private static JsonObject AudioOf(string json) => JsonNode.Parse(json)!["audio"]!.AsObject();
}
