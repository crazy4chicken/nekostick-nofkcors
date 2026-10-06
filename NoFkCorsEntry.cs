using System.Collections.Immutable;
using System.Linq;
using Nekolla.Nekostick.Contracts;

namespace Nekostick.NoFkCors;

/// <summary>
/// Disables CORS enforcement host-wide: every CORS request is answered with an allow-all policy.
/// </summary>
public sealed class NoFkCorsEntry : IExtensionEntry
{
    /// <summary>Registers a <see cref="ExtensionRouteEventStage.Return"/> stage route hook for the extension generation.</summary>
    /// <exception cref="InvalidOperationException">The host API &gt;= 1.3 (RouteEvents) is unavailable, or hook registration failed.</exception>
    public ValueTask StartAsync(IExtensionStartContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Host is not IExtensionHostBridge13 bridge13
            || !ExtensionAbi.IsCompatible(new HostApiVersion(1, 3, 0), bridge13.ApiVersion))
        {
            throw new InvalidOperationException(
                "Host API >= 1.3 (IExtensionHostBridge13.RouteEvents) is required for CORS interception.");
        }

        var result = bridge13.RouteEvents.TryRegisterHook(ExtensionRouteEventStage.Return, OnReturnAsync);
        if (result is ExtensionRouteRegistrationFailureResult failure)
        {
            throw new InvalidOperationException(
                $"Route hook registration failed with {failure.Code}: {failure.Detail.Message}");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hook registrations are generation-scoped: when the host stops or unloads the extension generation
    /// (i.e. the extension is disabled), all registered hooks are released automatically, which removes
    /// the interception. No explicit unregistration API exists and none is needed here.
    /// </summary>
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Fail-open: any unexpected problem (including snapshot validation errors) falls through to
    /// <see cref="ExtensionRouteHookAction.Continue"/> so the host never cancels the request because of this hook.
    /// </summary>
    private static ValueTask<ExtensionRouteHookResult> OnReturnAsync(
        ExtensionRouteHookContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;

            // Gate everything on CORS requests: no non-empty Origin header => pass through untouched.
            bool hasOrigin = request.Headers.TryGetValue("Origin", out var origins)
                && !origins.IsDefaultOrEmpty;
            if (!hasOrigin)
            {
                return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
            }

            // Preflight: OPTIONS + Access-Control-Request-Method => replace the whole response,
            // success even when the upstream target errors or does not implement OPTIONS.
            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)
                && request.Headers.ContainsKey("Access-Control-Request-Method"))
            {
                var preflight = new ExtensionRouteResponseSnapshot(
                    statusCode: 204,
                    headers:
                    [
                        Header("Access-Control-Allow-Origin", "*"),
                        Header("Access-Control-Allow-Methods", "*"),
                        Header("Access-Control-Allow-Headers", "*"),
                        // Max-Age 60: browsers cache a preflight per URL+method for one minute
                        // to bound preflight volume, so disabling the extension restores the
                        // original CORS policy within ~60s at worst.
                        Header("Access-Control-Max-Age", "60"),
                    ]);
                return ValueTask.FromResult(
                    new ExtensionRouteHookResult(ExtensionRouteHookAction.ReplaceResponse, response: preflight));
            }

            var response = context.Response;

            // Non-preflight CORS request without a response (or already truncated at the snapshot cap,
            // where replacing would corrupt the upstream body) passes through.
            if (response is null || response.Body.Length >= ExtensionRouteSnapshotLimits.MaximumBodyBytes)
            {
                return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
            }

            // Erase the upstream CORS policy, keep everything else, then stamp allow-all.
            var merged = response.Headers
                .Where(pair => !pair.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                .Select(pair => new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value.AsEnumerable()));

            var allowAll = new ExtensionRouteResponseSnapshot(
                response.StatusCode,
                merged.Append(Header("Access-Control-Allow-Origin", "*"))
                      .Append(Header("Access-Control-Expose-Headers", "*")),
                response.Body.AsMemory());

            return ValueTask.FromResult(
                new ExtensionRouteHookResult(ExtensionRouteHookAction.ReplaceResponse, response: allowAll));
        }
        catch
        {
            return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
        }
    }

    private static KeyValuePair<string, IEnumerable<string>> Header(string name, string value) =>
        new(name, new[] { value });
}