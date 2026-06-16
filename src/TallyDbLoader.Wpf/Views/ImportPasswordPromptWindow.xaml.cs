using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Wpf.Views
{
    public partial class ImportPasswordPromptWindow : Window
    {
        private readonly Dictionary<int, PasswordBox> _passwordBoxes = new();
        public Dictionary<int, string>? Results { get; private set; }

        public ImportPasswordPromptWindow(IEnumerable<ConfigImportPreviewDatabaseProfile> targetProfiles)
        {
            InitializeComponent();

            foreach (var db in targetProfiles)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = db.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var passwordBox = new PasswordBox
                {
                    Height = 28,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(passwordBox, 1);
                row.Children.Add(passwordBox);

                _passwordBoxes[db.SourceId] = passwordBox;
                PasswordsStackPanel.Children.Add(row);
            }
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            var dict = new Dictionary<int, string>();

            foreach (var kvp in _passwordBoxes)
            {
                string password = kvp.Value.Password;
                if (string.IsNullOrWhiteSpace(password))
                {
                    System.Windows.MessageBox.Show(this, "All listed database profile passwords are required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                dict[kvp.Key] = password;
            }

            Results = dict;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
