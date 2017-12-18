using Shaman.Connectors.SciHub;
using Shaman.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shaman.PaperDownload
{
    class Program
    {


        static void Main(string[] args)
        {
            ConfigurationManager.Initialize(typeof(Program).Assembly, false);
            ConfigurationManager.Initialize(typeof(HttpUtils).Assembly, false);

            Shaman.Runtime.SingleThreadSynchronizationContext.Run(async () =>
            {
                var paper = ConfigurationManager.PositionalCommandLineArgs.FirstOrDefault();
                if (paper != null)
                {
                    var progress = Program.CreateTransferConsoleProgress(paper);
                    try
                    {
                        await Paper.DownloadAsync(paper.AsUri(), progress);
                    }
                    catch (Exception ex)
                    {
                        progress.Controller.ReportError(ex);
                        Environment.Exit(1);
                    }
                }


            });
        }

        public static ConsoleProgress<DataTransferProgress> CreateTransferConsoleProgress(string name)
        {
            return ConsoleProgress.Create<DataTransferProgress>(name, (p, c) =>
            {
                if (p.Description != null) c.Report(p.Description);
                else if (p.Progress != null) c.Report(p.Progress.Value);
            });
        }
    }
}
