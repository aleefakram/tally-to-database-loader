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
    }
}
