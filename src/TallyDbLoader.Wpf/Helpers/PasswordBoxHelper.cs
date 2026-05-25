using System.Windows;
using System.Windows.Controls;

namespace TallyDbLoader.Wpf.Helpers
{
    public static class PasswordBoxHelper
    {
        public static readonly DependencyProperty BoundPasswordProperty =
            DependencyProperty.RegisterAttached("BoundPassword", typeof(string), typeof(PasswordBoxHelper),
                new FrameworkPropertyMetadata(string.Empty, OnBoundPasswordChanged));

        public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
        public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

        public static readonly DependencyProperty BindBehaviorProperty =
            DependencyProperty.RegisterAttached("BindBehavior", typeof(bool), typeof(PasswordBoxHelper),
                new PropertyMetadata(false, OnBindBehaviorChanged));

        public static bool GetBindBehavior(DependencyObject d) => (bool)d.GetValue(BindBehaviorProperty);
        public static void SetBindBehavior(DependencyObject d, bool value) => d.SetValue(BindBehaviorProperty, value);

        private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PasswordBox box)
            {
                box.PasswordChanged -= HandlePasswordChanged;
                if (e.NewValue as string != box.Password)
                {
                    box.Password = (e.NewValue as string) ?? string.Empty;
                }
                box.PasswordChanged += HandlePasswordChanged;
            }
        }

        private static void OnBindBehaviorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PasswordBox box)
            {
                if ((bool)e.NewValue)
                {
                    box.PasswordChanged += HandlePasswordChanged;
                }
                else
                {
                    box.PasswordChanged -= HandlePasswordChanged;
                }
            }
        }

        private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box)
            {
                SetBoundPassword(box, box.Password);
            }
        }
    }
}
