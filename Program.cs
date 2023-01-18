using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DynamicDnsMonitor
{
    public class Program
    {
        static BufferedFileLogger _logger;
        static IConfiguration _configuration;

        public static CancellationTokenSource _cancelToken = new CancellationTokenSource();

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, configBuilder) =>
                {
                    configBuilder.Sources.Clear();
                    configBuilder.AddEnvironmentVariables();

                    if (args.Length >= 1)
                    {
                        configBuilder.AddJsonFile(args[0], false);
                    }
                    else
                    {
                        var configFilenameMachineName = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"appSettingFile.{System.Environment.MachineName}.json");
                        Console.WriteLine($"Using configFilename={configFilenameMachineName}");
                        configBuilder.AddJsonFile(configFilenameMachineName, false);
                    }

                    _configuration = configBuilder.Build();

                    string logFolder;
                    if (args.Length >= 2)
                    {
                        logFolder = args[1];
                    }
                    else
                    {
                        logFolder = _configuration["LogFolder"];
                    }
                    Console.WriteLine($"Using logFolder={logFolder}");
                    _logger = new BufferedFileLogger(logFolder, nameof(DynamicDnsMonitor));
                })
                .ConfigureLogging((hostBuilderContext, loggingBuilder) =>
                {
                    loggingBuilder.ClearProviders();
                })
                .ConfigureServices((appBuilder, services) =>
                {
                    //var configuration = appBuilder.Configuration;

                    services.AddHttpClient();

                    services.AddSingleton(_logger);
                    services.AddHostedService<BufferedFileLogger.BufferedFileLoggerBackgroundService>();

                    services.AddHostedService<ServiceWorker>();
                });
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Exit("Console_CancelKeyPress()");
            e.Cancel = true;
        }

        public static void Exit(string message)
        {
            _cancelToken.Cancel();
            _logger.Log(message);
        }

        public static async Task Main(string[] args)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;

            var host = CreateHostBuilder(args).Build();

            _logger.Log("Main() Starting");

            await host.RunAsync(_cancelToken.Token);

            _logger.Log("Main() exiting...");

            await _logger.FlushAsync();

            _logger.Close();
        }
    }
}