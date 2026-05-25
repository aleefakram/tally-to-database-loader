using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TallyDbLoader.Wpf.Helpers
{
    public static class RichTextBoxHelper
    {
        public static readonly DependencyProperty LogTextProperty =
            DependencyProperty.RegisterAttached("LogText", typeof(string), typeof(RichTextBoxHelper),
                new FrameworkPropertyMetadata(string.Empty, OnLogTextChanged));

        public static string GetLogText(DependencyObject d) => (string)d.GetValue(LogTextProperty);
        public static void SetLogText(DependencyObject d, string value) => d.SetValue(LogTextProperty, value);

        private static readonly DependencyProperty LastTextLengthProperty =
            DependencyProperty.RegisterAttached("LastTextLength", typeof(int), typeof(RichTextBoxHelper),
                new PropertyMetadata(0));

        public static int GetLastTextLength(DependencyObject d) => (int)d.GetValue(LastTextLengthProperty);
        public static void SetLastTextLength(DependencyObject d, int value) => d.SetValue(LastTextLengthProperty, value);

        private static void OnLogTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is System.Windows.Controls.RichTextBox box)
            {
                var text = e.NewValue as string ?? string.Empty;
                if (box.Document == null)
                {
                    box.Document = new FlowDocument();
                }

                int lastLength = GetLastTextLength(box);

                // If text was cleared or is shorter, clear document and start fresh
                if (text.Length < lastLength || lastLength == 0)
                {
                    box.Document.Blocks.Clear();
                    lastLength = 0;
                }

                if (text.Length > lastLength)
                {
                    string newText = text.Substring(lastLength);
                    var lines = newText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    
                    Paragraph? p = box.Document.Blocks.LastBlock as Paragraph;
                    if (p == null)
                    {
                        p = new Paragraph();
                        box.Document.Blocks.Add(p);
                    }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (i > 0)
                        {
                            p.Inlines.Add(new LineBreak());
                        }

                        if (string.IsNullOrEmpty(line))
                            continue;

                        var run = new Run(line);
                        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                            run.Foreground = System.Windows.Media.Brushes.Crimson;
                        else if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
                            run.Foreground = System.Windows.Media.Brushes.Goldenrod;
                        else
                            run.Foreground = System.Windows.Media.Brushes.LightGray;

                        p.Inlines.Add(run);
                    }

                    SetLastTextLength(box, text.Length);
                    box.ScrollToEnd();
                }
            }
        }
    }
}
