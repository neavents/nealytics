namespace Nealytics.Engine.Infrastructure.Query;

internal enum FormulaUnitKind
{
    Annotation,
    Dimension,
    Measure
}

internal readonly record struct FormulaUnit(FormulaUnitKind Kind, string Name, string? Dimension);
