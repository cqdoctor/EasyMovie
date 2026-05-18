using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MovieManager.Core.Interfaces;
using MovieManager.Core.Services;
using MovieManager.Data;
using SkiaSharp;

namespace MovieManager.Client.Views;

public partial class StatisticsView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly IStatisticsService _statsService;

    public StatisticsView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _statsService = new StatisticsService(_context);
        Loaded += async (s, e) => await LoadAsync();
        Unloaded += (s, e) => _context.Dispose();
    }

    private async Task LoadAsync()
    {
        try
        {
            var d = await _statsService.GetStatisticsAsync();
            TotalMoviesText.Text = d.TotalMovies.ToString();
            WatchedText.Text = d.Watched.ToString();
            WantWatchText.Text = d.WantToWatch.ToString();
            AvgRatingText.Text = d.AverageRating.ToString("F1");
            FavoritesText.Text = d.Favorites.ToString();

            CategoryPieChart.Series = d.CategoryStats.Select(c => new PieSeries<ObservableValue> { Values = new[] { new ObservableValue(c.Count) }, Name = c.Name, DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer, DataLabelsPaint = new SolidColorPaint(SKColors.White), DataLabelsSize = 12 }).ToArray();
            CategoryPieChart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Right;

            var vals = Enumerable.Range(1,10).Select(r=>(double)(d.RatingStats.FirstOrDefault(s=>s.Rating==r)?.Count??0)).ToArray();
            RatingBarChart.Series = new ISeries[]{new ColumnSeries<double>{Values=vals,Fill=new SolidColorPaint(SKColor.Parse("#7C4DFF")),DataLabelsPaint=new SolidColorPaint(SKColors.White),DataLabelsSize=12,DataLabelsPosition=LiveChartsCore.Measure.DataLabelsPosition.Top}};
            RatingBarChart.XAxes = new[]{new Axis{Labels=Enumerable.Range(1,10).Select(r=>r+"分").ToArray(),LabelsPaint=new SolidColorPaint(SKColors.Gray)}};
            RatingBarChart.YAxes = new[]{new Axis{MinLimit=0,LabelsPaint=new SolidColorPaint(SKColors.Gray)}};

            YearlyTrendChart.Series = new ISeries[]{new LineSeries<int>{Values=d.YearlyStats.Select(y=>y.AddedCount).ToArray(),Name="新增",Stroke=new SolidColorPaint(SKColor.Parse("#7C4DFF"),3),GeometrySize=8,Fill=null},new LineSeries<int>{Values=d.YearlyStats.Select(y=>y.WatchedCount).ToArray(),Name="已看",Stroke=new SolidColorPaint(SKColor.Parse("#4CAF50"),3),GeometrySize=8,Fill=null}};
            YearlyTrendChart.XAxes = new[]{new Axis{Labels=d.YearlyStats.Select(y=>y.Year.ToString()).ToArray(),LabelsPaint=new SolidColorPaint(SKColors.Gray)}};
            YearlyTrendChart.YAxes = new[]{new Axis{MinLimit=0,LabelsPaint=new SolidColorPaint(SKColors.Gray)}};

            MonthlyWatchChart.Series = new ISeries[]{new ColumnSeries<int>{Values=d.MonthlyStats.Select(m=>m.WatchedCount).ToArray(),Fill=new SolidColorPaint(SKColor.Parse("#4CAF50")),DataLabelsSize=10,DataLabelsPosition=LiveChartsCore.Measure.DataLabelsPosition.Top}};
            MonthlyWatchChart.XAxes = new[]{new Axis{Labels=new[]{"1","2","3","4","5","6","7","8","9","10","11","12"},LabelsPaint=new SolidColorPaint(SKColors.Gray)}};
            MonthlyWatchChart.YAxes = new[]{new Axis{MinLimit=0,LabelsPaint=new SolidColorPaint(SKColors.Gray)}};
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message); }
    }
}
