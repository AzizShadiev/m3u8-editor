using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace WpfApp1;

public class StreamSource : INotifyPropertyChanged {
    private string _url;
    public string Url { get => _url; set { _url = value; OnPropertyChanged(); } }
    
    private Brush _statusColor = Brushes.Transparent;
    public Brush StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }
    
    private bool _isActive;
    public bool IsActive { get => _isActive; set { _isActive = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class M3uChannel : INotifyPropertyChanged {
    private string _name;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

    public string GroupTitle { get; set; }
    public string TvgId { get; set; }
    public string LogoUrl { get; set; } = "";
    public string RawOptions { get; set; } = "";
    public string HeaderComment { get; set; } = "";
    public string OtherAttributes { get; set; } = "";
    
    public ObservableCollection<StreamSource> Sources { get; set; } = new();
    public string StreamUrl => Sources.FirstOrDefault(s => s.IsActive)?.Url ?? Sources.FirstOrDefault()?.Url ?? "";

    private bool _isHidden;
    public bool IsHidden { 
        get => _isHidden; 
        set { _isHidden = value; OnPropertyChanged(); } 
    }

    private Brush _statusColor = Brushes.Transparent;
    public Brush StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }

    // НОВОЕ: Свойство для индикации наличия живых резервных потоков
    private bool _hasBackup;
    public bool HasBackup {
        get => _hasBackup;
        set { _hasBackup = value; OnPropertyChanged(); }
    }

    private BitmapSource _displayLogo;
    public BitmapSource DisplayLogo { get => _displayLogo; set { _displayLogo = value; OnPropertyChanged(); } }
    
    private static SemaphoreSlim _semaphore = new SemaphoreSlim(8);

    public async Task LoadImageAsync() {
        if (string.IsNullOrWhiteSpace(LogoUrl) || !LogoUrl.StartsWith("http")) { DisplayLogo = null; return; }
        await _semaphore.WaitAsync();
        try {
            byte[] data;
            using (var client = new WebClient()) {
                client.Headers.Add("user-agent", "Mozilla/5.0");
                data = await client.DownloadDataTaskAsync(LogoUrl);
            }

            await Application.Current.Dispatcher.InvokeAsync(() => {
                using (var stream = new MemoryStream(data)) {
                    try {
                        if (LogoUrl.ToLower().Contains(".svg")) {
                            var svg = new SKSvg(); 
                            svg.Load(stream);
                            DisplayLogo = ConvertSkPictureToBitmap(svg.Picture);
                        } else {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = stream;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat; 
                            bitmap.EndInit();

                            if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32) {
                                DisplayLogo = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                            } else {
                                DisplayLogo = bitmap;
                            }
                        }
                    } catch { DisplayLogo = null; }
                    
                    if (DisplayLogo != null) DisplayLogo.Freeze();
                }
            });
        } catch { DisplayLogo = null; } finally { _semaphore.Release(); }
    }

    private BitmapSource ConvertSkBitmapToBitmapSource(SKBitmap skBitmap) {
        using (var image = SKImage.FromBitmap(skBitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100)) {
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); 
            bitmap.StreamSource = data.AsStream(); 
            bitmap.CacheOption = BitmapCacheOption.OnLoad; 
            bitmap.EndInit();
            return bitmap;
        }
    }

    private BitmapSource ConvertSkPictureToBitmap(SKPicture picture) {
        int w = (int)(picture.CullRect.Width > 0 ? picture.CullRect.Width : 128);
        int h = (int)(picture.CullRect.Height > 0 ? picture.CullRect.Height : 128);
        
        using (var skBmp = new SKBitmap(w, h))
        using (var canvas = new SKCanvas(skBmp)) {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawPicture(picture);
            return ConvertSkBitmapToBitmapSource(skBmp);
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}