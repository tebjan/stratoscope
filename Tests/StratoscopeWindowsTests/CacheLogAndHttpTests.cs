using System.IO;
using System.Net;
using Stratoscope.Core.Engines.Strata;

namespace Stratoscope.Tests;

// Ported from StrataCacheLogTests in the Swift tests.
public class StrataCacheLogTests
{
    private static string Parked(int tokens, int total, int bytes = 1000) =>
        "conversation cache: parked " + tokens + " tokens in 12.3 ms; parked=" + total + " bytes=" + bytes + " evictions=0 snapshot_bytes=" + bytes;

    [Fact]
    public void ReadsAGrowingFileWhileTheEngineKeepsItOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "strata-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(writer) { AutoFlush = true })
            {
                var log = new StrataCacheLog();
                w.WriteLine("noise");
                w.WriteLine(Parked(1000, 1));
                log.Update(path);
                Assert.True(log.Readable);
                Assert.Single(log.Entries);
                Assert.Null(log.Entries[0].ParkedAt);          // read as history
                w.WriteLine(Parked(2000, 2));
                log.Update(path);
                Assert.Equal(2, log.Entries.Count);
                Assert.NotNull(log.Entries[1].ParkedAt);       // seen live
                w.Write("conversation cache: parked 9");       // a half-written line is left for the next read
                log.Update(path);
                Assert.Equal(2, log.Entries.Count);
            }
        }
        finally { File.Delete(path); }
    }

public sealed class HttpJsonSourceTests : IDisposable
{
    private readonly HttpListener listener = new HttpListener();
    private readonly string prefix;
    private readonly List<string> seen = new List<string>();

    public HttpJsonSourceTests()
    {
        var port = FreePort();
        prefix = "http://127.0.0.1:" + port;
        listener.Prefixes.Add(prefix + "/");
        listener.Start();
        Task.Run(Serve);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = listener.GetContext(); } catch (Exception) { return; }
            seen.Add(c.Request.HttpMethod + " " + c.Request.Url!.AbsolutePath);
            var path = c.Request.Url.AbsolutePath;
            if (path == "/slow") { Thread.Sleep(1500); }
            var (status, body) =
                path == "/ok" ? (200, "{\"engine\":{\"model\":\"m\"},\"n\":1.5}") :
                path == "/limit" ? (429, "{\"error\":\"too many\"}") :
                path == "/junk" ? (200, "<html>not json</html>") :
                path == "/array" ? (200, "[1,2]") :
                path == "/slow" ? (200, "{}") : (404, "{}");
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = status;
            try { c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close(); } catch (Exception) { }
        }
    }

    public void Dispose() { listener.Close(); }

    [Fact]
    public async Task Reports429AsAStatus()
    {
        using (var source = new HttpJsonSource())
        {
            var r = await source.GetAsync(new Uri(prefix + "/limit"), Json.MetricsType, CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(429, r.Status);
            Assert.Equal(FetchFailure.Status, r.Failure);
        }
    }

}
}
