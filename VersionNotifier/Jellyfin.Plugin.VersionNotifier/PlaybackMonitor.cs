using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VersionNotifier;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<AlternativeFinder>();
        services.AddHostedService<PlaybackMonitor>();
    }
}

public sealed class PlaybackMonitor : IHostedService, IDisposable
{
    private sealed record Pending(Guid Item, string Source, string? PlayId, Guid User, SessionInfo Session, CancellationTokenSource Cancellation);
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly object _gate = new();
    private readonly ISessionManager _sessions;
    private readonly Func<Guid, string, Guid, bool> _hasAlternative;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<PlaybackMonitor> _logger;
    private bool _stopped;

    public PlaybackMonitor(ISessionManager sessions, IUserManager users, AlternativeFinder finder, ILogger<PlaybackMonitor> logger)
        : this(sessions, (item, source, userId) => users.GetUserById(userId) is { } user && finder.HasAlternative(item, source, user),
            () => Plugin.Instance?.Configuration ?? new(), Task.Delay, logger) { }

    // Injectable clock and lookups keep lifecycle tests deterministic without a running server.
    public PlaybackMonitor(ISessionManager sessions, Func<Guid, string, Guid, bool> finder,
        Func<PluginConfiguration> configuration, Func<TimeSpan, CancellationToken, Task> delay, ILogger<PlaybackMonitor> logger)
    {
        _sessions = sessions;
        _hasAlternative = finder;
        _configuration = configuration;
        _delay = delay;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken token)
    {
        _sessions.PlaybackStart += Started;
        _sessions.PlaybackProgress += Progress;
        _sessions.PlaybackStopped += Stopped;
        _sessions.SessionEnded += Ended;
        return Task.CompletedTask;
    }

    private void Started(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Session is null) return;
        lock (_gate)
        {
            if (_stopped) return;
            var id = e.Item?.Id ?? e.Session.NowPlayingItem?.Id ?? Guid.Empty;
            var source = e.MediaSourceId ?? e.Session.PlayState?.MediaSourceId ?? string.Empty;
            if (_pending.TryGetValue(e.Session.Id, out var old))
            {
                if (old.Item == id && old.User == e.Session.UserId &&
                    (string.IsNullOrEmpty(e.PlaySessionId) || old.PlayId == e.PlaySessionId)) return;
                Remove(e.Session.Id);
            }
            if (!_configuration().Enabled || id == Guid.Empty) return;
            var pending = new Pending(id, source, e.PlaySessionId, e.Session.UserId, e.Session, new());
            _pending[e.Session.Id] = pending;
            _ = NotifyAsync(e.Session.Id, pending, _configuration().EffectiveDelay);
        }
    }

    private void Progress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Session is null) return;
        lock (_gate)
        {
            if (_pending.TryGetValue(e.Session.Id, out var p) &&
                ((e.Item is not null && e.Item.Id != p.Item) || e.Session.UserId != p.User ||
                (!string.IsNullOrEmpty(e.PlaySessionId) && !string.IsNullOrEmpty(p.PlayId) && e.PlaySessionId != p.PlayId))) Remove(e.Session.Id);
            else if (p is not null && !string.IsNullOrEmpty(e.MediaSourceId) && e.MediaSourceId != p.Source)
                p.Cancellation.Cancel(); // Retain the title marker to prevent a second notification.
        }
    }

    private void Stopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is null) return;
        lock (_gate)
        {
            if (_pending.TryGetValue(e.Session.Id, out var p) &&
                (e.Item is null || e.Item.Id == p.Item) &&
                (string.IsNullOrEmpty(e.PlaySessionId) || string.IsNullOrEmpty(p.PlayId) || p.PlayId == e.PlaySessionId)) Remove(e.Session.Id);
        }
    }

    private void Ended(object? sender, SessionEventArgs e) { lock (_gate) Remove(e.SessionInfo.Id); }

    private async Task NotifyAsync(string sessionId, Pending pending, int delaySeconds)
    {
        var token = pending.Cancellation.Token;
        try
        {
            // Yield even for zero delay, so playback-start dispatch can finish updating session state.
            await Task.Yield();
            await _delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!Current(sessionId, pending) || !_hasAlternative(pending.Item, pending.Source, pending.User)) return;
            lock (_gate)
            {
                if (!Current(sessionId, pending)) return;
            }
            var config = _configuration();
            if (!config.Enabled) return;
            await _sessions.SendMessageCommand(null, sessionId, new MessageCommand {
                Header = string.Empty, Text = config.EffectiveMessage, TimeoutMs = 8000
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Version Notifier could not notify session {SessionId}", sessionId); }
        // Keep the completed entry until stop/change: progress and repeated starts must not re-notify.
    }

    private bool Current(string id, Pending p)
    {
        lock (_gate)
        {
            if (_stopped || p.Cancellation.IsCancellationRequested || !_pending.TryGetValue(id, out var current) || !ReferenceEquals(current, p)) return false;
            var session = _sessions.Sessions.FirstOrDefault(s => s.Id == id);
            return _configuration().Enabled && ReferenceEquals(session, p.Session) && session?.UserId == p.User && session.NowPlayingItem?.Id == p.Item &&
                (string.IsNullOrEmpty(p.Source)
                    ? string.IsNullOrEmpty(session.PlayState?.MediaSourceId)
                    : string.Equals(session.PlayState?.MediaSourceId, p.Source, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Remove(string id)
    {
        if (_pending.Remove(id, out var p)) { p.Cancellation.Cancel(); p.Cancellation.Dispose(); }
    }

    public Task StopAsync(CancellationToken token) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        _sessions.PlaybackStart -= Started;
        _sessions.PlaybackProgress -= Progress;
        _sessions.PlaybackStopped -= Stopped;
        _sessions.SessionEnded -= Ended;
        lock (_gate) { _stopped = true; foreach (var id in _pending.Keys.ToArray()) Remove(id); }
    }
}
