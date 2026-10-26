using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace EagleTunnelApi.Tests.Architecture;

public class ProjectBoundaryTests
{
    private static readonly Assembly ApiAssembly = Assembly.Load("EagleTunnelApi");
    private static readonly Assembly DomainAssembly = Assembly.Load("EagleTunnelApi.Domain");
    private static readonly Assembly ApplicationAssembly = Assembly.Load("EagleTunnelApi.Application");
    private static readonly Assembly InfrastructureAssembly = Assembly.Load("EagleTunnelApi.Infrastructure");

    private static readonly string[] ApiLayerNamespaces =
    [
        "EagleTunnelApi.Configuration",
        "EagleTunnelApi.Logging",
        "EagleTunnelApi.PanelApi",
        "EagleTunnelApi.Telegram",
        "EagleTunnelApi.Webhook"
    ];

    private const string PanelWireModelsNamespace = "EagleTunnelApi.Infrastructure.PanelApi.Models";

    private static readonly string[] ForbiddenTechnicalNamespaces =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Telegram.Bot"
    ];

    [Fact]
    public void Domain_does_not_depend_on_other_projects()
    {
        AssertNoDependency(DomainAssembly,
            ["EagleTunnelApi.Application", "EagleTunnelApi.Infrastructure", .. ApiLayerNamespaces]);
    }

    [Fact]
    public void Domain_does_not_depend_on_technical_frameworks()
    {
        AssertNoDependency(DomainAssembly, [.. ForbiddenTechnicalNamespaces, "Microsoft.Extensions"]);
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        AssertNoDependency(ApplicationAssembly, ["EagleTunnelApi.Infrastructure", .. ApiLayerNamespaces]);
    }

    [Fact]
    public void Application_does_not_depend_on_technical_frameworks()
    {
        AssertNoDependency(ApplicationAssembly, ForbiddenTechnicalNamespaces);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_api()
    {
        AssertNoDependency(InfrastructureAssembly, ApiLayerNamespaces);
    }

    [Fact]
    public void Api_does_not_depend_on_panel_wire_models()
    {
        AssertNoDependency(ApiAssembly, [PanelWireModelsNamespace]);
    }

    private static void AssertNoDependency(Assembly assembly, string[] forbiddenNamespaces)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        var failingTypes = result.FailingTypeNames is null or { Count: 0 }
            ? "(none reported)"
            : string.Join(", ", result.FailingTypeNames);

        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on any of [{string.Join(", ", forbiddenNamespaces)}]. "
            + $"Failing types: {failingTypes}");
    }
}
