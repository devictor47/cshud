using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics;
using System.Text;

namespace UdpReceiver
{
    internal struct DelayedPacket
    {
        public byte[] Buffer;
        public int Length;
        public long SendAt;

        public void Release()
        {
            if (Buffer != null)
            {
                ArrayPool<byte>.Shared.Return(Buffer);
                Buffer = [];
            }
        }
    }

    static class Time
    {
        public static long Now => Stopwatch.GetTimestamp();

        public static long NowPlusSeconds(int seconds)
            => Now + (seconds * Stopwatch.Frequency);
    }

    internal class PacketRingBuffer
    {
        private readonly DelayedPacket[] buffer;
        private readonly int mask;

        protected volatile int readerPtr;
        protected volatile int writerPtr;

        public PacketRingBuffer(int sizePowerOfTwo)
        {
            if (sizePowerOfTwo <= 0 ||
                (sizePowerOfTwo & (sizePowerOfTwo - 1)) != 0)
            {
                throw new ArgumentException("Size must be greater than zero and a power of two (2, 4, 8, 16, ...).", nameof(sizePowerOfTwo));
            }

            buffer = new DelayedPacket[sizePowerOfTwo];
            mask = sizePowerOfTwo - 1;
        }

        public virtual void Enqueue(in DelayedPacket pkt)
        {
            // Wraps around to 0 when sizePowerOfTwo is hit.
            int nextWriteIdx = (writerPtr + 1) & mask;

            if (nextWriteIdx == readerPtr)
            {
                // Move the reader pointer ahead,
                // and replace old packet with new one.
                readerPtr = (readerPtr + 1) & mask;
            }

            buffer[writerPtr] = pkt;
            writerPtr = nextWriteIdx;
        }

        public bool TryPeek(out DelayedPacket pkt)
        {
            if (readerPtr == writerPtr)
            {
                pkt = default;
                return false;
            }

            pkt = buffer[readerPtr];
            return true;
        }

        public void Advance()
        {
            readerPtr = (readerPtr + 1) & mask;
        }

        public int Count()
        {
            return (writerPtr - readerPtr) & mask;
        }
    }

    internal static class WsJsonResponses
    {
        const string GENERIC_ERROR_REASON = "an unespecified error occurred";

        public static readonly byte[] BadRequest =
            Encoding.UTF8.GetBytes("{\"error\":\"malformed request\"}");

        public static readonly byte[] ClientNotSubscribed =
            Encoding.UTF8.GetBytes("{\"error\":\"client requested server state while not subscribed to any servers\"}");

        public static class Subscribe
        {
            public static readonly byte[] SubscribeSuccess =
                Encoding.UTF8.GetBytes("{\"subscribe\":\"success\"}");

            public static readonly byte[] UnsubscribeSuccess =
                Encoding.UTF8.GetBytes("{\"unsubscribe\":\"success\"}");

            public static byte[] Failed(string reason)
            {
                if (string.IsNullOrEmpty(reason))
                    reason = GENERIC_ERROR_REASON;

                return Encoding.UTF8.GetBytes($"{{\"subscribe\":\"failed\",\"error\":\"{reason}\"}}");
            }
        }
    }
}
