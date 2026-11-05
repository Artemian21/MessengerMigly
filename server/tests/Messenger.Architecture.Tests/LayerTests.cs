using Messenger.Application;
using Messenger.Domain.Common;
using NetArchTest.Rules;
using Shouldly;

namespace Messenger.Architecture.Tests;

/// <summary>NFR-08: Clean Architecture dependency rules.</summary>
public sealed class LayerTests
{
    private static readonly System.Reflection.Assembly Domain = typeof(Entity).Assembly;
    private static readonly System.Reflection.Assembly Application = typeof(DependencyInjection).Assembly;

    [Fact]
    public void Domain_has_no_framework_or_outer_layer_dependencies()
    {
        var result = Types.InAssembly(Domain).ShouldNot().HaveDependencyOnAny(
            "Messenger.Application", "Messenger.Infrastructure", "Messenger.Api",
            "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "MediatR", "FluentValidation", "Microsoft.Extensions")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_api_or_database_provider()
    {
        var result = Types.InAssembly(Application).ShouldNot().HaveDependencyOnAny(
            "Messenger.Infrastructure", "Messenger.Api", "Microsoft.AspNetCore", "Npgsql", "BCrypt", "Microsoft.IdentityModel")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_project_references_only_domain()
    {
        var references = Application.GetReferencedAssemblies().Select(a => a.Name).Where(n => n!.StartsWith("Messenger.")).ToList();

        references.ShouldBe(["Messenger.Domain"]);
    }

    [Fact]
    public void Handlers_are_sealed()
    {
        var result = Types.InAssembly(Application).That().HaveNameEndingWith("Handler").Should().BeSealed().GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
