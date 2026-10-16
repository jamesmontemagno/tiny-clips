using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

public sealed class StudioCleanupPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 22, 41, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_DoesNotDeleteProjectExactlyThirtyDaysOld()
    {
        var plan = StudioCleanupPolicy.Plan(
            [Eligible("exact", Now - TimeSpan.FromDays(30))],
            Now);

        Assert.Empty(plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_DeletesProjectsOlderThanRetention()
    {
        var plan = StudioCleanupPolicy.Plan(
            [Eligible("old", Now - TimeSpan.FromDays(30) - TimeSpan.FromTicks(1))],
            Now);

        Assert.Equal(["old"], plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_SizeRuleDeletesOldestEligibleUntilUnderCap()
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("new", Now - TimeSpan.FromDays(1), sizeBytes: 60),
                Eligible("old", Now - TimeSpan.FromDays(3), sizeBytes: 40),
                Eligible("middle", Now - TimeSpan.FromDays(2), sizeBytes: 30),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 70));

        Assert.Equal(["old", "middle"], plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_SizeRuleStartsFromProjectsRemainingAfterAgeRule()
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("a", Now - TimeSpan.FromDays(40), sizeBytes: 5L * 1024 * 1024 * 1024),
                Eligible("b", Now - TimeSpan.FromDays(2), sizeBytes: 4L * 1024 * 1024 * 1024),
                Eligible("c", Now - TimeSpan.FromDays(1), sizeBytes: 3L * 1024 * 1024 * 1024),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 10L * 1024 * 1024 * 1024));

        Assert.Equal(["a"], plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_DraftsAndPinnedProjectsAreExempt()
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("draft", Now - TimeSpan.FromDays(90), isDraft: true),
                Eligible("pinned", Now - TimeSpan.FromDays(90), keepSources: true),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 1));

        Assert.Empty(plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_DisabledRulesDoNotDeleteEligibleProjects()
    {
        var plan = StudioCleanupPolicy.Plan(
            [Eligible("old-large", Now - TimeSpan.FromDays(90), sizeBytes: 1000)],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 0));

        Assert.Empty(plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_FlatProjectWithMissingFileIsDeleted()
    {
        var plan = StudioCleanupPolicy.Plan(
            [Eligible("flat", Now, isFlat: true, externalVideoExists: false)],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 0));

        Assert.Equal(["flat"], plan.ProjectIdsToDelete);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("large")]
    [InlineData("flat")]
    public void Plan_InUseProjectsAreExcludedFromEveryRuleButStillCountTowardSize(string inUseId)
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("old", Now - TimeSpan.FromDays(90), sizeBytes: 60),
                Eligible("large", Now - TimeSpan.FromDays(3), sizeBytes: 60),
                Eligible("flat", Now, isFlat: true, externalVideoExists: false, sizeBytes: 60),
                Eligible("candidate", Now - TimeSpan.FromDays(2), sizeBytes: 60),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 100),
            [inUseId]);

        Assert.DoesNotContain(inUseId, plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_SizeRuleCountsOnlyWhatCleanupMayRemove()
    {
        // Each of the four that cleanup never removes is over the limit by itself.
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("draft", Now - TimeSpan.FromDays(9), isDraft: true, sizeBytes: 500),
                Eligible("pinned", Now - TimeSpan.FromDays(8), keepSources: true, sizeBytes: 500),
                Eligible("flat", Now - TimeSpan.FromDays(7), isFlat: true, sizeBytes: 500),
                Eligible("video-gone", Now - TimeSpan.FromDays(6), exportMissing: true, sizeBytes: 500),
                Eligible("old", Now - TimeSpan.FromDays(3), sizeBytes: 40),
                Eligible("new", Now - TimeSpan.FromDays(1), sizeBytes: 60),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 100));

        Assert.Empty(plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_SizeRuleStopsOnceWhatCleanupMayRemoveIsUnderTheLimit()
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("draft", Now - TimeSpan.FromDays(9), isDraft: true, sizeBytes: 500),
                Eligible("old", Now - TimeSpan.FromDays(3), sizeBytes: 40),
                Eligible("middle", Now - TimeSpan.FromDays(2), sizeBytes: 30),
                Eligible("new", Now - TimeSpan.FromDays(1), sizeBytes: 60),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 100));

        // 130 of removable projects, and 90 without the oldest. The draft's 500 are not in it.
        Assert.Equal(["old"], plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_AnExportedProjectWhoseVideosAreAllGoneIsKeptAsADraftIs()
    {
        var plan = StudioCleanupPolicy.Plan(
            [Eligible("video-gone", Now - TimeSpan.FromDays(90), exportMissing: true, sizeBytes: 500)],
            Now,
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 1));

        Assert.Empty(plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_AnInUseProjectCountsTowardTheLimitAndIsNotTheOneThatGoes()
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("open", Now - TimeSpan.FromDays(3), sizeBytes: 80),
                Eligible("closed", Now - TimeSpan.FromDays(2), sizeBytes: 30),
                Eligible("last", Now - TimeSpan.FromDays(1), sizeBytes: 30),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 100),
            ["open"]);

        // 140 with the open one counted, and 60 without it, which would be under the limit.
        Assert.Equal(["closed"], plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_SizeRuleNeverDeletesTheProjectOpenedLast()
    {
        var rules = new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 50);

        // By itself over the limit: it stays. It would otherwise go the moment its editor closed.
        Assert.Empty(StudioCleanupPolicy.Plan(
            [Eligible("only", Now - TimeSpan.FromDays(1), sizeBytes: 80)],
            Now,
            rules).ProjectIdsToDelete);

        // With others, they go first, and it stays although what is left is still over the limit.
        Assert.Equal(
            ["oldest", "older"],
            StudioCleanupPolicy.Plan(
                [
                    Eligible("last", Now - TimeSpan.FromDays(1), sizeBytes: 80),
                    Eligible("older", Now - TimeSpan.FromDays(3), sizeBytes: 10),
                    Eligible("oldest", Now - TimeSpan.FromDays(5), sizeBytes: 10),
                ],
                Now,
                rules).ProjectIdsToDelete);

        // The one opened last and one that is open: neither is the one that goes.
        Assert.Empty(StudioCleanupPolicy.Plan(
            [
                Eligible("open", Now - TimeSpan.FromDays(3), sizeBytes: 80),
                Eligible("last", Now - TimeSpan.FromDays(1), sizeBytes: 30),
            ],
            Now,
            rules,
            ["open"]).ProjectIdsToDelete);

        // The one opened last is the one that is open, and an older one is closed: the older
        // one goes. What stays is the project opened last, not the last of those that are closed.
        Assert.Equal(
            ["closed"],
            StudioCleanupPolicy.Plan(
                [
                    Eligible("closed", Now - TimeSpan.FromDays(3), sizeBytes: 80),
                    Eligible("open", Now - TimeSpan.FromDays(1), sizeBytes: 30),
                ],
                Now,
                rules,
                ["open"]).ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_TheAgeRuleStillDeletesTheProjectOpenedLast()
    {
        var plan = StudioCleanupPolicy.Plan(
            [Eligible("only", Now - TimeSpan.FromDays(31), sizeBytes: 80)],
            Now,
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 50));

        Assert.Equal(["only"], plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_AProjectThatDoesNotSayWhenItWasLastOpenedIsKeptAndNotCounted()
    {
        // A project file without the time reads as 1970: older than any rule allows, and the
        // first in line for the limit.
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("undated", DateTimeOffset.UnixEpoch, sizeBytes: 500),
                Eligible("old", Now - TimeSpan.FromDays(3), sizeBytes: 40),
                Eligible("new", Now - TimeSpan.FromDays(1), sizeBytes: 60),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 30, SizeCapBytes: 100));

        Assert.Empty(plan.ProjectIdsToDelete);
    }

    [Fact]
    public void Plan_SizeRuleBreaksLastOpenedTiesByOrdinalId()
    {
        var plan = StudioCleanupPolicy.Plan(
            [
                Eligible("b", Now, sizeBytes: 60),
                Eligible("a", Now, sizeBytes: 60),
            ],
            Now,
            new StudioCleanupOptions(RetentionDays: 0, SizeCapBytes: 60));

        Assert.Equal(["a"], plan.ProjectIdsToDelete);
    }

    private static StudioProjectSummary Eligible(
        string id,
        DateTimeOffset lastOpenedAt,
        bool isDraft = false,
        bool isFlat = false,
        bool keepSources = false,
        long sizeBytes = 1,
        bool externalVideoExists = true,
        bool exportMissing = false) =>
        new(id, id, Now, lastOpenedAt, isDraft, isFlat, keepSources, sizeBytes, externalVideoExists, exportMissing);
}
