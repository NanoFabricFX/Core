﻿using System;
using Autofac;
using Microsoft.Extensions.Logging;
using Quidjibo;
using Quidjibo.Autofac.Extensions;
using Quidjibo.Autofac.Modules;
using Quidjibo.DataProtection.Extensions;
using Quidjibo.Extensions;
using Quidjibo.Models;
using Quidjibo.SqlServer.Extensions;
using Resgrid.Config;
using Resgrid.Model.Helpers;
using Resgrid.Model.Providers;
using Resgrid.Model.Services;
using Resgrid.Providers.Bus;
using Resgrid.Workers.Console.Commands;
using Resgrid.Workers.Framework;
using System.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Resgrid.Workers.Console.Tasks;
using Serilog.Formatting.Json;

namespace Resgrid.Workers.Console
{
	public class Program
	{
		public static IConfigurationRoot Configuration { get; private set; }


		static async Task Main(string[] args)
		{
			System.Console.WriteLine("Resgrid Worker Engine");
			System.Console.WriteLine("-----------------------------------------");

			LoadConfiguration(args);
			Prime();

			var builder = new HostBuilder()
				.ConfigureAppConfiguration((hostingContext, config) =>
				{
					config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
					config.AddEnvironmentVariables();

					if (args != null)
					{
						config.AddCommandLine(args);
					}
				})
				.ConfigureServices((hostContext, services) =>
				{
					services.AddOptions();
					services.AddSingleton<IHostedService, QueuesProcessingService>();
					services.AddSingleton<IHostedService, SystemProcessingService>();
					services.AddSingleton<IHostedService, ScheduledJobsService>();
				})
				.ConfigureLogging((hostingContext, logging) => {
					logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
					logging.AddConsole();
				});

			await builder.RunConsoleAsync();
		}

		private static void Prime()
		{
			System.Console.WriteLine("Initializing Dependencies...");

			if (!String.IsNullOrWhiteSpace(Configuration["DOTNET_RUNNING_IN_CONTAINER"]))
				ConfigProcessor.LoadAndProcessConfig(ConfigurationManager.AppSettings["ConfigPath"]);

			SetConnectionString();

			Bootstrapper.Initialize();

			var eventAggragator = Bootstrapper.GetKernel().Resolve<IEventAggregator>();
			var outbound = Bootstrapper.GetKernel().Resolve<IOutboundEventProvider>();
			var coreEventService = Bootstrapper.GetKernel().Resolve<ICoreEventService>();

			SerializerHelper.WarmUpProtobufSerializer();
			System.Console.WriteLine("Finished Initializing Dependencies.");
		}

		private static void LoadConfiguration(string[] args)
		{
			System.Console.WriteLine("Loading Configuration...");

			var builder = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
				.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
				.AddCommandLine(args)
				.AddEnvironmentVariables();

			Configuration = builder.Build();
			System.Console.WriteLine("Finished Loading Configuration.");
		}

		private static void SetConnectionString()
		{
			var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
			var connectionStringsSection = (ConnectionStringsSection)config.GetSection("connectionStrings");

			//var test = Configuration["ConnectionStrings:ResgridContext"];

			ConfigProcessor.LoadAndProcessEnvVariables(Configuration.AsEnumerable());

			if (connectionStringsSection.ConnectionStrings["ResgridContext"] != null)
				connectionStringsSection.ConnectionStrings["ResgridContext"].ConnectionString = DataConfig.ConnectionString;
			else
				connectionStringsSection.ConnectionStrings.Add(new ConnectionStringSettings("ResgridContext", DataConfig.ConnectionString));

			config.Save();
			ConfigurationManager.RefreshSection("connectionStrings");
		}
	}

	public class QueuesProcessingService : BackgroundService
	{
		private ILogger _logger;

		public QueuesProcessingService(ILogger<QueuesProcessingService> logger)
		{
			_logger = logger;
		}

		protected override Task ExecuteAsync(CancellationToken stoppingToken)
		{
			_logger.Log(LogLevel.Information, "Starting Queues Event Watcher");

			Task.Run(async () =>
			{
				var queuesTask = new QueuesProcessorTask(_logger);
				await queuesTask.ProcessAsync(new QueuesProcessorCommand(4), null, stoppingToken);
			}, stoppingToken);

			return Task.CompletedTask;
		}
	}

	public class SystemProcessingService : BackgroundService
	{
		private ILogger _logger;

		public SystemProcessingService(ILogger<SystemProcessingService> logger)
		{
			_logger = logger;
		}

