using EagleTunnelApi.TributeShop;
using Xunit;

namespace EagleTunnelApi.Tests.TributeShop;

public class SubscriptionPlansTests
{
    [Theory]
    [InlineData(PlanType.Weekly, 100, 7, true, "weekly")]
    [InlineData(PlanType.Monthly, 300, 30, true, "monthly")]
    [InlineData(PlanType.Quarterly, 800, 90, true, "quarterly")]
    [InlineData(PlanType.HalfYearly, 1600, 180, true, "halfyearly")]
    [InlineData(PlanType.Yearly, 3300, 365, true, "yearly")]
    [InlineData(PlanType.OneTime, 300, 30, false, "onetime")]
    public void Catalog_ContainsPlansWithSpecifiedPricing(PlanType type, int priceRubles, int durationDays,
        bool isRecurring, string tributePeriod)
    {
        var plan = Assert.Single(SubscriptionPlans.All, p => p.Type == type);

        Assert.Equal(priceRubles, plan.PriceRubles);
        Assert.Equal(priceRubles * 100, plan.AmountKopecks);
        Assert.Equal(durationDays, plan.DurationDays);
        Assert.Equal(isRecurring, plan.IsRecurring);
        Assert.Equal(tributePeriod, plan.TributePeriod);
    }

    [Fact]
    public void ByPeriod_ReturnsMatchingPlan()
    {
        Assert.Equal(PlanType.Yearly, SubscriptionPlans.ByPeriod("yearly")!.Type);
        Assert.Equal(PlanType.OneTime, SubscriptionPlans.ByPeriod("onetime")!.Type);
        Assert.Null(SubscriptionPlans.ByPeriod("unknown"));
    }

    [Fact]
    public void ByAmount_ReturnsFirstMatchingPlan()
    {
        Assert.Equal(PlanType.OneTime, SubscriptionPlans.ByAmount(30000)!.Type);
        Assert.Equal(PlanType.Weekly, SubscriptionPlans.ByAmount(10000)!.Type);
        Assert.Equal(PlanType.Yearly, SubscriptionPlans.ByAmount(330000)!.Type);
        Assert.Null(SubscriptionPlans.ByAmount(12345));
    }
}