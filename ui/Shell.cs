using System;
using System.Diagnostics;
using System.Text;

namespace zapret
{
    public sealed class ShellResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    public static class Shell
    {
        public static ShellResult Run(string fileName, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.Default;
            psi.StandardErrorEncoding = Encoding.Default;

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    return new ShellResult { ExitCode = -1, Output = "", Error = "Process was not started." };
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return new ShellResult { ExitCode = -2, Output = output, Error = "Timeout." + Environment.NewLine + error };
                }

                return new ShellResult { ExitCode = process.ExitCode, Output = output, Error = error };
            }
        }

        public static void StartVisible(string fileName, string arguments, string workingDirectory)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.WorkingDirectory = workingDirectory;
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Normal;
            Process.Start(psi);
        }

        public static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}

