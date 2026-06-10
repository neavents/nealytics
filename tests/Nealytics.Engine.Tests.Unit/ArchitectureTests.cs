using System.Reflection;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

public class ArchitectureTests
{
    private static readonly Assembly SourceAssembly = typeof(Nealytics.Engine.Infrastructure.Configuration.TelemetryEngineOptions).Assembly;
    private static readonly Type[] AllTypes = SourceAssembly.GetTypes()
        .Where(t => !t.Name.Contains('<') && !t.Name.Contains('>'))
        .ToArray();

    // ─── Layer Dependency Rules ───

    [Fact]
    public void InfrastructureTypes_ShouldNotReference_FeaturesNamespace()
    {
        var infraTypes = AllTypes
            .Where(t => t.Namespace?.StartsWith("Nealytics.Engine.Infrastructure") == true
                        && t.Name != "TelemetryAotContext")
            .ToArray();

        var violations = new List<string>();

        foreach (var type in infraTypes)
        {
            var referencedTypes = GetReferencedTypes(type);
            foreach (var refType in referencedTypes)
            {
                if (refType.Namespace?.StartsWith("Nealytics.Engine.Features") == true)
                {
                    violations.Add($"{type.Name} -> {refType.Name}");
                }
            }
        }

        violations.Should().BeEmpty("infrastructure should not depend on feature types");
    }

    [Fact]
    public void ResponseModels_ShouldBeSealed()
    {
        var violations = AllTypes
            .Where(t => t.Name.EndsWith("Response") && !t.IsSealed)
            .Select(t => t.FullName)
            .ToList();

        violations.Should().BeEmpty("response types should be sealed");
    }

    [Fact]
    public void PayloadTypes_ShouldBeSealed()
    {
        var violations = AllTypes
            .Where(t => t.Name.Contains("Payload") && !t.IsSealed)
            .Select(t => t.FullName)
            .ToList();

        violations.Should().BeEmpty("payload types should be sealed");
    }

    [Fact]
    public void QueryClasses_ShouldEndWith_Query()
    {
        var queryTypes = AllTypes
            .Where(t => t.Name.EndsWith("Query"))
            .ToArray();

        queryTypes.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void EndpointClasses_ShouldEndWith_Endpoint()
    {
        var violations = AllTypes
            .Where(t => t.Namespace?.Contains("Features") == true
                        && t.Name.Contains("Endpoint")
                        && !t.Name.EndsWith("Endpoint"))
            .Select(t => t.Name)
            .ToList();

        violations.Should().BeEmpty("endpoint types should end with 'Endpoint'");
    }

    [Fact]
    public void InfrastructureClasses_ShouldBeSealed_UnlessSerializationContext()
    {
        var violations = AllTypes
            .Where(t => t.Namespace?.StartsWith("Nealytics.Engine.Infrastructure") == true
                        && t.IsClass && !t.IsAbstract && !t.IsSealed
                        && !t.Name.EndsWith("Context"))
            .Select(t => t.FullName)
            .ToList();

        violations.Should().BeEmpty("infrastructure classes should be sealed");
    }

    // ─── Helpers ───

    private static HashSet<Type> GetReferencedTypes(Type type)
    {
        var referenced = new HashSet<Type>();

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            CollectType(field.FieldType, referenced);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            CollectType(prop.PropertyType, referenced);
        }

        foreach (var ctor in type.GetConstructors())
        {
            foreach (var param in ctor.GetParameters())
            {
                CollectType(param.ParameterType, referenced);
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            CollectType(method.ReturnType, referenced);
            foreach (var param in method.GetParameters())
            {
                CollectType(param.ParameterType, referenced);
            }
        }

        return referenced;
    }

    private static void CollectType(Type type, HashSet<Type> target)
    {
        target.Add(type);
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                target.Add(arg);
        }
    }
}
