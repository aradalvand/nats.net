using System.Buffers;
using System.Text;
using NATS.Client.Core2.Tests;
using NATS.Client.Core2.Tests.ExtraUtils.FrameworkPolyfillExtensions;

namespace NATS.Client.Core.Tests;

[Collection("nats-server")]
public class ProtocolTest
{
    private readonly ITestOutputHelper _output;
    private readonly NatsServerFixture _server;

    public ProtocolTest(ITestOutputHelper output, NatsServerFixture server)
    {
        _output = output;
        _server = server;
    }

    [Fact]
    public async Task Subscription_with_same_subject()
    {
        var nats1 = new NatsConnection(new NatsOpts { Url = _server.Url });
        var proxy = new NatsProxy(_server.Port);
        var nats2 = new NatsConnection(new NatsOpts { Url = $"nats://127.0.0.1:{proxy.Port}", ConnectTimeout = TimeSpan.FromSeconds(10) });

        var sub1 = await nats2.SubscribeCoreAsync<int>("foo.bar");
        var sub2 = await nats2.SubscribeCoreAsync<int>("foo.bar");
        var sub3 = await nats2.SubscribeCoreAsync<int>("foo.baz");

        var sync1 = 0;
        var sync2 = 0;
        var sync3 = 0;
        var count = new WaitSignal(3);

        var reg1 = sub1.Register(m =>
        {
            if (m.Data == 0)
            {
                Interlocked.Exchange(ref sync1, 1);
                return;
            }

            count.Pulse(m.Subject == "foo.bar" ? null : new Exception($"Subject mismatch {m.Subject}"));
        });

        var reg2 = sub2.Register(m =>
        {
            if (m.Data == 0)
            {
                Interlocked.Exchange(ref sync2, 1);
                return;
            }

            count.Pulse(m.Subject == "foo.bar" ? null : new Exception($"Subject mismatch {m.Subject}"));
        });

        var reg3 = sub3.Register(m =>
        {
            if (m.Data == 0)
            {
                Interlocked.Exchange(ref sync3, 1);
                return;
            }

            count.Pulse(m.Subject == "foo.baz" ? null : new Exception($"Subject mismatch {m.Subject}"));
        });

        // Since subscription and publishing are sent through different connections there is
        // a race where one or more subscriptions are made after the publishing happens.
        // So, we make sure subscribers are accepted by the server before we send any test data.
        await Retry.Until(
            "all subscriptions are active",
            () => Volatile.Read(ref sync1) + Volatile.Read(ref sync2) + Volatile.Read(ref sync3) == 3,
            async () =>
            {
                await nats1.PublishAsync("foo.bar", 0);
                await nats1.PublishAsync("foo.baz", 0);
            });

        await nats1.PublishAsync("foo.bar", 1);
        await nats1.PublishAsync("foo.baz", 1);

        // Wait until we received all test data
        await count;

        var frames = proxy.ClientFrames.OrderBy(f => f.Message).ToList();

        foreach (var frame in frames)
        {
            _output.WriteLine($"[PROXY] {frame}");
        }

        Assert.Equal(3, frames.Count);
        Assert.StartsWith("SUB foo.bar", frames[0].Message);
        Assert.StartsWith("SUB foo.bar", frames[1].Message);
        Assert.StartsWith("SUB foo.baz", frames[2].Message);
        Assert.False(frames[0].Message.Equals(frames[1].Message), "Should have different SIDs");

        await sub1.DisposeAsync();
        await reg1;
        await sub2.DisposeAsync();
        await reg2;
        await sub3.DisposeAsync();
        await reg3;
        await nats1.DisposeAsync();
        await nats2.DisposeAsync();
        proxy.Dispose();
    }

    [Fact]
    public async Task Subscription_queue_group()
    {
        var proxy = new NatsProxy(_server.Port);
        var nats = new NatsConnection(new NatsOpts { Url = $"nats://127.0.0.1:{proxy.Port}", ConnectTimeout = TimeSpan.FromSeconds(10) });
        var subject = $"{_server.GetNextId()}.foo";

        await using var sub1 = await nats.SubscribeCoreAsync<int>(subject, queueGroup: "group1");
        await using var sub2 = await nats.SubscribeCoreAsync<int>(subject, queueGroup: "group2");

        await Retry.Until(
            "frames collected",
            () => proxy.ClientFrames.Count(f => f.Message.StartsWith($"SUB {subject}")) == 2);

        var frames = proxy.ClientFrames.Select(f => f.Message).ToList();

        foreach (var frame in frames)
        {
            _output.WriteLine($"frame: {frame}");
        }

        Assert.StartsWith($"SUB {subject} group1 ", frames[0]);
        Assert.StartsWith($"SUB {subject} group2 ", frames[1]);

        await nats.DisposeAsync();
    }

