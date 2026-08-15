using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Every value type the insert path can hand to the ClickHouse client is rooted in
/// <c>rd.xml</c>.
///
/// <b>What breaks without this.</b> Octonica dispatches a column by doing
/// <c>Activator.CreateInstance(typeof(Dispatcher&lt;&gt;).MakeGenericType(t))</c>. Under NativeAOT
/// there is no JIT, and a generic instantiation over a <i>value</i> type has no shared canonical
/// code to fall back on — so if ILC was never told to emit <c>Dispatcher&lt;decimal&gt;</c>, the
/// call throws at runtime. The published binary still returns 202 on ingest, still passes
/// <c>/health</c> and <c>/ready</c>, and never commits a row.
///
/// <b>Why a test can catch it at all.</b> It cannot catch it dynamically: <c>dotnet test</c> runs
/// on the JIT, where <c>MakeGenericType</c> always works, so the AOT failure is invisible to every
/// test in this project. What it can catch is the <i>static</i> half — that <c>rd.xml</c> is an
/// enumeration, and the set of types the buffers produce has outgrown it. Declaring a measure of a
/// type nobody rooted is the realistic way this breaks, and it costs a production outage to find
/// out otherwise.
///
/// The required set is read out of the buffers themselves rather than written down here. A
/// hand-maintained list would be a third copy to keep in step, which is the failure this guards.
/// </summary>
public class RuntimeDirectivesCoverageTests
{
    private static string RuntimeDirectives()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Nealytics.Engine", "rd.xml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "src/Nealytics.Engine/rd.xml is missing. The csproj references it as an RdXmlFile, and "
            + "without it a published AOT binary accepts events and stores none of them.");
    }

    /// <summary>
    /// One buffer set covering every declarable type, built through the real construction path so
    /// the element types are the ones production actually produces.
    /// </summary>
    private static TelemetryColumnBuffers AllTypes()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions =
            [
                new DimensionOptions { Name = "d_string", Type = "String" },
                new DimensionOptions { Name = "d_lowcard", Type = "LowCardinality" },
                new DimensionOptions { Name = "d_uint", Type = "UInt64" },
                new DimensionOptions { Name = "d_int", Type = "Int64" },
                new DimensionOptions { Name = "d_datetime", Type = "DateTime" },
            ],
            Measures =
            [
                new MeasureOptions { Name = "m_uint8", Type = "UInt8" },
                new MeasureOptions { Name = "m_uint16", Type = "UInt16" },
                new MeasureOptions { Name = "m_uint32", Type = "UInt32" },
                new MeasureOptions { Name = "m_uint64", Type = "UInt64" },
                new MeasureOptions { Name = "m_int16", Type = "Int16" },
                new MeasureOptions { Name = "m_int32", Type = "Int32" },
                new MeasureOptions { Name = "m_int64", Type = "Int64" },
                new MeasureOptions { Name = "m_float32", Type = "Float32" },
                new MeasureOptions { Name = "m_float64", Type = "Float64" },
                new MeasureOptions { Name = "m_decimal", Type = "Decimal" },
            ],
        };

        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);

        TelemetryColumnBuffers buffers = new(1, new TelemetryColumnLayout(dimensions, measures));
        buffers.Fill([new GlobalTelemetryPayload
        {
            ProjectId = "p",
            TenantId = "t",
            SessionId = "s",
            EventType = "e",
        }]);

        return buffers;
    }

    /// <summary>
    /// The element type of every column segment: <c>ArraySegment&lt;T&gt;</c> becomes <c>T</c>.
    /// This is exactly what Octonica sees, so it is exactly what ILC must have code for.
    /// </summary>
    private static HashSet<Type> RequiredTypes()
    {
        using TelemetryColumnBuffers buffers = AllTypes();

        HashSet<Type> types = [];

        foreach (object? segment in buffers.BuildColumns().Values)
        {
            if (segment is null) continue;

            Type segmentType = segment.GetType();
            if (segmentType.IsGenericType && segmentType.GetGenericTypeDefinition() == typeof(ArraySegment<>))
            {
                types.Add(segmentType.GetGenericArguments()[0]);
            }
        }

        return types;
    }

    /// <summary>
    /// How a CLR type appears inside a runtime directive, e.g. <c>ulong?</c> becomes
    /// <c>System.Nullable`1[[System.UInt64, System.Private.CoreLib]]</c>.
    /// </summary>
    private static string DirectiveName(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);

        return underlying is null
            ? $"{type.FullName}, System.Private.CoreLib"
            : $"System.Nullable`1[[{underlying.FullName}, System.Private.CoreLib]], System.Private.CoreLib";
    }

    private const string Dispatcher =
        "Octonica.ClickHouseClient.Utils.TypeDispatcher+Dispatcher`1";

    /// <summary>
    /// The dispatchers whose type parameter carries <c>NotNullableValueTypeConstraint</c>, so the
    /// argument is always the <i>underlying</i> struct of a nullable column and never
    /// <c>Nullable&lt;T&gt;</c>. Read off the assembly's own generic parameter attributes, not
    /// guessed: a <c>Nullable&lt;T&gt;</c> entry here would name an instantiation the constraint
    /// forbids, and would root nothing while looking like coverage.
    ///
    /// <c>NullableTableColumn+NullableObjTableColumnDispatcher</c> is deliberately absent — it is
    /// reference-constrained, and reference types share canonical code under AOT.
    /// </summary>
    private static readonly string[] UnderlyingOnlyDispatchers =
    [
        "Octonica.ClickHouseClient.Types.NullableTypeInfo+ValueOrDefaultListDispatcher`1",
        "Octonica.ClickHouseClient.Types.NullableTypeInfo+NullableStructParameterWriterDispatcher`1",
        "Octonica.ClickHouseClient.Types.NullableTableColumn+NullableStructTableColumnDispatcher`1",
    ];

    [Fact]
    public void TheDirectivesFileExists()
    {
        // Not decoration. It is untracked-by-default new-file territory, and a commit that ships
        // the C# without it produces a binary that builds, starts, answers /health and loses every
        // row -- which is the same shape as every other silent-loss failure in this estate.
        RuntimeDirectives().Should().Contain("Dispatcher");
    }

    [Fact]
    public void TheBuffersProduceTheTypesWeThinkTheyDo()
    {
        // Guards the guard: if BuildColumns stopped returning ArraySegments, every assertion below
        // would pass over an empty set.
        HashSet<Type> required = RequiredTypes();

        required.Should().Contain(typeof(Guid), "event_id is a Guid and was the first AOT failure");
        required.Should().Contain(typeof(decimal?), "a Decimal measure is the widest declarable type");
        required.Count.Should().BeGreaterThan(8);
    }

    public static TheoryData<string> EveryRequiredType()
    {
        TheoryData<string> data = [];

        foreach (Type type in RequiredTypes())
        {
            // Reference types share canonical generic code under AOT, so they need no directive.
            // Only value types have no code to find.
            if (!type.IsValueType) continue;

            data.Add(type.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRequiredType))]
    public void EveryValueTypeTheInsertPathProducesIsRooted(string typeName)
    {
        Type type = RequiredTypes().First(candidate => candidate.FullName == typeName);
        string directives = RuntimeDirectives();

        directives.Should().Contain(
            $"{Dispatcher}[[{DirectiveName(type)}]]",
            $"Octonica reaches Dispatcher<{typeName}> through MakeGenericType, and a value-type "
            + "instantiation ILC was not told to emit has no code at runtime. The binary would "
            + "still return 202 and pass every health check while storing nothing. dotnet test "
            + "cannot catch that dynamically -- the test host is JIT -- so this static check is "
            + "the only thing standing between a new declared type and a silent production outage.");
    }

    public static TheoryData<string, string> EveryNullableDispatcherAndType()
    {
        TheoryData<string, string> data = [];

        foreach (Type type in RequiredTypes())
        {
            Type? underlying = Nullable.GetUnderlyingType(type);
            if (underlying is null) continue;

            foreach (string dispatcher in UnderlyingOnlyDispatchers)
            {
                data.Add(dispatcher, underlying.FullName!);
            }
        }

        return data;
    }

    /// <summary>
    /// <c>Dispatcher&lt;T&gt;</c> is not the only type Octonica reaches through
    /// <c>MakeGenericType</c>, and rooting it is not enough.
    ///
    /// <b>How this was found.</b> The AOT smoke run got past every <c>Dispatcher&lt;T&gt;</c> and
    /// then died on
    /// <c>NullableTypeInfo+ValueOrDefaultListDispatcher`1[System.UInt64] is missing native code</c>
    /// — while this very test file was green. The earlier assertion looked for the type <i>name</i>
    /// anywhere in the file, so <c>Nullable`1[[System.UInt64]]</c> matched the <c>Dispatcher</c>
    /// line and the missing dispatcher read as covered. A guard that matches a substring of a
    /// different entry is worse than no guard: it reports the thing it failed to check.
    ///
    /// The nullable dispatchers only became reachable when measures introduced the first nullable
    /// numeric columns, which is why an rd.xml that was correct when it was written stopped being
    /// correct without anyone touching it.
    ///
    /// The three names come from disassembling the shipped assembly and taking every arity-1
    /// generic type that reaches a <c>MakeGenericType</c>/<c>Activator.CreateInstance</c> call
    /// site, not from reading a stack trace one failure at a time. Each round of that costs a
    /// multi-hour ILC publish to discover the next one.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryNullableDispatcherAndType))]
    public void EveryNullableColumnDispatcherIsRootedForItsUnderlyingType(
        string dispatcher, string underlyingType)
    {
        RuntimeDirectives().Should().Contain(
            $"{dispatcher}[[{underlyingType}, System.Private.CoreLib]]",
            $"a nullable column of {underlyingType} routes through {dispatcher}, which ILC will "
            + "not emit unless the closed instantiation is named. Missing it fails on the write "
            + "path with a 202 already returned to the caller, and on the read path as a 500 from "
            + "an endpoint that worked yesterday.");
    }

    [Fact]
    public void AMissingRootWouldActuallyFail()
    {
        // A guard nobody has seen fail is a guard nobody has written.
        string directives = RuntimeDirectives();

        directives.Should().NotContain(
            DirectiveName(typeof(System.Numerics.BigInteger)),
            "no column produces a BigInteger, so if this were present the check above would be "
            + "matching something other than what it claims to");

        // The specific way the earlier version of this file was fooled: it asserted on a bare type
        // name, which is a substring of every entry mentioning that type. Naming the dispatcher is
        // what makes the assertion about the dispatcher.
        foreach (string dispatcher in UnderlyingOnlyDispatchers)
        {
            directives.Should().NotContain(
                $"{dispatcher}[[System.Nullable`1",
                $"{dispatcher} is constrained to non-nullable value types, so a Nullable<T> "
                + "instantiation of it cannot exist. An entry naming one is dead weight that "
                + "reads as coverage.");
        }
    }
}
