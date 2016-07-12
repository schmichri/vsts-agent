using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace AgentService
{
    public partial class AgentService : ServiceBase
    {
        public const string EventSourceName = "VstsAgentService";
        private const int CTRL_C_EVENT = 0;
        private Process AgentListener { get; set; }
        private bool Stopping { get; set; }
        private object ServiceLock { get; set; }
        private Task RunningLoop { get; set; }

        public AgentService(string serviceName)
        {
            ServiceLock = new Object();
            InitializeComponent();
            base.ServiceName = serviceName;
        }

        protected override void OnStart(string[] args)
        {
            RunningLoop = Task.Run(
                () =>
                    {
                        try
                        {
                            bool stopping;
                            WriteInfo("Starting VSTS Agent Service");
                            TimeSpan timeBetweenRetries = TimeSpan.FromSeconds(5);

                            lock (ServiceLock)
                            {
                                stopping = Stopping;
                            }

                            while (!stopping)
                            {
                                WriteInfo("Starting VSTS Agent listener");
                                lock (ServiceLock)
                                {
                                    AgentListener = CreateAgentListener();
                                    AgentListener.OutputDataReceived += AgentListener_OutputDataReceived;
                                    AgentListener.ErrorDataReceived += AgentListener_ErrorDataReceived;
                                    AgentListener.Start();
                                    AgentListener.BeginOutputReadLine();
                                    AgentListener.BeginErrorReadLine();
                                }

                                AgentListener.WaitForExit();
                                int exitCode = AgentListener.ExitCode;

                                // exit code 0 and 1 need stop service
                                // exit code 2 and 3 need restart agent
                                switch (exitCode)
                                {
                                    case 0:
                                        Stopping = true;
                                        WriteInfo(Resource.AgentExitWithoutError);
                                        break;
                                    case 1:
                                        Stopping = true;
                                        WriteInfo(Resource.AgentExitWithTerminatedError);
                                        break;
                                    case 2:
                                        WriteInfo(Resource.AgentExitWithError);
                                        break;
                                    case 3:
                                        WriteInfo(Resource.AgentUpdateInProcess);
                                        // sleep 10 seconds wait for upgrade script to finish
                                        Thread.Sleep(10000);
                                    // looking update result record under _diag folder (the log file itself will indicate the result)
                                    // SelfUpdate-20160711-160300-Succeed.log or SelfUpdate-20160711-160300-Fail.log
                                    // Find the latest upgrade log, make sure the log is created less than 15 seconds.
                                    // When upgrade succeed, Exit(int.max), this will trigger SCM to recovery the service by restart it, 
                                    // in this way we can upgrade the ServiceHost as well, since SCM cache the ServiceHost in memory, we need update the servicehost as well.
                                    DirectoryInfo dirInfo = new DirectoryInfo(GetDiagnosticFolderPath());
                                    FileInfo[] updateLogs = dirInfo.GetFiles("SelfUpdate_*_*.log") ?? new FileInfo[0];
                                    if (updateLogs.Length == 0)
                                    {
                                        // totally wrong, we are not even get a update log.
                                    }
                                    else
                                    {
                                        String latestLogFile;
                                        DateTime latestLogTimestamp = DateTime.MinValue;
                                        foreach (var logFile in updateLogs)
                                        {
                                            int timestampStartIndex = logFile.Name.IndexOf("_") + 1;
                                            int timestampEndIndex = logFile.Name.LastIndexOf("_") - 1;
                                            string timestamp = logFile.Name.Substring(timestampStartIndex, timestampEndIndex - timestampStartIndex + 1);
                                            DateTime updateTime;
                                            if (DateTime.TryParseExact(timestamp, "yyyyMMdd-HHmmss", null, DateTimeStyles.None, out updateTime) &&
                                                updateTime > latestLogTimestamp)
                                            {
                                                latestLogFile = logFile.Name;
                                                latestLogTimestamp = updateTime;
                                            }
                                        }

                                        if (DateTime.UtcNow - latestLogTimestamp <= TimeSpan.FromSeconds(30))
                                        {

                                        }
                                    }
                                    break;
                                    default:
                                        WriteInfo(Resource.AgentExitWithUndefinedReturnCode);
                                        break;
                                }

                                if (Stopping)
                                {
                                    ExitCode = exitCode;
                                    Stop();
                                }
                                else
                                {
                                    // wait for few seconds before restarting the process
                                    Thread.Sleep(timeBetweenRetries);
                                }

                                lock (ServiceLock)
                                {
                                    AgentListener.OutputDataReceived -= AgentListener_OutputDataReceived;
                                    AgentListener.ErrorDataReceived -= AgentListener_ErrorDataReceived;
                                    AgentListener.Dispose();
                                    AgentListener = null;
                                    stopping = Stopping;
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            WriteException(exception);
                            ExitCode = 99;
                            Stop();
                        }
                    });
        }

        private void AgentListener_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                WriteToEventLog(e.Data, EventLogEntryType.Error);
            }
        }

        private void AgentListener_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                WriteToEventLog(e.Data, EventLogEntryType.Information);
            }
        }

        private Process CreateAgentListener()
        {
            string exeLocation = Assembly.GetEntryAssembly().Location;
            string agentExeLocation = Path.Combine(Path.GetDirectoryName(exeLocation), "Agent.Listener.exe");
            Process newProcess = new Process();
            newProcess.StartInfo = new ProcessStartInfo(agentExeLocation, "run");
            newProcess.StartInfo.CreateNoWindow = true;
            newProcess.StartInfo.UseShellExecute = false;
            newProcess.StartInfo.RedirectStandardInput = true;
            newProcess.StartInfo.RedirectStandardOutput = true;
            newProcess.StartInfo.RedirectStandardError = true;
            return newProcess;
        }

        protected override void OnStop()
        {
            lock (ServiceLock)
            {
                Stopping = true;

                // TODO If agent service is killed make sure AgentListener also is killed
                try
                {
                    if (AgentListener != null && !AgentListener.HasExited)
                    {
                        // Try to let the agent process know that we are stopping
                        //Attach service process to console of Agent.Listener process. This is needed,
                        //because windows service doesn't use its own console.
                        if (AttachConsole((uint)AgentListener.Id))
                        {
                            //Prevent main service process from stopping because of Ctrl + C event with SetConsoleCtrlHandler
                            SetConsoleCtrlHandler(null, true);
                            try
                            {
                                //Generate console event for current console with GenerateConsoleCtrlEvent (processGroupId should be zero)
                                GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                                //Wait for the process to finish (give it up to 30 seconds)
                                AgentListener.WaitForExit(30000);
                            }
                            finally
                            {
                                //Disconnect from console and restore Ctrl+C handling by main process
                                FreeConsole();
                                SetConsoleCtrlHandler(null, false);
                            }
                        }

                        // if agent is still running, kill it
                        if (!AgentListener.HasExited)
                        {
                            AgentListener.Kill();
                        }
                    }
                }
                catch (Exception exception)
                {
                    // InvalidOperationException is thrown when there is no process associated to the process object. 
                    // There is no process to kill, Log the exception and shutdown the service. 
                    // If we don't handle this here, the service get into a state where it can neither be stoped nor restarted (Error 1061)
                    WriteException(exception);
                }
            }
        }

        private static void WriteToEventLog(string eventText, EventLogEntryType entryType)
        {
            EventLog.WriteEntry(EventSourceName, eventText, entryType, 100);
        }

        private static string GetDiagnosticFolderPath()
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)), "_diag");
        }

        private void WriteError(int exitCode)
        {
            String diagFolder = GetDiagnosticFolderPath();
            String eventText = String.Format(
                CultureInfo.InvariantCulture,
                "The AgentListener process failed to start successfully. It exited with code {0}. Check the latest Agent log files in {1} for more information.",
                exitCode,
                diagFolder);

            WriteToEventLog(eventText, EventLogEntryType.Error);
        }

        private void WriteInfo(string message)
        {
            WriteToEventLog(message, EventLogEntryType.Information);
        }

        private void WriteException(Exception exception)
        {
            WriteToEventLog(exception.ToString(), EventLogEntryType.Error);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate HandlerRoutine, bool Add);

        // Delegate type to be used as the Handler Routine for SetConsoleCtrlHandler
        delegate Boolean ConsoleCtrlDelegate(uint CtrlType);
    }
}