    [Fact]
    public async Task Publish_empty_message_for_notifications()
    {
        void Log(string text)
        {
            _output.WriteLine($"[TESTS] {DateTime.Now:HH:mm:ss.fff} {text}");
        }

        var proxy = new NatsProxy(_server.Port);
        var nats = new NatsConnection(new NatsOpts { Url = $"nats://127.0.0.1:{proxy.Port}", ConnectTimeout = TimeSpan.FromSeconds(10) });

        var prefix = $"{_server.GetNextId()}.foo";

        var sync = 0;
        var signal1 = new WaitSignal<NatsMsg<int>>();
        var signal2 = new WaitSignal<NatsMsg<int>>();
        var sub = await nats.SubscribeCoreAsync<int>($"{prefix}.*");
        var reg = sub.Register(m =>
        {
            if (m.Subject == $"{prefix}.sync")
            {
                Interlocked.Exchange(ref sync, 1);
            }
            else if (m.Subject == $"{prefix}.signal1")
            {
                signal1.Pulse(m);
            }
            else if (m.Subject == $"{prefix}.signal2")
            {
                signal2.Pulse(m);
            }
        });

        await Retry.Until(
            "subscription is active",
            () => Volatile.Read(ref sync) == 1,
            async () => await nats.PublishAsync($"{prefix}.sync"),
            retryDelay: TimeSpan.FromSeconds(1));

        Log("PUB notifications");
        await nats.PublishAsync($"{prefix}.signal1");
        var msg1 = await signal1;
        Assert.Equal(0, msg1.Data);
        Assert.Null(msg1.Headers);
        var pubFrame1 = proxy.Frames.First(f => f.Message.StartsWith($"PUB {prefix}.signal1"));
        Assert.Equal($"PUB {prefix}.signal1 0␍␊", pubFrame1.Message);
        var msgFrame1 = proxy.Frames.First(f => f.Message.StartsWith($"MSG {prefix}.signal1"));
        Assert.Matches($@"^MSG {prefix}.signal1 \w+ 0␍␊$", msgFrame1.Message);

        Log("HPUB notifications");
        await nats.PublishAsync($"{prefix}.signal2", headers: new NatsHeaders());
        var msg2 = await signal2;
        Assert.Equal(0, msg2.Data);
        Assert.NotNull(msg2.Headers);
        Assert.Empty(msg2.Headers!);
        var pubFrame2 = proxy.Frames.First(f => f.Message.StartsWith($"HPUB {prefix}.signal2"));
        Assert.Equal($"HPUB {prefix}.signal2 12 12␍␊NATS/1.0␍␊␍␊", pubFrame2.Message);
        var msgFrame2 = proxy.Frames.First(f => f.Message.StartsWith($"HMSG {prefix}.signal2"));
        Assert.Matches($@"^HMSG {prefix}.signal2 \w+ 12 12␍␊NATS/1.0␍␊␍␊$", msgFrame2.Message);

        await sub.DisposeAsync();
        await reg;
    }

