using System;
using System.Collections.Generic;

namespace TallyDbLoader.Core.Data
{
    public class ConfigImportValidationException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public ConfigImportValidationException(IEnumerable<string> errors)
            : base("Validation failed for the configuration import payload. See the Errors property for details.")
        {
            Errors = new List<string>(errors ?? Array.Empty<string>()).AsReadOnly();
        }
    }
}
