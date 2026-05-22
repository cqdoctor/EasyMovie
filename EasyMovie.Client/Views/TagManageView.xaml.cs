using System;
using System.Collections.Generic;
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

public partial class TagManageView : UserControl
{
    private readonly MovieDbContext _context;
    private readonly ITagService _tagService;
    private Tag? _selectedTag;
    private string _selectedColor = "#5C6BC0";

    public TagManageView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _tagService = new TagService(new TagRepository(_context));
        Loaded += async (s, e) => await InitAsync();
        Unloaded += (s, e) => _context.Dispose();
    }

    private async Task InitAsync() { BuildColorPicker(); await LoadTagsAsync(); DeleteBtn.Visibility = Visibility.Collapsed; }

    private void BuildColorPicker()
    {
        var colors = new[] { "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5","#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50","#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800","#FF5722","#795548","#607D8B","#9E9E9E","#000000" };
        foreach (var c in colors) { var b = new Button { Width=28,Height=28,Margin=new Thickness(2),Tag=c }; b.Content = new System.Windows.Shapes.Ellipse { Width=18,Height=18,Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)) }; b.Click += (s,e) => { if (s is Button bt && bt.Tag is string cl) { _selectedColor=cl; UpdatePreview(); } }; ColorPicker.Children.Add(b); }
    }

    private void UpdatePreview() { try { ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColor)); } catch { ColorPreview.Background = Brushes.Gray; } }

    private async Task LoadTagsAsync() { try { TagListBox.ItemsSource = await _tagService.GetAllAsync(); } catch (Exception ex) { MessageBox.Show(ex.Message); } }

    private void TagListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TagListBox.SelectedItem is Tag t) { _selectedTag=t; FormTitle.Text="编辑: "+t.Name; TagNameBox.Text=t.Name; _selectedColor=t.Color??"#5C6BC0"; UpdatePreview(); DeleteBtn.Visibility=Visibility.Visible; }
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) { _selectedTag=null; FormTitle.Text="添加标签"; TagNameBox.Text=""; _selectedColor="#5C6BC0"; UpdatePreview(); TagListBox.SelectedItem=null; DeleteBtn.Visibility=Visibility.Collapsed; }

    private async void SaveTag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = TagNameBox.Text.Trim(); if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show("请输入名称"); return; }
            if (_selectedTag!=null) { _selectedTag.Name=name; _selectedTag.Color=_selectedColor; await _tagService.UpdateAsync(_selectedTag); }
            else await _tagService.AddAsync(new Tag{Name=name,Color=_selectedColor});
            await LoadTagsAsync(); _selectedTag=null; FormTitle.Text="保存成功！"; DeleteBtn.Visibility=Visibility.Collapsed;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTag==null || MessageBox.Show("确定删除？","确认",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
        await _tagService.DeleteAsync(_selectedTag.Id); await LoadTagsAsync(); _selectedTag=null; FormTitle.Text="选择标签"; TagNameBox.Text=""; DeleteBtn.Visibility=Visibility.Collapsed;
    }
}