    [Fact]
    public async Task Unsubscribe_max_msgs()
    {
        const int maxMsgs = 10;
        const int pubMsgs = 5;
        const int extraMsgs = 3;

        void Log(string text)
        {
            _output.WriteLine($"[TESTS] {DateTime.Now:HH:mm:ss.fff} {text}");
        }

        // Use a single server to test multiple scenarios to make test runs more efficient
        var proxy = new NatsProxy(_server.Port);
        var nats = new NatsConnection(new NatsOpts { Url = $"nats://127.0.0.1:{proxy.Port}", ConnectTimeout = TimeSpan.FromSeconds(10) });
        var sid = 0;

        Log("### Auto-unsubscribe after consuming max-msgs");
        {
            var opts = new NatsSubOpts { MaxMsgs = maxMsgs };
            await using var sub = await nats.SubscribeCoreAsync<int>("foo", opts: opts);
            sid++;

            await Retry.Until("all frames arrived", () => proxy.Frames.Count >= 2);
            Assert.Equal($"SUB foo {sid}", proxy.Frames[0].Message);
            Assert.Equal($"UNSUB {sid} {maxMsgs}", proxy.Frames[1].Message);

            Log("Send more messages than max to check we only get max");
            for (var i = 0; i < maxMsgs + extraMsgs; i++)
            {
                await nats.PublishAsync("foo", i);
            }

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cancellationToken = cts.Token;
            var count = 0;
            await foreach (var natsMsg in sub.Msgs.ReadAllAsync(cancellationToken))
            {
                Assert.Equal(count, natsMsg.Data);
                count++;
            }

            Assert.Equal(maxMsgs, count);
            Assert.Equal(NatsSubEndReason.MaxMsgs, ((NatsSubBase)sub).EndReason);
        }

        Log("### Manual unsubscribe");
        {
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await proxy.FlushFramesAsync(nats, clear: true, cts1.Token);

            await using var sub = await nats.SubscribeCoreAsync<int>("foo2");
            sid++;
            await sub.UnsubscribeAsync();

            await Retry.Until("all frames arrived", () => proxy.ClientFrames.Count == 2);

            Assert.Equal($"SUB foo2 {sid}", proxy.ClientFrames[0].Message);
            Assert.Equal($"UNSUB {sid}", proxy.ClientFrames[1].Message);

            Log("Send messages to check we receive none since we're already unsubscribed");
            for (var i = 0; i < pubMsgs; i++)
            {
                await nats.PublishAsync("foo2", i);
            }

            await Retry.Until("all pub frames arrived", () => proxy.Frames.Count(f => f.Message.StartsWith("PUB foo2")) == pubMsgs);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cancellationToken = cts.Token;
            var count = 0;
            await foreach (var unused in sub.Msgs.ReadAllAsync(cancellationToken))
            {
                count++;
            }

            Assert.Equal(0, count);
            Assert.Equal(NatsSubEndReason.None, ((NatsSubBase)sub).EndReason);
        }

        Log("### Reconnect");
        {
            proxy.Reset();

            var opts = new NatsSubOpts { MaxMsgs = maxMsgs };
            var sub = await nats.SubscribeCoreAsync<int>("foo3", opts: opts);
            sid++;
            var count = 0;
            var reg = sub.Register(_ => Interlocked.Increment(ref count));
            await Retry.Until("subscribed", () => proxy.Frames.Any(f => f.Message == $"SUB foo3 {sid}"));

            for (var i = 0; i < pubMsgs; i++)
            {
                await nats.PublishAsync("foo3", i);
            }

            await Retry.Until("published", () => proxy.Frames.Count(f => f.Message.StartsWith("PUB foo3")) == pubMsgs);
            await Retry.Until("received", () => Volatile.Read(ref count) == pubMsgs);

            var pending = maxMsgs - pubMsgs;
            Assert.Equal(pending, ((NatsSubBase)sub).PendingMsgs);

            proxy.Reset();

            Log("Expect SUB + UNSUB");
            await Retry.Until("re-subscribed", () => proxy.ClientFrames.Count == 2);

            Log("Make sure we're still using the same SID");
            Assert.Equal($"SUB foo3 {sid}", proxy.ClientFrames[0].Message);
            Assert.Equal($"UNSUB {sid} {pending}", proxy.ClientFrames[1].Message);

            Log("We already published a few, this should exceed max-msgs");
            for (var i = 0; i < maxMsgs; i++)
            {
                await nats.PublishAsync("foo3", i);
            }

            await Retry.Until(
                "published more",
                () => proxy.ClientFrames.Count(f => f.Message.StartsWith("PUB foo3")) == maxMsgs);

            await Retry.Until(
                "unsubscribed with max-msgs",
                () => ((NatsSubBase)sub).EndReason == NatsSubEndReason.MaxMsgs);

            // Wait until msg channel is completed and drained
            await reg;

            Assert.Equal(maxMsgs, Volatile.Read(ref count));

            await sub.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconnect_with_sub_and_additional_commands()
    {
        var proxy = new NatsProxy(_server.Port);
        var nats = new NatsConnection(new NatsOpts { Url = $"nats://127.0.0.1:{proxy.Port}", ConnectTimeout = TimeSpan.FromSeconds(10) });

        var subject = $"{_server.GetNextId()}.foo";
        var cmdSubject = $"{_server.GetNextId()}.bar";

        var sync = 0;
        await using var sub = new NatsSubReconnectTest(nats, subject, cmdSubject, i => Interlocked.Exchange(ref sync, i));
        await nats.AddSubAsync(sub);

        await Retry.Until(
            "subscribed",
            () => Volatile.Read(ref sync) == 1,
            async () => await nats.PublishAsync(subject, 1));

        var disconnected = new WaitSignal();
        nats.ConnectionDisconnected += (_, _) =>
        {
            disconnected.Pulse();
            return default;
        };

        proxy.Reset();

        await disconnected;

        await Retry.Until(
            "re-subscribed",
            () => Volatile.Read(ref sync) == 2,
            async () => await nats.PublishAsync(subject, 2));

        await Retry.Until(
            "frames collected",
            () => proxy.ClientFrames.Any(f => f.Message.StartsWith($"PUB {subject}")));

        var frames = proxy.ClientFrames.Select(f => f.Message).ToList();

        foreach (var frame in frames)
        {
            _output.WriteLine($"frame: {frame}");
        }

        Assert.StartsWith($"SUB {subject}", frames[0]);

        for (var i = 0; i < 100; i++)
        {
            Assert.StartsWith($"PUB {cmdSubject}{i}", frames[i + 1]);
        }

        Assert.StartsWith($"PUB {subject}", frames[101]);

        await nats.DisposeAsync();
    }

    [Fact]
    public async Task Proactively_reject_payloads_over_the_threshold_set_by_server()
    {
        await using var nats = new NatsConnection(new NatsOpts { Url = _server.Url });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var sync = 0;
        var count = 0;
        var signal1 = new WaitSignal<NatsMsg<byte[]>>();
        var signal2 = new WaitSignal<NatsMsg<byte[]>>();
        var subTask = Task.Run(
            async () =>
            {
                await foreach (var m in nats.SubscribeAsync<byte[]>("foo.*", cancellationToken: cts.Token))
                {
                    if (m.Subject == "foo.sync")
                    {
                        Interlocked.Exchange(ref sync, 1);
                        continue;
                    }

                    Interlocked.Increment(ref count);

                    if (m.Subject == "foo.signal1")
                    {
                        signal1.Pulse(m);
                    }
                    else if (m.Subject == "foo.signal2")
                    {
                        signal2.Pulse(m);
                    }
                    else if (m.Subject == "foo.end")
                    {
                        break;
                    }
                }
            },
            cancellationToken: cts.Token);

        await Retry.Until(
            reason: "subscription is active",
            condition: () => Volatile.Read(ref sync) == 1,
            action: async () => await nats.PublishAsync("foo.sync", cancellationToken: cts.Token),
            retryDelay: TimeSpan.FromSeconds(.3));
        {
            var payload = new byte[nats.ServerInfo!.MaxPayload];
            await nats.PublishAsync("foo.signal1", payload, cancellationToken: cts.Token);
            var msg1 = await signal1;
            Assert.Equal(payload.Length, msg1.Data!.Length);
        }

        {
            var payload = new byte[nats.ServerInfo!.MaxPayload + 1];
            var exception = await Assert.ThrowsAsync<NatsPayloadTooLargeException>(async () =>
                await nats.PublishAsync("foo.none", payload, cancellationToken: cts.Token));
            Assert.Matches(@"Payload size \d+ exceeds server's maximum payload size \d+", exception.Message);
        }

        {
            var payload = new byte[123];
            await nats.PublishAsync("foo.signal2", payload, cancellationToken: cts.Token);
            var msg1 = await signal2;
            Assert.Equal(payload.Length, msg1.Data!.Length);
        }

        await nats.PublishAsync("foo.end", cancellationToken: cts.Token);

        await subTask;

        Assert.Equal(3, Volatile.Read(ref count));
    }

    private sealed class NatsSubReconnectTest : NatsSubBase
    {
        private readonly string _cmdSubject;
        private readonly Action<int> _callback;

        internal NatsSubReconnectTest(NatsConnection connection, string subject, string cmdSubject, Action<int> callback)
            : base(connection, connection.SubscriptionManager, subject, queueGroup: default, opts: default)
        {
            _cmdSubject = cmdSubject;
            _callback = callback;
        }

        internal override async ValueTask WriteReconnectCommandsAsync(CommandWriter commandWriter, int sid)
        {
            await base.WriteReconnectCommandsAsync(commandWriter, sid);

            // Any additional commands to send on reconnect
            for (var i = 0; i < 100; i++)
            {
                await commandWriter.PublishAsync($"{_cmdSubject}{i}", default, default, default, NatsRawSerializer<byte>.Default, default);
            }
        }

        protected override ValueTask ReceiveInternalAsync(string subject, string? replyTo, ReadOnlySequence<byte>? headersBuffer, ReadOnlySequence<byte> payloadBuffer)
        {
            _callback(int.Parse(Encoding.UTF8.GetString(payloadBuffer.ToArray())));
            DecrementMaxMsgs();
            return default;
        }

        protected override void TryComplete()
        {
        }
    }
}
