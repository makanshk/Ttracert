using Mapsui;
using Mapsui.Layers;
using Mapsui.Limiting;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using SystemMessageBox = System.Windows.MessageBox;

namespace Ttracert
{
    public partial class MainWindow : FluentWindow
    {
        private readonly ObservableCollection<HopInfo> _hops = new();

        private readonly MemoryLayer _pointsLayer = new("Points");
        private readonly MemoryLayer _lineLayer = new("Lines");
        private readonly MemoryLayer _highlightLayer = new("Highlight"); // 🔧 Слой для подсветки

        private readonly HttpClient _httpClient = new();
        private readonly List<MPoint> _routePoints = new();

        private readonly List<IFeature> _pointFeatures = new();
        private readonly List<IFeature> _lineFeatures = new();
        private readonly List<IFeature> _highlightFeatures = new(); // 🔧 Для выделенной точки

        public MainWindow()
        {
            InitializeComponent();
            InitializeMap();
            HopsList.ItemsSource = _hops;
        }

        private void InitializeMap()
        {
            MapControl.Map.Layers.Clear();
            var osmLayer = OpenStreetMap.CreateTileLayer();
            MapControl.Map.Layers.Add(osmLayer);

            osmLayer.Attribution = null;                  // Удаляет текст атрибуции

            // 🔧 СТИЛЬ ЛИНИИ
            _lineLayer.Style = new VectorStyle
            {
                Line = new Pen
                {
                    Color = Color.FromString("#00CCFF"),
                    Width = 4,
                    PenStyle = PenStyle.Solid,
                    PenStrokeCap = PenStrokeCap.Round,
                    StrokeJoin = StrokeJoin.Round
                }
            };

            // 🔧 СТИЛЬ ТОЧЕК
            _pointsLayer.Style = new SymbolStyle
            {
                Fill = new Brush(Color.FromString("#00CCFF")),
                SymbolScale = 0.5,
                Outline = new Pen(Color.White, 2)
            };

            // 🔧 СТИЛЬ ВЫДЕЛЕННОЙ ТОЧКИ
            _highlightLayer.Style = new SymbolStyle
            {
                Fill = new Brush(Color.FromString("#FF4444")),
                SymbolScale = 1.2,
                Outline = new Pen(Color.White, 3)
            };

            _pointFeatures.Clear();
            _lineFeatures.Clear();
            _highlightFeatures.Clear();
            _pointsLayer.Features = _pointFeatures;
            _lineLayer.Features = _lineFeatures;
            _highlightLayer.Features = _highlightFeatures;

            MapControl.Map.Layers.Add(_lineLayer);
            MapControl.Map.Layers.Add(_pointsLayer);
            MapControl.Map.Layers.Add(_highlightLayer);

            var (x, y) = SphericalMercator.FromLonLat(27.56, 53.9);
            MapControl.Map.Navigator.CenterOn(new MPoint(x, y));

            // 🔧 НАЧАЛЬНЫЙ ЗУМ
            MapControl.Map.Navigator.ZoomTo(1000);
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string target = TargetBox.Text;
            if (string.IsNullOrWhiteSpace(target)) return;

            _hops.Clear();
            _routePoints.Clear();

            await Dispatcher.InvokeAsync(() =>
            {
                _pointFeatures.Clear();
                _lineFeatures.Clear();
                _highlightFeatures.Clear();
                _pointsLayer.Features = _pointFeatures;
                _lineLayer.Features = _lineFeatures;
                _highlightLayer.Features = _highlightFeatures;
                _pointsLayer.DataHasChanged();
                _lineLayer.DataHasChanged();
                _highlightLayer.DataHasChanged();
                MapControl.Refresh();
            });

            LoadingRing.Visibility = Visibility.Visible;
            StartButton.IsEnabled = false;

            try
            {
                var selfGeo = await GetLocation("");
                if (selfGeo != null && selfGeo.status == "success")
                {
                    await AddStepToMapAsync(selfGeo.lat, selfGeo.lon, "Мой компьютер", isStartingPoint: true);
                }

                await RunTraceroute(target);
            }
            catch (Exception ex)
            {
                SystemMessageBox.Show($"Ошибка: {ex.Message}");
            }
            finally
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                StartButton.IsEnabled = true;
            }
        }

