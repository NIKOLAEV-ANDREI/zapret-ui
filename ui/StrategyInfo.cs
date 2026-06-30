namespace zapret
{
    public sealed class StrategyInfo
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public bool NotRecommended { get; set; }
        public int Blocks { get; set; }

        public override string ToString()
        {
            return NotRecommended ? Name + " (не рекомендуется)" : Name;
        }
    }
}

