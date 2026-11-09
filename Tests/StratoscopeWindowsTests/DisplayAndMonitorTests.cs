using Stratoscope.Core.Engines.Demo;

namespace Stratoscope.Tests;

public class ServerMonitorTests
{
    private sealed class Scripted : IEngineProvider
    {
        private readonly Func<EngineSnapshot?> answer;
        public Scripted(Func<EngineSnapshot?> a) { answer = a; }
        public string? LastError { get; private set; }
        public Task<EngineSnapshot?> PollAsync(CancellationToken c)
        {
            var s = answer();
            LastError = s == null ? "down" : null;
            return Task.FromResult(s);
        }
    }

    private static ServerMonitor Monitor(string name, EngineStatus? status) =>
        new ServerMonitor(name, new Scripted(() => status == null ? null : new EngineSnapshot("S", "m", status.Value)));

    [Fact]
    public async Task ConnectingUntilTheFirstPollThenFollowsTheEngineAndReachabilityDecidesVisibility()
    {
        var m = Monitor("A", EngineStatus.Idle);
        Assert.Equal(ServerState.Connecting, m.State);
        Assert.True(await m.PollAsync(CancellationToken.None));
        Assert.Equal(ServerState.Ready, m.State);
        Assert.Null(m.Error);
        await ReachabilityDecidesVisibility();
    }

    [Fact]
    public async Task ExceptionsBecomeAnErrorNotOfflineAndOverlappingPollsAreSkipped()
    {
        var m = new ServerMonitor("A", new Scripted(() => throw new InvalidOperationException("boom")));
        await m.PollAsync(CancellationToken.None);
        Assert.Equal(ServerState.Error, m.State);
        Assert.True(m.Faulted);
        Assert.Contains("boom", m.Error);
        await OverlappingPollsAreSkipped();
    }

    private static async Task OverlappingPollsAreSkipped()
    {
        var gate = new TaskCompletionSource<EngineSnapshot?>();
        var provider = new SlowProvider(gate.Task);
        var m = new ServerMonitor("A", provider);
        var first = m.PollAsync(CancellationToken.None);
        Assert.False(await m.PollAsync(CancellationToken.None));
        gate.SetResult(new EngineSnapshot("S", "m"));
        Assert.True(await first);
        Assert.Equal(1, provider.Calls);
    }

    private sealed class SlowProvider : IEngineProvider
    {
        private readonly Task<EngineSnapshot?> result;
        public int Calls;
        public SlowProvider(Task<EngineSnapshot?> r) { result = r; }
        public string? LastError => null;
        public Task<EngineSnapshot?> PollAsync(CancellationToken c) { Calls++; return result; }
    }

    private static async Task ReachabilityDecidesVisibility()
    {
        var a = Monitor("A", null); var b = Monitor("B", EngineStatus.NoModel);
        Assert.False(Servers.AllPolled(new[] { a, b }));
        await a.PollAsync(CancellationToken.None);
        Assert.False(Servers.AnyReachable(new[] { a }));            // nothing answers: hidden, like the Mac menu bar item
        await b.PollAsync(CancellationToken.None);
        Assert.True(Servers.AllPolled(new[] { a, b }));
        Assert.True(Servers.AnyReachable(new[] { a, b }));          // a router without a model still answers
    }

}
