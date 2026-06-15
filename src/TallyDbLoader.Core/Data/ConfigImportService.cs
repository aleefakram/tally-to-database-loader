using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public class ConfigImportService
    {
        private readonly IConfigRepository _repository;

        public ConfigImportService(IConfigRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        private class ExportEnvelope
        {
            public string Format { get; set; } = "";
            public int Schema_Version { get; set; }
            public string Application_Version { get; set; } = "";
            public ExportPayload? Payload { get; set; }
        }

        private class ExportPayload
        {
            public List<ExportDatabaseProfile>? Database_Profiles { get; set; }
            public List<ExportCompanyProfile>? Company_Profiles { get; set; }
        }

        private class ExportDatabaseProfile
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string? Technology { get; set; }
            public string Server { get; set; } = "";
            public int Port { get; set; }
            public string Username { get; set; } = "";
            public bool? Has_Password { get; set; }
        }

        private class ExportCompanyProfile
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string? Tally_Guid { get; set; }
            public bool Consolidated { get; set; }
            public string? Books_From { get; set; }
            public string? Books_To { get; set; }
            public int Db_Profile_Id { get; set; }
            public string Target_Catalog { get; set; } = "";
            public string Schema { get; set; } = "public";
            public string Table_Prefix { get; set; } = "";
            public string Mode { get; set; } = "full";
            public int Interval_Minutes { get; set; }
            public bool Enabled { get; set; }
            public bool Notify_On_Error { get; set; }
            public bool Pause_On_Tally_Close { get; set; }
            public int Entity_Flags { get; set; }
        }

        public void ImportJson(string json, ImportDecision decision, string actor, string reason)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));
            if (decision == null)
                throw new ArgumentNullException(nameof(decision));
            if (string.IsNullOrWhiteSpace(actor))
                throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));

            ExportEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidOperationException("Failed to deserialize JSON.");
            }
            catch (Exception ex)
            {
                throw new ConfigImportValidationException(new[] { $"Invalid JSON content: {ex.Message}" });
            }

            var errors = new List<string>();

            if (envelope.Format != "tally-db-loader.config-export")
            {
                errors.Add("Unsupported or invalid format string.");
            }
            if (envelope.Schema_Version != 1)
            {
                errors.Add("Unsupported schema version. Only version 1 is supported.");
            }
            if (string.IsNullOrWhiteSpace(envelope.Application_Version))
            {
                errors.Add("Application version must be a non-empty string.");
            }
            if (envelope.Payload == null)
            {
                errors.Add("Configuration payload is missing or empty.");
            }

            if (errors.Count > 0)
                throw new ConfigImportValidationException(errors);

            var payload = envelope.Payload!;
            var dbProfiles = payload.Database_Profiles ?? new List<ExportDatabaseProfile>();
            var companyProfiles = payload.Company_Profiles ?? new List<ExportCompanyProfile>();

            // 1. Basic structural validation to prevent NullReferenceException on dereferences
            foreach (var db in dbProfiles)
            {
                if (db == null)
                {
                    errors.Add("Database profile element is null.");
                    continue;
                }
                if (db.Id <= 0)
                {
                    errors.Add("Database profile has an invalid or missing ID.");
                }
                if (string.IsNullOrWhiteSpace(db.Name))
                {
                    errors.Add($"Database profile ID {db.Id} is missing a name.");
                }
                if (string.IsNullOrWhiteSpace(db.Technology))
                {
                    errors.Add($"Database profile '{db.Name}' (ID {db.Id}) is missing technology.");
                }
                if (string.IsNullOrWhiteSpace(db.Server))
                {
                    errors.Add($"Database profile '{db.Name}' (ID {db.Id}) is missing server host.");
                }
                if (db.Has_Password == null)
                {
                    errors.Add($"Database profile '{db.Name}' (ID {db.Id}) is missing has_password flag.");
                }
            }

            foreach (var comp in companyProfiles)
            {
                if (comp == null)
                {
                    errors.Add("Company profile element is null.");
                    continue;
                }
                if (comp.Id <= 0)
                {
                    errors.Add("Company profile has an invalid or missing ID.");
                }
                if (string.IsNullOrWhiteSpace(comp.Name))
                {
                    errors.Add($"Company profile ID {comp.Id} is missing a name.");
                }
                if (comp.Db_Profile_Id <= 0)
                {
                    errors.Add($"Company profile '{comp.Name}' (ID {comp.Id}) is missing db_profile_id.");
                }
                if (string.IsNullOrWhiteSpace(comp.Target_Catalog))
                {
                    errors.Add($"Company profile '{comp.Name}' (ID {comp.Id}) is missing target_catalog.");
                }
            }

            if (errors.Count > 0)
                throw new ConfigImportValidationException(errors);

            // 2. Duplicate source ID checks
            var dbSourceIds = new HashSet<int>();
            foreach (var db in dbProfiles)
            {
                if (!dbSourceIds.Add(db.Id))
                    errors.Add($"Duplicate database profile source ID: {db.Id}");
            }

            var compSourceIds = new HashSet<int>();
            foreach (var comp in companyProfiles)
            {
                if (!compSourceIds.Add(comp.Id))
                    errors.Add($"Duplicate company profile source ID: {comp.Id}");
            }

            if (errors.Count > 0)
                throw new ConfigImportValidationException(errors);

            // 3. Load existing models for conflict matching
            var existingDbs = _repository.GetAllDatabaseProfiles() ?? new List<DatabaseProfile>();
            var existingComps = _repository.GetAllCompanyProfiles() ?? new List<CompanyProfile>();

            var resolvedDbs = new List<ResolvedDatabaseProfileImport>();
            var resolvedComps = new List<ResolvedCompanyProfileImport>();

            var skippedDbIds = new HashSet<int>();
            var skippedCompIds = new HashSet<int>();

            // 4. Resolve Database Conflicts & Passwords
            foreach (var sourceDb in dbProfiles)
            {
                var sourceNameNorm = sourceDb.Name.Trim().ToLowerInvariant();
                var existingMatch = existingDbs.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);

                if (existingMatch != null)
                {
                    if (!decision.DatabaseConflicts.TryGetValue(sourceDb.Id, out var strategy))
                    {
                        errors.Add($"Conflict detected for database profile '{sourceDb.Name}' (Source ID {sourceDb.Id}). No conflict resolution strategy provided.");
                        continue;
                    }

                    if (strategy == ConflictResolutionStrategy.Skip)
                    {
                        skippedDbIds.Add(sourceDb.Id);
                        continue;
                    }

                    // Overwrite
                    string? password = null;
                    bool preservePassword = true;

                    if (sourceDb.Has_Password.GetValueOrDefault())
                    {
                        if (!decision.DatabasePasswords.TryGetValue(sourceDb.Id, out password) || string.IsNullOrEmpty(password))
                        {
                            errors.Add($"Database profile '{sourceDb.Name}' (Source ID {sourceDb.Id}) requires a password on overwrite, but none was provided.");
                            continue;
                        }
                        preservePassword = false;
                    }

                    resolvedDbs.Add(new ResolvedDatabaseProfileImport
                    {
                        SourceId = sourceDb.Id,
                        ExistingLocalId = existingMatch.Id,
                        Action = ImportAction.Overwrite,
                        Password = password,
                        PreserveExistingPassword = preservePassword,
                        Profile = new DatabaseProfile
                        {
                            Name = sourceDb.Name,
                            Technology = sourceDb.Technology ?? string.Empty,
                            Server = sourceDb.Server,
                            Port = sourceDb.Port,
                            Username = sourceDb.Username ?? string.Empty
                        }
                    });
                }
                else
                {
                    // Create
                    string? password = null;
                    if (sourceDb.Has_Password.GetValueOrDefault())
                    {
                        if (!decision.DatabasePasswords.TryGetValue(sourceDb.Id, out password) || string.IsNullOrEmpty(password))
                        {
                            errors.Add($"Database profile '{sourceDb.Name}' (Source ID {sourceDb.Id}) is new and requires a password, but none was provided.");
                            continue;
                        }
                    }

                    resolvedDbs.Add(new ResolvedDatabaseProfileImport
                    {
                        SourceId = sourceDb.Id,
                        Action = ImportAction.Create,
                        Password = password,
                        PreserveExistingPassword = false,
                        Profile = new DatabaseProfile
                        {
                            Name = sourceDb.Name,
                            Technology = sourceDb.Technology ?? string.Empty,
                            Server = sourceDb.Server,
                            Port = sourceDb.Port,
                            Username = sourceDb.Username ?? string.Empty
                        }
                    });
                }
            }

            // 5. Resolve Company Conflicts & skipped DB profiles validation
            foreach (var sourceComp in companyProfiles)
            {
                // A company profile must only reference a DB profile in the payload
                var dbInPayload = dbProfiles.FirstOrDefault(d => d.Id == sourceComp.Db_Profile_Id);
                if (dbInPayload == null)
                {
                    errors.Add($"Company profile '{sourceComp.Name}' references database profile ID {sourceComp.Db_Profile_Id} which is not present in the import payload.");
                    continue;
                }

                // If referenced DB profile is skipped, company MUST also be skipped
                bool dbIsSkipped = skippedDbIds.Contains(sourceComp.Db_Profile_Id);

                var sourceNameNorm = sourceComp.Name.Trim().ToLowerInvariant();
                CompanyProfile? existingMatch = null;

                if (!string.IsNullOrEmpty(sourceComp.Tally_Guid))
                {
                    var matchByGuid = existingComps.FirstOrDefault(e => e.TallyGuid == sourceComp.Tally_Guid);
                    var matchByName = existingComps.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);

                    if (matchByGuid != null && matchByName != null && matchByGuid.Id != matchByName.Id)
                    {
                        errors.Add($"Ambiguous conflict for company profile '{sourceComp.Name}': matches GUID with one profile and Name with another. Import blocked.");
                        continue;
                    }

                    existingMatch = matchByGuid ?? matchByName;
                }
                else
                {
                    existingMatch = existingComps.FirstOrDefault(e => e.Name.Trim().ToLowerInvariant() == sourceNameNorm);
                }

                // Parse dates safely with TryParse
                DateTime? booksFromVal = null;
                if (!string.IsNullOrEmpty(sourceComp.Books_From))
                {
                    if (DateTime.TryParse(sourceComp.Books_From, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dtFrom))
                    {
                        booksFromVal = dtFrom;
                    }
                    else
                    {
                        errors.Add($"Company profile '{sourceComp.Name}' has an invalid books_from date format: '{sourceComp.Books_From}'.");
                    }
                }

                DateTime? booksToVal = null;
                if (!string.IsNullOrEmpty(sourceComp.Books_To))
                {
                    if (DateTime.TryParse(sourceComp.Books_To, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dtTo))
                    {
                        booksToVal = dtTo;
                    }
                    else
                    {
                        errors.Add($"Company profile '{sourceComp.Name}' has an invalid books_to date format: '{sourceComp.Books_To}'.");
                    }
                }

                if (existingMatch != null)
                {
                    if (!decision.CompanyConflicts.TryGetValue(sourceComp.Id, out var strategy))
                    {
                        errors.Add($"Conflict detected for company profile '{sourceComp.Name}' (Source ID {sourceComp.Id}). No conflict resolution strategy provided.");
                        continue;
                    }

                    if (strategy == ConflictResolutionStrategy.Skip || dbIsSkipped)
                    {
                        if (dbIsSkipped && strategy != ConflictResolutionStrategy.Skip)
                        {
                            errors.Add($"Company profile '{sourceComp.Name}' cannot be imported because its referenced database profile (ID {sourceComp.Db_Profile_Id}) is skipped, but the company profile is not marked to skip.");
                        }
                        skippedCompIds.Add(sourceComp.Id);
                        continue;
                    }

                    resolvedComps.Add(new ResolvedCompanyProfileImport
                    {
                        SourceId = sourceComp.Id,
                        ExistingLocalId = existingMatch.Id,
                        SourceDbProfileId = sourceComp.Db_Profile_Id,
                        Action = ImportAction.Overwrite,
                        Profile = new CompanyProfile
                        {
                            Name = sourceComp.Name,
                            TallyGuid = sourceComp.Tally_Guid,
                            Consolidated = sourceComp.Consolidated,
                            BooksFrom = booksFromVal,
                            BooksTo = booksToVal,
                            TargetCatalog = sourceComp.Target_Catalog,
                            Schema = sourceComp.Schema,
                            TablePrefix = sourceComp.Table_Prefix,
                            Mode = sourceComp.Mode,
                            IntervalMinutes = sourceComp.Interval_Minutes,
                            NotifyOnError = sourceComp.Notify_On_Error,
                            PauseOnTallyClose = sourceComp.Pause_On_Tally_Close,
                            EntityFlags = sourceComp.Entity_Flags
                        }
                    });
                }
                else
                {
                    if (dbIsSkipped)
                    {
                        if (!decision.CompanyConflicts.TryGetValue(sourceComp.Id, out var strategy) || strategy != ConflictResolutionStrategy.Skip)
                        {
                            errors.Add($"Company profile '{sourceComp.Name}' cannot be imported because its referenced database profile (ID {sourceComp.Db_Profile_Id}) is skipped, but the company profile is not marked to skip.");
                        }
                        skippedCompIds.Add(sourceComp.Id);
                        continue;
                    }

                    resolvedComps.Add(new ResolvedCompanyProfileImport
                    {
                        SourceId = sourceComp.Id,
                        SourceDbProfileId = sourceComp.Db_Profile_Id,
                        Action = ImportAction.Create,
                        Profile = new CompanyProfile
                        {
                            Name = sourceComp.Name,
                            TallyGuid = sourceComp.Tally_Guid,
                            Consolidated = sourceComp.Consolidated,
                            BooksFrom = booksFromVal,
                            BooksTo = booksToVal,
                            TargetCatalog = sourceComp.Target_Catalog,
                            Schema = sourceComp.Schema,
                            TablePrefix = sourceComp.Table_Prefix,
                            Mode = sourceComp.Mode,
                            IntervalMinutes = sourceComp.Interval_Minutes,
                            NotifyOnError = sourceComp.Notify_On_Error,
                            PauseOnTallyClose = sourceComp.Pause_On_Tally_Close,
                            EntityFlags = sourceComp.Entity_Flags
                        }
                    });
                }
            }

            if (errors.Count > 0)
                throw new ConfigImportValidationException(errors);

            // 6. Build Compact Audit JSON Payloads (overwritten records and skipped/created summary counts)
            var auditBefore = new
            {
                overwritten_database_profiles = existingDbs
                    .Where(e => resolvedDbs.Any(r => r.Action == ImportAction.Overwrite && r.ExistingLocalId == e.Id))
                    .Select(d => new { name = d.Name, technology = d.Technology }).ToList(),
                overwritten_company_profiles = existingComps
                    .Where(e => resolvedComps.Any(r => r.Action == ImportAction.Overwrite && r.ExistingLocalId == e.Id))
                    .Select(c => new { name = c.Name, target_catalog = c.TargetCatalog }).ToList(),
                created_database_profiles_count = resolvedDbs.Count(r => r.Action == ImportAction.Create),
                created_company_profiles_count = resolvedComps.Count(r => r.Action == ImportAction.Create),
                skipped_database_profiles_count = skippedDbIds.Count,
                skipped_company_profiles_count = skippedCompIds.Count
            };

            var auditAfter = new
            {
                database_profiles = resolvedDbs.Select(r => new { name = r.Profile.Name, action = r.Action.ToString().ToLower() }).ToList(),
                company_profiles = resolvedComps.Select(r => new { name = r.Profile.Name, action = r.Action.ToString().ToLower(), enabled = false, status = "review_required" }).ToList()
            };

            string beforeJson = JsonSerializer.Serialize(auditBefore);
            string afterJson = JsonSerializer.Serialize(auditAfter);

            // 7. Invoke transactional repository write
            _repository.ImportSanitizedConfig(
                resolvedDbs,
                resolvedComps,
                actor,
                reason,
                beforeJson,
                afterJson);
        }
    }
}
