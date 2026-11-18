using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panda.Bridge.Hosting;

namespace Panda.Relay;

/// <summary>
/// The cloud relay (spec §78, plan-relay P4): serves only <c>/v1/browser</c> and <c>/v1/stream</c>; every other path
/// is 404, so no relay page can ever read a connection code's fragment. TLS is terminated in front of it (plan 7c).
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayServer(WebApplication app, RelayOptions options, int port, IPEndPoint? internalEndPoint)
    {
        _app = app;
        Options = options;
        Port = port;
        InternalEndPoint = internalEndPoint;
    }

    public RelayOptions Options { get; }

    /// <summary>The bound port (the configured one, or the free one picked for port 0).</summary>
    public int Port { get; }

    /// <summary>The internal listener's bound port (C5), or null when there is none.</summary>
    public int? InternalPort => InternalEndPoint?.Port;

    /// <summary>The internal listener's bound endpoint (always 127.0.0.1), or null when there is none.</summary>
    internal IPEndPoint? InternalEndPoint { get; }

    internal RelayHub Hub => _app.Services.GetRequiredService<RelayHub>();

    internal IHostApplicationLifetime Lifetime => _app.Lifetime;

    /// <summary>
    /// Completes once the host was asked to stop (SIGTERM / Ctrl+C through the console lifetime, or
    /// <paramref name="cancellationToken"/>) and has stopped: every control socket is closed 1001 and the port is released.
    /// </summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => _app.WaitForShutdownAsync(cancellationToken);

    /// <summary>Starts listening; a bad configuration throws <see cref="ArgumentException"/>, a busy port <see cref="IOException"/>.</summary>
    public static async Task<RelayServer> StartAsync(RelayOptions options, TimeProvider time, ILoggerProvider? logs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        options.Validate();

        ListenOptions? publicListen = null;
        ListenOptions? internalListen = null;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(options.ListenAddress, options.Port, lo => publicListen = lo);
            if (options.InternalPort is { } internalPort) // C5: loopback only, whatever --listen says
            {
                k.Listen(IPAddress.Loopback, internalPort, lo =>
                {
                    internalListen = lo;
                    lo.Use(next => connection =>
                    {
                        connection.Items[InternalListener.Key] = true;
                        return next(connection);
                    });
                });
            }

            k.Limits.MaxRequestBodySize = 0; // no endpoint reads a body
        });
        if (logs is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
        }
        else if (Environment.GetEnvironmentVariable("JOURNAL_STREAM") is not null)
        {
            // plan-vps-logs: stdout is the systemd journal — one line per entry, its level as a <N> priority journald
            // reads (CreateSlimBuilder does not bind Logging:Console:FormatterName from configuration)
            builder.Logging.AddSystemdConsole();
        }

        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        // C2 critic #5: at Debug the forwarded-headers middleware logs header values. A configured rule for that
        // category, a longer one, or a provider-specific one would beat a plain AddFilter (LogPin).
        builder.Services.PostConfigure<LoggerFilterOptions>(o => LogPin.AtLeast(o, ForwardedHeadersCategory, LogLevel.Warning));

        // C2 critic #1: ASPNETCORE_FORWARDEDHEADERS_ENABLED=true makes the host add a forwarded-headers middleware that
        // trusts EVERY peer (MS Learn, proxy-load-balancer); only --trusted-proxy may choose (UseClientIp)
        builder.Configuration["FORWARDEDHEADERS_ENABLED"] = "false";
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton<HostedAdmin>(); // plan-relay-url U3: disposed with the container (in-flight calls end)
        builder.Services.AddSingleton<RelayHub>();
        builder.Services.AddSingleton<ProxyWarning>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        var app = builder.Build();
        app.Use((context, next) =>
        {
            // C5: the internal listener serves only the hosted bridge's dials
            if (InternalListener.Is(context)
                && !(context.Request.Path.Equals("/v1/stream", StringComparison.OrdinalIgnoreCase) && context.Request.Query.ContainsKey("dial")))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
        UseClientIp(app, options); // before anything reads RemoteIpAddress (the per-IP limits)
        app.UseWebSockets(); // keepalive is set per socket (RelayHub.AcceptContext)
        var hub = app.Services.GetRequiredService<RelayHub>();
        app.Map("/v1/browser", hub.RunControlAsync);
        app.Map("/v1/stream", hub.RunStreamAsync);
        app.MapMethods("/c/{relayId}", [HttpMethods.Get, HttpMethods.Head], HelpPage.ServeAsync); // plan-https-code T2
        app.MapFallback(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        app.Lifetime.ApplicationStopping.Register(hub.CloseAll);

        await app.StartAsync(cancellationToken);
        return new RelayServer(app, options, publicListen!.IPEndPoint!.Port, internalListen?.IPEndPoint);
    }

    /// <summary>
    /// plan-relay-deploy C2 (seam S9): with <see cref="RelayOptions.TrustedProxies"/>, RemoteIpAddress becomes the
    /// right-most address of <see cref="RelayOptions.ClientIpHeader"/> when — and only when — the TCP peer is one of
    /// them (ASP.NET ForwardedHeadersMiddleware, ForwardLimit 1; its loopback defaults are cleared). A trusted peer
    /// whose header gives no usable address keeps its own address, and <see cref="ProxyWarning"/> notes it.
    /// </summary>
    private static void UseClientIp(WebApplication app, RelayOptions options)
    {
        if (options.TrustedProxies.Count == 0)
        {
            return;
        }

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardedForHeaderName = options.ClientIpHeader,
            ForwardLimit = 1,
        };
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var network in options.TrustedProxies)
        {
            forwarded.KnownIPNetworks.Add(network);
        }

        var warning = app.Services.GetRequiredService<ProxyWarning>();
        app.UseWhen(context => !InternalListener.Is(context), branch => // C5: public listener only (C2 critic N3)
        {
            branch.Use((context, next) =>
            {
                context.Items[PeerKey] = context.Connection.RemoteIpAddress;
                return next(context);
            });
            branch.UseForwardedHeaders(forwarded);
            branch.Use((context, next) =>
            {
                if (context.Items[PeerKey] is IPAddress peer && Equals(peer, context.Connection.RemoteIpAddress)
                    && options.TrustedProxies.Any(n => n.Contains(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)))
                {
                    warning.Note();
                }

                return next(context);
            });
        });
    }

    private static readonly object PeerKey = new();

    private const string ForwardedHeadersCategory = "Microsoft.AspNetCore.HttpOverrides";

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }
}
