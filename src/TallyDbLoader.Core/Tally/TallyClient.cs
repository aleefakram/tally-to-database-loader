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

        public async Task<System.Collections.Generic.List<string>> FetchActiveCompaniesAsync()
        {
            var requestXml = @"<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Data</TYPE>
    <ID>MiniCompanyReport</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
      </STATICVARIABLES>
      <TDL>
        <TDLMSG>
          <REPORT NAME=""MiniCompanyReport"">
            <FORM>MiniCompanyForm</FORM>
          </REPORT>
          <FORM NAME=""MiniCompanyForm"">
            <PART>MiniCompanyPart</PART>
          </FORM>
          <PART NAME=""MiniCompanyPart"">
            <LINE>MiniCompanyLine</LINE>
            <REPEAT>MiniCompanyLine : MiniCompanyCollection</REPEAT>
            <SCROLL>Vertical</SCROLL>
          </PART>
          <LINE NAME=""MiniCompanyLine"">
            <FIELD>MiniCompanyNameField</FIELD>
          </LINE>
          <FIELD NAME=""MiniCompanyNameField"">
            <SET>$Name</SET>
            <XMLTAG>""COMPANYNAME""</XMLTAG>
          </FIELD>
          <COLLECTION NAME=""MiniCompanyCollection"">
            <TYPE>Company</TYPE>
          </COLLECTION>
        </TDLMSG>
      </TDL>
    </DESC>
  </BODY>
</ENVELOPE>";
            
            try
            {
                var responseXml = await PostXMLAsync(requestXml);
                var doc = System.Xml.Linq.XDocument.Parse(responseXml);
                var companies = new System.Collections.Generic.List<string>();
                
                foreach (var el in doc.Descendants())
                {
                    string localName = el.Name.LocalName;
                    if (localName.Equals("COMPANYNAME", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = el.Value?.Trim();
                        if (!string.IsNullOrEmpty(val) && !companies.Contains(val))
                        {
                            companies.Add(val);
                        }
                    }
                }
                return companies;
            }
            catch
            {
                return new System.Collections.Generic.List<string>();
            }
        }
    }
}
