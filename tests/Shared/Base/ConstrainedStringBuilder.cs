using System.Reflection;
using AutoFixture.Kernel;

namespace Nealytics.Engine.Tests.Shared.Base;

public class ConstrainedStringBuilder : ISpecimenBuilder
{
    private readonly int _maxLength;

    public ConstrainedStringBuilder(int maxLength) => _maxLength = maxLength;

    public object Create(object request, ISpecimenContext context)
    {
        if (request is PropertyInfo pi && pi.PropertyType == typeof(string))
        {
            return new string('x', Math.Min(_maxLength, 50));
        }

        return new NoSpecimen();
    }
}
