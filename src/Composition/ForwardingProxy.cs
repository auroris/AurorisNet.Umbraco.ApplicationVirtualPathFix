using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>
/// Implements <typeparamref name="TService"/> by passing every call to the service it wraps, except calls to the
/// methods that have a replacement.
/// </summary>
/// <remarks>
/// Umbraco adds interface members with default implementations in minor releases, as it added
/// <c>IForgotPasswordUriProvider.CanCreateForgotPasswordUriAsync</c> in 18.3. The package compiles against the oldest
/// Umbraco release it supports, so a decorator class cannot implement a member added later, and calls to that member
/// would run its default implementation instead of the wrapped service. A proxy receives every call, including calls to
/// members added after the package was built, and passes them on.
/// </remarks>
internal class ForwardingProxy<TService> : DispatchProxy where TService : class
{
    private TService _inner = null!;
    private IReadOnlyDictionary<string, Func<object?[], object?>> _replacements = null!;

    /// <summary>
    /// Creates a <typeparamref name="TService"/> that passes every call to <paramref name="inner"/>, except calls to the
    /// methods named in <paramref name="replacements"/>, which run the replacement with the call's arguments instead.
    /// </summary>
    internal static TService Wrap(TService inner, IReadOnlyDictionary<string, Func<object?[], object?>> replacements)
    {
        var service = Create<TService, ForwardingProxy<TService>>();
        var proxy = (ForwardingProxy<TService>)(object)service;
        proxy._inner = inner;
        proxy._replacements = replacements;
        return service;
    }

    /// <summary>
    /// Runs the replacement for <paramref name="targetMethod"/> when one exists, and otherwise calls the same member on
    /// the wrapped service. An exception from the wrapped service propagates unchanged.
    /// </summary>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (_replacements.TryGetValue(targetMethod.Name, out var replacement))
            return replacement(args);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
    }
}
