using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TaskbarPlayerProto;

// Discord Rich Presence: "Listening via MMP" on the user's Discord profile (sent as a game, so it comes before Spotify).
// Talks to the Discord app on this PC through its local pipe (\\.\pipe\discord-ipc-N); nothing goes over the
// internet from here. A background thread sends the newest activity when it changed, and connects again when
// Discord starts later or restarts.
sealed class DiscordPresence : IDisposable
{
    const int OpHandshake = 0, OpFrame = 1, OpClose = 2;
    const int MinGapMs = 4000;          // Discord takes at most 5 updates in 20 s

    readonly string _clientId;
    readonly object _lock = new();
    readonly Thread _thread;
    volatile bool _stop;
    string? _wanted;                    // activity JSON to show, "null" to clear; null = nothing asked yet
    string? _sent;
    NamedPipeClientStream? _pipe;
    long _nextConnect, _lastSend = -MinGapMs;
    public string Status { get; private set; } = "off";

    public DiscordPresence(string clientId)
    {
        _clientId = clientId;
        _thread = new Thread(Run) { IsBackground = true, Name = "Discord presence" };
        _thread.Start();
    }

    // the activity to show (an object serialised to JSON), or null to clear it
    public void Set(object? activity)
    {
        string json = activity == null ? "null" : JsonSerializer.Serialize(activity);
        lock (_lock) _wanted = json;
    }

    void Run()
    {
        while (!_stop)
        {
            Thread.Sleep(500);
            string? wanted;
            lock (_lock) wanted = _wanted;
            if (wanted == null || wanted == _sent) continue;
            if (_pipe == null)
            {
                if (wanted == "null") { _sent = wanted; continue; }     // nothing shown, nothing to clear
                if (Environment.TickCount64 < _nextConnect || !Connect()) continue;
            }
            if (Environment.TickCount64 - _lastSend < MinGapMs) continue;
            try
            {
                string frame = $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{Environment.ProcessId},\"activity\":{wanted}}},\"nonce\":\"{Guid.NewGuid():N}\"}}";
                Write(OpFrame, frame);
                _sent = wanted;
                _lastSend = Environment.TickCount64;
                Status = wanted == "null" ? "connected, cleared" : "showing";
            }
            catch (Exception ex) { Drop("send failed: " + ex.Message); }
        }
        try { if (_pipe != null) Write(OpFrame, $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{Environment.ProcessId},\"activity\":null}},\"nonce\":\"x\"}}"); } catch { }
        _pipe?.Dispose();
    }

    bool Connect()
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(200); }
            catch { pipe.Dispose(); continue; }
            _pipe = pipe;
            try
            {
                Write(OpHandshake, $"{{\"v\":1,\"client_id\":\"{_clientId}\"}}");
                // READY, or a close with the reason (a wrong application id). A Discord that is still starting may
                // not answer at all: don't wait for it forever (Drop closes the pipe, which ends the read)
                var ready = Task.Run(Read);
                if (!ready.Wait(5000)) { Drop("Discord did not answer"); return false; }
                if (ready.Result.op != OpFrame) { Drop("Discord refused the connection", 60000); return false; }
                _sent = null;
                Status = "connected";
                // Discord answers every update; read them in the background so its pipe never fills up
                var reader = new Thread(() => { try { while (!_stop && _pipe == pipe) Read(); } catch { } if (_pipe == pipe) Drop("Discord closed"); })
                    { IsBackground = true, Name = "Discord presence reader" };
                reader.Start();
                return true;
            }
            catch (Exception ex) { Drop("handshake failed: " + ex.Message); return false; }
        }
        Status = "Discord is not running";
        _nextConnect = Environment.TickCount64 + 15000;
        return false;
    }

    void Drop(string why, int retryMs = 15000)
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _sent = null;                       // shown again after reconnecting
        Status = why;
        _nextConnect = Environment.TickCount64 + retryMs;
    }

    void Write(int op, string json)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] head = new byte[8];
        BitConverter.GetBytes(op).CopyTo(head, 0);
        BitConverter.GetBytes(body.Length).CopyTo(head, 4);
        lock (pipe) { pipe.Write(head, 0, 8); pipe.Write(body, 0, body.Length); pipe.Flush(); }
    }

    (int op, string json) Read()
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        byte[] head = ReadExactly(pipe, 8);
        int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1 << 20) throw new IOException("bad frame");
        string json = Encoding.UTF8.GetString(ReadExactly(pipe, len));
        if (op == OpClose) throw new IOException("closed by Discord: " + json);
        return (op, json);
    }

    static byte[] ReadExactly(Stream s, int n)
    {
        var buf = new byte[n];
        for (int got = 0; got < n;)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) throw new EndOfStreamException();
            got += r;
        }
        return buf;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1500);
    }
}
