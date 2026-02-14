using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using LibVLCSharp.Shared;

namespace WpfApp1;

public partial class MainWindow : Window, INotifyPropertyChanged {
    public ObservableCollection<M3uChannel> Channels { get; set; } = new();
    private ICollectionView _view;
    private bool _isDirty = false;
    private string _m3uHeader = "#EXTM3U";
    private double _currentItemWidth = 180;
    private CancellationTokenSource _checkerCts;
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private LibVLC _libVLC;
    private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

    public double CurrentItemWidth { get => _currentItemWidth; set { _currentItemWidth = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentItemHeight)); } }
    public double CurrentItemHeight => CurrentItemWidth * 0.95;

    public MainWindow() {
        LibVLCSharp.Shared.Core.Initialize();
        _libVLC = new LibVLC();
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
        InitializeComponent();
        VideoPlayer.MediaPlayer = _mediaPlayer;
        this.DataContext = this;
        _view = CollectionViewSource.GetDefaultView(Channels);
        _view.Filter = item => {
            if (string.IsNullOrWhiteSpace(TxtSearch.Text)) return true;
            var ch = (M3uChannel)item;
            return ch.Name.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) || 
                   (ch.GroupTitle != null && ch.GroupTitle.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase));
        };
        ChannelsList.ItemsSource = _view;
        Channels.CollectionChanged += (s, e) => { _isDirty = true; UpdateStats(); };
    }

    public void PlayUrl(string url) {
        if (string.IsNullOrWhiteSpace(url)) return;
        try {
            PlayerColumn.Width = new GridLength(350);
            TxtPlayingUrl.Text = url;
            using var media = new Media(_libVLC, new Uri(url));
            _mediaPlayer.Play(media);
        } catch { TxtStatus.Text = "Ошибка воспроизведения"; }
    }

    private void ChannelsList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (ChannelsList.SelectedItem is M3uChannel ch) {
            TxtPlayingName.Text = ch.Name; 
            TxtPlayingGroup.Text = ch.GroupTitle;
            PlayUrl(ch.StreamUrl);
        }
    }

    private void MenuHideChannel_Click(object sender, RoutedEventArgs e) {
        if (ChannelsList.SelectedItem is M3uChannel ch) { 
            ch.IsHidden = !ch.IsHidden; 
            _isDirty = true; 
        }
    }

    private void MenuDeleteChannel_Click(object sender, RoutedEventArgs e) {
        if (ChannelsList.SelectedItem is M3uChannel s) {
            if (MessageBox.Show($"Удалить {s.Name}?", "Удаление", MessageBoxButton.YesNo) == MessageBoxResult.Yes) Channels.Remove(s);
        }
    }

    private void BtnClosePlayer_Click(object sender, RoutedEventArgs e) {
        _mediaPlayer.Stop(); PlayerColumn.Width = new GridLength(0); ChannelsList.SelectedItem = null;
    }

    private async void BtnCheck_Click(object sender, RoutedEventArgs e) {
        if (_checkerCts != null) _checkerCts.Cancel();
        _checkerCts = new CancellationTokenSource();
        var token = _checkerCts.Token;
        TxtStatus.Text = "Проверка...";
        var sem = new SemaphoreSlim(10);
        
        var tasks = Channels.Select(async ch => {
            try {
                await sem.WaitAsync(token);
                bool activeSuccess = false;
                bool backupSuccess = false;

                foreach (var src in ch.Sources) {
                    src.StatusColor = Brushes.Orange;
                    bool ok = await CheckUrlAsync(src.Url, token);
                    src.StatusColor = ok ? Brushes.LimeGreen : Brushes.Red;
                    
                    if (ok) {
                        if (src.IsActive) activeSuccess = true;
                        else backupSuccess = true;
                    }
                }

                // ФИКС: Точка канала (StatusColor) теперь зависит ТОЛЬКО от активного потока
                ch.StatusColor = activeSuccess ? Brushes.LimeGreen : Brushes.Red;
                
                // ФИКС: Указываем, есть ли рабочие резервы (HasBackup)
                ch.HasBackup = backupSuccess;

            } catch { 
                ch.StatusColor = Brushes.Red; 
                ch.HasBackup = false;
            } 
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks); 
        TxtStatus.Text = "Готово";
    }

    private async Task<bool> CheckUrlAsync(string url, CancellationToken ct) {
        try {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return res.IsSuccessStatusCode;
        } catch { return false; }
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog { Filter = "M3U8|*.m3u8" };
        if (dlg.ShowDialog() == true) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] rawBytes = File.ReadAllBytes(dlg.FileName);
            string text;
            if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF) {
                text = Encoding.UTF8.GetString(rawBytes);
            } else {
                string utf8Text = Encoding.UTF8.GetString(rawBytes);
                bool hasCyrillic = Regex.IsMatch(utf8Text, @"[а-яА-ЯёЁ]");
                text = (!hasCyrillic && rawBytes.Any(b => b > 127)) ? Encoding.GetEncoding(1251).GetString(rawBytes) : utf8Text;
            }
            Channels.Clear();
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int startLine = 0;
            for (int k = 0; k < lines.Length; k++) {
                if (lines[k].Contains("#EXTM3U")) { _m3uHeader = lines[k].Trim(); startLine = k + 1; break; }
            }
            for (int i = startLine; i < lines.Length; i++) {
                if (lines[i].Trim().StartsWith("#EXTINF")) {
                    var ch = new M3uChannel();
                    if (i > 0) {
                        string prev = lines[i - 1].Trim();
                        if (prev.StartsWith("#") && !prev.StartsWith("#EXT") && !prev.StartsWith("#http")) ch.HeaderComment = prev;
                    }
                    var m = Regex.Match(lines[i], @"#EXTINF:-?\d+\s+(.*),(.*)");
                    if (m.Success) {
                        string attr = m.Groups[1].Value;
                        ch.Name = m.Groups[2].Value.Trim();
                        ch.LogoUrl = Regex.Match(attr, @"tvg-logo=""([^""]*)""").Groups[1].Value;
                        ch.GroupTitle = Regex.Match(attr, @"group-title=""([^""]*)""").Groups[1].Value;
                        ch.TvgId = Regex.Match(attr, @"tvg-id=""([^""]*)""").Groups[1].Value;
                        string rem = attr;
                        string[] toRemove = { @"tvg-logo=""[^""]*""\s?", @"group-title=""[^""]*""\s?", @"tvg-id=""[^""]*""\s?", @"tvg-name=""[^""]*""\s?" };
                        foreach (var pattern in toRemove) rem = Regex.Replace(rem, pattern, "");
                        ch.OtherAttributes = rem.Trim();
                    }
                    int j = i + 1;
                    bool hasVisibleUrl = false;
                    while (j < lines.Length && !lines[j].Trim().StartsWith("#EXTINF")) {
                        string cur = lines[j].Trim();
                        if (!string.IsNullOrEmpty(cur)) {
                            if (cur.StartsWith("#") && !cur.StartsWith("#EXT") && !cur.StartsWith("#http")) break;
                            if (cur.StartsWith("#EXTVLCOPT:")) ch.RawOptions += cur + Environment.NewLine;
                            else if (cur.Contains("http")) {
                                bool isCommented = cur.StartsWith("#");
                                if (!isCommented) hasVisibleUrl = true;
                                
                                ch.Sources.Add(new StreamSource { 
                                    Url = cur.TrimStart('#'), 
                                    IsActive = !isCommented && !ch.Sources.Any(s => s.IsActive) 
                                });
                            }
                        }
                        j++;
                    }
                    ch.IsHidden = !hasVisibleUrl;
                    Channels.Add(ch); 
                    _ = ch.LoadImageAsync(); 
                    i = j - 1;
                }
            }
            _isDirty = false; UpdateStats();
        }
    }

    private bool SaveFile() {
        var dlg = new SaveFileDialog { Filter = "M3U8 (*.m3u8)|*.m3u8" };
        if (dlg.ShowDialog() == true) {
            var encodingWithBom = new UTF8Encoding(true);
            var sb = new StringBuilder(_m3uHeader);
            foreach (var ch in Channels) {
                sb.AppendLine();
                sb.AppendLine(!string.IsNullOrEmpty(ch.HeaderComment) ? ch.HeaderComment : $"#-----{ch.Name}-----");
                string a = $"tvg-id=\"{ch.TvgId}\" tvg-logo=\"{ch.LogoUrl}\" group-title=\"{ch.GroupTitle}\" tvg-name=\"{ch.Name}\"";
                if (!string.IsNullOrEmpty(ch.OtherAttributes)) a += " " + ch.OtherAttributes.Trim();
                sb.AppendLine($"#EXTINF:-1 {a.Trim()},{ch.Name}");
                if (!string.IsNullOrEmpty(ch.RawOptions)) sb.Append(ch.RawOptions.Trim() + Environment.NewLine);
                
                foreach (var src in ch.Sources) {
                    bool commentIt = ch.IsHidden || !src.IsActive;
                    sb.AppendLine((commentIt ? "#" : "") + src.Url.Trim());
                }
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), encodingWithBom);
            _isDirty = false; TxtStatus.Text = "Сохранено"; return true;
        }
        return false;
    }

    private void UpdateStats() => TxtTotalCount.Text = Channels.Count.ToString();
    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();
    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveFile();

    private void BtnRenameGroup_Click(object sender, RoutedEventArgs e) {
        var groups = Channels.Select(c => c.GroupTitle).Distinct().Where(g => !string.IsNullOrEmpty(g)).OrderBy(g => g).ToList();
        var win = new RenameGroupWindow(groups) { Owner = this };
        if (win.ShowDialog() == true) {
            foreach (var ch in Channels.Where(c => c.GroupTitle == win.OldGroup)) ch.GroupTitle = win.NewGroup;
            _isDirty = true; _view.Refresh();
        }
    }

    private void ComboSort_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_view == null) return;
        _view.SortDescriptions.Clear();
        var sel = (ComboSort.SelectedItem as ComboBoxItem)?.Content.ToString();
        if (sel == "По алфавиту (А-Я)") _view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        else if (sel == "По категориям") {
            _view.SortDescriptions.Add(new SortDescription("GroupTitle", ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        }
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) { if (CurrentItemWidth < 500) CurrentItemWidth += 20; LblZoom.Text = CurrentItemWidth.ToString(); }
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) { if (CurrentItemWidth > 80) CurrentItemWidth -= 20; LblZoom.Text = CurrentItemWidth.ToString(); }
    
    private void ChannelsList_PreviewMouseMove(object sender, MouseEventArgs e) {
        if (e.LeftButton == MouseButtonState.Pressed && ComboSort.SelectedIndex == 0) {
            var item = GetItemAtPos(e.GetPosition(ChannelsList));
            if (item != null) DragDrop.DoDragDrop(ChannelsList, item, DragDropEffects.Move);
        }
    }

    private void ChannelsList_Drop(object sender, DragEventArgs e) {
        var dropped = e.Data.GetData(typeof(M3uChannel)) as M3uChannel;
        var target = GetItemAtPos(e.GetPosition(ChannelsList));
        if (dropped != null && target != null && dropped != target) {
            int oldI = Channels.IndexOf(dropped); int newI = Channels.IndexOf(target);
            Channels.Move(oldI, newI);
        }
    }

    private M3uChannel GetItemAtPos(Point pos) {
        var res = VisualTreeHelper.HitTest(ChannelsList, pos);
        if (res == null) return null;
        DependencyObject dep = res.VisualHit;
        while (dep != null && !(dep is ListBoxItem)) dep = VisualTreeHelper.GetParent(dep);
        return (dep as ListBoxItem)?.Content as M3uChannel;
    }

    private void ChannelsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
        if (ChannelsList.SelectedItem is M3uChannel s) {
            var win = new EditChannelWindow(s) { Owner = this };
            if (win.ShowDialog() == true) { if (win.IsDeleted) Channels.Remove(s); _view.Refresh(); }
        }
    }

    private void ChannelsList_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Delete) MenuDeleteChannel_Click(null, null);
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e) {
        var ch = new M3uChannel { Name = "Новый канал" };
        if (new EditChannelWindow(ch) { Owner = this }.ShowDialog() == true) Channels.Add(ch);
    }

    protected override void OnClosing(CancelEventArgs e) {
        if (_isDirty) {
            var res = MessageBox.Show("Сохранить изменения?", "Выход", MessageBoxButton.YesNoCancel);
            if (res == MessageBoxResult.Yes) { if (!SaveFile()) e.Cancel = true; } else if (res == MessageBoxResult.Cancel) e.Cancel = true;
        }
        _mediaPlayer?.Stop(); _mediaPlayer?.Dispose(); _libVLC?.Dispose();
        base.OnClosing(e);
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}