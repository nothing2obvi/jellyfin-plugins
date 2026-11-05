using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JmpNotice;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host) => services.AddHostedService<SessionMonitor>();
}

public sealed class SessionMonitor : IHostedService, IDisposable
{
    private sealed class Notice(bool connection)
    {
        public bool Connection { get; set; } = connection;
        public bool Playback { get; set; }
        public bool Running { get; set; }
    }

    private sealed class Entry(SessionInfo session)
    {
        public SessionInfo Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = new();
        public HashSet<ISessionController> Controllers { get; } = new(ReferenceEqualityComparer.Instance);
        public bool Connected { get; set; }
        public string? PlaybackKey { get; set; }
        public HashSet<string> SeenPlaybackKeys { get; } = new(StringComparer.Ordinal);
        public List<Notice> Pending { get; } = new();
        public DateTimeOffset? ConnectionSent { get; set; }
    }

    private readonly ISessionManager _sessions;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<SessionMonitor> _logger;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly object _gate = new();
    private bool _stopped;
    private readonly Func<DateTimeOffset> _now;

    public SessionMonitor(ISessionManager sessions, ILogger<SessionMonitor> logger)
        : this(sessions, () => Plugin.Instance?.Configuration ?? new(), Task.Delay, logger) { }

    public SessionMonitor(ISessionManager sessions, Func<PluginConfiguration> configuration,
        Func<TimeSpan, CancellationToken, Task> delay, ILogger<SessionMonitor> logger, Func<DateTimeOffset>? now = null)
    {
        _sessions = sessions;
        _configuration = configuration;
        _delay = delay;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static bool IsTarget(string? client) => string.Equals(client, "Jellyfin Media Player", StringComparison.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += PlaybackStarted;
        _sessions.PlaybackStopped += PlaybackStopped;
        _sessions.SessionStarted += Changed;
        _sessions.SessionControllerConnected += Changed;
        _sessions.CapabilitiesChanged += Changed;
        _sessions.SessionEnded += Ended;
        // Cover sessions established before hosted services started.
        foreach (var session in _sessions.Sessions) Observe(session);
        return Task.CompletedTask;
    }

    private void PlaybackStarted(object? sender, PlaybackProgressEventArgs args)
    {
        if (args.Session is null) return;
        // Item identity is media-type agnostic; PlaySessionId distinguishes replaying the same item.
        var item = args.Item?.Id ?? args.Session.NowPlayingItem?.Id ?? Guid.Empty;
        var key = $"{item:N}:{args.PlaySessionId}";
        Observe(args.Session, key, !string.IsNullOrEmpty(args.PlaySessionId));
    }

    private void PlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        if (args.Session is null) return;
        lock (_gate)
        {
            if (_entries.TryGetValue(args.Session.Id, out var entry) && ReferenceEquals(entry.Session, args.Session))
            {
                var item = args.Item?.Id ?? args.Session.NowPlayingItem?.Id ?? Guid.Empty;
                var key = $"{item:N}:{args.PlaySessionId}";
                if (entry.PlaybackKey == key) entry.PlaybackKey = null;
            }
        }
    }

    private void Changed(object? sender, SessionEventArgs args) => Observe(args.SessionInfo);

