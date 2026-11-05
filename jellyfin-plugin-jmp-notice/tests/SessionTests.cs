using Jellyfin.Plugin.JmpNotice;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JmpNotice.Tests;

public sealed class SessionTests
{
    [Theory]
    [InlineData("Jellyfin Media Player", true)]
    [InlineData("jellyfin media player", true)]
    [InlineData("Jellyfin Desktop", false)]
    [InlineData("Jellium Desktop", false)]
    [InlineData("Pelagica", false)]
    [InlineData("Plezy", false)]
    [InlineData("Jellyfin Web", false)]
    [InlineData("Jellyfin Media Player Beta", false)]
    [InlineData(null, false)]
    public void Names(string? name, bool expected) => Assert.Equal(expected, SessionMonitor.IsTarget(name));

    private sealed class Harness : IAsyncDisposable
    {
        public ISessionManager Sessions { get; } = Substitute.For<ISessionManager>();
        public PluginConfiguration Config { get; } = new();
        public SessionInfo Session { get; } = new(null!, null!) { Id = "jmp", Client = "Jellyfin Media Player", ApplicationVersion = "1.0", UserId = Guid.NewGuid() };
        public ISessionController Controller { get; } = Substitute.For<ISessionController>();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public SessionMonitor Monitor { get; }
        public List<(string Id, MessageCommand Message)> Messages { get; } = new();
        private readonly System.Threading.Channels.Channel<TaskCompletionSource> _ticks = System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();
        public Harness()
        {
            Controller.IsSessionActive.Returns(true); Controller.SupportsMediaControl.Returns(true);
            Session.SessionControllers = [Controller];
            Sessions.Sessions.Returns([Session]);
            Sessions.SendMessageCommand(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>())
                .Returns(c => { lock (Messages) Messages.Add((c.ArgAt<string>(1), c.Arg<MessageCommand>())); return Task.CompletedTask; });
            Monitor = new(Sessions, () => Config, async (_, token) => {
                var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _ticks.Writer.WriteAsync(tick, token); await tick.Task.WaitAsync(token);
            }, NullLogger<SessionMonitor>.Instance, () => Now);
        }
        public void Connected() => Sessions.SessionControllerConnected += Raise.Event<EventHandler<SessionEventArgs>>(this, new SessionEventArgs { SessionInfo = Session });
        public void Play(string playId, Guid item, string mediaType = "Movie") => Sessions.PlaybackStart += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this,
            new PlaybackProgressEventArgs { Session = Session, Item = MediaItem(item, mediaType), PlaySessionId = playId });
        private static MediaBrowser.Controller.Entities.BaseItem MediaItem(Guid id, string type)
        {
            MediaBrowser.Controller.Entities.BaseItem item = type switch {
                "Episode" => new MediaBrowser.Controller.Entities.TV.Episode(),
                "Audio" => new MediaBrowser.Controller.Entities.Audio.Audio(),
                "LiveTv" => new MediaBrowser.Controller.LiveTv.LiveTvChannel(),
                "Other" => new MediaBrowser.Controller.Entities.Video(),
                _ => new MediaBrowser.Controller.Entities.Movies.Movie()
            };
            item.Id = id; return item;
        }
        public void End() => Sessions.SessionEnded += Raise.Event<EventHandler<SessionEventArgs>>(this, new SessionEventArgs { SessionInfo = Session });
        public async Task Tick()
        {
            var tick = await _ticks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            tick.SetResult();
            await Task.Delay(30);
        }
        public ValueTask DisposeAsync() => new(Monitor.StopAsync(default));
    }

    [Theory]
    [InlineData("0.9.0")][InlineData("1.12.0")][InlineData("99.0")]
    public async Task SendsWithoutPlaybackAndDeduplicatesRepeatedConnectionEvents(string version)
    {
        await using var h = new Harness(); h.Session.ApplicationVersion = version;
        await h.Monitor.StartAsync(default); h.Connected(); h.Connected(); await h.Tick();
        h.Connected();
        var message = Assert.Single(h.Messages);
        Assert.Equal("jmp", message.Id); Assert.Equal("JMP Notice", message.Message.Header);
        Assert.Equal(PluginConfiguration.DefaultMessage, message.Message.Text); Assert.Equal(12000, message.Message.TimeoutMs);
        Assert.Null(h.Session.NowPlayingItem);
        Assert.DoesNotContain(h.Sessions.ReceivedCalls(), c => c.GetMethodInfo().Name is "SendPlayCommand" or "SendPlaystateCommand");
    }

    [Fact]
    public async Task WaitsForControllerAndAllowsNewSessionAfterEnd()
    {
        await using var h = new Harness(); h.Controller.IsSessionActive.Returns(false);
        await h.Monitor.StartAsync(default); await h.Tick(); Assert.Empty(h.Messages);
        h.Controller.IsSessionActive.Returns(true); await h.Tick(); Assert.Single(h.Messages);
        h.End(); h.Connected(); await h.Tick(); Assert.Equal(2, h.Messages.Count);
    }

    [Theory]
    [InlineData("disabled")][InlineData("ended")][InlineData("shutdown")][InlineData("removed")][InlineData("renamed")]
    public async Task ChangesBeforeDeliveryCancelNotice(string change)
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default);
        switch (change)
        {
            case "disabled": h.Config.Enabled = false; break;
            case "ended": h.End(); break;
            case "shutdown": await h.Monitor.StopAsync(default); break;
            case "removed": h.Sessions.Sessions.Returns([]); break;
            case "renamed": h.Session.Client = "Jellyfin Desktop"; break;
        }
        await h.Tick(); Assert.Empty(h.Messages);
    }

    [Theory]
    [InlineData("Jellyfin Desktop")][InlineData("Jellium Desktop")][InlineData("Pelagica")][InlineData("Plezy")][InlineData("Jellyfin Web")]
    public async Task OtherClientsNeverReceive(string client)
    {
        await using var h = new Harness(); h.Session.Client = client;
        await h.Monitor.StartAsync(default); h.Connected(); await h.Monitor.StopAsync(default); Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task CustomSettingsAndAmbiguousFailureAreNotRetried()
    {
        await using var h = new Harness(); h.Config.Message = "Custom"; h.Config.DurationSeconds = 15;
        h.Sessions.SendMessageCommand(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new IOException("Disconnected")));
        await h.Monitor.StartAsync(default); await h.Tick(); h.Connected();
        await h.Sessions.Received(1).SendMessageCommand(null, "jmp", Arg.Is<MessageCommand>(m => m.Text == "Custom" && m.TimeoutMs == 15000), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaybackTriggersWhenConnectionNoticeWasSkippedAndDoesNotRepeat()
    {
        await using var h = new Harness(); h.Config.Enabled = false;
        await h.Monitor.StartAsync(default); h.Config.Enabled = true;
        void Play() => h.Sessions.PlaybackStart += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this,
            new PlaybackProgressEventArgs { Session = h.Session });
        Play(); Play(); await h.Tick(); Play(); h.Connected();
        Assert.Single(h.Messages);
        Assert.DoesNotContain(h.Sessions.ReceivedCalls(), c => c.GetMethodInfo().Name is "SendPlayCommand" or "SendPlaystateCommand");
    }

    [Fact]
    public async Task LateConnectionAfterReadinessWindowCanNotify()
    {
        await using var h = new Harness(); h.Controller.IsSessionActive.Returns(false);
        await h.Monitor.StartAsync(default); await h.Tick(); await h.Tick(); await h.Tick();
        Assert.Empty(h.Messages); h.Controller.IsSessionActive.Returns(true); h.Connected();
        await h.Tick(); Assert.Single(h.Messages);
    }

    [Theory]
    [InlineData("Movie")][InlineData("Episode")][InlineData("Audio")][InlineData("LiveTv")][InlineData("Other")]
    public async Task EveryDistinctStartNotifiesIncludingAutomaticTransitions(string mediaType)
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        h.Now += TimeSpan.FromSeconds(3);
        var first = Guid.NewGuid(); var next = Guid.NewGuid();
        h.Play(mediaType + "1", first, mediaType); h.Play(mediaType + "1", first, mediaType); await h.Tick();
        h.Play(mediaType + "2", next, mediaType); await h.Tick();
        h.Play(mediaType + "3", next, mediaType); await h.Tick();
        Assert.Equal(4, h.Messages.Count);
    }

    [Fact]
    public async Task LaunchOverlapDoesNotSuppressFuturePlayback()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default);
        h.Play("first", Guid.NewGuid()); await h.Tick(); Assert.Single(h.Messages);
        h.Play("second", Guid.NewGuid()); await h.Tick(); Assert.Equal(2, h.Messages.Count);
    }

    [Fact]
    public async Task RecentLaunchNoticeMergesOnlyOnePlayback()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        h.Play("first", Guid.NewGuid()); Assert.Single(h.Messages);
        h.Play("second", Guid.NewGuid()); await h.Tick(); Assert.Equal(2, h.Messages.Count);
    }

    [Fact]
    public async Task ReplacementConnectionNotifiesButRepeatedEventsDoNot()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        var controller = Substitute.For<ISessionController>();
        controller.IsSessionActive.Returns(true); controller.SupportsMediaControl.Returns(true);
        h.Session.SessionControllers = [controller]; h.Connected(); h.Connected(); await h.Tick();
        h.Connected(); Assert.Equal(2, h.Messages.Count);
    }

    [Fact]
    public async Task ObservedDisconnectThenReconnectNotifies()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        h.Controller.IsSessionActive.Returns(false); h.Connected();
        h.Controller.IsSessionActive.Returns(true); h.Connected(); await h.Tick(); Assert.Equal(2, h.Messages.Count);
    }

    [Fact]
    public async Task ProgressPauseResumeAndSeekDoNotNotify()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        h.Now += TimeSpan.FromSeconds(3); var item = Guid.NewGuid(); h.Play("play", item); await h.Tick();
        foreach (var paused in new[] { true, false, false })
            h.Sessions.PlaybackProgress += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(this,
                new PlaybackProgressEventArgs { Session = h.Session, PlaySessionId = "play", IsPaused = paused });
        h.Play("play", item); Assert.Equal(2, h.Messages.Count);
    }

    [Fact]
    public async Task DelayedDuplicateStartFromPreviousItemDoesNotNotify()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        h.Now += TimeSpan.FromSeconds(3);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        h.Play("first", first); await h.Tick(); h.Play("second", second); await h.Tick();
        h.Play("first", first); Assert.Equal(3, h.Messages.Count);
    }

    [Fact]
    public async Task StopAllowsSameItemWithoutPlaySessionIdToNotifyAgain()
    {
        await using var h = new Harness(); await h.Monitor.StartAsync(default); await h.Tick();
        h.Now += TimeSpan.FromSeconds(3); var id = Guid.NewGuid();
        h.Play("", id); await h.Tick(); h.Play("", id); Assert.Equal(2, h.Messages.Count);
        h.Sessions.PlaybackStopped += Raise.Event<EventHandler<PlaybackStopEventArgs>>(this,
            new PlaybackStopEventArgs { Session = h.Session, Item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = id } });
        h.Play("", id); await h.Tick(); Assert.Equal(3, h.Messages.Count);
    }

    [Fact]
    public async Task FailureDoesNotSuppressNextDistinctPlayback()
    {
        await using var h = new Harness();
        h.Sessions.SendMessageCommand(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new IOException("Disconnected")));
        await h.Monitor.StartAsync(default); await h.Tick(); h.Now += TimeSpan.FromSeconds(3);
        var item = Guid.NewGuid(); h.Play("first", item); await h.Tick();
        h.Play("first", item); h.Play("second", item); await h.Tick();
        await h.Sessions.Received(3).SendMessageCommand(null, "jmp", Arg.Any<MessageCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConfigurationBounds()
    {
        var c = new PluginConfiguration(); Assert.True(c.Enabled); Assert.Equal(12, c.EffectiveDuration);
        c.Message = " "; Assert.Equal(PluginConfiguration.DefaultMessage, c.EffectiveMessage);
        c.DurationSeconds = int.MaxValue; Assert.Equal(60, c.EffectiveDuration);
        c.DurationSeconds = int.MinValue; Assert.Equal(1, c.EffectiveDuration);
    }
}
