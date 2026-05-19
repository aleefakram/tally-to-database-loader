using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TallyDbLoader.Core.Tally
{
    public static class TallyLauncher
    {
        public static bool IsTallyRunning()
        {
            return Process.GetProcessesByName("tally").Length > 0;
        }

        public static void LaunchTally(string tallyExePath)
        {
            if (string.IsNullOrEmpty(tallyExePath) || !File.Exists(tallyExePath))
            {
                throw new FileNotFoundException("Tally.exe executable not found at specified path.", tallyExePath);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = tallyExePath,
                UseShellExecute = true
            });
        }

        public static void AddCompanyToIni(string iniPath, string companyFolderPath)
        {
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath)) return;
            
            var lines = File.ReadAllLines(iniPath).ToList();
            var settingIndex = lines.FindIndex(l => l.Trim().Equals("[Setting]", StringComparison.OrdinalIgnoreCase));
            
            if (settingIndex == -1)
            {
                lines.Add("[Setting]");
                settingIndex = lines.Count - 1;
            }
            
            string targetLine = $"UserOpen = {companyFolderPath}";
            bool alreadyOpen = lines.Any(l => {
                var parts = l.Split('=');
                if (parts.Length == 2 && parts[0].Trim().Equals("UserOpen", StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim().Trim('"').Equals(companyFolderPath.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
                }
                return false;
            });

            if (!alreadyOpen)
            {
                lines.Insert(settingIndex + 1, targetLine);
                File.WriteAllLines(iniPath, lines);
            }
        }
    }
}
