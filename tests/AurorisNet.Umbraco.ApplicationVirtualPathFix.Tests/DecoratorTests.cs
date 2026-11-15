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
    /// Checks that a forwarding proxy runs the replacement for a named member and passes every other call to the wrapped
    /// service, including a member with a default body, which reaches the service's own implementation. That is the case
    /// of a member Umbraco adds after the package was built.
    /// </summary>
    [Fact]
    public void Forwarding_proxies_pass_every_member_except_replaced_ones()
    {
        var proxy = ForwardingProxy<IForwarded>.Wrap(new Implementation(), new Dictionary<string, Func<object?[], object?>>
        {
            [nameof(IForwarded.Replaced)] = args => $"replaced {args[0]}",
        });
        Assert.Equal("replaced 1", proxy.Replaced(1));
        Assert.Equal("wrapped 2", proxy.Existing(2));
        Assert.Equal("wrapped added", proxy.Added());
    }

    /// <summary>
    /// Checks that an exception from the wrapped service reaches the caller as it was thrown, rather than wrapped in a
    /// <see cref="System.Reflection.TargetInvocationException"/>.
    /// </summary>
    [Fact]
    public void Forwarding_proxies_rethrow_the_wrapped_service_exception()
    {
        var proxy = ForwardingProxy<IForwarded>.Wrap(new Implementation(), new Dictionary<string, Func<object?[], object?>>());
        Assert.Equal("wrapped -1", Assert.Throws<ArgumentOutOfRangeException>(() => proxy.Existing(-1)).ParamName);
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

    /// <summary>A service for the forwarding proxy, with a member added later with a default body.</summary>
    internal interface IForwarded
    {
        /// <summary>Stands for the member a decorator replaces.</summary>
        string Replaced(int value);

        /// <summary>Stands for a member the decorator passes on.</summary>
        string Existing(int value);

        /// <summary>Stands for a member added later; its default body returns <c>default</c>.</summary>
        string Added() => "default";
    }

    /// <summary>The wrapped service, which implements every member of <see cref="IForwarded"/>, the later one included.</summary>
    private sealed class Implementation : IForwarded
    {
        /// <summary>Returns <c>wrapped</c> and <paramref name="value"/>; the proxy replaces this member.</summary>
        public string Replaced(int value) => $"wrapped {value}";

        /// <summary>
        /// Returns <c>wrapped</c> and <paramref name="value"/>. Throws <see cref="ArgumentOutOfRangeException"/>, with that
        /// text as its parameter name, when <paramref name="value"/> is negative.
        /// </summary>
        public string Existing(int value)
            => value < 0 ? throw new ArgumentOutOfRangeException($"wrapped {value}") : $"wrapped {value}";

        /// <summary>Returns <c>wrapped added</c>, which shows that the call reached this implementation.</summary>
        public string Added() => "wrapped added";
    }
}
