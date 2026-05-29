using System.Windows;
using System.Windows.Controls;
using EasyMovie.Core.Models;
using EasyMovie.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyMovie.Client.Views;

public partial class AddMovieToCollectionDialog : Window
{
    private readonly int _collectionId;
    private readonly MovieDbContext _context;
    private List<Movie> _allMovies;
    private readonly CollectionService _collectionService;
    private HashSet<int> _existingIds;

    public AddMovieToCollectionDialog(int collectionId, MovieDbContext context)
    {
        InitializeComponent();
        _collectionId = collectionId;
        _context = context;
        _collectionService = new CollectionService(_context);
        _allMovies = new List<Movie>();
        _existingIds = new HashSet<int>();
        Loaded += async (_, _) => await LoadMoviesAsync();
    }

    private async Task LoadMoviesAsync()
    {
        _allMovies = await _context.Movies
            .OrderBy(m => m.Title)
            .ToListAsync();

        _existingIds = (await _context.Movies
            .Where(m => m.CollectionId == _collectionId)
            .Select(m => m.Id)
            .ToListAsync()).ToHashSet();

        RefreshList(_allMovies);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = SearchBox.Text.Trim().ToLower();
        if (string.IsNullOrEmpty(keyword))
        {
            RefreshList(_allMovies);
            return;
        }

        var filtered = _allMovies.Where(m =>
        {
            if (m.Title.ToLower().Contains(keyword)) return true;
            if (m.Year > 0 && m.Year.ToString().Contains(keyword)) return true;
            if (!string.IsNullOrEmpty(m.OriginalTitle) && m.OriginalTitle.ToLower().Contains(keyword)) return true;
            if (!string.IsNullOrEmpty(m.Director) && m.Director.ToLower().Contains(keyword)) return true;
            if (!string.IsNullOrEmpty(m.SearchIndex) && m.SearchIndex.ToLower().Contains(keyword)) return true;
            return false;
        }).ToList();

        RefreshList(filtered);
    }

    private void RefreshList(List<Movie> movies)
    {
        MovieListBox.Items.Clear();
        foreach (var m in movies)
        {
            var isInCollection = _existingIds.Contains(m.Id);
            var item = new ListBoxItem
            {
                Content = $"{m.Title} ({m.Year})" + (isInCollection ? $" [{LanguageManager.GetString("Collection_AlreadyIn")}]" : ""),
                Tag = m.Id,
                IsSelected = isInCollection,
                IsEnabled = !isInCollection
            };
            MovieListBox.Items.Add(item);
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = MovieListBox.Items.Cast<ListBoxItem>()
            .Where(i => i.IsSelected && i.IsEnabled)
            .Select(i => (int)i.Tag!)
            .ToList();

        foreach (var id in selectedIds)
        {
            await _collectionService.AddMovieToCollectionAsync(_collectionId, id);
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
