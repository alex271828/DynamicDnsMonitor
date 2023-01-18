using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicDnsMonitor
{
    public class BufferedFileLogger
    {
        public class BufferedFileLoggerBackgroundService : BackgroundService
        {
            readonly BufferedFileLogger _logger;

            public BufferedFileLoggerBackgroundService(BufferedFileLogger logger)
            {
                _logger = logger;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);

                    await _logger.FlushAsync();
                }
            }
        }

        static System.Text.UTF8Encoding utf8enc = new System.Text.UTF8Encoding(false, true);
        Stream _logStream = null;

        public BufferedFileLogger(string logFolder, string filenamePrefix)
        {
            if (!string.IsNullOrEmpty(logFolder) && Directory.Exists(logFolder))
            {
                var nowDirString = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fffffff");

                var filename = Path.Combine(logFolder, $"{filenamePrefix}_latest.log");

                var filenameBak = Path.Combine(logFolder, $"{filenamePrefix}_{nowDirString}.log");

                if (File.Exists(filename)) File.Move(filename, filenameBak, false);

                _logStream = File.Open(filename, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            }
        }

        public void Close()
        {
            if (_logStream != null)
            {
                try
                {
                    _logStream.Close();
                    _logStream.Dispose();
                }
                finally
                {
                    _logStream = null;
                }
            }
        }

        ConcurrentQueue<byte[]> _messages = new ConcurrentQueue<byte[]>();

        void WriteLog(string message, IEnumerable<KeyValuePair<string, string>> properties, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"################################ {DateTime.UtcNow.ToString("o")}");
            sb.AppendLine(message);
            sb.AppendLine();
            if (properties != null)
            {
                foreach (var item in properties)
                {
                    sb.AppendLine($"  {item.Key}={item.Value}");
                }
            }
            if (ex != null)
            {
                sb.AppendLine($"  Exception={ex.ToString()}");
            }

            sb.AppendLine();

            string messageText = sb.ToString();

            var bytes = utf8enc.GetBytes(messageText);

            if (_logStream != null)
            {
                _messages.Enqueue(bytes);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(messageText);
            }
        }

        public async Task FlushAsync()
        {
            if (_logStream != null)
            {
                while (_messages.TryDequeue(out var message))
                {
                    if (message != null)
                    {
                        await _logStream.WriteAsync(message, 0, message.Length);
                    }
                }
                await _logStream.FlushAsync();
            }
        }

        public void Log(string message, IEnumerable<KeyValuePair<string, string>> properties)
        {
            WriteLog(message, properties, null);
        }

        public void Log(string message, IEnumerable<KeyValuePair<string, string>> properties, Exception ex)
        {
            WriteLog(message, properties, ex);
        }

        public void Log(string message, Exception ex)
        {
            WriteLog(message, null, ex);
        }

        public void Log(string message)
        {
            WriteLog(message, null, null);
        }
    }
}
