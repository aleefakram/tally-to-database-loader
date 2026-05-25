using System;

namespace TallyDbLoader.Core.Tally
{
    public class TallyCompanyInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? Guid { get; set; }
        public bool IsGroup { get; set; }
        public DateTime? BooksFrom { get; set; }
        public DateTime? BooksTo { get; set; }
        public long AltMstId { get; set; }
        public long AltVchId { get; set; }

        public override string ToString()
        {
            return IsGroup ? $"{Name} (Consolidated)" : Name;
        }
    }
}
