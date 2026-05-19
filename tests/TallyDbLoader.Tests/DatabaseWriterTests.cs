using Xunit;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;
using System.Collections.Generic;

namespace TallyDbLoader.Tests
{
    public class DatabaseWriterTests
    {
        [Fact]
        public void Test_DatabaseWriter_UnsupportedTech_Throws()
        {
            var profile = new DatabaseProfile
            {
                Name = "Invalid",
                Technology = "unsupported_tech",
                Server = "localhost",
                Port = 1234
            };

            Assert.ThrowsAny<System.Exception>(() => 
                DatabaseWriter.InitializeTargetTables(profile, "test_db")
            );
        }
    }
}
