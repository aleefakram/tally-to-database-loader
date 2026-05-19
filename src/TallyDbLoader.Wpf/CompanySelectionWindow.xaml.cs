using System.Collections.Generic;
using System.Windows;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Wpf
{
    public partial class CompanySelectionWindow : Window
    {
        public TallyCompanyInfo? SelectedCompany { get; private set; }

        public CompanySelectionWindow(List<TallyCompanyInfo> companies)
        {
            InitializeComponent();
            CompaniesListBox.ItemsSource = companies;
            if (companies.Count > 0)
            {
                CompaniesListBox.SelectedIndex = 0;
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedCompany = CompaniesListBox.SelectedItem as TallyCompanyInfo;
            if (SelectedCompany != null)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                System.Windows.MessageBox.Show("Please select a company from the list.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
