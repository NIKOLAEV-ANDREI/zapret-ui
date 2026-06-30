using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace zapret
{
    public sealed class StandardTestRunner
    {
        private readonly AppPaths paths;
        private readonly ZapretManager manager;

        public StandardTestRunner(AppPaths paths, ZapretManager manager)
        {
            this.paths = paths;
            this.manager = manager;
        }

        public IList<TestTarget> LoadTargets()
        {
            var file = Path.Combine(paths.Utils, "targets.txt");
            var result = new List<TestTarget>();

            if (File.Exists(file))
            {
                foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    var match = Regex.Match(line, @"^\s*(?<name>[A-Za-z0-9_]+)\s*=\s*""(?<value>.+)""\s*$");
                    if (!match.Success) continue;

                    var name = match.Groups["name"].Value;
                    var value = match.Groups["value"].Value;
                    result.Add(ConvertTarget(name, value));
                }
            }

            if (result.Count > 0) return result;

            result.Add(ConvertTarget("DiscordMain", "https://discord.com"));
            result.Add(ConvertTarget("DiscordGateway", "https://gateway.discord.gg"));
            result.Add(ConvertTarget("DiscordCDN", "https://cdn.discordapp.com"));
            result.Add(ConvertTarget("YouTubeWeb", "https://www.youtube.com"));
            result.Add(ConvertTarget("YouTubeShort", "https://youtu.be"));
            result.Add(ConvertTarget("GoogleMain", "https://www.google.com"));
            result.Add(ConvertTarget("CloudflareDNS1111", "PING:1.1.1.1"));
            result.Add(ConvertTarget("GoogleDNS8888", "PING:8.8.8.8"));
            return result;
        }

        public void Run(
            IList<StrategyInfo> strategies,
            IList<TestTarget> targets,
            Func<bool> shouldCancel,
            Action<TestProgress> progress)
        {
            if (strategies == null || strategies.Count == 0)
            {
                throw new InvalidOperationException("Не выбраны стратегии для теста.");
            }

            if (targets == null || targets.Count == 0)
            {
                throw new InvalidOperationException("Не найдены цели для теста.");
            }

            foreach (var strategy in strategies)
            {
                if (shouldCancel()) break;

                try
                {
                    progress(new TestProgress { Message = "Запуск стратегии: " + strategy.Name });
                    manager.StartStandalone(strategy);
                    if (!WaitWithCancel(5000, shouldCancel))
                    {
                        break;
                    }

                    foreach (var target in targets)
                    {
                        if (shouldCancel()) break;

                        progress(new TestProgress { Message = strategy.Name + " -> " + target.Name });
                        var row = TestTarget(strategy, target, shouldCancel);
                        if (row != null)
                        {
                            progress(new TestProgress { Row = row });
                        }
                    }
                }
                finally
                {
                    manager.StopStandalone();
                    WaitWithCancel(1000, shouldCancel);
                }
            }
        }

        public string SaveResults(ObservableCollection<TestRow> rows)
        {
            var dir = Path.Combine(paths.Utils, "test results");
            Directory.CreateDirectory(dir);

            var file = Path.Combine(dir, "ui_test_results_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            var sb = new StringBuilder();
            sb.AppendLine("Zapret UI test results");
            sb.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            foreach (var row in rows)
            {
                sb.AppendLine(row.Strategy + " | " + row.Target + " | HTTP=" + row.Http + " | TLS1.2=" + row.Tls12 + " | TLS1.3=" + row.Tls13 + " | Ping=" + row.Ping + " | " + row.Status);
            }

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            return file;
        }

        private TestRow TestTarget(StrategyInfo strategy, TestTarget target, Func<bool> shouldCancel)
        {
            if (shouldCancel()) return null;
            var http = target.IsUrl ? RunCurl(target.Url, "--http1.1", shouldCancel) : "n/a";
            if (shouldCancel()) return null;
            var tls12 = target.IsUrl ? RunCurl(target.Url, "--tlsv1.2 --tls-max 1.2", shouldCancel) : "n/a";
            if (shouldCancel()) return null;
            var tls13 = target.IsUrl ? RunCurl(target.Url, "--tlsv1.3 --tls-max 1.3", shouldCancel) : "n/a";
            if (shouldCancel()) return null;
            var ping = RunPing(target.PingHost, shouldCancel);

            var okCount = new[] { http, tls12, tls13 }.Count(x => x == "OK");
            var pingOk = ping != "Timeout" && ping != "n/a";
            var status = okCount > 0 || pingOk ? "OK" : "FAIL";

            return new TestRow
            {
                Strategy = strategy.Name,
                Target = target.Name,
                Http = http,
                Tls12 = tls12,
                Tls13 = tls13,
                Ping = ping,
                Status = status
            };
        }

        private static TestTarget ConvertTarget(string name, string value)
        {
            if (value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
            {
                return new TestTarget
                {
                    Name = name,
                    Url = null,
                    PingHost = value.Substring(5).Trim()
                };
            }

            return new TestTarget
            {
                Name = name,
                Url = value,
                PingHost = Regex.Replace(value, "^https?://", "", RegexOptions.IgnoreCase).Split('/')[0]
            };
        }

        private static string RunCurl(string url, string tlsArgs, Func<bool> shouldCancel)
        {
            var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\curl.exe");
            if (!File.Exists(curl)) curl = "curl.exe";

            var args = "-I -s -m 5 -o NUL -w \"%{http_code}\" --show-error " + tlsArgs + " " + Shell.Quote(url);
            var result = RunCancelable(curl, args, 12000, shouldCancel);
            if (result.ExitCode == -3) return "CANCEL";
            var text = ((result.Output ?? "") + " " + (result.Error ?? "")).Trim();

            if (text.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Unrecognized option", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "UNSUP";
            }

            if (text.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Could not resolve host", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "SSL";
            }

            var code = Regex.Match(text, @"\b\d{3}\b");
            if (result.ExitCode == 0 && code.Success) return "OK";
            return "ERR";
        }

        private static string RunPing(string host, Func<bool> shouldCancel)
        {
            if (string.IsNullOrWhiteSpace(host)) return "n/a";

            try
            {
                using (var ping = new Ping())
                {
                    var times = new List<long>();
                    for (var i = 0; i < 3; i++)
                    {
                        if (shouldCancel()) return "CANCEL";
                        var reply = ping.Send(host, 2500);
                        if (reply != null && reply.Status == IPStatus.Success)
                        {
                            times.Add(reply.RoundtripTime);
                        }
                    }

                    if (times.Count == 0) return "Timeout";
                    return ((long)times.Average()).ToString() + " ms";
                }
            }
            catch
            {
                return "Timeout";
            }
        }

        private static bool WaitWithCancel(int milliseconds, Func<bool> shouldCancel)
        {
            var waited = 0;
            while (waited < milliseconds)
            {
                if (shouldCancel()) return false;
                Thread.Sleep(100);
                waited += 100;
            }

            return true;
        }

        private static ShellResult RunCancelable(string fileName, string arguments, int timeoutMs, Func<bool> shouldCancel)
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

                var started = DateTime.UtcNow;
                while (!process.HasExited)
                {
                    if (shouldCancel())
                    {
                        try { process.Kill(); } catch { }
                        return new ShellResult { ExitCode = -3, Output = "", Error = "Canceled." };
                    }

                    if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                    {
                        try { process.Kill(); } catch { }
                        return new ShellResult { ExitCode = -2, Output = "", Error = "Timeout." };
                    }

                    Thread.Sleep(100);
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                return new ShellResult { ExitCode = process.ExitCode, Output = output, Error = error };
            }
        }
    }
}

