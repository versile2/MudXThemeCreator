using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MudXtra.ThemeCreator.Infrastructure.Data;
using MudXtra.ThemeCreator.UI.Extensions;

// Reflection keeps the pre-implementation RED executable: absence is an assertion, not a compiler error.
var assembly = typeof(IServiceCollectionExtensions).Assembly;
var readiness = assembly.GetType("MudXtra.ThemeCreator.UI.Startup.PostgresReadiness");
var failures = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e.GetBaseException().Message}"); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
IConfiguration Config(string? connection) => new ConfigurationBuilder().AddInMemoryCollection(
    new Dictionary<string, string?> { ["ConnectionStrings:postgresql"] = connection }).Build();
async Task<(int Code, string Classification)> Probe(IConfiguration config, CancellationToken token = default, TimeSpan? timeout = null)
{
    Check(readiness != null, "separate PostgreSQL probe missing");
    var task = (Task)readiness!.GetMethod("ProbeAsync")!.Invoke(null, [config, token, timeout])!;
    await task;
    var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
    var code = (int)result.GetType().GetProperty("ExitCode")!.GetValue(result)!;
    var classification = (string)result.GetType().GetProperty("Classification")!.GetValue(result)!;
    return (code, classification);
}
foreach (var connection in new string?[] { null, "", "Host=[dbName];Username=u;Password=p;Database=d", "Host=localhost;Username=u;Password=[dbPassword];Database=d", "Host=localhost;Port=bogus", "Host=localhost;Username=u;Password=p" })
    await Test("invalid/missing/unresolved config: " + (connection is null ? "missing" : "redacted"), async () => Check((await Probe(Config(connection))).Code == 20, "must fail permanently before connecting"));

await Test("one authenticated SELECT 1 succeeds and closes connection", async () =>
{
    await using var peer = new PgPeer("success");
    Check((await Probe(Config(peer.Connection))).Code == 0, "expected readiness");
    await peer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Check(peer.Queries == 1 && peer.QueryText == "SELECT 1", "probe must execute only SELECT 1");
    Check(peer.Closed, "connection not disposed");
});
foreach (var state in new[] { "28P01", "28000", "3D000", "42501", "42601" })
    await Test("permanent SQLSTATE " + state, async () =>
    {
        await using var peer = new PgPeer(state);
        var result = await Probe(Config(peer.Connection));
        Check(result.Code == 20, "must not retry permanent database/config/auth failure");
        if (state.StartsWith("28")) Check(result.Classification == "authentication", "authentication classification missing");
        if (state == "3D000") Check(result.Classification == "database-missing", "missing database classification missing");
        Check(!result.Classification.Contains("secret"), "message leaked peer error");
    });
foreach (var state in new[] { "57P03", "53300", "08006" })
    await Test("transient SQLSTATE " + state, async () =>
    {
        await using var peer = new PgPeer(state);
        Check((await Probe(Config(peer.Connection))).Code == 10, "expected transient failure");
    });
