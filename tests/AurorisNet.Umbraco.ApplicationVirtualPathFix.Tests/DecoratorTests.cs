namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>Checks that the package's decorators forward every member of the Umbraco interfaces they implement.</summary>
public sealed class DecoratorTests
{
    /// <summary>
    /// Checks that the package's classes implement every member of their Umbraco interfaces themselves, so a decorator
    /// forwards each call to the service it wraps. It also confirms that the detection finds a member left to its
    /// default body.
    /// </summary>
    [Fact]
    public void Umbraco_interface_members_are_implemented_rather_than_defaulted()
    {
        // Umbraco adds interface members with default bodies, as it added IForgotPasswordUriProvider's
        // CanCreateForgotPasswordUriAsync in 18.3. A decorator written before such a member still compiles, but it
        // runs the default body instead of forwarding to the service it wraps.
        var defaulted = typeof(ApplicationVirtualPathFixComposer).Assembly.GetTypes()
            .SelectMany(type => DefaultedMembers(type, service => service.Namespace?.StartsWith("Umbraco.", StringComparison.Ordinal) == true));
        Assert.Empty(defaulted);
        // Confirm that the detection finds a member left to its default body.
        Assert.Equal(["Partial: IExample.Added"], DefaultedMembers(typeof(Partial), _ => true));
    }

    /// <summary>
    /// Lists, as <c>Type: Interface.Member</c>, the members of <paramref name="type"/>'s interfaces selected by
    /// <paramref name="include"/> that run the interface's default body. Abstract and open generic classes, and types
    /// other than classes, have none.
    /// </summary>
    private static IEnumerable<string> DefaultedMembers(Type type, Func<Type, bool> include)
    {
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            return [];
        return type.GetInterfaces().Where(include).SelectMany(service =>
        {
            // A member without its own implementation maps to the interface's default body.
            var map = type.GetInterfaceMap(service);
            return map.TargetMethods.Where(method => method.DeclaringType == service).Select(method => $"{type.Name}: {service.Name}.{method.Name}");
        });
    }

    /// <summary>An interface with a member added later with a default body, as Umbraco adds them.</summary>
    private interface IExample
    {
        /// <summary>Stands for a member that existed when the decorator was written.</summary>
        void Existing();

        /// <summary>Stands for a member added later with a default body, which does nothing.</summary>
        void Added() { }
    }

    /// <summary>
    /// Implements only <see cref="IExample.Existing"/>, leaving <see cref="IExample.Added"/> to its default body.
    /// </summary>
    private sealed class Partial : IExample
    {
        /// <summary>Does nothing; the check needs only an implementation to exist.</summary>
        public void Existing() { }
    }
}
