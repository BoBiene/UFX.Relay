using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;

namespace ReverseTunnel.Yarp.Tunnel;

public class Tunnel : IAsyncDisposable, IDisposable
{
    private readonly Channel<MultiplexingStream.Channel> channels = Channel.CreateUnbounded<MultiplexingStream.Channel>();
    private readonly ILogger? logger;
    private readonly object sync = new();
    private readonly long createdTimestamp = Stopwatch.GetTimestamp();
    private MultiplexingStream? stream;
    private bool channelOfferedSubscribed;
    private volatile bool invalidated;
    private long lastActivityTimestamp = Stopwatch.GetTimestamp();
    private long channelsServed;

    public Tunnel(MultiplexingStream stream, ILogger? logger = null)
    {
        this.stream = stream;
        this.logger = logger;
    }

    public Uri? Uri { get; set; }

    /// <summary>Identifies this connection in both peers' logs.</summary>
    public string? ConnectionId { get; set; }

    public Task Completion => stream?.Completion ?? Task.CompletedTask;

    /// <summary>
    /// True while this tunnel can be expected to serve channels: it holds a stream, has not been
    /// invalidated, the stream has not completed, and the transport still reports itself usable.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            MultiplexingStream? current = stream;
            return current is not null && !invalidated && !current.Completion.IsCompleted && IsTransportAlive;
        }
    }

    /// <summary>How long this tunnel has been up.</summary>
    public TimeSpan Age => Stopwatch.GetElapsedTime(createdTimestamp);

    /// <summary>Time since the last channel this tunnel served or accepted.</summary>
    public TimeSpan IdleTime => Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivityTimestamp));

    /// <summary>Channels served since this tunnel was established.</summary>
    public long ChannelsServed => Volatile.Read(ref channelsServed);

    /// <summary>
    /// Whether the underlying transport reports itself usable. A WebSocket that aborts on its
    /// keep-alive timeout does not complete the multiplexing stream, so the transport is the only
    /// place that knows. Overridden by tunnels that own a transport connection.
    /// </summary>
    protected virtual bool IsTransportAlive => true;

    /// <summary>The transport's own description of its state, or "n/a" without a transport.</summary>
    public virtual string DescribeTransport() => "n/a";

    public TunnelDiagnostics GetDiagnostics() => new(
        ConnectionId ?? "unknown",
        DescribeTransport(),
        DescribeMuxCompletion(),
        Age,
        IdleTime,
        ChannelsServed);

    /// <summary>
    /// Marks this tunnel unusable without tearing it down, so <see cref="IsConnected"/> reports
    /// false. Callers that own the tunnel collection should also remove and dispose it.
    /// </summary>
    public void Invalidate(string reason)
    {
        if (invalidated) return;
        invalidated = true;
        logger?.LogInformation("Tunnel invalidated: {Reason}. {Diagnostics}", reason, GetDiagnostics());
    }

    public Task<MultiplexingStream.Channel> GetChannelAsync(string? channelId, CancellationToken cancellationToken = default)
        => GetChannelAsync(channelId, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>
    /// Offers a channel and waits for the peer to accept it, bounded by <paramref name="offerTimeout"/>.
    /// </summary>
    /// <exception cref="TunnelChannelOfferTimeoutException">
    /// The peer did not accept within the timeout. Distinct from <see cref="OperationCanceledException"/>
    /// so callers can tell "this tunnel is not serving channels" from "the caller went away".
    /// </exception>
    public async Task<MultiplexingStream.Channel> GetChannelAsync(string? channelId, TimeSpan offerTimeout, CancellationToken cancellationToken = default)
    {
        MultiplexingStream current = stream ?? throw new ObjectDisposedException(nameof(stream));
        if (channelId == null) return await GetChannelAsync(cancellationToken).ConfigureAwait(false);

        bool bounded = offerTimeout != Timeout.InfiniteTimeSpan;
        using CancellationTokenSource? offerCts = bounded
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        offerCts?.CancelAfter(offerTimeout);

        try
        {
            MultiplexingStream.Channel channel = await current
                .OfferChannelAsync(channelId, offerCts?.Token ?? cancellationToken)
                .ConfigureAwait(false);
            MarkActive();
            return channel;
        }
        catch (OperationCanceledException) when (bounded && !cancellationToken.IsCancellationRequested)
        {
            throw new TunnelChannelOfferTimeoutException(channelId, offerTimeout, Uri);
        }
    }

    public async Task<MultiplexingStream.Channel> GetChannelAsync(CancellationToken cancellationToken = default)
    {
        MultiplexingStream current = stream ?? throw new ObjectDisposedException(nameof(stream));
        EnsureAcceptingOffers();

        var channelResult = channels.Reader.ReadAsync(cancellationToken).AsTask();
        var streamCompletion = current.Completion;
#pragma warning disable VSTHRD003 // Waiting on (Completion) task outside context
        await Task.WhenAny(streamCompletion, channelResult).ConfigureAwait(false);
#pragma warning restore VSTHRD003

        // Return a channel that already arrived even if the stream finished in the same moment,
        // otherwise it is leaked.
        if (channelResult.IsCompletedSuccessfully) return await channelResult.ConfigureAwait(false);

        if (streamCompletion.IsCompleted) throw new UnderlyingStreamClosedException();

        return await channelResult.ConfigureAwait(false);
    }

    /// <summary>
    /// Starts accepting channel offers. Safe to call repeatedly. The accepting side subscribes as
    /// soon as its stream exists, because an offer raised with no subscriber is never accepted.
    /// </summary>
    protected void EnsureAcceptingOffers()
    {
        MultiplexingStream? current = stream;
        if (current is null) return;
        lock (sync)
        {
            if (channelOfferedSubscribed) return;
            current.ChannelOffered += StreamOnChannelOffered;
            channelOfferedSubscribed = true;
        }
    }

    /// <summary>Whether the multiplexing stream has completed, and how.</summary>
    private string DescribeMuxCompletion()
    {
        Task completion = Completion;
        if (!completion.IsCompleted) return "pending";
        if (completion.IsFaulted) return $"faulted:{completion.Exception?.InnerException?.GetType().Name ?? "unknown"}";
        if (completion.IsCanceled) return "canceled";
        return "completed";
    }

    private void MarkActive()
    {
        Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref channelsServed);
    }

    private async void StreamOnChannelOffered(object? sender, MultiplexingStream.ChannelOfferEventArgs e)
    {
        try
        {
            MultiplexingStream? current = stream;
            if (current == null) return;
            var channel = await current.AcceptChannelAsync(e.Name).ConfigureAwait(false);
            MarkActive();
            await channels.Writer.WriteAsync(channel).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Stream was disposed while accepting channel - expected during shutdown
        }
        catch (Exception ex)
        {
            // Nothing may be rethrown from async void, but an offer that cannot be accepted makes
            // every forwarded request time out, so it must be visible.
            logger?.LogWarning(ex, "Failed to accept channel offer {ChannelName}. {Diagnostics}", e.Name, GetDiagnostics());
        }
    }

    public override string ToString() => (Uri?.ToString() ?? base.ToString())!;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || stream is null) return;
        stream.ChannelOffered -= StreamOnChannelOffered;
        if (stream is IDisposable disposable) disposable.Dispose();
        else
        {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
        stream = null;
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (stream is not null)
        {
            stream.ChannelOffered -= StreamOnChannelOffered;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        stream = null;
    }
}
