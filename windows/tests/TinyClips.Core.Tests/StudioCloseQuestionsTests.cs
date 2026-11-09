using TinyClips.Core.Studio.Editing;

namespace TinyClips.Core.Tests;

/// <summary>
/// What the Studio window asks before it closes, by its close button and by Esc. The window
/// cannot be driven with a key in tests, so the choice of the question is here.
/// </summary>
public sealed class StudioCloseQuestionsTests
{
    private static readonly bool[] Either = [false, true];

    /// <summary>A way of asking the window to close, and what the editor is in then.</summary>
    private readonly record struct State(StudioCloseRequest Request, StudioClosePrompt Prompt, bool IsProjectOpen, bool ConfirmEscape)
    {
        public StudioCloseQuestion Question => StudioCloseQuestions.Resolve(Request, Prompt, IsProjectOpen, ConfirmEscape);
    }

    private static State[] EveryState() =>
    [
        .. from request in Enum.GetValues<StudioCloseRequest>()
           from prompt in Enum.GetValues<StudioClosePrompt>()
           from open in Either
           from confirm in Either
           select new State(request, prompt, open, confirm),
    ];

    [Fact]
    public void EveryState_AgainstTheRuleWrittenOutAgain()
    {
        var states = EveryState();
        Assert.Equal(40, states.Length);

        foreach (var state in states)
        {
            var expected =
                // What closing asks of itself comes first, however it was asked for.
                state.Prompt == StudioClosePrompt.ExportRunning ? StudioCloseQuestion.RunningExport
                : state.Prompt == StudioClosePrompt.ProjectSaveRunning ? StudioCloseQuestion.RunningProjectSave
                : state.Prompt == StudioClosePrompt.Draft ? StudioCloseQuestion.Draft
                : state.Prompt == StudioClosePrompt.UnsavedChanges ? StudioCloseQuestion.UnsavedChanges

                // Where closing asks nothing, the close button asks nothing.
                : state.Request == StudioCloseRequest.CloseButton ? StudioCloseQuestion.None

                // Esc asks whether it was meant: of a project that is open, behind the setting.
                : state.IsProjectOpen && state.ConfirmEscape ? StudioCloseQuestion.Escape
                : StudioCloseQuestion.None;

            Assert.True(expected == state.Question, $"{state}: expected {expected}, and it was {state.Question}");
        }
    }

    [Fact]
    public void TheSetting_NeverTakesTheDraftOrUnsavedChangesQuestionAway()
    {
        // The question decides what happens to a recording. Switching the confirmation off
        // must not let Esc, or the close button, close a draft or a saved project with changes.
        foreach (var state in EveryState().Where(state => state.Prompt == StudioClosePrompt.Draft))
        {
            Assert.True(state.Question == StudioCloseQuestion.Draft, $"{state}: {state.Question}");
        }

        foreach (var state in EveryState().Where(state => state.Prompt == StudioClosePrompt.UnsavedChanges))
        {
            Assert.True(state.Question == StudioCloseQuestion.UnsavedChanges, $"{state}: {state.Question}");
        }
    }

    [Fact]
    public void Escape_NeverAsksItsOwnQuestion_OfADraftOrUnsavedChanges()
    {
        // Its question says that the edits are saved and asks nothing about the recording: asked
        // there, with the setting on, it would stand where the closing question has to.
        foreach (var state in EveryState().Where(state => state.Prompt is StudioClosePrompt.Draft or StudioClosePrompt.UnsavedChanges))
        {
            Assert.True(state.Question != StudioCloseQuestion.Escape, $"{state}: {state.Question}");
        }
    }

    [Fact]
    public void Escape_NeverAsksItsOwnQuestion_OfAWindowWhoseProjectIsNotOpen()
    {
        // Still being opened, or one that cannot be shown: such a window closes on Esc as it
        // does by its close button, whatever the setting says.
        foreach (var state in EveryState().Where(state => !state.IsProjectOpen))
        {
            Assert.True(state.Question != StudioCloseQuestion.Escape, $"{state}: {state.Question}");
        }

        Assert.Equal(
            StudioCloseQuestion.None,
            StudioCloseQuestions.Resolve(StudioCloseRequest.Escape, StudioClosePrompt.None, isProjectOpen: false, confirmEscape: true));
    }

