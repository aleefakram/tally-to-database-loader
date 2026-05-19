using System.Data;
using System.Threading.Tasks;

namespace TallyDbLoader.Core.DatabaseLoaders
{
    public interface IDatabaseLoader
    {
        Task LoadBulkDataAsync(DataTable data, string tableName);
    }
}
