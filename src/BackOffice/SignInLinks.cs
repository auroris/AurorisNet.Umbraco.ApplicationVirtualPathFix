using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services.OperationStatus;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Adds the virtual path to the invitation and password-reset links that Umbraco emails to users.</summary>
/// <remarks>
/// Umbraco 18.2 and 18.3 build these links by replacing the application URL's path with <c>/umbraco/login</c>, which
/// drops the virtual path. Each wrapper replaces only the method that creates a link and passes every other call to
/// Umbraco's provider through <see cref="ForwardingProxy{TService}"/>.
/// </remarks>
internal static class SignInLinks
{
    /// <summary>Wraps Umbraco's invitation link provider so the links it creates include the virtual path.</summary>
    internal static IInviteUriProvider Invitations(IInviteUriProvider inner, IHostingEnvironment hosting)
        => ForwardingProxy<IInviteUriProvider>.Wrap(inner, new Dictionary<string, Func<object?[], object?>>
        {
            [nameof(IInviteUriProvider.CreateInviteUriAsync)] =
                args => WithVirtualPathAsync(inner.CreateInviteUriAsync((IUser)args[0]!), hosting),
        });

    /// <summary>Wraps Umbraco's password-reset link provider so the links it creates include the virtual path.</summary>
    internal static IForgotPasswordUriProvider PasswordResets(IForgotPasswordUriProvider inner, IHostingEnvironment hosting)
        => ForwardingProxy<IForgotPasswordUriProvider>.Wrap(inner, new Dictionary<string, Func<object?[], object?>>
        {
            [nameof(IForgotPasswordUriProvider.CreateForgotPasswordUriAsync)] =
                args => WithVirtualPathAsync(inner.CreateForgotPasswordUriAsync((IUser)args[0]!), hosting),
        });

    /// <summary>Waits for <paramref name="link"/> and adds the virtual path to it with <see cref="WithVirtualPath"/>.</summary>
    private static async Task<Attempt<Uri, UserOperationStatus>> WithVirtualPathAsync(Task<Attempt<Uri, UserOperationStatus>> link,
        IHostingEnvironment hosting)
        => WithVirtualPath(await link, hosting);

    /// <summary>
    /// Adds the virtual path to a successful link. A failed attempt, a site at the root and a link that already has the
    /// virtual path are returned as they are.
    /// </summary>
    internal static Attempt<Uri, UserOperationStatus> WithVirtualPath(Attempt<Uri, UserOperationStatus> attempt, IHostingEnvironment hosting)
    {
        if (!attempt.Success || attempt.Result is not { } uri || VirtualPathHosting.Prefix(hosting) is not { } prefix
            || uri.AbsolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return attempt;

        return Attempt.SucceedWithStatus(attempt.Status, new UriBuilder(uri) { Path = prefix + uri.AbsolutePath }.Uri);
    }
}
