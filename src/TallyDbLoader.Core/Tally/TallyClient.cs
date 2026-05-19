using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace TallyDbLoader.Core.Tally
{
    public class TallyClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _tallyUrl;

        public TallyClient(string server, int port)
            : this(new HttpClient(), server, port)
        {
        }

        public TallyClient(HttpClient httpClient, string server, int port)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tallyUrl = $"http://{server}:{port}";
        }

        public static StringContent CreateTallyContent(string xmlRequest)
        {
            // Tally Prime expects UTF-16 (Unicode in .NET) for special symbol compatibility
            return new StringContent(xmlRequest, Encoding.Unicode, "text/xml");
        }

        public async Task<string> PostXMLAsync(string xmlRequest)
        {
            using (var content = CreateTallyContent(xmlRequest))
            {
                var response = await _httpClient.PostAsync(_tallyUrl, content);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }

        public async Task<string> FetchLedgersXmlAsync(string companyName)
        {
            var requestXml = $@"<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Data</TYPE>
    <ID>Ledger</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
        <SVCURRENTCOMPANY>{companyName}</SVCURRENTCOMPANY>
      </STATICVARIABLES>
    </DESC>
  </BODY>
</ENVELOPE>";
            return await PostXMLAsync(requestXml);
        }

        public async Task<System.Collections.Generic.List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync()
        {
            var requestXml = @"<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Data</TYPE>
    <ID>MyReportLedgerTable</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
      </STATICVARIABLES>
      <TDL>
        <TDLMESSAGE>
          <REPORT NAME=""MyReportLedgerTable"">
            <FORMS>MyForm</FORMS>
          </REPORT>
          <FORM NAME=""MyForm"">
            <PARTS>MyPart01</PARTS>
            <XMLTAG>DATA</XMLTAG>
          </FORM>
          <PART NAME=""MyPart01"">
            <LINES>MyLine01</LINES>
            <REPEAT>MyLine01 : MyCollection</REPEAT>
            <SCROLLED>Vertical</SCROLLED>
          </PART>
          <LINE NAME=""MyLine01"">
            <FIELDS>FldName, FldIsGroup</FIELDS>
            <XMLTAG>ROW</XMLTAG>
          </LINE>
          <FIELD NAME=""FldName"">
            <SET>$Name</SET>
            <XMLTAG>NAME</XMLTAG>
          </FIELD>
          <FIELD NAME=""FldIsGroup"">
            <SET>$IsGroupCompany</SET>
            <XMLTAG>ISGROUP</XMLTAG>
          </FIELD>
          <COLLECTION NAME=""MyCollection"">
            <TYPE>Company</TYPE>
            <FETCH></FETCH>
          </COLLECTION>
        </TDLMESSAGE>
      </TDL>
    </DESC>
  </BODY>
</ENVELOPE>";
            
            try
            {
                var responseXml = await PostXMLAsync(requestXml);
                var doc = System.Xml.Linq.XDocument.Parse(responseXml);
                var companies = new System.Collections.Generic.List<TallyCompanyInfo>();
                
                foreach (var el in doc.Descendants())
                {
                    string localName = el.Name.LocalName;
                    if (localName.Equals("ROW", StringComparison.OrdinalIgnoreCase))
                    {
                        var nameEl = el.Element("NAME") ?? el.Element("COMPANYNAME");
                        var isGroupEl = el.Element("ISGROUP") ?? el.Element("ISGROUPCOMPANY");
                        
                        var name = nameEl?.Value?.Trim();
                        if (!string.IsNullOrEmpty(name) && !companies.Exists(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            var isGroupStr = isGroupEl?.Value?.Trim();
                            bool isGroup = isGroupStr != null && (
                                isGroupStr.Equals("yes", StringComparison.OrdinalIgnoreCase) || 
                                isGroupStr.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                                isGroupStr.Equals("1")
                            );
                            
                            companies.Add(new TallyCompanyInfo { Name = name, IsGroup = isGroup });
                        }
                    }
                    else if (localName.Equals("COMPANYNAME", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = el.Value?.Trim();
                        if (!string.IsNullOrEmpty(name) && !companies.Exists(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            companies.Add(new TallyCompanyInfo { Name = name, IsGroup = false });
                        }
                    }
                    else if (localName.Equals("COMPANY", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = el.Attribute("NAME")?.Value?.Trim() ?? el.Value?.Trim();
                        if (!string.IsNullOrEmpty(name) && !companies.Exists(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            companies.Add(new TallyCompanyInfo { Name = name, IsGroup = false });
                        }
                    }
                }
                return companies;
            }
            catch
            {
                return new System.Collections.Generic.List<TallyCompanyInfo>();
            }
        }

        public async Task<System.Collections.Generic.List<string>> FetchActiveCompaniesAsync()
        {
            var detailed = await FetchActiveCompaniesDetailedAsync();
            var names = new System.Collections.Generic.List<string>();
            foreach (var c in detailed)
            {
                names.Add(c.Name);
            }
            return names;
        }
    }
}
