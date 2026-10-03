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

    private static StudioProjectSummary Eligible(
        string id,
        DateTimeOffset lastOpenedAt,
        bool isDraft = false,
        bool isFlat = false,
        bool keepSources = false,
        long sizeBytes = 1,
        bool externalVideoExists = true) =>
        new(id, id, Now, lastOpenedAt, isDraft, isFlat, keepSources, sizeBytes, externalVideoExists);
}
