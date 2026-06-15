using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    public enum ConflictResolutionStrategy
    {
        Skip,
        Overwrite
    }

    public class ImportDecision
    {
        public Dictionary<int, ConflictResolutionStrategy> DatabaseConflicts { get; set; } = new Dictionary<int, ConflictResolutionStrategy>();
        public Dictionary<int, string> DatabasePasswords { get; set; } = new Dictionary<int, string>();
        public Dictionary<int, ConflictResolutionStrategy> CompanyConflicts { get; set; } = new Dictionary<int, ConflictResolutionStrategy>();
    }
}
