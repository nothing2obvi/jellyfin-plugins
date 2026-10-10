using Jellyfin.Plugin.VersionNotifier;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace VersionNotifier.Tests;

public sealed class PlaybackTests : IDisposable
{
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly PluginConfiguration _config = new();
    private readonly TaskCompletionSource _elapsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PlaybackMonitor _monitor;
    private readonly SessionInfo _session;
    private readonly Movie _item = new() { Id = Guid.NewGuid() };
    private bool _available = true;
    private TimeSpan _delay;
    private int _sends;
    private MessageCommand? _message;

    public PlaybackTests()
    {
        _session = new SessionInfo(null!, null!) { Id = "session", UserId = Guid.NewGuid(),
            NowPlayingItem = new() { Id = _item.Id }, PlayState = new() { MediaSourceId = _item.Id.ToString("N") },
            Capabilities = new() { SupportedCommands = [GeneralCommandType.DisplayMessage] } };
        _sessions.Sessions.Returns([_session]);
        _sessions.SendMessageCommand(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => { _message = call.Arg<MessageCommand>(); Interlocked.Increment(ref _sends); _sent.TrySetResult(); return Task.CompletedTask; });
        _monitor = new(_sessions, (_, _, _) => _available, () => _config,
            async (delay, token) => { _delay = delay; _waiting.TrySetResult(); await _elapsed.Task.WaitAsync(token); },
            NullLogger<PlaybackMonitor>.Instance);
        _monitor.StartAsync(default).GetAwaiter().GetResult();
    }
    private PlaybackProgressEventArgs Args(string playId = "play") => new() {
        Session = _session, Item = _item, MediaSourceId = _item.Id.ToString("N"), PlaySessionId = playId
    };
    private void Start() => _sessions.PlaybackStart += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this, Args());
    private async Task Release()
    {
        await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _elapsed.TrySetResult();
        await Task.Delay(100);
    }
    [Fact]
    public async Task ConfiguredMessageDelayAndDuplicateEvents()
    {
        _config.DelaySeconds = 23; _config.Message = "Custom message";
        Start(); Start();
        _sessions.PlaybackProgress += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this, Args());
        await Release(); await _sent.Task.WaitAsync(TimeSpan.FromSeconds(3)); Start();
        Assert.Equal(TimeSpan.FromSeconds(23), _delay); Assert.Equal("Custom message", _message!.Text); Assert.Equal(string.Empty, _message!.Header); Assert.Equal(1, _sends);
        await _sessions.Received(1).SendMessageCommand(null, "session", Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>());
    }
    [Theory]
    [InlineData("stop")] [InlineData("switch")] [InlineData("4k")]
    [InlineData("unavailable")] [InlineData("disabled")] [InlineData("logout")]
    [InlineData("ended")] [InlineData("shutdown")] [InlineData("replacement")]
    public async Task ChangesBeforeDelaySuppressMessage(string change)
    {
        Start(); await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        switch (change) {
            case "stop": _sessions.PlaybackStopped += Raise.Event<EventHandler<PlaybackStopEventArgs>>(this,
                new PlaybackStopEventArgs { Session = _session, Item = _item, PlaySessionId = "play" }); break;
            case "switch": _session.NowPlayingItem.Id = Guid.NewGuid(); break;
            case "4k": _session.PlayState.MediaSourceId = "4k"; break;

            case "unavailable": _available = false; break;
            case "disabled": _config.Enabled = false; break;
            case "logout": _session.UserId = Guid.NewGuid(); break;
            case "ended": _sessions.SessionEnded += Raise.Event<EventHandler<SessionEventArgs>>(this,
                new SessionEventArgs { SessionInfo = _session }); break;
            case "shutdown": await _monitor.StopAsync(default); break;
            case "replacement": _sessions.Sessions.Returns([new SessionInfo(null!, null!) {
                Id = _session.Id, UserId = _session.UserId, NowPlayingItem = _session.NowPlayingItem,
                PlayState = _session.PlayState, Capabilities = _session.Capabilities }]); break;
        }
        await Release(); Assert.Equal(0, _sends);
    }
    [Fact]
    public async Task MissingMediaSourceIdCanStillNotify()
    {
        _session.PlayState.MediaSourceId = null!;
        var start = Args();
        start.MediaSourceId = null!;
        _sessions.PlaybackStart += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this, start);
        await Release();
        await _sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, _sends);
    }

    [Fact]
    public async Task EmptySupportedCommandsStillNotifies()
    {
        _session.Capabilities.SupportedCommands = [];
        Start();
        await Release();
        await _sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, _sends);
    }

    [Fact]
    public async Task SendFailureIsHandledWithoutRetry()
    {
        _sessions.SendMessageCommand(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new NotSupportedException()));
        Start(); await Release(); Start(); await Task.Delay(50);
        await _sessions.Received(1).SendMessageCommand(null, "session", Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task SourceSwitchKeepsDuplicateMarker()
    {
        Start(); await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var change = Args(); change.MediaSourceId = "different";
        _sessions.PlaybackProgress += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this, change);
        Start(); await Release(); Assert.Equal(0, _sends);
    }
    [Fact]
    public async Task StaleStopDoesNotCancelNewPlayback()
    {
        Start(); await _waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _sessions.PlaybackStart += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this, Args("new-play"));
        _sessions.PlaybackStopped += Raise.Event<EventHandler<PlaybackStopEventArgs>>(this,
            new PlaybackStopEventArgs { Session = _session, Item = _item, PlaySessionId = "play" });
        await Release(); await _sent.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, _sends);
    }
    [Fact]
    public async Task FinderFailureDoesNotDisruptPlayback()
    {
        using var monitor = new PlaybackMonitor(_sessions, (_, _, _) => throw new IOException("missing source"),
            () => _config, (_, _) => Task.CompletedTask, NullLogger<PlaybackMonitor>.Instance);
        await _monitor.StopAsync(default); await monitor.StartAsync(default);
        Start(); await Task.Delay(100); Assert.Equal(0, _sends);
    }
    [Fact]
    public void DefaultsAndValidation()
    {
        Assert.True(_config.Enabled); Assert.Equal(15, _config.EffectiveDelay);
        Assert.Equal("[Auto] This title is also available in 4K.", _config.EffectiveMessage);
        _config.DelaySeconds = -1; _config.Message = " "; Assert.Equal(0, _config.EffectiveDelay);
        Assert.Equal(PluginConfiguration.DefaultMessage, _config.EffectiveMessage);
        _config.DelaySeconds = 999; Assert.Equal(300, _config.EffectiveDelay);
    }
    public void Dispose() => _monitor.Dispose();
}
