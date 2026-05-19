using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TallyDbLoader.Core.Tally
{
    public static class DynamicTdlXmlGenerator
    {
        public static string GenerateXml(TableConfig tableConfig, string targetCompany, string fromDate, string toDate)
        {
            var sb = new StringBuilder();

            // XML Header
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>TallyDatabaseLoaderReport</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>XML (Data Interchange)</SVEXPORTFORMAT><SVFROMDATE>{fromDate}</SVFROMDATE><SVTODATE>{toDate}</SVTODATE>");
            
            if (string.IsNullOrEmpty(targetCompany))
            {
                sb.Append("</STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=\"TallyDatabaseLoaderReport\"><FORMS>MyForm</FORMS></REPORT><FORM NAME=\"MyForm\"><PARTS>MyPart01</PARTS></FORM>");
            }
            else
            {
                sb.Append("<SVCURRENTCOMPANY>{targetCompany}</SVCURRENTCOMPANY></STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=\"TallyDatabaseLoaderReport\"><FORMS>MyForm</FORMS></REPORT><FORM NAME=\"MyForm\"><PARTS>MyPart01</PARTS></FORM>");
            }

            var xml = sb.ToString();
            xml = xml.Replace("{fromDate}", fromDate);
            xml = xml.Replace("{toDate}", toDate);
            if (!string.IsNullOrEmpty(targetCompany))
            {
                xml = xml.Replace("{targetCompany}", EscapeXml(targetCompany));
            }

            sb.Clear();
            sb.Append(xml);

            // Push routes list
            var lstRoutes = tableConfig.Collection.Split('.').ToList();
            var targetCollection = lstRoutes[0];
            lstRoutes.RemoveAt(0);
            lstRoutes.Insert(0, "MyCollection");

            // Loop through and append PART XML
            for (int i = 0; i < lstRoutes.Count; i++)
            {
                var xmlPart = $"MyPart{(i + 1):D2}";
                var xmlLine = $"MyLine{(i + 1):D2}";
                sb.Append($"<PART NAME=\"{xmlPart}\"><LINES>{xmlLine}</LINES><REPEAT>{xmlLine} : {lstRoutes[i]}</REPEAT><SCROLLED>Vertical</SCROLLED></PART>");
            }

            // Loop through and append LINE XML (except last line which contains field data)
            for (int i = 0; i < lstRoutes.Count - 1; i++)
            {
                var xmlLine = $"MyLine{(i + 1):D2}";
                var xmlPart = $"MyPart{(i + 2):D2}";
                sb.Append($"<LINE NAME=\"{xmlLine}\"><FIELDS>FldBlank</FIELDS><EXPLODE>{xmlPart}</EXPLODE></LINE>");
            }

            // Last line
            var lastLineName = $"MyLine{lstRoutes.Count:D2}";
            sb.Append($"<LINE NAME=\"{lastLineName}\"><FIELDS>");

            // Append field declaration list
            for (int i = 0; i < tableConfig.Fields.Count; i++)
            {
                sb.Append($"Fld{(i + 1):D2},");
            }
            if (tableConfig.Fields.Count > 0)
            {
                sb.Length--; // Remove last comma
            }
            sb.Append("</FIELDS></LINE>");

            // Loop through each field
            for (int i = 0; i < tableConfig.Fields.Count; i++)
            {
                var fieldName = $"Fld{(i + 1):D2}";
                var xmlTag = $"F{(i + 1):D2}";
                sb.Append($"<FIELD NAME=\"{fieldName}\">");

                var iField = tableConfig.Fields[i];

                if (Regex.IsMatch(iField.Field, @"^(\.\.)?[a-zA-Z0-9_]+$"))
                {
                    if (iField.Type == "text")
                        sb.Append($"<SET>${iField.Field}</SET>");
                    else if (iField.Type == "logical")
                        sb.Append($"<SET>if ${iField.Field} then 1 else 0</SET>");
                    else if (iField.Type == "date")
                        sb.Append($"<SET>if $$IsEmpty:${iField.Field} then $$StrByCharCode:241 else $$PyrlYYYYMMDDFormat:${iField.Field}:\"-\"</SET>");
                    else if (iField.Type == "number")
                        sb.Append($"<SET>if $$IsEmpty:${iField.Field} then \"0\" else $$String:${iField.Field}</SET>");
                    else if (iField.Type == "amount")
                        sb.Append($"<SET>$$StringFindAndReplace:(if $$IsDebit:${iField.Field} then -$$NumValue:${iField.Field} else $$NumValue:${iField.Field}):\"(-)\":\"-\"</SET>");
                    else if (iField.Type == "quantity")
                        sb.Append($"<SET>$$StringFindAndReplace:(if $$IsInwards:${iField.Field} then $$Number:$$String:${iField.Field}:\"TailUnits\" else -$$Number:$$String:${iField.Field}:\"TailUnits\"):\"(-)\":\"-\"</SET>");
                    else if (iField.Type == "rate")
                        sb.Append($"<SET>if $$IsEmpty:${iField.Field} then 0 else $$Number:${iField.Field}</SET>");
                    else
                        sb.Append($"<SET>{iField.Field}</SET>");
                }
                else
                {
                    sb.Append($"<SET>{iField.Field}</SET>");
                }

                sb.Append($"<XMLTAG>{xmlTag}</XMLTAG></FIELD>");
            }

            // Blank Field specification
            sb.Append("<FIELD NAME=\"FldBlank\"><SET>\"\"</SET></FIELD>");

            // Collection
            sb.Append($"<COLLECTION NAME=\"MyCollection\"><TYPE>{targetCollection}</TYPE>");

            // Fetch list
            if (tableConfig.Fetch != null && tableConfig.Fetch.Count > 0)
            {
                sb.Append($"<FETCH>{string.Join(",", tableConfig.Fetch)}</FETCH>");
            }

            // Filter definition on collection
            if (tableConfig.Filters != null && tableConfig.Filters.Count > 0)
            {
                sb.Append("<FILTER>");
                for (int j = 0; j < tableConfig.Filters.Count; j++)
                {
                    sb.Append($"Fltr{(j + 1):D2},");
                }
                sb.Length--; // Remove last comma
                sb.Append("</FILTER>");
            }

            sb.Append("</COLLECTION>");

            // Filter formulae
            if (tableConfig.Filters != null && tableConfig.Filters.Count > 0)
            {
                for (int j = 0; j < tableConfig.Filters.Count; j++)
                {
                    sb.Append($"<SYSTEM TYPE=\"Formulae\" NAME=\"Fltr{(j + 1):D2}\">{tableConfig.Filters[j]}</SYSTEM>");
                }
            }

            // XML Footer
            sb.Append("</TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>");

            return sb.ToString();
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("\"", "&quot;")
                        .Replace("'", "&apos;");
        }
    }
}
