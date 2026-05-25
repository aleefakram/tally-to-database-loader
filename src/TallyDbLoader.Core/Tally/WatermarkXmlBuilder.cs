using System.Security;

namespace TallyDbLoader.Core.Tally
{
    public static class WatermarkXmlBuilder
    {
        private const string XmlTemplate = """
<?xml version="1.0" encoding="utf-8"?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>MyReport</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME="MyReport"><FORMS>MyForm</FORMS></REPORT><FORM NAME="MyForm"><PARTS>MyPart</PARTS></FORM><PART NAME="MyPart"><LINES>MyLine</LINES><REPEAT>MyLine : MyCollection</REPEAT><SCROLLED>Vertical</SCROLLED></PART><LINE NAME="MyLine"><FIELDS>FldAlterMaster,FldAlterTransaction</FIELDS></LINE><FIELD NAME="FldAlterMaster"><SET>$AltMstId</SET></FIELD><FIELD NAME="FldAlterTransaction"><SET>$AltVchId</SET></FIELD><COLLECTION NAME="MyCollection"><TYPE>Company</TYPE><FILTER>FilterActiveCompany</FILTER></COLLECTION><SYSTEM TYPE="Formulae" NAME="FilterActiveCompany">$$IsEqual:##SVCurrentCompany:$Name</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>
""";

        public static string Build(string? companyName)
        {
            if (string.IsNullOrEmpty(companyName))
            {
                return XmlTemplate;
            }

            string escapedName = SecurityElement.Escape(companyName);
            return XmlTemplate.Replace("##SVCurrentCompany", $"\"{escapedName}\"");
        }
    }
}
