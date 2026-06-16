using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Models
{
    public sealed class ConfigImportPreview
    {
        public IReadOnlyList<ConfigImportPreviewDatabaseProfile> DatabaseProfiles { get; init; } = Array.Empty<ConfigImportPreviewDatabaseProfile>();
        public IReadOnlyList<ConfigImportPreviewCompanyProfile> CompanyProfiles { get; init; } = Array.Empty<ConfigImportPreviewCompanyProfile>();
        public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
        public bool HasConflicts { get; init; }
        public bool IsValid => ValidationErrors.Count == 0;
    }

    public sealed class ConfigImportPreviewDatabaseProfile
    {
        public int SourceId { get; init; }
        public string Name { get; init; } = "";
        public bool HasPassword { get; init; }
        public bool HasConflict { get; init; }
    }

    public sealed class ConfigImportPreviewCompanyProfile
    {
        public int SourceId { get; init; }
        public string Name { get; init; } = "";
        public bool HasConflict { get; init; }
    }
}
