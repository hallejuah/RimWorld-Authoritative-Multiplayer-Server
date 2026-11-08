using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Multiplayer.Authoritative;

namespace Multiplayer.Authoritative.Transport;

// Credentials are validated against an application-supplied authority. The
// session ID is assigned by the authenticator, never trusted from the wire.
public interface ILoopbackAuthenticator
{
    bool TryAuthenticate(string credential, out string authenticatedSessionId);
}

// A bounded LOOPBACK-ONLY protocol experiment. Do not expose it to LAN or WAN:
// credentials are sent in cleartext and no TLS, persistence or rate limit exists.
public sealed class LoopbackTransportServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly ILoopbackAuthenticator authenticator;
    private readonly AuthoritativeSimulationHost host;
    private readonly StatePublicationHub feed;
    private readonly SemaphoreSlim slots;
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private bool started;
    private volatile bool stopping;

    public int Port { get; private set; }

    public LoopbackTransportServer(ILoopbackAuthenticator authenticator,
        AuthoritativeSimulationHost host, StatePublicationHub feed, int maxClients = 8)
    {
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.feed = feed ?? throw new ArgumentNullException(nameof(feed));
        if (maxClients <= 0) throw new ArgumentOutOfRangeException(nameof(maxClients));
        slots = new SemaphoreSlim(maxClients, maxClients);
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException("Transport already started");
        if (stopping) throw new ObjectDisposedException(nameof(LoopbackTransportServer));
        // Refuse startup unless an initial snapshot has been published.
        feed.LatestSnapshot();
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        started = true;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (stopping) { break; }
            catch (SocketException) when (stopping) { break; }

            if (!slots.Wait(0))
            {
                client.Close();
                continue;
            }

            clients.TryAdd(client, 0);
            _ = ServeClientAsync(client);
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                WireFrame? first;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    first = await WireFrameCodec.ReadAsync(stream, timeout.Token)
                        .ConfigureAwait(false);

                if (first == null || first.Kind != WireFrameKind.Hello)
                    return;
                var hello = WireFrameCodec.Deserialize<WireHello>(first.Payload);
                if (hello.ProtocolVersion != AuthoritativeProtocol.Version ||
                    hello.Credential is null ||
                    !authenticator.TryAuthenticate(hello.Credential, out var session) ||
                    string.IsNullOrWhiteSpace(session))
                {
                    await SendErrorAsync(stream, "Protocol or credentials rejected").ConfigureAwait(false);
                    return;
                }

                await WireFrameCodec.WriteAsync(stream, WireFrameKind.Snapshot,
                    ProtocolCodec.EncodeSnapshot(feed.LatestSnapshot())).ConfigureAwait(false);

                while (!stopping)
                {
                    var request = await WireFrameCodec.ReadAsync(stream).ConfigureAwait(false);
                    if (request is null) break;
                    if (request.Kind == WireFrameKind.Command)
                    {
                        var command = ProtocolCodec.DecodeCommand(request.Payload);
                        // Do not execute on the networking thread. Pump() must
                        // run on the owning Unity simulation thread.
                        var receipt = await host.EnqueueCommand(session, command).ConfigureAwait(false);
                        await WireFrameCodec.WriteAsync(stream, WireFrameKind.Receipt,
                            WireFrameCodec.Serialize(receipt)).ConfigureAwait(false);
                    }
                    else if (request.Kind == WireFrameKind.Poll)
                    {
                        var poll = WireFrameCodec.Deserialize<WirePoll>(request.Payload);
                        if (feed.TryReadAfter(poll.AfterVersion, out var changes))
                        {
                            foreach (var change in changes)
                                await WireFrameCodec.WriteAsync(stream, WireFrameKind.Delta,
                                    ProtocolCodec.EncodeDelta(change)).ConfigureAwait(false);
                        }
                        else
                        {
                            await WireFrameCodec.WriteAsync(stream, WireFrameKind.Snapshot,
                                ProtocolCodec.EncodeSnapshot(feed.LatestSnapshot())).ConfigureAwait(false);
                        }
                        // Explicit batch delimiter, including when there are no changes.
                        await WireFrameCodec.WriteAsync(stream, WireFrameKind.EndPoll,
                            Array.Empty<byte>()).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendErrorAsync(stream, "Unexpected wire operation").ConfigureAwait(false);
                        return;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException || error is SocketException ||
                                      error is ObjectDisposedException ||
                                      error is OperationCanceledException ||
                                      error is System.Text.Json.JsonException ||
                                      error is ArgumentException)
        {
            // Malformed or disconnected local peers are not simulation failures.
            // The connection closes in finally. Never log provided credentials.
        }
        finally
        {
            client.Close();
            clients.TryRemove(client, out _);
            slots.Release();
        }
    }

    private static Task SendErrorAsync(Stream stream, string text) =>
        WireFrameCodec.WriteAsync(stream, WireFrameKind.Error,
            WireFrameCodec.Serialize(new WireError(text)));

    public void Dispose()
    {
        stopping = true;
        listener.Stop();
        foreach (var client in clients.Keys)
            client.Close();
    }
}
