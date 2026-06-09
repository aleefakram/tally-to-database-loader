using System.Windows;

namespace TallyDbLoader.Wpf.Views
{
    public partial class ResolveSafetyBlockWindow : Window
    {
        public string Reason { get; private set; } = string.Empty;

        public ResolveSafetyBlockWindow(string companyName)
        {
            InitializeComponent();
            SubtitleText.Text = $"Please enter the reason for resolving the safety block on '{companyName}' to continue. An immutable audit record will be logged.";
            ReasonTextBox.Focus();
        }

        private void ResolveButton_Click(object sender, RoutedEventArgs e)
        {
            string txt = ReasonTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(txt))
            {
                System.Windows.MessageBox.Show("Reason is required to resolve a safety block.", "Reason Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reason = txt;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