        private async Task RunTraceroute(string host)
        {
            const int maxHops = 30;
            const int timeout = 2000;
            byte[] buffer = Encoding.ASCII.GetBytes("ping");
            using var ping = new Ping();

            // Список для хранения всех найденных координат, чтобы перерисовывать путь корректно
            var hopDataMap = new SortedDictionary<int, MPoint>();

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                var options = new PingOptions(ttl, true);
                var watch = System.Diagnostics.Stopwatch.StartNew(); // Замеряем время

                try
                {
                    var reply = await ping.SendPingAsync(host, timeout, buffer, options);
                    watch.Stop();

                    var hop = new HopInfo
                    {
                        HopNumber = ttl,
                        IpAddress = reply.Address?.ToString() ?? "*",
                        ResponseTime = reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired ? watch.ElapsedMilliseconds : 0
                    };

                    if (reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.Success)
                    {
                        var geo = await GetLocation(hop.IpAddress);
                        if (geo != null && geo.status == "success")
                        {
                            hop.Location = $"{geo.city}, {geo.country}";
                            hop.Latitude = geo.lat;
                            hop.Longitude = geo.lon;

                            // Добавляем координаты в словарь для построения последовательной линии
                            var (x, y) = SphericalMercator.FromLonLat(geo.lon, geo.lat);
                            hopDataMap[ttl] = new MPoint(x, y);

                            // Обновляем карту, передавая актуальный список точек в правильном порядке
                            await UpdateRouteOnMapAsync(hopDataMap.Values.ToList());
                        }
                    }

                    _hops.Add(hop);
                    if (reply.Status == IPStatus.Success) break;
                }
                catch
                {
                    _hops.Add(new HopInfo { HopNumber = ttl, IpAddress = "Request Timed Out" });
                }
            }
        }

        // Новый метод для точной отрисовки последовательности
        private async Task UpdateRouteOnMapAsync(List<MPoint> orderedPoints)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _pointFeatures.Clear();
                _lineFeatures.Clear();

                // 1. Отрисовка всех точек
                foreach (var point in orderedPoints)
                {
                    var feature = new PointFeature(point);
                    _pointFeatures.Add(feature);
                }
                _pointsLayer.Features = _pointFeatures;

                // 2. Отрисовка линии строго по порядку
                if (orderedPoints.Count > 1)
                {
                    var coords = orderedPoints.Select(p => new Coordinate(p.X, p.Y)).ToArray();
                    var lineString = new LineString(coords);
                    _lineFeatures.Add(new GeometryFeature { Geometry = lineString });
                    _lineLayer.Features = _lineFeatures;
                }

                _pointsLayer.DataHasChanged();
                _lineLayer.DataHasChanged();
                MapControl.Refresh();
            });
        }

        private async Task<IpGeoResponse?> GetLocation(string ip)
        {
            try
            {
                if (!string.IsNullOrEmpty(ip) && (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.16.")))
                    return null;

                string url = string.IsNullOrEmpty(ip) ? "http://ip-api.com/json/" : $"http://ip-api.com/json/{ip}";
                return await _httpClient.GetFromJsonAsync<IpGeoResponse>(url);
            }
            catch { return null; }
        }

        private async Task AddStepToMapAsync(double lat, double lon, string label, bool isStartingPoint = false)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                var (x, y) = SphericalMercator.FromLonLat(lon, lat);
                var mPoint = new MPoint(x, y);

                if (_routePoints.Count > 0 &&
                    Math.Abs(_routePoints.Last().X - mPoint.X) < 0.001 &&
                    Math.Abs(_routePoints.Last().Y - mPoint.Y) < 0.001)
                    return;

                _routePoints.Add(mPoint);

                // 🔧 Добавляем точку
                var pointFeature = new PointFeature(mPoint);
                pointFeature.Styles.Add(new SymbolStyle
                {
                    Fill = new Brush(Color.FromString("#00CCFF")),
                    SymbolScale = 0.5,
                    Outline = new Pen(Color.White, 2)
                });
                _pointFeatures.Add(pointFeature);
                _pointsLayer.Features = _pointFeatures;

                // 🔧 Обновляем линию
                if (_routePoints.Count > 1)
                {
                    _lineFeatures.Clear();

                    var coords = _routePoints.Select(p => new Coordinate(p.X, p.Y)).ToArray();
                    var lineString = new LineString(coords);
                    var lineFeature = new GeometryFeature { Geometry = lineString };

                    _lineFeatures.Add(lineFeature);
                    _lineLayer.Features = _lineFeatures;
                }

                _pointsLayer.DataHasChanged();
                _lineLayer.DataHasChanged();

                MapControl.Refresh();

                // 🔧 Анимация только для первой точки (стартовой)
                if (isStartingPoint)
                {
                    MapControl.Map.Navigator.FlyTo(mPoint, MapControl.Map.Navigator.Viewport.Resolution, 500);
                }
            });
        }

        // 🔧 НОВОЕ: Обработка выбора элемента в ListView
        private void HopsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HopsList.SelectedItem is HopInfo selectedHop)
            {
                // 🔧 Проверяем есть ли координаты у выбранного хопа
                if (selectedHop.Latitude != 0 && selectedHop.Longitude != 0)
                {
                    HighlightHopOnMap(selectedHop.Latitude, selectedHop.Longitude);
                }
                else
                {

                }
            }
        }

        // 🔧 НОВОЕ: Подсветка выбранного хопа на карте
        private void HighlightHopOnMap(double lat, double lon)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var (x, y) = SphericalMercator.FromLonLat(lon, lat);
                var mPoint = new MPoint(x, y);

                // 🔧 Очищаем предыдущую подсветку
                _highlightFeatures.Clear();
                _highlightLayer.Features = _highlightFeatures;

                // 🔧 Создаём новую выделенную точку
                var highlightFeature = new PointFeature(mPoint);
                highlightFeature.Styles.Add(new SymbolStyle
                {
                    Fill = new Brush(Color.FromString("#FF4444")), // 🔧 Красный цвет
                    SymbolScale = 1.0, // 🔧 Больше обычных точек
                    Outline = new Pen(Color.White, 3)
                });
                _highlightFeatures.Add(highlightFeature);
                _highlightLayer.Features = _highlightFeatures;

                _highlightLayer.DataHasChanged();
                MapControl.Refresh();
            });
        }
    }

    public record IpGeoResponse(string status, string country, string city, double lat, double lon);
    public class HopInfo
    {
        public int HopNumber { get; set; }
        public string IpAddress { get; set; } = "";
        public string Location { get; set; } = "---";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public long ResponseTime { get; set; } // ⏱️ Время в мс
    }
}