    [Fact]
    public void TheCloseButton_IsNeverAskedEscapesQuestion()
    {
        foreach (var state in EveryState().Where(state => state.Request == StudioCloseRequest.CloseButton))
        {
            Assert.True(state.Question != StudioCloseQuestion.Escape, $"{state}: {state.Question}");
        }
    }

    [Fact]
    public void TheCloseButton_AsksWhatClosingAsksOfItself_AndNothingElseComesIntoIt()
    {
        // What the close button did before Esc closed the window: neither the setting nor
        // whether the project is open changes it.
        foreach (var state in EveryState().Where(state => state.Request == StudioCloseRequest.CloseButton))
        {
            var expected = state.Prompt switch
            {
                StudioClosePrompt.ExportRunning => StudioCloseQuestion.RunningExport,
                StudioClosePrompt.ProjectSaveRunning => StudioCloseQuestion.RunningProjectSave,
                StudioClosePrompt.Draft => StudioCloseQuestion.Draft,
                StudioClosePrompt.UnsavedChanges => StudioCloseQuestion.UnsavedChanges,
                _ => StudioCloseQuestion.None,
            };

            Assert.True(expected == state.Question, $"{state}: expected {expected}, and it was {state.Question}");
        }
    }

    [Fact]
    public void Escape_NeverAsksLessThanTheCloseButton()
    {
        // Esc must never do more than the close button would: wherever the close button asks
        // a question, Esc asks the same one.
        foreach (var state in EveryState().Where(state => state.Request == StudioCloseRequest.Escape))
        {
            var byCloseButton = (state with { Request = StudioCloseRequest.CloseButton }).Question;
            if (byCloseButton != StudioCloseQuestion.None)
            {
                Assert.True(byCloseButton == state.Question, $"{state}: the close button asks {byCloseButton}, and Esc {state.Question}");
            }
        }
    }

    [Fact]
    public void WhileAnExportRuns_TheQuestionIsAboutTheExport()
    {
        // The keys stop an export on Esc and never ask the window to close then. Should the
        // window be asked all the same, it is asked what its close button asks.
        foreach (var state in EveryState().Where(state => state.Prompt == StudioClosePrompt.ExportRunning))
        {
            Assert.True(state.Question == StudioCloseQuestion.RunningExport, $"{state}: {state.Question}");
        }
    }

    [Fact]
    public void WhileTheProjectIsBeingSavedAsAFolder_TheQuestionIsAboutThatSave()
    {
        // The keys stop the save on Esc and never ask the window to close then. The close
        // button asks, however the setting stands: no window closes from over a copy that is
        // half made without saying so.
        foreach (var state in EveryState().Where(state => state.Prompt == StudioClosePrompt.ProjectSaveRunning))
        {
            Assert.True(state.Question == StudioCloseQuestion.RunningProjectSave, $"{state}: {state.Question}");
        }
    }

    [Fact]
    public void Escape_AsksWhetherItWasMeant_InExactlyOneCase()
    {
        // The project is open, closing asks nothing of itself, which is a saved project without
        // changes, and the setting is on.
        var asked = EveryState().Where(state => state.Question == StudioCloseQuestion.Escape).ToArray();

        Assert.Equal(
            [new State(StudioCloseRequest.Escape, StudioClosePrompt.None, IsProjectOpen: true, ConfirmEscape: true)],
            asked);

        // With the setting off, the same window closes at once.
        Assert.Equal(
            StudioCloseQuestion.None,
            StudioCloseQuestions.Resolve(StudioCloseRequest.Escape, StudioClosePrompt.None, isProjectOpen: true, confirmEscape: false));
    }
}
