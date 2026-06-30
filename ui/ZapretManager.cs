using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;

namespace zapret
{
    public sealed class ZapretManager
    {
        private const string AppStartupTaskName = "Zapret";
        private const string LegacyAppStartupTaskName = "Zapret Fix";
        private const string RepositoryOwner = "NIKOLAEV-ANDREI";
        private const string RepositoryName = "zapret-ui";
        private const string RepositoryRawBaseUrl = "https://raw.githubusercontent.com/" + RepositoryOwner + "/" + RepositoryName + "/refs/heads/main";
        private const string ReleasesApiUrl = "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
        private const string LocalVersionFileName = "zapret_version.txt";
        private const string LocalAppVersionFileName = "app_version.txt";
        private const string AppVersionManifestUrl = RepositoryRawBaseUrl + "/.service/app-version.json";
        private const string AppUpdatePendingVersionFileName = "app_update_pending.version";
        private const string AppUpdateInstalledNoticeFileName = "app_update_installed.notice";
        private readonly AppPaths paths;

        public ZapretManager(AppPaths paths)
        {
            this.paths = paths;
            EnsureUserLists();
        }

        public IList<StrategyInfo> GetStrategies()
        {
            var files = Directory.GetFiles(paths.Root, "*.bat")
                .Where(x => !Path.GetFileName(x).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .OrderBy(NaturalKey)
                .ToArray();

            var result = new List<StrategyInfo>();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file, Encoding.Default);
                result.Add(new StrategyInfo
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FilePath = file,
                    NotRecommended = text.IndexOf("NOT RECOMMENDED", StringComparison.OrdinalIgnoreCase) >= 0,
                    Blocks = Regex.Matches(text, "--new", RegexOptions.IgnoreCase).Count + 1
                });
            }

