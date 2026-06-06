using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class MovieRelationView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly MainWindow? _mainWindow;
    private List<MovieNode> _nodes = new();
    private List<MovieLink> _links = new();
    private int? _dragNodeId;
    private Point _dragOffset;
    private bool _isDragging;
    private Point _lastMousePos;
    private bool _isPanning;
    private Random _rng = new();

    private const double NodeRadius = 28;
    private const double Repulsion = 8000;
    private const double Attraction = 0.005;
    private const double Damping = 0.85;
    private const double IdealLength = 160;
    private const double CanvasPadding = 80; // 画布边距

    private bool _centered;

    public MovieRelationView(MainWindow? mainWindow = null)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _context = DbHelper.CreateContext();
        Loaded += async (s, e) => await LoadDataAsync();
        GraphContainer.SizeChanged += (s, e) => { if (_centered && _nodes.Count > 0) ApplyTransform(); };
    }

    private async Task LoadDataAsync()
    {
        var movies = await _context.Movies
            .Include(m => m.MovieTags).ThenInclude(mt => mt.Tag)
            .Where(m => !string.IsNullOrEmpty(m.Director) || !string.IsNullOrEmpty(m.Cast)
                || !string.IsNullOrEmpty(m.Country) || m.MovieTags.Any() || m.Year > 0)
            .ToListAsync();

        BuildGraph(movies);
        RunForceLayout(200);
        NormalizeCoordinates();
        _centered = true;
        ApplyTransform();
        RenderGraph();
    }

    private void NormalizeCoordinates()
    {
        if (_nodes.Count == 0) return;

        var margin = NodeRadius + 60;
        var minX = _nodes.Min(n => n.X) - margin;
        var minY = _nodes.Min(n => n.Y) - margin;

        // 偏移节点坐标，使图左上角从 (0,0) 开始
        foreach (var node in _nodes)
        {
            node.X -= minX;
            node.Y -= minY;
        }

        // 设置 Canvas 尺寸
        var maxX = _nodes.Max(n => n.X) + margin;
        var maxY = _nodes.Max(n => n.Y) + margin;
        GraphCanvas.Width = maxX;
        GraphCanvas.Height = maxY;
    }

    private void ApplyTransform()
    {
        if (_nodes.Count == 0) return;

        var graphW = GraphCanvas.Width;
        var graphH = GraphCanvas.Height;
        var viewW = GraphContainer.ActualWidth > 0 ? GraphContainer.ActualWidth : 800;
        var viewH = GraphContainer.ActualHeight > 0 ? GraphContainer.ActualHeight : 600;

        var scale = Math.Min(viewW / graphW, viewH / graphH);
        scale = Math.Max(0.2, Math.Min(1.5, scale));

        CanvasScale.ScaleX = scale;
        CanvasScale.ScaleY = scale;
        CanvasTranslate.X = (viewW - graphW * scale) / 2;
        CanvasTranslate.Y = (viewH - graphH * scale) / 2;

        ZoomText.Text = $"{(int)(scale * 100)}%";
    }

    private void BuildGraph(List<Movie> movies)
    {
        _nodes.Clear();
        _links.Clear();

        var showDirector = ShowDirectorLinks.IsChecked == true;
        var showActor = ShowActorLinks.IsChecked == true;
        var showGenre = ShowGenreLinks.IsChecked == true;
        var showCountry = ShowCountryLinks.IsChecked == true;
        var showDecade = ShowDecadeLinks.IsChecked == true;
        var minLinks = 0;
        if (MinLinksCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag && int.TryParse(tag, out var ml))
            minLinks = ml;

        var directorMap = new Dictionary<string, List<int>>();
        var actorMap = new Dictionary<string, List<int>>();
        var genreMap = new Dictionary<string, List<int>>();
        var countryMap = new Dictionary<string, List<int>>();
        var decadeMap = new Dictionary<int, List<int>>();

        foreach (var m in movies)
        {
            if (showDirector && !string.IsNullOrEmpty(m.Director))
            {
                foreach (var d in SplitPeople(m.Director))
                {
                    if (!directorMap.ContainsKey(d)) directorMap[d] = new List<int>();
                    directorMap[d].Add(m.Id);
                }
            }
            if (showActor && !string.IsNullOrEmpty(m.Cast))
            {
                foreach (var a in SplitPeople(m.Cast).Take(5))
                {
                    if (!actorMap.ContainsKey(a)) actorMap[a] = new List<int>();
                    actorMap[a].Add(m.Id);
                }
            }
            if (showGenre)
            {
                foreach (var mt in m.MovieTags)
                {
                    var tagName = mt.Tag?.Name;
                    if (string.IsNullOrEmpty(tagName)) continue;
                    if (!genreMap.ContainsKey(tagName)) genreMap[tagName] = new List<int>();
                    genreMap[tagName].Add(m.Id);
                }
            }
            if (showCountry && !string.IsNullOrEmpty(m.Country))
            {
                foreach (var c in SplitBySlash(m.Country))
                {
                    if (!countryMap.ContainsKey(c)) countryMap[c] = new List<int>();
                    countryMap[c].Add(m.Id);
                }
            }
            if (showDecade && m.Year > 0)
            {
                var decade = m.Year / 10 * 10;
                if (!decadeMap.ContainsKey(decade)) decadeMap[decade] = new List<int>();
                decadeMap[decade].Add(m.Id);
            }
        }

        var linkPairs = new HashSet<(int, int, string, string)>();

        void AddLinks(Dictionary<string, List<int>> map, string type)
        {
            foreach (var kv in map)
            {
                var ids = kv.Value.Distinct().ToList();
                if (ids.Count < 2) continue;
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        var (a, b) = ids[i] < ids[j] ? (ids[i], ids[j]) : (ids[j], ids[i]);
                        linkPairs.Add((a, b, kv.Key, type));
                    }
            }
        }

        void AddDecadeLinks(Dictionary<int, List<int>> map)
        {
            foreach (var kv in map)
            {
                var ids = kv.Value.Distinct().ToList();
                if (ids.Count < 2) continue;
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        var (a, b) = ids[i] < ids[j] ? (ids[i], ids[j]) : (ids[j], ids[i]);
                        linkPairs.Add((a, b, kv.Key.ToString(), "decade"));
                    }
            }
        }

        AddLinks(directorMap, "director");
        AddLinks(actorMap, "actor");
        AddLinks(genreMap, "genre");
        AddLinks(countryMap, "country");
        AddDecadeLinks(decadeMap);

        var movieLinkCount = new Dictionary<int, int>();
        foreach (var (id1, id2, _, _) in linkPairs)
        {
            movieLinkCount.TryGetValue(id1, out var c1); movieLinkCount[id1] = c1 + 1;
            movieLinkCount.TryGetValue(id2, out var c2); movieLinkCount[id2] = c2 + 1;
        }

        var filteredIds = movieLinkCount.Where(kv => kv.Value >= minLinks).Select(kv => kv.Key).ToHashSet();

        var movieDict = movies.ToDictionary(m => m.Id);
        var canvasWidth = GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : 800;
        var canvasHeight = GraphCanvas.ActualHeight > 0 ? GraphCanvas.ActualHeight : 600;

        foreach (var id in filteredIds)
        {
            if (!movieDict.TryGetValue(id, out var m)) continue;
            _nodes.Add(new MovieNode
            {
                MovieId = m.Id,
                Title = m.Title,
                Year = m.Year,
                PosterData = m.PosterData,
                X = canvasWidth / 2 + _rng.NextDouble() * 300 - 150,
                Y = canvasHeight / 2 + _rng.NextDouble() * 300 - 150
            });
        }

        var nodeIds = _nodes.Select(n => n.MovieId).ToHashSet();
        var mergedLinks = new Dictionary<(int, int), MovieLink>();

        foreach (var (id1, id2, person, type) in linkPairs)
        {
            if (!nodeIds.Contains(id1) || !nodeIds.Contains(id2)) continue;
            var key = id1 < id2 ? (id1, id2) : (id2, id1);
            if (!mergedLinks.ContainsKey(key))
                mergedLinks[key] = new MovieLink { SourceId = key.Item1, TargetId = key.Item2 };
            var link = mergedLinks[key];
            if (type == "director") { link.HasDirectorLink = true; link.DirectorNames.Add(person); }
            else if (type == "actor") { link.HasActorLink = true; link.ActorNames.Add(person); }
            else if (type == "genre") { link.HasGenreLink = true; link.GenreNames.Add(person); }
            else if (type == "country") { link.HasCountryLink = true; link.CountryNames.Add(person); }
            else if (type == "decade") { link.HasDecadeLink = true; link.DecadeLabel = person; }
        }

        _links = mergedLinks.Values.ToList();

        SummaryText.Text = string.Format(LanguageManager.GetString("Relation_Summary"),
            _nodes.Count, _links.Count);
    }

    private static IEnumerable<string> SplitPeople(string text)
    {
        return text.Split(new[] { ", ", "、", " / ", "/", ", " }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p) && p.Length < 20);
    }

    private static IEnumerable<string> SplitBySlash(string text)
    {
        return text.Split(new[] { " / ", "/", "、", ", " }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p) && p.Length < 20);
    }

    private void RunForceLayout(int iterations)
    {
        if (_nodes.Count == 0) return;

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    var dx = _nodes[i].X - _nodes[j].X;
                    var dy = _nodes[i].Y - _nodes[j].Y;
                    var dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1);
                    var force = Repulsion / (dist * dist);
                    var fx = force * dx / dist;
                    var fy = force * dy / dist;
                    _nodes[i].Vx += fx; _nodes[i].Vy += fy;
                    _nodes[j].Vx -= fx; _nodes[j].Vy -= fy;
                }
            }

            foreach (var link in _links)
            {
                var source = _nodes.FirstOrDefault(n => n.MovieId == link.SourceId);
                var target = _nodes.FirstOrDefault(n => n.MovieId == link.TargetId);
                if (source == null || target == null) continue;

                var dx = target.X - source.X;
                var dy = target.Y - source.Y;
                var dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1);
                var force = Attraction * (dist - IdealLength);
                var fx = force * dx / dist;
                var fy = force * dy / dist;
                source.Vx += fx; source.Vy += fy;
                target.Vx -= fx; target.Vy -= fy;
            }

            foreach (var node in _nodes)
            {
                if (node.MovieId == _dragNodeId) continue;
                node.Vx *= Damping;
                node.Vy *= Damping;
                node.X += node.Vx;
                node.Y += node.Vy;
            }
        }
    }

    private void RenderGraph()
    {
        GraphCanvas.Children.Clear();

        var nodeDict = _nodes.ToDictionary(n => n.MovieId);

        // 绘制连接线 + 关联原因标签
        foreach (var link in _links)
        {
            if (!nodeDict.TryGetValue(link.SourceId, out var src) ||
                !nodeDict.TryGetValue(link.TargetId, out var tgt)) continue;

            var isMultiLink = link.LinkTypeCount > 1;
            var line = new Line
            {
                X1 = src.X, Y1 = src.Y,
                X2 = tgt.X, Y2 = tgt.Y,
                StrokeThickness = isMultiLink ? 2.5 : 1.5,
                Opacity = 0.5
            };

            // 多种关系用紫色，否则用对应类型颜色
            if (isMultiLink)
                line.Stroke = new SolidColorBrush(Color.FromRgb(0x9C, 0x27, 0xB0));
            else if (link.HasDirectorLink)
                line.Stroke = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF));
            else if (link.HasActorLink)
                line.Stroke = new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x9A));
            else if (link.HasGenreLink)
                line.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
            else if (link.HasCountryLink)
                line.Stroke = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
            else if (link.HasDecadeLink)
                line.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x96, 0x88));

            // 连接线 Tooltip 显示关联详情
            var tipParts = new List<string>();
            if (link.HasDirectorLink) tipParts.Add($"🎬 {LanguageManager.GetString("Relation_DirectorLabel")}: {string.Join(", ", link.DirectorNames)}");
            if (link.HasActorLink) tipParts.Add($"🎭 {LanguageManager.GetString("Relation_ActorLabel")}: {string.Join(", ", link.ActorNames)}");
            if (link.HasGenreLink) tipParts.Add($"🏷️ {LanguageManager.GetString("Relation_GenreLabel")}: {string.Join(", ", link.GenreNames)}");
            if (link.HasCountryLink) tipParts.Add($"🌍 {LanguageManager.GetString("Relation_CountryLabel")}: {string.Join(", ", link.CountryNames)}");
            if (link.HasDecadeLink) tipParts.Add($"📅 {LanguageManager.GetString("Relation_DecadeLabel")}: {link.DecadeLabel}s");
            line.ToolTip = string.Join("\n", tipParts);

            GraphCanvas.Children.Add(line);

            // 在连接线中点显示关联标签
            var midX = (src.X + tgt.X) / 2;
            var midY = (src.Y + tgt.Y) / 2;

            // 最多显示2个标签，避免太拥挤
            var names = new List<string>();
            if (link.HasDirectorLink) names.AddRange(link.DirectorNames.Take(1));
            if (link.HasActorLink) names.AddRange(link.ActorNames.Take(1));
            if (names.Count == 0 && link.HasGenreLink) names.AddRange(link.GenreNames.Take(1));
            if (names.Count == 0 && link.HasCountryLink) names.AddRange(link.CountryNames.Take(1));
            if (names.Count == 0 && link.HasDecadeLink) names.Add(link.DecadeLabel + "s");
            var labelText = string.Join(", ", names);
            if (labelText.Length > 10) labelText = labelText[..10] + "…";

            var label = new TextBlock
            {
                Text = labelText,
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xCC, 0xCC, 0xCC)),
                Background = new SolidColorBrush(Color.FromArgb(0xA0, 0x30, 0x30, 0x30)),
                Padding = new Thickness(3, 1, 3, 1),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Canvas.SetLeft(label, midX - labelText.Length * 2.5);
            Canvas.SetTop(label, midY - 6);
            label.ToolTip = string.Join("\n", tipParts);
            GraphCanvas.Children.Add(label);
        }

        // 绘制节点
        foreach (var node in _nodes)
        {
            var nodeGroup = new Canvas { Width = NodeRadius * 2, Height = NodeRadius * 2 };
            Canvas.SetLeft(nodeGroup, node.X - NodeRadius);
            Canvas.SetTop(nodeGroup, node.Y - NodeRadius);

            var circle = new Ellipse
            {
                Width = NodeRadius * 2,
                Height = NodeRadius * 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF)),
                StrokeThickness = 2
            };
            nodeGroup.Children.Add(circle);

            if (node.PosterData is { Length: > 0 })
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new System.IO.MemoryStream(node.PosterData);
                    bmp.EndInit();
                    bmp.Freeze();

                    var img = new Image
                    {
                        Source = bmp,
                        Width = NodeRadius * 2 - 4,
                        Height = NodeRadius * 2 - 4,
                        Stretch = Stretch.UniformToFill,
                        Clip = new EllipseGeometry(new Point(NodeRadius - 2, NodeRadius - 2), NodeRadius - 2, NodeRadius - 2)
                    };
                    Canvas.SetLeft(img, 2);
                    Canvas.SetTop(img, 2);
                    nodeGroup.Children.Add(img);
                }
                catch { }
            }
            else
            {
                var initial = new TextBlock
                {
                    Text = node.Title.Length > 0 ? node.Title[0].ToString() : "?",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Canvas.SetLeft(initial, NodeRadius - 10);
                Canvas.SetTop(initial, NodeRadius - 14);
                nodeGroup.Children.Add(initial);
            }

            // 标题（节点下方）
            var titleBlock = new TextBlock
            {
                Text = node.Title.Length > 8 ? node.Title[..8] + "…" : node.Title,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(titleBlock, node.X - 40);
            Canvas.SetTop(titleBlock, node.Y + NodeRadius + 2);
            titleBlock.Width = 80;
            GraphCanvas.Children.Add(titleBlock);

            nodeGroup.Tag = node.MovieId;
            nodeGroup.MouseLeftButtonDown += Node_MouseDown;
            nodeGroup.MouseLeftButtonUp += Node_MouseUp;
            nodeGroup.MouseEnter += Node_MouseEnter;
            nodeGroup.MouseLeave += Node_MouseLeave;
            nodeGroup.Cursor = Cursors.Hand;

            GraphCanvas.Children.Add(nodeGroup);
        }
    }

    private void Node_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Canvas c && c.Tag is int id)
        {
            _dragNodeId = id;
            _isDragging = false;
            var node = _nodes.First(n => n.MovieId == id);
            var pos = e.GetPosition(GraphCanvas);
            _dragOffset = new Point(node.X - pos.X, node.Y - pos.Y);
            e.Handled = true;
        }
    }

    private async void Node_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging && sender is Canvas c && c.Tag is int id)
        {
            var movie = await _context.Movies.FindAsync(id);
            if (movie != null) _mainWindow?.ShowMovieDetail(movie);
        }
    }

    private void Node_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Canvas c && c.Tag is int id)
        {
            var node = _nodes.FirstOrDefault(n => n.MovieId == id);
            if (node == null) return;

            TooltipTitle.Text = node.Title;
            var info = new List<string>();
            if (node.Year > 0) info.Add(node.Year.ToString());
            var linkCount = _links.Count(l => l.SourceId == id || l.TargetId == id);
            info.Add($"{linkCount} {LanguageManager.GetString("Relation_Connections")}");
            TooltipInfo.Text = string.Join(" | ", info);

            // 显示关联详情
            var relatedLinks = _links.Where(l => l.SourceId == id || l.TargetId == id).ToList();
            var linkDetails = new List<string>();
            foreach (var link in relatedLinks)
            {
                var otherId = link.SourceId == id ? link.TargetId : link.SourceId;
                var otherNode = _nodes.FirstOrDefault(n => n.MovieId == otherId);
                if (otherNode == null) continue;
                var parts = new List<string>();
                if (link.HasDirectorLink) parts.Add($"🎬{string.Join(",", link.DirectorNames)}");
                if (link.HasActorLink) parts.Add($"🎭{string.Join(",", link.ActorNames.Take(2))}");
                if (link.HasGenreLink) parts.Add($"🏷️{string.Join(",", link.GenreNames.Take(2))}");
                if (link.HasCountryLink) parts.Add($"🌍{string.Join(",", link.CountryNames)}");
                if (link.HasDecadeLink) parts.Add($"📅{link.DecadeLabel}s");
                linkDetails.Add($"→ {otherNode.Title} ({string.Join(" ", parts)})");
            }
            TooltipLinks.Text = string.Join("\n", linkDetails.Take(5));

            var pos = e.GetPosition(this);
            TooltipBorder.Margin = new Thickness(pos.X + 16, pos.Y + 16, 0, 0);
            TooltipBorder.Visibility = Visibility.Visible;
        }
    }

    private void Node_MouseLeave(object sender, MouseEventArgs e)
    {
        TooltipBorder.Visibility = Visibility.Collapsed;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Canvas || e.OriginalSource is Line || e.OriginalSource is TextBlock)
        {
            _dragNodeId = null;
            // 左键在空白区域拖拽画布平移
            _isPanning = true;
            _lastMousePos = e.GetPosition(GraphContainer);
            e.Handled = true;
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        // 拖拽节点
        if (_dragNodeId.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            var node = _nodes.FirstOrDefault(n => n.MovieId == _dragNodeId.Value);
            if (node == null) return;

            var pos = e.GetPosition(GraphCanvas);
            node.X = pos.X + _dragOffset.X;
            node.Y = pos.Y + _dragOffset.Y;
            node.Vx = 0; node.Vy = 0;

            RunForceLayout(5);
            RenderGraph();
            return;
        }

        // 左键拖拽平移画布
        if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(GraphContainer);
            CanvasTranslate.X += pos.X - _lastMousePos.X;
            CanvasTranslate.Y += pos.Y - _lastMousePos.Y;
            _lastMousePos = pos;
        }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging && _dragNodeId.HasValue)
        {
            RunForceLayout(30);
            RenderGraph();
        }
        _dragNodeId = null;
        _isDragging = false;
        _isPanning = false;
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 用容器坐标计算，避免 Canvas RenderTransform 干扰
        var mousePos = e.GetPosition(GraphContainer);
        var delta = e.Delta > 0 ? 1.1 : 1 / 1.1;

        var oldScale = CanvasScale.ScaleX;
        var newScale = oldScale * delta;
        newScale = Math.Max(0.2, Math.Min(5, newScale));

        // 以鼠标位置为中心缩放：鼠标指向的容器坐标在缩放前后保持不变
        CanvasTranslate.X = mousePos.X - (mousePos.X - CanvasTranslate.X) * (newScale / oldScale);
        CanvasTranslate.Y = mousePos.Y - (mousePos.Y - CanvasTranslate.Y) * (newScale / oldScale);

        CanvasScale.ScaleX = newScale;
        CanvasScale.ScaleY = newScale;

        ZoomText.Text = $"{(int)(newScale * 100)}%";
        e.Handled = true;
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_nodes.Count == 0) return;
        await LoadDataAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        CanvasScale.ScaleX = 1; CanvasScale.ScaleY = 1;
        CanvasTranslate.X = 0; CanvasTranslate.Y = 0;
        _centered = false;
        await LoadDataAsync();
    }
}

internal class MovieNode
{
    public int MovieId { get; set; }
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public byte[]? PosterData { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
}

internal class MovieLink
{
    public int SourceId { get; set; }
    public int TargetId { get; set; }
    public bool HasDirectorLink { get; set; }
    public bool HasActorLink { get; set; }
    public bool HasGenreLink { get; set; }
    public bool HasCountryLink { get; set; }
    public bool HasDecadeLink { get; set; }
    public List<string> DirectorNames { get; set; } = new();
    public List<string> ActorNames { get; set; } = new();
    public List<string> GenreNames { get; set; } = new();
    public List<string> CountryNames { get; set; } = new();
    public string? DecadeLabel { get; set; }

    public int LinkTypeCount =>
        (HasDirectorLink ? 1 : 0) + (HasActorLink ? 1 : 0) +
        (HasGenreLink ? 1 : 0) + (HasCountryLink ? 1 : 0) + (HasDecadeLink ? 1 : 0);
}
