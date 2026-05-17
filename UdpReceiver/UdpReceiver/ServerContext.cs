using Fleck;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace UdpReceiver
{
    internal class ServerContext : IDisposable
    {
        private readonly Lock clientsLock = new();
        
        public ServerState State = new();
        public IWebSocketConnection[] Clients = [];

        public readonly AwaitablePacketRingBuffer PacketQueue = new(256);

        public void AddClient(IWebSocketConnection ws)
        {
            lock (clientsLock)
            {
                Clients = [.. Clients, ws];
            }
        }

        public void RemoveClient(IWebSocketConnection ws)
        {
            lock (clientsLock)
            {
                Clients = [.. Clients.Where(x => x != ws)];
            }
        }

        public void Dispose()
        {
            PacketQueue.Dispose();
        }
    }

    
    internal class AwaitablePacketRingBuffer : PacketRingBuffer, IDisposable
    {
        private readonly SemaphoreSlim signal;

        public AwaitablePacketRingBuffer(int sizePowerOfTwo)
            : base(sizePowerOfTwo)
        {
            signal = new(0);
        }

        public override void Enqueue(in DelayedPacket pkt)
        {
            bool wasEmpty = readerPtr == writerPtr;

            base.Enqueue(pkt);

            if (wasEmpty)
                signal.Release();
        }

        public async Task AwaitPacket()
        {
            await signal.WaitAsync();
        }

        public void Dispose()
        {
            signal.Dispose();
        }
    }

    // Different mechanism. Deprecated.
    internal class DelayedPacketQueueDepre : IDisposable
    {
        private readonly PacketRingBuffer buffer = new(256);
        private readonly SemaphoreSlim signal = new(0);

        public void Enqueue(DelayedPacket pkt)
        {
            buffer.Enqueue(pkt);

            signal.Release();
        }

        public async Task<DelayedPacket> WaitForNextPacketAsync()
        {
            while (true)
            {
                if (buffer.TryPeek(out var pkt))
                {
                    if (pkt.SendAt <= Time.Now)
                    {
                        buffer.Advance();
                        return pkt;
                    }

                    // Calculate how much to wait.
                    int delay = (int)Math.Max(1, (pkt.SendAt - Time.Now) * 1000 / Stopwatch.Frequency);
                    await signal.WaitAsync(delay);
                    continue;
                }

                // Queue is empty.
                await signal.WaitAsync();
            }
        }

        public void Dispose()
        {
            signal.Dispose();
        }
    }
}
