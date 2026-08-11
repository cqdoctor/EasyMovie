﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace EasyMovie.Client.Views;

public partial class TagManageView : UserControl
{
    private readonly MovieDbContext _context;
    private bool _disposed;
    private readonly TagManageViewModel _vm;
    private Tag? _selectedTag;
    private string _selectedColor = "#5C6BC0";

    public TagManageView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        // 通过 DI 容器解析 ViewModel；DI 不可用时回退手工创建，行为等价
        _vm = App.Services?.GetService<TagManageViewModel>()
              ?? new TagManageViewModel(new TagService(new TagRepository(_context)));
        Loaded += async (s, e) => await InitAsync();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        Unloaded -= OnUnloaded;
        _context.Dispose();
    }

    private async Task InitAsync() { BuildColorPicker(); await LoadTagsAsync(); DeleteBtn.Visibility = Visibility.Collapsed; }

    private void BuildColorPicker()
    {
        var colors = new[] { "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5","#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50","#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800","#FF5722","#795548","#607D8B","#9E9E9E","#000000" };
        foreach (var c in colors)
        {
            var border = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)),
                Cursor = Cursors.Hand, Margin = new Thickness(2), Tag = c
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (s is Border bd && bd.Tag is string cl) { _selectedColor = cl; UpdatePreview(); }
            };
            ColorPicker.Children.Add(border);
        }
    }

    private void UpdatePreview() { try { ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColor)); } catch (Exception ex) { Log.Error(ex, "颜色预览转换失败"); ColorPreview.Background = Brushes.Gray; } }

    private async Task LoadTagsAsync() { try { TagListBox.ItemsSource = await _vm.GetAllAsync(); } catch (Exception ex) { AppMessageBox.ShowError(ex.Message); } }

    private void TagListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TagListBox.SelectedItem is Tag t) { _selectedTag=t; FormTitle.Text="编辑: "+t.Name; TagNameBox.Text=t.Name; _selectedColor=t.Color??"#5C6BC0"; UpdatePreview(); DeleteBtn.Visibility=Visibility.Visible; }
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) { _selectedTag=null; FormTitle.Text="添加标签"; TagNameBox.Text=""; _selectedColor="#5C6BC0"; UpdatePreview(); TagListBox.SelectedItem=null; DeleteBtn.Visibility=Visibility.Collapsed; }

    private async void SaveTag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = TagNameBox.Text.Trim(); if (string.IsNullOrWhiteSpace(name)) { AppMessageBox.ShowInfo("请输入名称"); return; }
            if (_selectedTag!=null) { _selectedTag.Name=name; _selectedTag.Color=_selectedColor; await _vm.UpdateAsync(_selectedTag); }
            else await _vm.AddAsync(new Tag{Name=name,Color=_selectedColor});
            await LoadTagsAsync(); _selectedTag=null; FormTitle.Text="保存成功！"; DeleteBtn.Visibility=Visibility.Collapsed;
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTag==null || !AppMessageBox.Confirm("确定删除？","确认")) return;
        await _vm.DeleteAsync(_selectedTag.Id); await LoadTagsAsync(); _selectedTag=null; FormTitle.Text="选择标签"; TagNameBox.Text=""; DeleteBtn.Visibility=Visibility.Collapsed;
    }
}
