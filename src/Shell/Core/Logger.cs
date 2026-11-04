using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace IWar3Key.Shell.Core
{
    public sealed class Logger
    {
        private readonly object _gate = new object();
        private readonly string _directory;

        public Logger(AppPaths paths)
        {
            _directory = paths.LogDirectory;
        }

        public void Info(string message) => Write("INFO", message);

        public void Warn(string message) => Write("WARN", message);

        public void Error(string message, Exception exception = null)
        {
            Write("ERR ", exception == null ? message : message + " :: " + exception);
        }

        public void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message;
            Debug.WriteLine(line);
            lock (_gate)
            {
                try
                {
                    string file = Path.Combine(_directory, "shell-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never break the application.
                }
            }
        }
    }
}
