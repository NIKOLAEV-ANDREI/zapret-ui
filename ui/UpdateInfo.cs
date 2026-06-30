namespace zapret
{
    public sealed class UpdateInfo
    {
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public string Notes { get; set; }
        public bool HasUpdate { get; set; }
    }
}
