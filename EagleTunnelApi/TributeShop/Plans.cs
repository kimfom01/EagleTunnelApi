namespace EagleTunnelApi.TributeShop;

public enum PlanType
{
    OneTime,
    Weekly,
    Monthly,
    Quarterly,
    HalfYearly,
    Yearly
}

public sealed record SubscriptionPlan(
    PlanType Type,
    string Title,
    int PriceRubles,
    int DurationDays,
    bool IsRecurring,
    string TributePeriod
)
{
    public int AmountKopecks => PriceRubles * 100;

    public string Description => IsRecurring
        ? $"{PriceRubles} ₽ / {PeriodLabel}"
        : $"{PriceRubles} ₽ one-time · {PeriodLabel}";

    private string PeriodLabel => Type switch
    {
        PlanType.Weekly => "week",
        PlanType.Monthly => "month",
        PlanType.Quarterly => "3 months",
        PlanType.HalfYearly => "6 months",
        PlanType.Yearly => "year",
        _ => "1 month"
    };
}

public static class SubscriptionPlans
{
    public const string PlanPrefix = "plan:";

    public static readonly IReadOnlyList<SubscriptionPlan> All =
    [
        new(PlanType.OneTime, "One-time", 300, 30, IsRecurring: false, TributePeriod: "onetime"),
        new(PlanType.Weekly, "Weekly", 100, 7, IsRecurring: true, TributePeriod: "weekly"),
        new(PlanType.Monthly, "Monthly", 300, 30, IsRecurring: true, TributePeriod: "monthly"),
        new(PlanType.Quarterly, "3 Months", 800, 90, IsRecurring: true, TributePeriod: "quarterly"),
        new(PlanType.HalfYearly, "6 Months", 1600, 180, IsRecurring: true, TributePeriod: "halfyearly"),
        new(PlanType.Yearly, "1 Year", 3300, 365, IsRecurring: true, TributePeriod: "yearly")
    ];

    public static SubscriptionPlan? ByPeriod(string period) =>
        All.FirstOrDefault(plan => plan.TributePeriod.Equals(period, StringComparison.OrdinalIgnoreCase));

    public static SubscriptionPlan? ByAmount(int amountKopecks) =>
        All.FirstOrDefault(plan => plan.AmountKopecks == amountKopecks);
}