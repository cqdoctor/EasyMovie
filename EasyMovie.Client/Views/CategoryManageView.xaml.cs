﻿﻿﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EasyMovie.Core.Interfaces;
using EasyMovie.Core.Models;
using EasyMovie.Core.Services;
using EasyMovie.Data;
using EasyMovie.Data.Repositories;
using EasyMovie.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EasyMovie.Client.Views;

public partial class CategoryManageView : UserControl
{
    private readonly MovieDbContext _context;
    private bool _disposed;
    private readonly CategoryManageViewModel _vm;
    private Category? _selectedCategory;
    private bool _isAddingChild;
    private int? _addChildParentId;

    public CategoryManageView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        // 通过 DI 容器解析 ViewModel（首个 DI 消费示范）；DI 不可用时回退手工创建，行为等价
        _vm = App.Services?.GetService<CategoryManageViewModel>()
              ?? new CategoryManageViewModel(new CategoryService(new CategoryRepository(_context)));
        Loaded += async (s, e) => await LoadTreeAsync();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        Unloaded -= OnUnloaded;
        _context.Dispose();
    }

    private async Task LoadTreeAsync()
    {
        try { CategoryTree.ItemsSource = await _vm.GetCategoryTreeAsync(); }
        catch (Exception ex) { AppMessageBox.ShowError($"加载分类树失败: {ex.Message}"); }
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
        ParentInfo.Text = cat.ParentId.HasValue ? "父分类: " + (await _vm.GetByIdAsync(cat.ParentId.Value))?.Name : "根分类";
        var children = await _vm.GetChildrenAsync(cat.Id);
        var canDel = await _vm.CanDeleteAsync(cat.Id);
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
            if (string.IsNullOrWhiteSpace(name)) { AppMessageBox.ShowInfo("请输入分类名称"); return; }
            var desc = string.IsNullOrWhiteSpace(CategoryDescBox.Text) ? null : CategoryDescBox.Text.Trim();
            if (_isAddingChild) await _vm.AddAsync(new Category { Name = name, Description = desc, ParentId = _addChildParentId });
            else if (_selectedCategory != null) { _selectedCategory.Name = name; _selectedCategory.Description = desc; await _vm.UpdateAsync(_selectedCategory); }
            else await _vm.AddAsync(new Category { Name = name, Description = desc });
            await LoadTreeAsync(); ClearForm("保存成功！", "");
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null || !await _vm.CanDeleteAsync(_selectedCategory.Id)) { AppMessageBox.ShowInfo("无法删除"); return; }
        if (AppMessageBox.Confirm("确定删除？", "确认"))
        { await _vm.DeleteAsync(_selectedCategory.Id); await LoadTreeAsync(); ClearForm("选择分类", ""); }
    }

    private void ClearForm(string title, string parent)
    {
        _selectedCategory = null; _isAddingChild = false;
        FormTitle.Text = title; CategoryNameBox.Text = ""; CategoryDescBox.Text = "";
        ParentInfo.Text = parent; StatInfo.Text = "";
        DeleteBtn.Visibility = Visibility.Collapsed; AddChildBtn.Visibility = Visibility.Collapsed;
    }
}
