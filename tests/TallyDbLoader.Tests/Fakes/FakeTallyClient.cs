using System.Collections.Generic;
using System.Threading.Tasks;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Tests.Fakes
{
    public class FakeTallyClient : ITallyClient
    {
        private readonly List<(string key, string response)> _responses = new();
        public Dictionary<string, int> CallCounts { get; } = new();
        public List<string> AllRequests { get; } = new();

        public TallyCompanyInfo? CompanyInfo { get; set; } = new TallyCompanyInfo
        {
            Name = "TestCo",
            BooksFrom = new System.DateTime(2026, 4, 1),
            BooksTo = new System.DateTime(2026, 5, 25),
            AltMstId = 1000,
            AltVchId = 2000
        };

        public void Register(string requestKeySubstring, string response)
            => _responses.Add((requestKeySubstring, response));

        public Task<string> PostXMLAsync(string xmlRequest)
        {
            AllRequests.Add(xmlRequest);
            foreach (var (key, resp) in _responses)
            {
                if (xmlRequest.Contains(key))
                {
                    CallCounts[key] = CallCounts.GetValueOrDefault(key) + 1;
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult("");
        }

        public Task<TallyCompanyInfo?> FetchCompanyInfoAsync(string? companyName) => Task.FromResult(CompanyInfo);
        public Task<List<TallyCompanyInfo>> FetchActiveCompaniesDetailedAsync() => Task.FromResult(new List<TallyCompanyInfo> { CompanyInfo! });
        public Task<List<string>> FetchActiveCompaniesAsync() => Task.FromResult(new List<string> { CompanyInfo!.Name });
        public Task<string> FetchLedgersXmlAsync(string companyName) => throw new System.NotImplementedException();
    }
}
