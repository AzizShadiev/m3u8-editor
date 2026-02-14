using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace WpfApp1 {
    public partial class EditChannelWindow : Window {
        private M3uChannel _ch;
        public bool IsDeleted { get; private set; } = false;
        public ObservableCollection<StreamSource> LocalSources { get; set; }

        public EditChannelWindow(M3uChannel ch) {
            InitializeComponent();
            _ch = ch;
            
            // Копируем источники для редактирования
            LocalSources = new ObservableCollection<StreamSource>(
                ch.Sources.Select(s => new StreamSource { 
                    Url = s.Url, 
                    IsActive = s.IsActive, 
                    StatusColor = s.StatusColor 
                })
            );

            // ФИКС: Если канал скрыт (IsHidden) и ни один поток не активен,
            // делаем первый поток активным внутри окна редактирования, чтобы его можно было проверить/включить
            if (!LocalSources.Any(s => s.IsActive) && LocalSources.Any()) {
                LocalSources[0].IsActive = true;
            }
            
            ItemsSources.ItemsSource = LocalSources;
            TxtName.Text = ch.Name;
            TxtGroup.Text = ch.GroupTitle;
            TxtLogo.Text = ch.LogoUrl;
            TxtOptions.Text = ch.RawOptions;
        }

        private void BtnAddSource_Click(object sender, RoutedEventArgs e) {
            // При добавлении нового, если список пуст, делаем его активным
            LocalSources.Add(new StreamSource { Url = "http://", IsActive = !LocalSources.Any() });
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e) {
            if (sender is Button btn && btn.DataContext is StreamSource src) {
                LocalSources.Remove(src);
                // Если удалили активный, назначаем новый активный (если есть)
                if (src.IsActive && LocalSources.Any()) LocalSources[0].IsActive = true;
            }
        }

        private void BtnPlaySource_Click(object sender, RoutedEventArgs e) {
            if (sender is Button btn && btn.DataContext is StreamSource src) {
                if (Owner is MainWindow main) main.PlayUrl(src.Url);
            }
        }

        private void BtnParseRaw_Click(object sender, RoutedEventArgs e) {
            string input = TxtRawInput.Text;
            if (string.IsNullOrWhiteSpace(input)) return;
            LocalSources.Clear();

            var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines) {
                string l = line.Trim();
                if (l.StartsWith("#EXTINF")) {
                    var match = Regex.Match(l, @"#EXTINF:-?\d+\s+(.*),(.*)");
                    if (match.Success) {
                        string attrs = match.Groups[1].Value;
                        TxtName.Text = match.Groups[2].Value.Trim();
                        TxtLogo.Text = GetAttr(attrs, "tvg-logo");
                        TxtGroup.Text = GetAttr(attrs, "group-title");
                    }
                }
                else if (l.Contains("http")) {
                    // ФИКС: Для парсинга "сырых" данных тоже проверяем активность
                    bool isCommented = l.StartsWith("#");
                    LocalSources.Add(new StreamSource { 
                        Url = l.TrimStart('#'), 
                        IsActive = !isCommented && !LocalSources.Any(s => s.IsActive) 
                    });
                }
            }
            // Если после парсинга всё закомментировано, активируем первый
            if (!LocalSources.Any(s => s.IsActive) && LocalSources.Any()) LocalSources[0].IsActive = true;
        }

        private string GetAttr(string text, string attr) => Regex.Match(text, attr + @"=""([^""]*)""").Groups[1].Value;

        private void BtnSave_Click(object sender, RoutedEventArgs e) {
            _ch.Name = TxtName.Text;
            _ch.GroupTitle = TxtGroup.Text;
            _ch.LogoUrl = TxtLogo.Text;
            _ch.RawOptions = TxtOptions.Text;
            
            _ch.Sources.Clear();
            foreach (var s in LocalSources) _ch.Sources.Add(s);

            // Если в окне редактирования мы выбрали какой-то активный поток, 
            // то логично предположить, что пользователь хочет "проявить" канал
            if (_ch.IsHidden && _ch.Sources.Any(s => s.IsActive)) {
                _ch.IsHidden = false;
            }
            
            _ = _ch.LoadImageAsync();
            DialogResult = true;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e) {
            if (MessageBox.Show("Удалить этот канал?", "Удаление", MessageBoxButton.YesNo) == MessageBoxResult.Yes) {
                IsDeleted = true; DialogResult = true;
            }
        }
    }
}