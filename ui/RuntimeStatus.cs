namespace zapret
{
    public sealed class RuntimeStatus
    {
        public bool WinwsExists { get; set; }
        public bool WinwsRunning { get; set; }
        public bool ZapretServiceRunning { get; set; }
        public bool WinDivertRunning { get; set; }
        public string ZapretServiceStatus { get; set; }
        public string WinDivertStatus { get; set; }
        public string InstalledStrategyName { get; set; }
        public string GameFilterStatus { get; set; }
        public string IpsetStatus { get; set; }
        public bool UpdateCheckEnabled { get; set; }
    }
}

