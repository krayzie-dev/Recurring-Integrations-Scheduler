//https://github.com/quartznet/quartznet
using System;
using System.IO;
using System.Reflection;
using log4net.Config;
using Topshelf;

namespace RecurringIntegrationsScheduler.Server
{
    /// <summary>
    /// The server's main entry point.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Main.
        /// </summary>
        public static void Main()
        {
            // change from service account's dir to more logical one
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            var logRepository = log4net.LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.Configure(logRepository, new FileInfo(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile));

            HostFactory.Run(x =>
            {
                x.RunAsLocalSystem();
                // Stop scheduler gracefully also on Windows shutdown/restart, so interrupted jobs are logged.
                x.EnableShutdown();

                x.SetDescription(Configuration.ServiceDescription);
                x.SetDisplayName(Configuration.ServiceDisplayName);
                x.SetServiceName(Configuration.ServiceName);

                x.Service(factory =>
                {
                    QuartzServer server = QuartzServerFactory.CreateServer();
                    server.Initialize().GetAwaiter().GetResult();
                    return server;
                });
            });
        }
    }
}
