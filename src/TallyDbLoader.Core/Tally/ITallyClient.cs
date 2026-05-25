using System.Collections.Generic;
using System.Threading.Tasks;

namespace TallyDbLoader.Core.Tally
{
    public interface ITallyClient
    {
        Task<string> PostXMLAsync(string xmlRequest);
        Task<string> FetchLedgersXmlAsync(string companyName);
        Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync();
        Task<List<string>> FetchActiveCompaniesAsync();
        Task<TallyCompanyInfo?> FetchCompanyInfoAsync(string companyName);
    }
}
