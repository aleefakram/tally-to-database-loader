using System;
using System.Text;
using System.Text.RegularExpressions;
using TallyDbLoader.Core.Logging;

namespace TallyDbLoader.Core.Tally
{
    public static class XmlSanitizer
    {
        private static readonly Regex CharacterEntityRegex = new Regex(@"&#(?:([0-9]+)|x([0-9a-fA-F]+));", RegexOptions.Compiled);

        public static string Sanitize(string? xmlContent)
        {
            if (string.IsNullOrEmpty(xmlContent))
            {
                return xmlContent ?? string.Empty;
            }

            // 1. Remove invalid XML numeric character references (e.g. &#x04; or &#4;)
            string step1 = CharacterEntityRegex.Replace(xmlContent, m =>
            {
                if (m.Groups[1].Success)
                {
                    if (int.TryParse(m.Groups[1].Value, out int val))
                    {
                        if (IsInvalidXmlCharacterValue(val))
                        {
                            return string.Empty;
                        }
                    }
                }
                else if (m.Groups[2].Success)
                {
                    if (int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out int val))
                    {
                        if (IsInvalidXmlCharacterValue(val))
                        {
                            return string.Empty;
                        }
                    }
                }
                return m.Value;
            });

            // 2. Remove invalid raw XML characters
            return RemoveInvalidRawXmlCharacters(step1);
        }

        private static string RemoveInvalidRawXmlCharacters(string xmlContent)
        {
            StringBuilder? sanitized = null;
            var removedCount = 0;

            for (var i = 0; i < xmlContent.Length; i++)
            {
                var ch = xmlContent[i];
                if (IsValidXmlCharacter(ch))
                {
                    sanitized?.Append(ch);
                    continue;
                }

                if (sanitized == null)
                {
                    sanitized = new StringBuilder(xmlContent.Length);
                    sanitized.Append(xmlContent, 0, i);
                }
                removedCount++;
            }

            if (sanitized == null)
            {
                return xmlContent;
            }

            FileLogger.LogMessage($"[XML Sanitizer] Removed {removedCount} invalid raw XML control character(s) before parsing.");
            return sanitized.ToString();
        }

        private static bool IsValidXmlCharacter(char ch)
        {
            return ch == '\t'
                || ch == '\n'
                || ch == '\r'
                || (ch >= ' ' && ch <= '\uD7FF')
                || (ch >= '\uE000' && ch <= '\uFFFD');
        }

        private static bool IsInvalidXmlCharacterValue(int val)
        {
            return !(val == 0x9
                || val == 0xA
                || val == 0xD
                || (val >= 0x20 && val <= 0xD7FF)
                || (val >= 0xE000 && val <= 0xFFFD)
                || (val >= 0x10000 && val <= 0x10FFFF));
        }
    }
}