await Test("query error is not readiness", async () =>
{
    await using var peer = new PgPeer("query-error");
    Check((await Probe(Config(peer.Connection))).Code == 10, "open alone cannot mark ready");
});
await Test("wrong SELECT result is not readiness", async () =>
{
    await using var peer = new PgPeer("wrong-result");
    Check((await Probe(Config(peer.Connection))).Code == 20, "unexpected query result must fail closed");
});
await Test("single attempt deadline includes query and disposes", async () =>
{
    await using var peer = new PgPeer("stall-query");
    var timer = Stopwatch.StartNew();
    Check((await Probe(Config(peer.Connection), timeout: TimeSpan.FromMilliseconds(150))).Code == 10, "deadline must be transient");
    Check(timer.Elapsed < TimeSpan.FromSeconds(2), "deadline not bounded");
    await peer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Check(peer.Closed, "timed out connection not disposed");
});
await Test("shutdown distinct from timeout, prompt and disposed", async () =>
{
    await using var peer = new PgPeer("stall-open");
    using var shutdown = new CancellationTokenSource(150);
    var timer = Stopwatch.StartNew();
    Check((await Probe(Config(peer.Connection), shutdown.Token, TimeSpan.FromMilliseconds(150))).Code == 130, "shutdown cannot retry as timeout");
    Check(timer.Elapsed < TimeSpan.FromSeconds(2), "shutdown not prompt");
    await peer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Check(peer.Closed, "cancelled connection not disposed");
});
await Test("TLS policy failure is permanent and redacted", async () =>
{
    await using var peer = new PgPeer("tls-refuse");
    var result = await Probe(Config(peer.Connection.Replace("SSL Mode=Disable", "SSL Mode=Require")));
    Check(result.Code == 20, "TLS policy cannot retry");
});
await Test("guard success then registration race cannot change provider", async () =>
{
    await using var peer = new PgPeer("success");
    var config = Config(peer.Connection);
    Check((await Probe(config)).Code == 0, "guard success prerequisite");
    await peer.DisposeAsync(); // Database goes away between guard and app.
    var services = new ServiceCollection();
    await services.AddThemeDataBaseConnection(config);
    using var provider = services.BuildServiceProvider();
    using var db = provider.GetRequiredService<IDbContextFactory<ThemeDbContext>>().CreateDbContext();
    Check(db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL", "provider fallback");
    bool failed = false;
    try { await db.Database.OpenConnectionAsync(); } catch { failed = true; }
    Check(failed, "database loss must fail, not become ready/fallback");
});
return failures == 0 ? 0 : 1;

// Minimal isolated wire peer, not a database. Exercises the real Npgsql open/query/disposal path.
sealed class PgPeer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly string mode;
    public Task Completion { get; }
    public string Connection { get; }
    public int Queries { get; private set; }
    public string QueryText { get; private set; } = "";
    public bool Closed { get; private set; }
    public PgPeer(string mode)
    {
        this.mode = mode;
        listener.Start();
        Connection = $"Host=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};Username=u;Password=p;Database=d;SSL Mode=Disable;Pooling=false;Cancellation Timeout=-1";
        Completion = Run();
    }
    static byte[] Int(int n) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, n); return bytes; }
    static byte[] Text(string text) => Encoding.UTF8.GetBytes(text + "\0");
    async Task Send(Stream stream, char type, byte[] payload)
    {
        await stream.WriteAsync(new[] { (byte)type }.Concat(Int(payload.Length + 4)).Concat(payload).ToArray(), stop.Token);
    }
    async Task<byte[]> Read(Stream stream, int length)
    {
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, stop.Token); return bytes;
    }
    async Task Error(Stream stream, string state) => await Send(stream, 'E', new byte[] { (byte)'S' }.Concat(Text("FATAL")).Concat(new byte[] { (byte)'C' }).Concat(Text(state)).Concat(new byte[] { (byte)'M' }).Concat(Text("secret peer diagnostic")).Concat(new byte[] { 0 }).ToArray());
    async Task Run()
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = client.GetStream();
            var size = BinaryPrimitives.ReadInt32BigEndian(await Read(stream, 4));
            await Read(stream, size - 4);
            if (mode == "tls-refuse") { await stream.WriteAsync(new byte[] { (byte)'N' }, stop.Token); return; }
            if (mode == "stall-open") { await WaitForClose(stream); return; }
            if (mode.Length == 5) { await Error(stream, mode); return; }
            await Send(stream, 'R', Int(0));
            await Send(stream, 'S', Text("server_version").Concat(Text("15.0")).ToArray());
            await Send(stream, 'S', Text("client_encoding").Concat(Text("UTF8")).ToArray());
            await Send(stream, 'K', Int(123).Concat(Int(456)).ToArray());
            await Send(stream, 'Z', new byte[] { (byte)'I' });
            while (true)
            {
                var type = (char)(await Read(stream, 1))[0];
                size = BinaryPrimitives.ReadInt32BigEndian(await Read(stream, 4));
                var body = await Read(stream, size - 4);
                if (type == 'X') { Closed = true; return; }
                if (type == 'P')
                {
                    Queries++; QueryText = Encoding.UTF8.GetString(body).Split('\0')[1];
                    if (mode == "stall-query") { await WaitForClose(stream); return; }
                    if (mode == "query-error") { await Error(stream, "57P03"); return; }
                    await Send(stream, '1', []);
                }
                if (type == 'B') await Send(stream, '2', []);
                if (type == 'D')
                {
                    var column = Text("?column?").Concat(Int(0)).Concat(new byte[] { 0, 0 }).Concat(Int(23)).Concat(new byte[] { 0, 4 }).Concat(Int(-1)).Concat(new byte[] { 0, 1 }).ToArray();
                    await Send(stream, 'T', new byte[] { 0, 1 }.Concat(column).ToArray());
                }
                if (type == 'E')
                {
                    await Send(stream, 'D', new byte[] { 0, 1 }.Concat(Int(4)).Concat(Int(mode == "wrong-result" ? 2 : 1)).ToArray());
                    await Send(stream, 'C', Text("SELECT 1"));
                }
                if (type == 'S') await Send(stream, 'Z', new byte[] { (byte)'I' });
            }
        }
        catch (EndOfStreamException) { Closed = true; }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    async Task WaitForClose(Stream stream) { var b = new byte[512]; while (await stream.ReadAsync(b, stop.Token) > 0) { } Closed = true; }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await Completion; }
}
