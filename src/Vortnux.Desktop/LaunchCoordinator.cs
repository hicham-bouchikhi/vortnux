using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using Vortnux.Core;

namespace Vortnux.Desktop;

/// <summary>One owner per user/state directory; secondary processes deliver requests before exiting.</summary>
public sealed class LaunchCoordinator : IDisposable
{
    private volatile bool accepting = true;
    public bool Accepting { get => accepting; set => accepting = value; }
    private int disposed;
    private const int MaximumBytes = 65536;
    private readonly FileStream ownership;
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<string?> pending = new();
    private Func<string?, bool>? receiver;
    private readonly Task listener;

    private LaunchCoordinator(FileStream ownership, string pipeName)
    {
        this.ownership = ownership;
        server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        listener = Task.Run(ListenAsync);
    }

    private static string PipeName(string directory) => "vortnux-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + Path.GetFullPath(directory))))[..32];

    public static LaunchCoordinator? TryOwn(string directory, bool handover = false)
    {
        Directory.CreateDirectory(directory);
        FileStream ownership;
        try
        {
            using var reservation = handover ? null : ReserveHandover(directory);
            ownership = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        try { return new LaunchCoordinator(ownership, PipeName(directory)); }
        catch { ownership.Dispose(); throw; }
    }

    internal static FileStream ReserveHandover(string directory)
    {
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "handover.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static string? LinkArgument(string[] args) => args.FirstOrDefault(value =>
        value.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static async Task<bool> ForwardAsync(string directory, string? link, CancellationToken token = default)
    {
        if (link is not null) _ = NexusLinks.Parse(link);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            var bytes = Encoding.UTF8.GetBytes(link ?? "");
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await pipe.WriteAsync(header, timeout.Token); await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var reply = new byte[1]; await pipe.ReadExactlyAsync(reply, timeout.Token);
            return reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { return false; }
    }

    public void Attach(Func<string?, bool> handler)
    {
        lock (pending)
        {
            receiver = handler;
            while (pending.TryDequeue(out var link)) Dispatch(handler, link);
        }
    }

    private static void Dispatch(Func<string?, bool> handler, string? link) => Dispatcher.UIThread.Post(() => handler(link));

    private async Task<bool> DeliverAsync(Func<string?, bool> handler, string? link) =>
        await Dispatcher.UIThread.InvokeAsync(() => Accepting && handler(link));

    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                var pipe = server;
                await pipe.WaitForConnectionAsync(lifetime.Token);
                connected = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 0 or > MaximumBytes) continue;
                var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, timeout.Token);
                var link = length == 0 ? null : Encoding.UTF8.GetString(bytes);
                if (link is not null) _ = NexusLinks.Parse(link);
                if (!Accepting) continue;
                Func<string?, bool>? handler;
                lock (pending)
                {
                    handler = receiver;
                    if (handler is null)
                    {
                        if (pending.Count >= 128) continue;
                        pending.Enqueue(link);
                    }
                }
                if (handler is not null && !await DeliverAsync(handler, link).WaitAsync(timeout.Token)) continue;
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ModManagerException) { }
            finally { if (connected) server.Disconnect(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try { listener.GetAwaiter().GetResult(); }
        finally { server.Dispose(); ownership.Dispose(); lifetime.Dispose(); }
    }
}
