using System.Collections.Concurrent;
using System.Threading.Channels;

namespace UdpReceiver
{
    internal class Logger : IDisposable
    {
        static readonly HashSet<char> InvalidChars = [.. Path.GetInvalidFileNameChars()];

        private readonly StreamWriter writer;
        Channel<(string message, bool printConsole)> channel = Channel.CreateUnbounded<(string message, bool printConsole)>();

        private readonly CancellationTokenSource cts = new();
        private readonly Task writerTask;

        private readonly string timestamp;

        public Logger(string nameWithoutExtension = "", bool autoFlush = false, string? timestamp = null)
        {
            if (string.IsNullOrEmpty(nameWithoutExtension))
                nameWithoutExtension = string.Empty;

            nameWithoutExtension = string.Create(nameWithoutExtension.Length + 1, nameWithoutExtension, (span, src) =>
            {
                for (int i = 0; i < src.Length; i++)
                    span[i] = InvalidChars.Contains(src[i]) ? '*' : src[i];
                span[src.Length] = '_';
            });

            if (timestamp == null)
            {
                this.timestamp = $"{DateTime.UtcNow:dd_MM_yyyy-HH_mm_ss_fff}";
            }
            else
            {
                this.timestamp = timestamp;
            }

            writer = new(
            new FileStream(
                $"{nameWithoutExtension}{this.timestamp}.log",
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read))
            {
                AutoFlush = autoFlush
            };

            writerTask = Write(cts.Token);
        }

        public void Write(string message, bool printConsole = true)
        {
            channel.Writer.TryWrite(new(message, printConsole));
        }

        public void WriteLine(string message, bool printConsole = true)
        {
            channel.Writer.TryWrite(new(message + Environment.NewLine, printConsole));
        }

        private async Task Write(CancellationToken token)
        {
            int failures = 0;

            try
            {
                while (await channel.Reader.WaitToReadAsync(token))
                {
                    while (channel.Reader.TryRead(out var item))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(item.message))
                            {
                                var ts = DateTime.Now.ToString("HH:mm:ss");

                                writer.Write($"{ts} {item.message}");

                                if (item.printConsole)
                                    Console.Write($"{ts} {item.message}");

                                failures = 0; // reset on success
                            }
                        }
                        catch
                        {
                            failures++;
                            await Task.Delay(
                                Math.Min(10_000, 1_000 * failures),
                                token);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception)
            {
                // Channel failure?
                throw;
            }
        }

        public async Task DisposeAsync()
        {
            channel.Writer.Complete();

            // Cancel tasks.
            cts.Cancel();
            await writerTask;

            cts.Dispose();
            writerTask.Dispose();

            // Dispose resources.
            writer.Dispose();
            cts.Dispose();
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
