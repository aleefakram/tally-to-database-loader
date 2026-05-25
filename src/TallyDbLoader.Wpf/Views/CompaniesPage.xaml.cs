using System.Windows.Controls;
using System.Windows.Input;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Wpf.Views
{
    public partial class CompaniesPage : Page
    {
        public CompaniesPage() => InitializeComponent();

        private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is CompanyProfile profile)
            {
                var vm = (MainViewModel)this.DataContext;
                vm.StartEditingCompanyCommand.Execute(profile.Id);
            }
        }
    }
}
