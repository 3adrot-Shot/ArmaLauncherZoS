using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class ServerStatsDialog : Window
{
    private readonly ServerMonitorService _service;
    private readonly GameServerInfo _server;
    private ServerStatsData? _stats;
    
    // Zoom & pan state
    private double _zoomLevel = 1.0;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.2;
    
    // Chart data for interaction
    private List<int> _displayData = [];
    private List<string> _displayLabels = [];
    private double _chartWidth;
    private double _chartHeight;
    private int _maxValue;
    
    public ServerStatsDialog(GameServerInfo server, HttpClient httpClient)
    {
        InitializeComponent();
        _server = server;
        _service = new ServerMonitorService(httpClient);
        
        ServerNameText.Text = server.Name;
        AddressText.Text = $"Адрес: {server.Ip}:{server.Port}";
        
        Loaded += async (_, _) => await LoadStatsAsync();
        SizeChanged += (_, _) => RedrawChart();
    }
    
    public static void Show(Window owner, GameServerInfo server, HttpClient httpClient)
    {
        var dialog = new ServerStatsDialog(server, httpClient)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }
    
    private async Task LoadStatsAsync()
    {
        try
        {
            _stats = await _service.GetServerStatsAsync(_server.Ip, _server.Port);
            
            if (_stats == null || _stats.PlayerCounts.Count == 0)
            {
                ShowError();
                return;
            }
            
            Dispatcher.Invoke(() =>
            {
                MaxPlayersText.Text = _stats.MaxPlayers.ToString();
                AvgPlayersText.Text = _stats.AvgPlayers.ToString("F1");
                
                LoadingState.Visibility = Visibility.Collapsed;
                ChartContainer.Visibility = Visibility.Visible;
                
                // Delay drawing to ensure layout is complete
                Dispatcher.BeginInvoke(new Action(RedrawChart), System.Windows.Threading.DispatcherPriority.Loaded);
            });
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[STATS] Error: {ex.Message}");
            ShowError();
        }
    }
    
    private void ShowError()
    {
        Dispatcher.Invoke(() =>
        {
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Visible;
        });
    }
    
    private void RedrawChart()
    {
        if (_stats == null) return;
        DrawChart(_stats);
    }
    
    private void DrawChart(ServerStatsData stats)
    {
        ChartCanvas.Children.Clear();
        YAxisCanvas.Children.Clear();
        XAxisCanvas.Children.Clear();
        
        var data = stats.PlayerCounts;
        var labels = stats.Labels;
        if (data.Count == 0) return;
        
        _chartWidth = ChartCanvas.ActualWidth > 0 ? ChartCanvas.ActualWidth : 750;
        _chartHeight = ChartCanvas.ActualHeight > 0 ? ChartCanvas.ActualHeight : 320;
        
        // Apply zoom - show subset of data
        var visibleCount = (int)(data.Count / _zoomLevel);
        visibleCount = Math.Max(20, Math.Min(data.Count, visibleCount));
        
        // Take last N points when zoomed in
        var startIndex = Math.Max(0, data.Count - visibleCount);
        _displayData = data.Skip(startIndex).ToList();
        _displayLabels = labels.Skip(startIndex).ToList();
        
        // Sample if still too many points
        var maxPoints = 300;
        if (_displayData.Count > maxPoints)
        {
            var step = _displayData.Count / maxPoints;
            _displayData = _displayData.Where((_, i) => i % step == 0).ToList();
            _displayLabels = _displayLabels.Where((_, i) => i % step == 0).ToList();
        }
        
        _maxValue = Math.Max(_displayData.Count > 0 ? _displayData.Max() : 1, 1);
        // Round up to nice number
        _maxValue = (int)Math.Ceiling(_maxValue / 5.0) * 5;
        if (_maxValue < 5) _maxValue = 5;
        
        // Draw Y-axis
        DrawYAxis();
        
        // Draw grid lines
        DrawGridLines();
        
        // Draw chart line and fill
        DrawChartLine();
        
        // Draw X-axis
        DrawXAxis();
        
        // Update zoom text
        ZoomText.Text = $"Масштаб: {(int)(_zoomLevel * 100)}%";
    }
    
    private void DrawYAxis()
    {
        var ySteps = 5;
        for (int i = 0; i <= ySteps; i++)
        {
            var value = (_maxValue * i) / ySteps;
            var y = _chartHeight - (i * _chartHeight / ySteps);
            
            var text = new TextBlock
            {
                Text = value.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Right,
                Width = 35
            };
            
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, y - 8);
            YAxisCanvas.Children.Add(text);
        }
    }
    
    private void DrawGridLines()
    {
        var ySteps = 5;
        for (int i = 0; i <= ySteps; i++)
        {
            var y = (i * _chartHeight) / ySteps;
            var line = new Line
            {
                X1 = 0,
                Y1 = y,
                X2 = _chartWidth,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                StrokeThickness = 1
            };
            ChartCanvas.Children.Add(line);
        }
    }
    
    private void DrawChartLine()
    {
        if (_displayData.Count == 0) return;
        
        var points = new PointCollection();
        var pointWidth = _chartWidth / Math.Max(1, _displayData.Count - 1);
        
        for (int i = 0; i < _displayData.Count; i++)
        {
            var x = i * pointWidth;
            var y = _chartHeight - (_displayData[i] * _chartHeight / _maxValue);
            points.Add(new Point(x, Math.Max(0, Math.Min(_chartHeight, y))));
        }
        
        // Draw fill area
        if (points.Count > 0)
        {
            var fillPoints = new PointCollection(points);
            fillPoints.Add(new Point(_chartWidth, _chartHeight));
            fillPoints.Add(new Point(0, _chartHeight));
            
            var fillPolygon = new Polygon
            {
                Points = fillPoints,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(100, 99, 102, 241),
                    Color.FromArgb(10, 99, 102, 241),
                    90)
            };
            ChartCanvas.Children.Add(fillPolygon);
        }
        
        // Draw line
        var polyline = new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromRgb(99, 102, 241)),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round
        };
        ChartCanvas.Children.Add(polyline);
        
        // Draw points at data positions (only if not too many)
        if (_displayData.Count <= 50)
        {
            foreach (var point in points)
            {
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.FromRgb(99, 102, 241)),
                    Stroke = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    StrokeThickness = 2
                };
                Canvas.SetLeft(dot, point.X - 3);
                Canvas.SetTop(dot, point.Y - 3);
                ChartCanvas.Children.Add(dot);
            }
        }
    }
    
    private void DrawXAxis()
    {
        if (_displayLabels.Count == 0) return;
        
        // Show 5-7 time labels evenly distributed
        var labelCount = Math.Min(7, _displayLabels.Count);
        var step = Math.Max(1, _displayLabels.Count / labelCount);
        var pointWidth = _chartWidth / Math.Max(1, _displayLabels.Count - 1);
        
        for (int i = 0; i < _displayLabels.Count; i += step)
        {
            var x = i * pointWidth;
            var timeText = FormatDateTimeShort(_displayLabels[i]);
            
            var text = new TextBlock
            {
                Text = timeText,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                FontSize = 10
            };
            
            // Measure text width for centering
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var textWidth = text.DesiredSize.Width;
            
            Canvas.SetLeft(text, Math.Max(0, Math.Min(_chartWidth - textWidth, x - textWidth / 2)));
            Canvas.SetTop(text, 5);
            XAxisCanvas.Children.Add(text);
        }
    }
    
    private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_displayData.Count == 0 || _chartWidth <= 0) return;
        
        var pos = e.GetPosition(ChartCanvas);
        var pointWidth = _chartWidth / Math.Max(1, _displayData.Count - 1);
        var index = (int)Math.Round(pos.X / pointWidth);
        index = Math.Max(0, Math.Min(_displayData.Count - 1, index));
        
        // Update tooltip content first
        TooltipTime.Text = FormatDateTime(_displayLabels[index]);
        TooltipPlayers.Text = _displayData[index].ToString();
        
        // Make visible and force layout update
        TooltipBorder.Visibility = Visibility.Visible;
        TooltipBorder.UpdateLayout();
        
        // Get actual rendered size
        var tooltipWidth = TooltipBorder.ActualWidth > 0 ? TooltipBorder.ActualWidth : 140;
        var tooltipHeight = TooltipBorder.ActualHeight > 0 ? TooltipBorder.ActualHeight : 50;
        
        const double padding = 10;
        const double cursorOffset = 15;
        
        // Start by trying to center tooltip horizontally over cursor
        var tooltipX = pos.X - tooltipWidth / 2;
        
        // Clamp to stay within chart bounds
        tooltipX = Math.Max(padding, Math.Min(_chartWidth - tooltipWidth - padding, tooltipX));
        
        // Position vertically - prefer above cursor
        var tooltipY = pos.Y - tooltipHeight - cursorOffset;
        
        // If not enough space above, position below
        if (tooltipY < padding)
        {
            tooltipY = pos.Y + cursorOffset;
        }
        
        // Final Y clamp
        tooltipY = Math.Max(padding, Math.Min(_chartHeight - tooltipHeight - padding, tooltipY));
        
        // Apply position using Canvas attached properties
        Canvas.SetLeft(TooltipBorder, tooltipX);
        Canvas.SetTop(TooltipBorder, tooltipY);
        
        // Update cursor line at data point position
        var dataX = index * pointWidth;
        CursorLine.X1 = dataX;
        CursorLine.X2 = dataX;
        CursorLine.Y1 = 0;
        CursorLine.Y2 = _chartHeight;
        CursorLine.Visibility = Visibility.Visible;
    }
    
    private void ChartCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        TooltipBorder.Visibility = Visibility.Collapsed;
        CursorLine.Visibility = Visibility.Collapsed;
    }
    
    private void ChartCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_stats == null) return;
        
        var oldZoom = _zoomLevel;
        
        if (e.Delta > 0)
            _zoomLevel = Math.Min(MaxZoom, _zoomLevel + ZoomStep);
        else
            _zoomLevel = Math.Max(MinZoom, _zoomLevel - ZoomStep);
        
        if (Math.Abs(oldZoom - _zoomLevel) > 0.01)
        {
            RedrawChart();
        }
        
        e.Handled = true;
    }
    
    private static string FormatDateTime(string dt)
    {
        if (DateTime.TryParse(dt, out var parsed))
        {
            return parsed.ToString("dd.MM.yyyy HH:mm");
        }
        return dt;
    }
    
    private static string FormatDateTimeShort(string dt)
    {
        if (DateTime.TryParse(dt, out var parsed))
        {
            return parsed.ToString("dd.MM\nHH:mm");
        }
        return dt;
    }
    
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
