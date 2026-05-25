using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;

namespace EasyMovie.Client.Views;

public partial class CategoryTagManageView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly ICategoryService _categoryService;
    private readonly ITagService _tagService;
    private Category? _selectedCategory;
    private Tag? _selectedTag;
    private bool _isAddingChild;
    private int? _addChildParentId;
    private string _selectedColor = "#5C6BC0";

    public CategoryTagManageView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _categoryService = new CategoryService(new CategoryRepository(_context));
        _tagService = new TagService(new TagRepository(_context));
        Loaded += async (s, e) => { await LoadTreeAsync(); await InitTagsAsync(); };
    }

    #region 分类管理

    private async Task LoadTreeAsync()
    {
        try { CategoryTree.ItemsSource = await _categoryService.GetCategoryTreeAsync(); }
        catch (Exception ex) { MessageBox.Show($"加载分类树失败: {ex.Message}"); }
    }

    private async void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is Category cat) { _selectedCategory = cat; _isAddingChild = false; await LoadDetailAsync(cat); }
    }

    private async Task LoadDetailAsync(Category cat)
    {
        FormTitle.Text = "编辑分类: " + cat.Name;
        CategoryNameBox.Text = cat.Name;
        CategoryDescBox.Text = cat.Description ?? "";
        ParentInfo.Text = cat.ParentId.HasValue ? "父分类: " + (await _categoryService.GetByIdAsync(cat.ParentId.Value))?.Name : "根分类";
        var children = await _categoryService.GetChildrenAsync(cat.Id);
        var canDel = await _categoryService.CanDeleteAsync(cat.Id);
        StatInfo.Text = $"{(children.Any() ? $"{children.Count} 子分类 · " : "")}{(canDel ? "可删除" : "不可删除(有关联)")}";
        DeleteBtn.IsEnabled = canDel;
        DeleteBtn.Visibility = Visibility.Visible;
        AddChildBtn.Visibility = Visibility.Visible;
    }

    private void AddRootCategory_Click(object sender, RoutedEventArgs e) { ClearForm("添加根分类", "新根分类"); }

    private void AddChildBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null) return;
        _isAddingChild = true; _addChildParentId = _selectedCategory.Id;
        FormTitle.Text = "添加子分类 (父: " + _selectedCategory.Name + ")";
        CategoryNameBox.Text = ""; CategoryDescBox.Text = "";
        ParentInfo.Text = "父: " + _selectedCategory.Name; StatInfo.Text = "";
        DeleteBtn.Visibility = Visibility.Collapsed; AddChildBtn.Visibility = Visibility.Collapsed;
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = CategoryNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("请输入分类名称"); return; }
            var desc = string.IsNullOrWhiteSpace(CategoryDescBox.Text) ? null : CategoryDescBox.Text.Trim();
            if (_isAddingChild) await _categoryService.AddAsync(new Category { Name = name, Description = desc, ParentId = _addChildParentId });
            else if (_selectedCategory != null) { _selectedCategory.Name = name; _selectedCategory.Description = desc; await _categoryService.UpdateAsync(_selectedCategory); }
            else await _categoryService.AddAsync(new Category { Name = name, Description = desc });
            await LoadTreeAsync(); ClearForm("保存成功！", "");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null || !await _categoryService.CanDeleteAsync(_selectedCategory.Id)) { MessageBox.Show("无法删除"); return; }
        if (MessageBox.Show("确定删除？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        { await _categoryService.DeleteAsync(_selectedCategory.Id); await LoadTreeAsync(); ClearForm("选择分类", ""); }
    }

    private void ClearForm(string title, string parent)
    {
        _selectedCategory = null; _isAddingChild = false;
        FormTitle.Text = title; CategoryNameBox.Text = ""; CategoryDescBox.Text = "";
        ParentInfo.Text = parent; StatInfo.Text = "";
        DeleteBtn.Visibility = Visibility.Collapsed; AddChildBtn.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region 标签管理

    private async Task InitTagsAsync() { BuildColorPicker(); await LoadTagsAsync(); TagDeleteBtn.Visibility = Visibility.Collapsed; }

    private void BuildColorPicker()
    {
        var colors = new[] { "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5","#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50","#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800","#FF5722","#795548","#607D8B","#9E9E9E","#000000" };
        foreach (var c in colors)
        {
            var b = new Button { Width = 26, Height = 26, Margin = new Thickness(1), Tag = c };
            b.Content = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)) };
            b.Click += (s, e) => { if (s is Button bt && bt.Tag is string cl) { _selectedColor = cl; UpdatePreview(); } };
            ColorPicker.Children.Add(b);
        }
    }

    private void UpdatePreview() { try { ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColor)); } catch { ColorPreview.Background = Brushes.Gray; } }

    private async Task LoadTagsAsync() { try { TagListBox.ItemsSource = await _tagService.GetAllAsync(); } catch (Exception ex) { MessageBox.Show(ex.Message); } }

    private void TagListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TagListBox.SelectedItem is Tag t) { _selectedTag = t; TagFormTitle.Text = "编辑: " + t.Name; TagNameBox.Text = t.Name; _selectedColor = t.Color ?? "#5C6BC0"; UpdatePreview(); TagDeleteBtn.Visibility = Visibility.Visible; }
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) { _selectedTag = null; TagFormTitle.Text = "添加标签"; TagNameBox.Text = ""; _selectedColor = "#5C6BC0"; UpdatePreview(); TagListBox.SelectedItem = null; TagDeleteBtn.Visibility = Visibility.Collapsed; }

    private async void SaveTag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = TagNameBox.Text.Trim(); if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("请输入名称"); return; }
            if (_selectedTag != null) { _selectedTag.Name = name; _selectedTag.Color = _selectedColor; await _tagService.UpdateAsync(_selectedTag); }
            else await _tagService.AddAsync(new Tag { Name = name, Color = _selectedColor });
            await LoadTagsAsync(); _selectedTag = null; TagFormTitle.Text = "保存成功！"; TagDeleteBtn.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTag == null || MessageBox.Show("确定删除？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await _tagService.DeleteAsync(_selectedTag.Id); await LoadTagsAsync(); _selectedTag = null; TagFormTitle.Text = "选择标签"; TagNameBox.Text = ""; TagDeleteBtn.Visibility = Visibility.Collapsed;
    }

    #endregion
}
