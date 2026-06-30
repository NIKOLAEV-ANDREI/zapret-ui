namespace zapret
{
    public sealed class TestTarget
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string PingHost { get; set; }

        public bool IsUrl
        {
            get { return !string.IsNullOrWhiteSpace(Url); }
        }
    }
}

