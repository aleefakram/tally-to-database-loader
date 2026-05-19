namespace TallyDbLoader.Core.Tally
{
    public class TallyCompanyInfo
    {
        public string Name { get; set; } = string.Empty;
        public bool IsGroup { get; set; }

        public override string ToString()
        {
            return IsGroup ? $"{Name} (Consolidated)" : Name;
        }
    }
}
