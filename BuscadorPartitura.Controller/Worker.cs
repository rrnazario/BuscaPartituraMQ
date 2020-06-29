using System;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuscadorPartitura.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Events;
using BuscadorPartitura.Controller.Model;
using BuscadorPartitura.Infra.Constants;
using BuscadorPartitura.Core.Helpers;
using BuscadorPartitura.Core.Model;
using BuscadorPartitura.Infra.Helpers;

namespace BuscadorPartitura.Controller
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IMessageQueueConnection _mq;
        private readonly IDatabase _db;

        private readonly List<RunningCrawler> runningCrawlers = new List<RunningCrawler>(); //Buscar do banco caso morra

        public Worker(ILogger<Worker> logger, IMessageQueueConnection mq, IDatabase db)
        {
            _logger = logger;
            _mq = mq;
            _db = db;

            _mq.CreateQueue(MqHelper.OrchestratorQueueName());
            _mq.ConfigureConsumeQueueListener(MqHelper.OrchestratorQueueName(), true, CreateCrawler);
        }        

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var crawler in runningCrawlers)
                {
                    var process = Process.GetProcesses().FirstOrDefault(process => process.Id == crawler.ProcessId);
                    if (process == null)
                    {
                        //Set on queues return of sheets
                        if (crawler.Images.Count == 0)                        
                            _mq.WriteMessage(ControllerConstants.NoDataMessage, crawler.QueueReturnName);
                        else
                            foreach (var image in crawler.Images)
                                _mq.WriteMessage(image, crawler.QueueReturnName);

                        crawler.ToErase = true;
                    }
                }

                RemoveAlreadyQueriedCrawlers();

                await Task.Delay(2000, stoppingToken);
            }
        }

        #region Private Methods
        
        /// <summary>
        /// Create crawler
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="eventArgs"></param>
        private void CreateCrawler(object obj, object eventArgs)
        {
            var body = (eventArgs as BasicDeliverEventArgs).Body;
            var redelivered = (eventArgs as BasicDeliverEventArgs).Redelivered;

            if (redelivered) return;

            var arguments = Encoding.UTF8.GetString(body.ToArray()); //--termo TERMO DE BUSCA PODENDO TER ESPACO --tipo 0|queueName            
            var queueReturnName = arguments.Split('|').Last();
            arguments = arguments.Split('|').First();

            _logger.LogInformation($"Creating crawler '{arguments}' to queue {queueReturnName}...");

            var crawler = new RunningCrawler() { QueueReturnName = queueReturnName };

            var runningProcess = new Process();
            runningProcess.StartInfo.FileName = EnvironmentHelper.GetValue("CrawlerExePath");
            runningProcess.StartInfo.Arguments = arguments;
            runningProcess.StartInfo.UseShellExecute = false;
            runningProcess.StartInfo.RedirectStandardError = true;
            runningProcess.StartInfo.RedirectStandardOutput = true;

            var images = new List<string>();
            runningProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    crawler.Images.AddRange(e.Data.Split("\n"));
                    _logger.LogInformation($"[{arguments}] Image '{e.Data}' added!");
                }
            };

            runningProcess.Start();
            crawler.ProcessId = runningProcess.Id;
            runningCrawlers.Add(crawler);

            runningProcess.BeginErrorReadLine();
            runningProcess.BeginOutputReadLine();
        }

        /// <summary>
        /// Remove crawlers marked to be erased.
        /// </summary>
        private void RemoveAlreadyQueriedCrawlers()
        {
            //Remove crawler that were queried
            var removeCrawler = runningCrawlers.Where(w => w.ToErase).ToList();
            removeCrawler.ForEach(f => runningCrawlers.Remove(f));

            if (removeCrawler.Count > 0)
                UpdateMetrics();
        }

        /// <summary>
        /// Update computer metrics
        /// </summary>
        /// <param name="process"></param>
        private void UpdateMetrics()
        {
            var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", instanceName: "_Total");
            var ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            ramCounter.NextValue();
            cpuCounter.NextValue();

            Thread.Sleep(500);
            var cpuUsage = cpuCounter.NextValue();
            var ramUsage = ramCounter.NextValue() / 100;

            _db.SaveMetric(new MetricStatus()
            { 
                CpuUsage = cpuUsage,
                MemoryUsage = ramUsage,
                MachineName = Environment.MachineName
            });
        }
        #endregion
    }
}
