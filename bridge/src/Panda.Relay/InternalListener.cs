using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Panda.Relay;

/// <summary>
/// plan-relay-deploy C5 (D5): marks connections that arrived on the loopback listener for the hosted bridge. Kestrel's
/// connection middleware on that endpoint sets the mark, so it never depends on a port number.
/// </summary>
internal static class InternalListener
{
    public static readonly object Key = new();

    public static bool Is(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(Key) == true;
}
