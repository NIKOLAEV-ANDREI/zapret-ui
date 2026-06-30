using System;
using System.Net;
using System.Windows;

namespace zapret
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            ServicePointManager.SecurityProtocol =
                (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow());
        }
    }
}