		protected override Task ExecuteAsync(CancellationToken stoppingToken)
		{
			_logger.Log(LogLevel.Information, "Starting Queues Event Watcher");

			Task.Run(async () =>
			{
				var queuesTask = new QueuesProcessorTask(_logger);
				await queuesTask.ProcessAsync(new QueuesProcessorCommand(4), null, stoppingToken);
			}, stoppingToken);

			return Task.CompletedTask;
		}
	}

	public class ScheduledJobsService : BackgroundService
	{
		private ILogger _logger;

		public ScheduledJobsService(ILogger<ScheduledJobsService> logger)
		{
			_logger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			var aes = Aes.Create();
			var key = string.Join(",", aes.Key);
			//System.Console.CancelKeyPress += (s, e) => { cancellationToken..Cancel(); };

			_logger.Log(LogLevel.Information, "Starting Scheduler");

			var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

			var logger = loggerFactory.CreateLogger<Program>();

			// Setup DI
			var containerBuilder = new ContainerBuilder();
			containerBuilder.RegisterModule(new QuidjiboModule(typeof(Program).Assembly));
			containerBuilder.RegisterInstance<ILogger>(logger);
			var container = containerBuilder.Build();

			// Setup Quidjibo
			var quidjiboBuilder = new QuidjiboBuilder()
								  .ConfigureLogging(loggerFactory)
								  .UseAutofac(container)
								  .UseAes(Encoding.ASCII.GetBytes(WorkerConfig.PayloadKey))
								  .UseSqlServer(WorkerConfig.WorkerDbConnectionString)
								  .ConfigurePipeline(pipeline => pipeline.UseDefault());

			// Quidjibo Client
			var client = quidjiboBuilder.BuildClient();

			_logger.Log(LogLevel.Information, "Scheduler Started");

			//// Long Running Jobs
			////await client.PublishAsync(new SystemQueueProcessorCommand(8), cancellationToken);
			////await client.PublishAsync(new QueuesProcessorCommand(4), cancellationToken);

			var isEventsOnly = Environment.GetEnvironmentVariable("RESGRID__EVENTSONLY");

			if (String.IsNullOrWhiteSpace(isEventsOnly) || isEventsOnly == "False")
			{
				_logger.Log(LogLevel.Information, "Starting Scheduled Jobs");
				// Scheduled Jobs

				_logger.Log(LogLevel.Information, "Scheduling Calendar Notifications");
				await client.ScheduleAsync("Calendar Notifications",
					new CalendarNotificationCommand(1),
					Cron.MinuteIntervals(20),
					stoppingToken);

				//System.Console.WriteLine("Scheduling Calendar Notifications");
				//await client.ScheduleAsync("Call Email Import",
				//	new CallEmailImportCommand(2),
				//	Cron.MinuteIntervals(5),
				//	cancellationToken);

				_logger.Log(LogLevel.Information, "Scheduling Call Pruning");
				await client.ScheduleAsync("Call Pruning",
					new CallPruneCommand(3),
					Cron.MinuteIntervals(60),
					stoppingToken);

				_logger.Log(LogLevel.Information, "Scheduling Report Delivery");
				await client.ScheduleAsync("Report Delivery",
					new ReportDeliveryTaskCommand(5),
					Cron.MinuteIntervals(60),
					stoppingToken);

				_logger.Log(LogLevel.Information, "Scheduling Shift Notifier");
				await client.ScheduleAsync("Shift Notifier",
					new ShiftNotiferCommand(6),
					Cron.MinuteIntervals(60),
					stoppingToken);

				_logger.Log(LogLevel.Information, "Scheduling Staffing Schedule");
				await client.ScheduleAsync("Staffing Schedule",
					new Commands.StaffingScheduleCommand(7),
					Cron.MinuteIntervals(15),
					stoppingToken);

				_logger.Log(LogLevel.Information, "Scheduling Training Notifier");
				await client.ScheduleAsync("Training Notifier",
					new TrainingNotiferCommand(9),
					Cron.MinuteIntervals(60),
					stoppingToken);
			}
			else
			{
				_logger.Log(LogLevel.Information, "Starting in Events Only Mode!");
			}


			// Quidjibo Server
			using (var workServer = quidjiboBuilder.BuildServer())
			{
				// Start Quidjibo
				workServer.Start();
				stoppingToken.WaitHandle.WaitOne();
			}

			//while (!cancellationToken.IsCancellationRequested)
			//{
			//	await Task.Delay(TimeSpan.FromSeconds(1));
			//}
		}
	}
}