            return result;
        }

        public string GetStatusText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Корневая папка: " + paths.Root);
            sb.AppendLine("winws.exe: " + (File.Exists(paths.WinwsExe) ? "найден" : "не найден"));
            sb.AppendLine("Служба zapret: " + GetServiceStatus("zapret"));
            sb.AppendLine("Служба WinDivert: " + GetServiceStatus("WinDivert"));
            sb.AppendLine("Процесс winws.exe: " + (IsProcessRunning("winws") ? "запущен" : "не запущен"));
            sb.AppendLine("Стратегия службы: " + (GetInstalledStrategyName() ?? "не установлена"));
            sb.AppendLine("Game Filter: " + GetGameFilterStatus());
            sb.AppendLine("IPSet: " + GetIpsetStatus());
            sb.AppendLine("Авто-проверка обновлений: " + (IsUpdateCheckEnabled() ? "включена" : "выключена"));
            return sb.ToString();
        }

        public RuntimeStatus GetRuntimeStatus()
        {
            var zapretStatus = GetServiceStatus("zapret");
            var winDivertStatus = GetServiceStatus("WinDivert");

            return new RuntimeStatus
            {
                WinwsExists = File.Exists(paths.WinwsExe),
                WinwsRunning = IsProcessRunning("winws"),
                ZapretServiceRunning = string.Equals(zapretStatus, "Running", StringComparison.OrdinalIgnoreCase),
                WinDivertRunning = string.Equals(winDivertStatus, "Running", StringComparison.OrdinalIgnoreCase),
                ZapretServiceStatus = zapretStatus,
                WinDivertStatus = winDivertStatus,
                InstalledStrategyName = GetInstalledStrategyName(),
                GameFilterStatus = GetGameFilterStatus(),
                IpsetStatus = GetIpsetStatus(),
                UpdateCheckEnabled = IsUpdateCheckEnabled()
            };
        }

        public void StartStandalone(StrategyInfo strategy)
        {
            if (string.Equals(GetServiceStatus("zapret"), "Running", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Служба zapret уже запущена. Удали службу или установи новую стратегию через кнопку \"Установить службу\".");
            }

            StopStandalone();
            EnableTcpTimestamps();
            EnsureUserLists();

            var args = BuildWinwsArguments(strategy);
            var psi = new ProcessStartInfo();
            psi.FileName = paths.WinwsExe;
            psi.Arguments = args;
            psi.WorkingDirectory = paths.Bin;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }

        public void InstallService(StrategyInfo strategy)
        {
            EnableTcpTimestamps();
            EnsureUserLists();

            StopAndDeleteService("zapret");
            var args = BuildWinwsArguments(strategy);
            var binPath = "\"" + paths.WinwsExe + "\" " + args;
            var createArgs = "create zapret binPath= " + Shell.Quote(binPath) + " DisplayName= \"zapret\" start= auto";
            var create = Shell.Run("sc.exe", createArgs, 15000);
            if (create.ExitCode != 0)
            {
                throw new InvalidOperationException("Не удалось создать службу zapret." + Environment.NewLine + create.Output + create.Error);
            }

            Shell.Run("sc.exe", "description zapret \"Zapret DPI bypass software\"", 10000);
            Shell.Run("sc.exe", "start zapret", 15000);
            SaveInstalledStrategyName(strategy.Name);
        }

        public void StopStandalone()
        {
            foreach (var p in Process.GetProcessesByName("winws"))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            if (IsProcessRunning("winws"))
            {
                Shell.Run("taskkill.exe", "/F /IM winws.exe /T", 10000);
            }

            WaitForProcessExit("winws", 5000);
        }

        public void StopProtection()
        {
            StopService("zapret");
            StopStandalone();
        }

        public void RemoveServices()
        {
            StopAndDeleteService("zapret");
            StopStandalone();
            StopAndDeleteService("WinDivert");
            StopAndDeleteService("WinDivert14");
        }

        public void SetGameFilter(string mode)
        {
            var file = Path.Combine(paths.Utils, "game_filter.enabled");
            if (mode == "off")
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }

            File.WriteAllText(file, mode, Encoding.ASCII);
        }

        public string GetGameFilterStatus()
        {
            var file = Path.Combine(paths.Utils, "game_filter.enabled");
            if (!File.Exists(file)) return "выключен";

            var mode = File.ReadAllText(file).Trim().ToLowerInvariant();
            if (mode == "all") return "включен: TCP и UDP";
            if (mode == "tcp") return "включен: только TCP";
            if (mode == "udp") return "включен: только UDP";
            return "включен: UDP";
        }

        public string GetIpsetStatus()
        {
            var file = Path.Combine(paths.Lists, "ipset-all.txt");
            if (!File.Exists(file)) return "none";

            var lines = File.ReadAllLines(file);
            if (lines.Length == 0 || lines.All(x => string.IsNullOrWhiteSpace(x))) return "any";
            return lines.Any(x => Regex.IsMatch(x.Trim(), @"^203\.0\.113\.113/32$")) ? "none" : "loaded";
        }

        public void SwitchIpset()
        {
            var status = GetIpsetStatus();
            var listFile = Path.Combine(paths.Lists, "ipset-all.txt");
            var backupFile = listFile + ".backup";

            if (status == "loaded")
            {
                if (File.Exists(backupFile)) File.Delete(backupFile);
                File.Move(listFile, backupFile);
                File.WriteAllText(listFile, "203.0.113.113/32" + Environment.NewLine, Encoding.ASCII);
            }
            else if (status == "none")
            {
                File.WriteAllText(listFile, "", Encoding.ASCII);
            }
            else
            {
                if (!File.Exists(backupFile))
                {
                    throw new InvalidOperationException("Нет backup-файла ipset-all.txt.backup. Сначала обнови IPSet.");
                }

                if (File.Exists(listFile)) File.Delete(listFile);
                File.Move(backupFile, listFile);
            }
        }

        public bool IsUpdateCheckEnabled()
        {
            return File.Exists(Path.Combine(paths.Utils, "check_updates.enabled"));
        }

        public void ToggleUpdateCheck()
        {
            var file = Path.Combine(paths.Utils, "check_updates.enabled");
            if (File.Exists(file))
            {
                File.Delete(file);
            }
            else
            {
                File.WriteAllText(file, "ENABLED", Encoding.ASCII);
            }
        }

        public bool IsDarkThemeEnabled()
        {
            return File.Exists(Path.Combine(paths.Utils, "ui_theme.dark"));
        }

        public void SetDarkTheme(bool enabled)
        {
            var file = Path.Combine(paths.Utils, "ui_theme.dark");
            if (enabled)
            {
                File.WriteAllText(file, "ENABLED", Encoding.ASCII);
            }
            else if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        public string UpdateIpset()
        {
            var url = RepositoryRawBaseUrl + "/.service/ipset-service.txt";
            var outFile = Path.Combine(paths.Lists, "ipset-all.txt");
            using (var client = new WebClient())
            {
                client.DownloadFile(url, outFile);
            }
            return "IPSet обновлен: " + outFile;
        }

        public string CheckUpdates()
        {
            var update = GetAvailableUpdate();
            if (!update.HasUpdate)
            {
                return "Установлена актуальная версия zapret: " + update.CurrentVersion;
            }

            return "Доступна новая версия zapret: " + update.LatestVersion + Environment.NewLine + update.ReleaseUrl;
        }

        public string GetInstalledZapretVersion()
        {
            return GetLocalZapretVersion();
        }

        public string GetInstalledAppVersion()
        {
            return GetLocalAppVersion();
        }

        public string PopAppUpdateInstalledNotice()
        {
            var file = Path.Combine(paths.Utils, AppUpdateInstalledNoticeFileName);
            if (!File.Exists(file))
            {
                return null;
            }

            var text = File.ReadAllText(file, Encoding.UTF8).Trim();
            try { File.Delete(file); } catch { }
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        public string GetFeedbackBotUrl()
        {
            var file = Path.Combine(paths.Utils, "feedback_bot.url");
            if (!File.Exists(file))
            {
                return null;
            }

            var url = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!url.StartsWith("https://t.me/", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return url;
        }

        public UpdateInfo GetAvailableUpdate()
        {
            var current = GetLocalZapretVersion();
            var latest = GetLatestReleaseInfo();
            latest.CurrentVersion = current;
            latest.HasUpdate = !SameVersion(current, latest.LatestVersion);
            return latest;
        }

        public UpdateInfo GetAvailableAppUpdate()
        {
            var current = GetLocalAppVersion();
            var latest = GetLatestAppReleaseInfo();
            latest.CurrentVersion = current;
            latest.HasUpdate = !SameVersion(current, latest.LatestVersion);
            return latest;
        }

        public string PrepareAppUpdate(UpdateInfo update)
        {
            if (update == null) throw new ArgumentNullException("update");
            if (string.IsNullOrWhiteSpace(update.DownloadUrl)) throw new InvalidOperationException("Не найдена ссылка на новую версию приложения.");

            var tempRoot = Path.Combine(Path.GetTempPath(), "zapret-app-update-" + Guid.NewGuid().ToString("N"));
            var downloaded = Path.Combine(tempRoot, "zapret.exe");
            var currentExe = Path.Combine(paths.Root, "zapret.exe");
            var nextExe = Path.Combine(paths.Root, "zapret.next.exe");

            Directory.CreateDirectory(tempRoot);
            try
            {
                using (var client = CreateWebClient())
                {
                    client.DownloadFile(update.DownloadUrl, downloaded);
                }

                if (!string.IsNullOrWhiteSpace(update.Sha256))
                {
                    var actual = Sha256File(downloaded);
                    if (!string.Equals(actual, update.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Контрольная сумма обновления приложения не совпадает.");
                    }
                }

                var backupDir = Path.Combine(paths.Root, "app-backups");
                Directory.CreateDirectory(backupDir);
                if (File.Exists(currentExe))
                {
                    var backupName = "zapret-" + GetLocalAppVersion() + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".exe";
                    File.Copy(currentExe, Path.Combine(backupDir, backupName), true);
                }

                File.Copy(downloaded, nextExe, true);
                Directory.CreateDirectory(paths.Utils);
                File.WriteAllText(Path.Combine(paths.Utils, AppUpdatePendingVersionFileName), NormalizeVersion(update.LatestVersion), new UTF8Encoding(false));

                return "Новая версия приложения скачана: " + update.LatestVersion + Environment.NewLine +
                       "После закрытия приложение установит обновление и запустится снова.";
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        public void StartAppUpdateInstaller()
        {
            var script = Path.Combine(paths.Root, "ui", "apply-ui-update.ps1");
            if (!File.Exists(script))
            {
                throw new FileNotFoundException("Скрипт обновления приложения не найден.", script);
            }

            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Shell.Quote(script),
                WorkingDirectory = paths.Root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(info);
        }

        public string InstallZapretUpdate(UpdateInfo update)
        {
            if (update == null) throw new ArgumentNullException("update");
            if (string.IsNullOrWhiteSpace(update.DownloadUrl)) throw new InvalidOperationException("Не найдена ссылка на zip-архив релиза.");

            StopProtection();
            StopService("WinDivert");
            StopService("WinDivert14");

            var backupDir = CreateZapretBackup();
            var skipped = new List<string>();
            var tempRoot = Path.Combine(Path.GetTempPath(), "zapret-update-" + Guid.NewGuid().ToString("N"));
            var archive = Path.Combine(tempRoot, "release.zip");
            var extractDir = Path.Combine(tempRoot, "extract");

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(extractDir);

            try
            {
                using (var client = CreateWebClient())
                {
                    client.DownloadFile(update.DownloadUrl, archive);
                }

                ZipFile.ExtractToDirectory(archive, extractDir);
                var packageRoot = FindPackageRoot(extractDir);
                CopyPackageFiles(packageRoot, paths.Root, skipped);
                SetLocalZapretVersion(update.LatestVersion);

                var message = "zapret обновлен до версии " + update.LatestVersion + Environment.NewLine +
                              "Backup сохранен: " + backupDir;
                if (skipped.Count > 0)
                {
                    message += Environment.NewLine + "Некоторые занятые файлы не заменены: " + string.Join(", ", skipped.Distinct().ToArray());
                }

                return message;
            }
            catch
            {
                RestoreZapretBackup(backupDir, new List<string>());
                throw;
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        public string RollbackZapretUpdate()
        {
            StopProtection();
            StopService("WinDivert");
            StopService("WinDivert14");
            var backupDir = GetLatestBackupDir();
            if (backupDir == null)
            {
                throw new InvalidOperationException("Backup для отката не найден.");
            }

            var skipped = new List<string>();
            RestoreZapretBackup(backupDir, skipped);
            var message = "Откат выполнен из backup: " + backupDir;
            if (skipped.Count > 0)
            {
                message += Environment.NewLine + "Некоторые занятые файлы не восстановлены: " + string.Join(", ", skipped.Distinct().ToArray());
            }

            return message;
        }

        public string UpdateHosts()
        {
            var hostsUrl = RepositoryRawBaseUrl + "/.service/hosts";
            var tempFile = Path.Combine(Path.GetTempPath(), "zapret_hosts.txt");
            using (var client = new WebClient())
            {
                client.DownloadFile(hostsUrl, tempFile);
            }

            Process.Start("notepad.exe", Shell.Quote(tempFile));
            Process.Start("explorer.exe", "/select,\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\drivers\\etc\\hosts") + "\"");
            return "Файл hosts из репозитория открыт в Блокноте. Системный hosts открыт в Проводнике.";
        }

        private UpdateInfo GetLatestReleaseInfo()
        {
            using (var client = CreateWebClient())
            {
                var json = client.DownloadString(ReleasesApiUrl);
                var tag = MatchJsonString(json, "tag_name");
                var htmlUrl = MatchJsonString(json, "html_url");
                var assetUrl = MatchZipAssetUrl(json);

                if (string.IsNullOrWhiteSpace(tag))
                {
                    throw new InvalidOperationException("GitHub API не вернул версию релиза.");
                }

                if (string.IsNullOrWhiteSpace(assetUrl))
                {
                    throw new InvalidOperationException("В последнем релизе не найден zip-архив.");
                }

                return new UpdateInfo
                {
                    LatestVersion = NormalizeVersion(tag),
                    ReleaseUrl = htmlUrl,
                    DownloadUrl = assetUrl
                };
            }
        }

        private UpdateInfo GetLatestAppReleaseInfo()
        {
            using (var client = CreateWebClient())
            {
                var json = client.DownloadString(AppVersionManifestUrl);
                var version = MatchJsonString(json, "version");
                var downloadUrl = MatchJsonString(json, "download_url");
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    downloadUrl = MatchJsonString(json, "url");
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidOperationException("Манифест обновления приложения не содержит версию.");
                }

                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    throw new InvalidOperationException("Манифест обновления приложения не содержит ссылку на загрузку.");
                }

                return new UpdateInfo
                {
                    LatestVersion = NormalizeVersion(version),
                    DownloadUrl = downloadUrl,
                    ReleaseUrl = MatchJsonString(json, "release_url"),
                    Sha256 = MatchJsonString(json, "sha256"),
                    Notes = MatchJsonString(json, "notes")
                };
            }
        }

        private static WebClient CreateWebClient()
        {
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "Zapret";
            return client;
        }

        private static string MatchJsonString(string json, string name)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : null;
        }

        private static string MatchZipAssetUrl(string json)
        {
            var matches = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var url = Regex.Unescape(match.Groups[1].Value);
                if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }

            return null;
        }

        private string GetLocalZapretVersion()
        {
            var file = Path.Combine(paths.Utils, LocalVersionFileName);
            if (File.Exists(file))
            {
                var value = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return NormalizeVersion(value);
            }

            return "неизвестно";
        }

        private string GetLocalAppVersion()
        {
            var file = Path.Combine(paths.Utils, LocalAppVersionFileName);
            if (File.Exists(file))
            {
                var value = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return NormalizeVersion(value);
            }

            return "неизвестно";
        }

        private void SetLocalZapretVersion(string version)
        {
            Directory.CreateDirectory(paths.Utils);
            File.WriteAllText(Path.Combine(paths.Utils, LocalVersionFileName), NormalizeVersion(version), new UTF8Encoding(false));
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";
            version = version.Trim();
            return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version.Substring(1) : version;
        }

        private static bool SameVersion(string current, string latest)
        {
            current = NormalizeVersion(current);
            latest = NormalizeVersion(latest);
            return !string.IsNullOrWhiteSpace(current)
                && !string.Equals(current, "неизвестно", StringComparison.OrdinalIgnoreCase)
                && string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
        }

        private static string Sha256File(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private string CreateZapretBackup()
        {
            var backupRoot = Path.Combine(paths.Root, "backups");
            Directory.CreateDirectory(backupRoot);
            var backupDir = Path.Combine(backupRoot, "zapret-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backupDir);

            CopyDirectory(paths.Root, backupDir, delegate(string path, bool isDirectory)
            {
                var relative = RelativePath(paths.Root, path);
                return IsPreservedRootPath(relative) || relative.StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            });

            return backupDir;
        }

        private string GetLatestBackupDir()
        {
            var backupRoot = Path.Combine(paths.Root, "backups");
            if (!Directory.Exists(backupRoot)) return null;

            return Directory.GetDirectories(backupRoot, "zapret-*")
                .OrderByDescending(x => x)
                .FirstOrDefault();
        }

        private void RestoreZapretBackup(string backupDir, IList<string> skipped)
        {
            if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
            {
                throw new InvalidOperationException("Backup не найден: " + backupDir);
            }

            CopyDirectory(backupDir, paths.Root, skipped, delegate(string path, bool isDirectory)
            {
                var relative = RelativePath(backupDir, path);
                return IsPreservedRootPath(relative);
            });
        }

        private static string FindPackageRoot(string extractDir)
        {
            var candidates = Directory.GetDirectories(extractDir, "*", SearchOption.AllDirectories)
                .Concat(new[] { extractDir })
                .Where(x => File.Exists(Path.Combine(x, "service.bat")) && Directory.Exists(Path.Combine(x, "bin")))
                .OrderBy(x => x.Length)
                .ToArray();

            if (candidates.Length == 0)
            {
                throw new InvalidOperationException("В архиве не найдена папка zapret с service.bat и bin.");
            }

            return candidates[0];
        }

        private void CopyPackageFiles(string sourceDir, string targetDir, IList<string> skipped)
        {
            CopyDirectory(sourceDir, targetDir, skipped, delegate(string path, bool isDirectory)
            {
                var relative = RelativePath(sourceDir, path);
                return IsPreservedRootPath(relative) || IsUserListPath(relative);
            });
        }

        private static void CopyDirectory(string sourceDir, string targetDir, Func<string, bool, bool> skip)
        {
            CopyDirectory(sourceDir, targetDir, null, skip);
        }

        private static void CopyDirectory(string sourceDir, string targetDir, IList<string> skipped, Func<string, bool, bool> skip)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (skip != null && skip(dir, true)) continue;
                var relative = RelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(targetDir, relative));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (skip != null && skip(file, false)) continue;
                var relative = RelativePath(sourceDir, file);
                var target = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                try
                {
                    File.Copy(file, target, true);
                }
                catch (IOException)
                {
                    if (!IsSkippableLockedFile(relative)) throw;
                    if (skipped != null) skipped.Add(NormalizeRelative(relative));
                }
                catch (UnauthorizedAccessException)
                {
                    if (!IsSkippableLockedFile(relative)) throw;
                    if (skipped != null) skipped.Add(NormalizeRelative(relative));
                }
            }
        }

        private static bool IsSkippableLockedFile(string relative)
        {
            var name = Path.GetFileName(relative);
            return name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPreservedRootPath(string relative)
        {
            relative = NormalizeRelative(relative);
            return string.IsNullOrWhiteSpace(relative)
                || relative.Equals("ui", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("utils", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("utils/", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("ui-build", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("ui-build/", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("zapret.exe", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("zapret.next.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUserListPath(string relative)
        {
            relative = NormalizeRelative(relative);
            if (!relative.StartsWith("lists/", StringComparison.OrdinalIgnoreCase)) return false;
            var name = Path.GetFileName(relative);
            return name.IndexOf("-user", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(root));
            var pathUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }

        private static string NormalizeRelative(string relative)
        {
            return (relative ?? "").Replace('\\', '/').Trim('/');
        }

        public string RunDiagnostics()
        {
            var sb = new StringBuilder();
            AddCheck(sb, "Base Filtering Engine", GetServiceStatus("BFE") == "Running", "Служба BFE обязательна для работы WinDivert.");
            AddCheck(sb, "System proxy", !IsProxyEnabled(), "Включенный системный proxy может мешать Discord/YouTube.");
            AddCheck(sb, "TCP timestamps", AreTcpTimestampsEnabled(), "Если выключено, приложение попробует включить при запуске стратегии.");
            AddCheck(sb, "AdguardSvc.exe", !IsProcessRunning("AdguardSvc"), "Adguard часто конфликтует с подобными обходами.");
            AddCheck(sb, "Killer services", !HasServiceNamePart("Killer"), "Killer Network Service может конфликтовать.");
            AddCheck(sb, "Intel Connectivity Network Service", !HasIntelConnectivityService(), "Эта служба известна конфликтами с WinDivert.");
            AddCheck(sb, "SmartByte", !HasServiceNamePart("SmartByte"), "SmartByte лучше отключить или удалить.");
            AddCheck(sb, "WinDivert64.sys", File.Exists(Path.Combine(paths.Bin, "WinDivert64.sys")), "Файл драйвера должен быть в bin.");
            AddCheck(sb, "Другие bypass-службы", !HasConflictingBypassService(), "Найдены возможные конфликты: GoodbyeDPI/discordfix_zapret/winws1/winws2.");
            AddCheck(sb, "hosts YouTube", !HostsContainsYoutube(), "Записи YouTube в hosts могут ломать доступ.");
            return sb.ToString();
        }

        public void LaunchTests()
        {
            if (!File.Exists(paths.TestScript))
            {
                throw new FileNotFoundException("Не найден test zapret.ps1", paths.TestScript);
            }

            Shell.StartVisible("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + Shell.Quote(paths.TestScript), paths.Root);
        }

        public string ReadTextFile(string relativePath)
        {
            return File.ReadAllText(Path.Combine(paths.Root, relativePath), Encoding.UTF8);
        }

        public void WriteTextFile(string relativePath, string text)
        {
            File.WriteAllText(Path.Combine(paths.Root, relativePath), text, new UTF8Encoding(false));
        }

        public void CreateDesktopShortcut()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, "Zapret.lnk");
            var legacyShortcutPath = Path.Combine(desktop, "Zapret Fix.lnk");
            if (File.Exists(legacyShortcutPath)) File.Delete(legacyShortcutPath);

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Description = "Zapret UI";
            shortcut.IconLocation = exe;
            shortcut.Save();
        }

        public bool IsAppStartupEnabled()
        {
            var task = Shell.Run("schtasks.exe", "/Query /TN " + Shell.Quote(AppStartupTaskName), 10000);
            if (task.ExitCode == 0) return true;

            var legacyTask = Shell.Run("schtasks.exe", "/Query /TN " + Shell.Quote(LegacyAppStartupTaskName), 10000);
            return legacyTask.ExitCode == 0;
        }

        public void SetAppStartup(bool enabled)
        {
            RemoveLegacyRunStartupValue();
            Shell.Run("schtasks.exe", "/Delete /TN " + Shell.Quote(LegacyAppStartupTaskName) + " /F", 15000);

            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var args = "/Create /TN " + Shell.Quote(AppStartupTaskName)
                    + " /TR " + Shell.Quote("\"" + exe + "\"")
                    + " /SC ONLOGON /RL HIGHEST /F";
                var result = Shell.Run("schtasks.exe", args, 15000);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException("Не удалось включить автозапуск приложения." + Environment.NewLine + result.Output + result.Error);
                }
            }
            else
            {
                var result = Shell.Run("schtasks.exe", "/Delete /TN " + Shell.Quote(AppStartupTaskName) + " /F", 15000);
                if (result.ExitCode != 0 && IsAppStartupEnabled())
                {
                    throw new InvalidOperationException("Не удалось отключить автозапуск приложения." + Environment.NewLine + result.Output + result.Error);
                }
            }
        }

        private void RemoveLegacyRunStartupValue()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key != null) key.DeleteValue(AppStartupTaskName, false);
                }
            }
            catch
            {
            }
        }

        private string BuildWinwsArguments(StrategyInfo strategy)
        {
            var text = File.ReadAllText(strategy.FilePath, Encoding.Default);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var command = new StringBuilder();
            var capture = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    capture = true;
                    var index = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                    line = line.Substring(index + "winws.exe".Length);
                    var quote = line.IndexOf('"');
                    if (quote >= 0) line = line.Substring(quote + 1);
                }

                if (!capture) continue;
                if (line.EndsWith("^")) line = line.Substring(0, line.Length - 1);
                command.Append(" ");
                command.Append(line.Trim());
            }

            var result = command.ToString();
            result = result.Replace("%BIN%", paths.Bin + Path.DirectorySeparatorChar);
            result = result.Replace("%LISTS%", paths.Lists + Path.DirectorySeparatorChar);
            result = result.Replace("%GameFilterTCP%", GetGameFilterTcp());
            result = result.Replace("%GameFilterUDP%", GetGameFilterUdp());
            result = result.Replace("%GameFilter%", GetGameFilterAny());
            result = Regex.Replace(result, @"\s+", " ").Trim();
            return result;
        }

        private string GetGameFilterTcp()
        {
            var file = Path.Combine(paths.Utils, "game_filter.enabled");
            if (!File.Exists(file)) return "12";
            var mode = File.ReadAllText(file).Trim().ToLowerInvariant();
            return (mode == "all" || mode == "tcp") ? "1024-65535" : "12";
        }

        private string GetGameFilterUdp()
        {
            var file = Path.Combine(paths.Utils, "game_filter.enabled");
            if (!File.Exists(file)) return "12";
            var mode = File.ReadAllText(file).Trim().ToLowerInvariant();
            return (mode == "all" || mode == "udp") ? "1024-65535" : "12";
        }

        private string GetGameFilterAny()
        {
            var file = Path.Combine(paths.Utils, "game_filter.enabled");
            if (!File.Exists(file)) return "12";
            var mode = File.ReadAllText(file).Trim().ToLowerInvariant();
            return (mode == "all" || mode == "tcp" || mode == "udp") ? "1024-65535" : "12";
        }

        private void EnsureUserLists()
        {
            Directory.CreateDirectory(paths.Lists);
            Directory.CreateDirectory(paths.Utils);
            EnsureFile(Path.Combine(paths.Lists, "ipset-exclude-user.txt"), "203.0.113.113/32");
            EnsureFile(Path.Combine(paths.Lists, "list-general-user.txt"), "domain.example.abc");
            EnsureFile(Path.Combine(paths.Lists, "list-exclude-user.txt"), "domain.example.abc");
        }

        private static void EnsureFile(string path, string content)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string NaturalKey(string path)
        {
            return Regex.Replace(Path.GetFileName(path), @"\d+", m => m.Value.PadLeft(8, '0'));
        }

        private static string GetServiceStatus(string name)
        {
            try
            {
                using (var service = new ServiceController(name))
                {
                    return service.Status.ToString();
                }
            }
            catch
            {
                return "не установлена";
            }
        }

        private static bool IsProcessRunning(string name)
        {
            return Process.GetProcessesByName(name).Length > 0;
        }

        private static void WaitForProcessExit(string name, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsProcessRunning(name)) return;
                System.Threading.Thread.Sleep(250);
            }
        }

        private static void StopAndDeleteService(string name)
        {
            Shell.Run("net.exe", "stop " + Shell.Quote(name), 15000);
            Shell.Run("sc.exe", "delete " + Shell.Quote(name), 15000);
        }

        private static void StopService(string name)
        {
            if (GetServiceStatus(name) == "не установлена") return;
            Shell.Run("net.exe", "stop " + Shell.Quote(name), 15000);
            WaitForServiceStopped(name, 10000);
        }

        private static void WaitForServiceStopped(string name, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var status = GetServiceStatus(name);
                if (status == "не установлена" || string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase)) return;
                System.Threading.Thread.Sleep(250);
            }
        }

        private static void EnableTcpTimestamps()
        {
            Shell.Run("netsh.exe", "interface tcp set global timestamps=enabled", 15000);
        }

        private static bool AreTcpTimestampsEnabled()
        {
            var r = Shell.Run("netsh.exe", "interface tcp show global", 10000);
            return r.Output.IndexOf("timestamps", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   r.Output.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsProxyEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    var value = key == null ? null : key.GetValue("ProxyEnable");
                    return value != null && Convert.ToInt32(value) == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool HasServiceNamePart(string part)
        {
            try
            {
                return ServiceController.GetServices().Any(s => s.ServiceName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                s.DisplayName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasIntelConnectivityService()
        {
            try
            {
                return ServiceController.GetServices().Any(s =>
                    (s.ServiceName + " " + s.DisplayName).IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (s.ServiceName + " " + s.DisplayName).IndexOf("Connectivity", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (s.ServiceName + " " + s.DisplayName).IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasConflictingBypassService()
        {
            var names = new[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };
            return names.Any(x => GetServiceStatus(x) != "не установлена");
        }

        private static bool HostsContainsYoutube()
        {
            try
            {
                var hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\drivers\\etc\\hosts");
                if (!File.Exists(hosts)) return false;
                var text = File.ReadAllText(hosts);
                return text.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       text.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void AddCheck(StringBuilder sb, string name, bool ok, string details)
        {
            sb.Append(ok ? "[OK] " : "[!] ");
            sb.Append(name);
            if (!ok)
            {
                sb.Append(" - ");
                sb.Append(details);
            }
            sb.AppendLine();
        }

        private static string GetInstalledStrategyName()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\zapret"))
                {
                    var value = key == null ? null : key.GetValue("zapret-discord-youtube");
                    return value == null ? null : value.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static void SaveInstalledStrategyName(string name)
        {
            using (var key = Registry.LocalMachine.CreateSubKey(@"System\CurrentControlSet\Services\zapret"))
            {
                if (key != null)
                {
                    key.SetValue("zapret-discord-youtube", name, RegistryValueKind.String);
                }
            }
        }
    }
}

