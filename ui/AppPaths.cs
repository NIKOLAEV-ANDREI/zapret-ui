using System;
using System.IO;
using System.Reflection;

namespace zapret
{
    public sealed class AppPaths
    {
        public string Root { get; private set; }
        public string Bin { get { return Path.Combine(Root, "bin"); } }
        public string Lists { get { return Path.Combine(Root, "lists"); } }
        public string Utils { get { return Path.Combine(Root, "utils"); } }
        public string WinwsExe { get { return Path.Combine(Bin, "winws.exe"); } }
        public string ServiceBat { get { return Path.Combine(Root, "service.bat"); } }
        public string TestScript { get { return Path.Combine(Utils, "test zapret.ps1"); } }

        public AppPaths()
        {
            Root = FindRoot();
        }

        private static string FindRoot()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null)
            {
                dir = AppDomain.CurrentDomain.BaseDirectory;
            }

            var current = new DirectoryInfo(dir);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "bin")) &&
                    Directory.Exists(Path.Combine(current.FullName, "lists")) &&
                    File.Exists(Path.Combine(current.FullName, "service.bat")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return dir;
        }
    }
}

