using TinyClips.Core.Support;

namespace TinyClips.Core.Tests;

public class SupporterPlanTests
{
    [Theory]
    [InlineData(SupporterPlans.MonthlyProductId, SupporterPlan.Monthly)]
    [InlineData(SupporterPlans.YearlyProductId, SupporterPlan.Yearly)]
    [InlineData(SupporterPlans.OneTimeProductId, SupporterPlan.OneTime)]
    public void FromProductId_KnowsEachStoreProduct(string productId, SupporterPlan expected) =>
        Assert.Equal(expected, SupporterPlans.FromProductId(productId));

    [Theory]
    [InlineData("some.other.add-on")]
    [InlineData("COM.REFRACTORED.TINYCLIPS.PRO.MONTHLY")]
    [InlineData("")]
    [InlineData(null)]
    public void FromProductId_IgnoresEverythingElse(string? productId) =>
        Assert.Null(SupporterPlans.FromProductId(productId));

    [Fact]
    public void MostLasting_PrefersOneTime_ThenYearly_ThenMonthly_InAnyOrder()
    {
        Assert.Equal(
            SupporterPlan.OneTime,
            SupporterPlans.MostLasting([SupporterPlans.OneTimeProductId, SupporterPlans.MonthlyProductId, SupporterPlans.YearlyProductId]));
        Assert.Equal(
            SupporterPlan.Yearly,
            SupporterPlans.MostLasting([SupporterPlans.MonthlyProductId, SupporterPlans.YearlyProductId]));
    }

    [Fact]
    public void MostLasting_IsNull_WithoutASupporterProduct()
    {
        Assert.Null(SupporterPlans.MostLasting([]));
        Assert.Null(SupporterPlans.MostLasting(["some.other.add-on", null]));
    }
}
