namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

public sealed class DecoratorTests
{
    [Fact]
    public void Umbraco_interface_members_are_implemented_rather_than_defaulted()
    {
        // Umbraco adds interface members with default bodies, as IForgotPasswordUriProvider's
        // CanCreateForgotPasswordUriAsync was in 18.3. A decorator written before such a member still
        // compiles afterwards, but runs the default body instead of forwarding to the service it wraps.
        var defaulted = typeof(ApplicationVirtualPathFixComposer).Assembly.GetTypes()
            .SelectMany(type => DefaultedMembers(type, service => service.Namespace?.StartsWith("Umbraco.", StringComparison.Ordinal) == true));
        Assert.Empty(defaulted);
        // The check itself notices a member left to its default body.
        Assert.Equal(["Partial: IExample.Added"], DefaultedMembers(typeof(Partial), _ => true));
    }

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

    private interface IExample
    {
        void Existing();

        void Added() { }
    }

    private sealed class Partial : IExample
    {
        public void Existing() { }
    }
}