    private void Observe(SessionInfo session, string? playbackKey = null, bool hasPlaySessionId = false)
    {
        lock (_gate)
        {
            if (_stopped || !IsTarget(session.Client) || !_configuration().Enabled) return;
            if (_entries.TryGetValue(session.Id, out var entry) && !ReferenceEquals(entry.Session, session))
            {
                Remove(session.Id);
                entry = null;
            }
            if (entry is null)
            {
                _entries[session.Id] = entry = new(session);
                entry.Pending.Add(new(true));
            }
            var controllers = session.SessionControllers.Where(c => c.IsSessionActive && c.SupportsMediaControl).ToArray();
            var newConnection = controllers.Length > 0 &&
                (!entry.Connected || controllers.Any(c => !entry.Controllers.Contains(c)));
            entry.Connected = controllers.Length > 0;
            entry.Controllers.Clear();
            foreach (var controller in controllers) entry.Controllers.Add(controller);
            if (newConnection)
            {
                var pending = entry.Pending.FirstOrDefault(n => n.Playback);
                if (pending is not null) pending.Connection = true;
                else if (!entry.Pending.Any(n => n.Connection)) entry.Pending.Add(new(true));
            }
            if (playbackKey is not null && (hasPlaySessionId ? entry.SeenPlaybackKeys.Add(playbackKey) : entry.PlaybackKey != playbackKey))
            {
                entry.PlaybackKey = playbackKey;
                var connection = entry.Pending.FirstOrDefault(n => n.Connection && !n.Playback);
                if (connection is not null) connection.Playback = true;
                else if (entry.ConnectionSent is { } sent && _now() - sent < TimeSpan.FromSeconds(2))
                    entry.ConnectionSent = null; // Consume only one launch/playback overlap, never future starts.
                else entry.Pending.Add(new(false) { Playback = true });
            }
            foreach (var notice in entry.Pending.Where(n => !n.Running).ToArray())
            {
                notice.Running = true;
                var task = NotifyAsync(session.Id, entry, notice);
                _tasks.Add(task);
                _ = task.ContinueWith(completed => { lock (_gate) _tasks.Remove(completed); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task NotifyAsync(string id, Entry entry, Notice notice)
    {
        var token = entry.Cancellation.Token;
        var consumed = false;
        try
        {
            await Task.Yield();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await _delay(attempt == 0 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                PluginConfiguration config;
                lock (_gate)
                {
                    if (_stopped || !_entries.TryGetValue(id, out var current) || !ReferenceEquals(current, entry) ||
                        !_configuration().Enabled || !IsTarget(entry.Session.Client) ||
                        !_sessions.Sessions.Any(s => ReferenceEquals(s, entry.Session))) { consumed = true; return; }
                    if (!entry.Session.SessionControllers.Any(c => c.IsSessionActive && c.SupportsMediaControl)) continue;
                    config = _configuration();
                    // Commit before sending: a transport exception cannot prove the client did not display it.
                    consumed = true;
                    entry.Pending.Remove(notice);
                    if (notice.Connection)
                    {
                        entry.Connected = true;
                        entry.Controllers.Clear();
                        foreach (var controller in entry.Session.SessionControllers.Where(c => c.IsSessionActive && c.SupportsMediaControl))
                            entry.Controllers.Add(controller);
                    }
                    entry.ConnectionSent = notice.Connection && !notice.Playback ? _now() : null;
                }
                await _sessions.SendMessageCommand(null, id, new MessageCommand {
                    Header = "JMP Notice", Text = config.EffectiveMessage, TimeoutMs = config.EffectiveDuration * 1000
                }, token).ConfigureAwait(false);
                _logger.LogDebug("JMP Notice sent to session {SessionId}", id);
                return;
            }
            _logger.LogDebug("JMP Notice waiting for a messaging controller on session {SessionId}", id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { consumed = true; _logger.LogDebug(ex, "JMP Notice delivery failed for session {SessionId}; not repeating this trigger", id); }
        finally
        {
            lock (_gate)
            {
                notice.Running = false;
                if (consumed) entry.Pending.Remove(notice);
            }
        }
    }

    private void Ended(object? sender, SessionEventArgs args)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(args.SessionInfo.Id, out var entry) && ReferenceEquals(entry.Session, args.SessionInfo))
                Remove(args.SessionInfo.Id);
        }
    }

    private void Remove(string id)
    {
        if (_entries.Remove(id, out var entry)) { entry.Cancellation.Cancel(); entry.Cancellation.Dispose(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        Task[] tasks;
        lock (_gate) tasks = _tasks.ToArray();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _sessions.PlaybackStart -= PlaybackStarted;
        _sessions.PlaybackStopped -= PlaybackStopped;
        _sessions.SessionStarted -= Changed;
        _sessions.SessionControllerConnected -= Changed;
        _sessions.CapabilitiesChanged -= Changed;
        _sessions.SessionEnded -= Ended;
        lock (_gate)
        {
            _stopped = true;
            foreach (var id in _entries.Keys.ToArray()) Remove(id);
        }
    }
}
