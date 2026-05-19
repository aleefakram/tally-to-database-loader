using System.IO;
using Xunit;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Tests
{
    public class TallyLauncherTests
    {
        [Fact]
        public void Test_TallyIni_Modification()
        {
            var testIni = "test_tally.ini";
            if (File.Exists(testIni)) File.Delete(testIni);
            
            File.WriteAllLines(testIni, new[] {
                "[Setting]",
                "Port = 9000",
                "UserOpen = C:\\Data\\OldCompany"
            });
            
            TallyLauncher.AddCompanyToIni(testIni, "C:\\Data\\NewCompany");
            
            var lines = File.ReadAllLines(testIni);
            Assert.Contains("UserOpen = C:\\Data\\NewCompany", lines);
            
            if (File.Exists(testIni)) File.Delete(testIni);
        }
    }
}
