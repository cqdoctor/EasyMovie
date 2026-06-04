using System;
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
    private bool _isInitialized;

    public CategoryTagManageView()
    {
        InitializeComponent();
        _context = DbHelper.CreateContext();
        _categoryService = new CategoryService(new CategoryRepository(_context));
        _tagService = new TagService(new TagRepository(_context));
        Loaded += async (s, e) => await InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await LoadTreeAsync();
        await InitTagsAsync();
    }

    #region 分类管理

    private async Task LoadTreeAsync()
    {
        try { CategoryTree.ItemsSource = await _categoryService.GetCategoryTreeAsync(); }
        catch (Exception ex) { AppMessageBox.ShowError(LanguageManager.GetString("Msg_LoadCategoryFailed") + ex.Message); }
    }

    private async void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is Category cat) { _selectedCategory = cat; _isAddingChild = false; await LoadDetailAsync(cat); }
    }

    private async Task LoadDetailAsync(Category cat)
    {
        FormTitle.Text = LanguageManager.GetString("CatTag_EditCategory") + cat.Name;
        CategoryNameBox.Text = cat.Name;
        CategoryDescBox.Text = cat.Description ?? "";
        ParentInfo.Text = cat.ParentId.HasValue
            ? LanguageManager.GetString("CatTag_Parent") + (await _categoryService.GetByIdAsync(cat.ParentId.Value))?.Name
            : LanguageManager.GetString("CatTag_RootCategory");
        var children = await _categoryService.GetChildrenAsync(cat.Id);
        var canDel = await _categoryService.CanDeleteAsync(cat.Id);
        StatInfo.Text = $"{(children.Any() ? $"{children.Count} {LanguageManager.GetString("CatTag_SubCategories")} · " : "")}{(canDel ? LanguageManager.GetString("CatTag_CanDelete") : LanguageManager.GetString("CatTag_CannotDelete"))}";
        DeleteBtn.IsEnabled = canDel;
        DeleteBtn.Visibility = Visibility.Visible;
        AddChildBtn.Visibility = Visibility.Visible;
    }

    private void AddRootCategory_Click(object sender, RoutedEventArgs e) { ClearForm(LanguageManager.GetString("CatTag_AddRootCategory"), ""); }

    private void AddChildBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null) return;
        _isAddingChild = true; _addChildParentId = _selectedCategory.Id;
        FormTitle.Text = LanguageManager.GetString("CatTag_AddChildCategory") + " (" + LanguageManager.GetString("CatTag_Parent") + _selectedCategory.Name + ")";
        CategoryNameBox.Text = ""; CategoryDescBox.Text = "";
        ParentInfo.Text = LanguageManager.GetString("CatTag_Parent") + _selectedCategory.Name; StatInfo.Text = "";
        DeleteBtn.Visibility = Visibility.Collapsed; AddChildBtn.Visibility = Visibility.Collapsed;
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = CategoryNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_EnterName")); return; }
            var desc = string.IsNullOrWhiteSpace(CategoryDescBox.Text) ? null : CategoryDescBox.Text.Trim();
            if (_isAddingChild) await _categoryService.AddAsync(new Category { Name = name, Description = desc, ParentId = _addChildParentId });
            else if (_selectedCategory != null) { _selectedCategory.Name = name; _selectedCategory.Description = desc; await _categoryService.UpdateAsync(_selectedCategory); }
            else await _categoryService.AddAsync(new Category { Name = name, Description = desc });
            await LoadTreeAsync(); ClearForm(LanguageManager.GetString("Msg_Saved"), "");
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCategory == null || !await _categoryService.CanDeleteAsync(_selectedCategory.Id)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_CannotDelete")); return; }
        if (AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmDelete"), LanguageManager.GetString("Msg_Confirm")))
        { await _categoryService.DeleteAsync(_selectedCategory.Id); await LoadTreeAsync(); ClearForm(LanguageManager.GetString("CatTag_SelectOrAdd"), ""); }
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

    private async Task InitTagsAsync()
    {
        BuildColorPicker();
        await LoadTagsAsync();
        TagDeleteBtn.Visibility = Visibility.Collapsed;
    }

    private static readonly string[] TagPalette = {
        "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5",
        "#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50",
        "#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800",
        "#FF5722","#795548","#607D8B"
    };

    private async Task LoadTagsAsync()
    {
        try
        {
            var tags = await _tagService.GetAllAsync();
            // 给没有颜色的标签分配不同颜色并保存
            for (int i = 0; i < tags.Count; i++)
            {
                if (string.IsNullOrEmpty(tags[i].Color))
                {
                    tags[i].Color = TagPalette[i % TagPalette.Length];
                    await _tagService.UpdateAsync(tags[i]);
                }
            }
            TagListBox.ItemsSource = tags;
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private void BuildColorPicker()
    {
        var colors = new[] { "#F44336","#E91E63","#9C27B0","#673AB7","#3F51B5","#2196F3","#03A9F4","#00BCD4","#009688","#4CAF50","#8BC34A","#CDDC39","#FFEB3B","#FFC107","#FF9800","#FF5722","#795548","#607D8B","#9E9E9E","#000000" };
        foreach (var c in colors)
        {
            var border = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)),
                Cursor = Cursors.Hand, Margin = new Thickness(2),
                Tag = c
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (s is Border bd && bd.Tag is string cl) { _selectedColor = cl; UpdatePreview(); }
            };
            ColorPicker.Children.Add(border);
        }
    }

    private void UpdatePreview() { try { ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedColor)); } catch { ColorPreview.Background = Brushes.Gray; } }

    private void TagListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TagListBox.SelectedItem is Tag t) { _selectedTag = t; TagFormTitle.Text = LanguageManager.GetString("CatTag_EditTag") + t.Name; TagNameBox.Text = t.Name; _selectedColor = t.Color ?? "#5C6BC0"; UpdatePreview(); TagDeleteBtn.Visibility = Visibility.Visible; }
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) { _selectedTag = null; TagFormTitle.Text = LanguageManager.GetString("CatTag_AddTag"); TagNameBox.Text = ""; _selectedColor = "#5C6BC0"; UpdatePreview(); TagListBox.SelectedItem = null; TagDeleteBtn.Visibility = Visibility.Collapsed; }

    private async void SaveTag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = TagNameBox.Text.Trim(); if (string.IsNullOrWhiteSpace(name)) { AppMessageBox.ShowInfo(LanguageManager.GetString("Msg_EnterName")); return; }
            if (_selectedTag != null) { _selectedTag.Name = name; _selectedTag.Color = _selectedColor; await _tagService.UpdateAsync(_selectedTag); }
            else await _tagService.AddAsync(new Tag { Name = name, Color = _selectedColor });
            await LoadTagsAsync(); _selectedTag = null; TagFormTitle.Text = LanguageManager.GetString("Msg_Saved"); TagDeleteBtn.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { AppMessageBox.ShowError(ex.Message); }
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTag == null || !AppMessageBox.Confirm(LanguageManager.GetString("Msg_ConfirmDelete"), LanguageManager.GetString("Msg_Confirm"))) return;
        await _tagService.DeleteAsync(_selectedTag.Id); await LoadTagsAsync(); _selectedTag = null; TagFormTitle.Text = LanguageManager.GetString("CatTag_SelectOrAddTag"); TagNameBox.Text = ""; TagDeleteBtn.Visibility = Visibility.Collapsed;
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        // 使用 WPF 原生颜色选择器弹窗
        var dlg = new Window
        {
            Title = "🎨 " + LanguageManager.GetString("CatTag_CustomColor"),
            Width = 320,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        dlg.SourceInitialized += (s, args) =>
        {
            var hwnd = new System.Runtime.InteropServices.HandleRef(null, new System.Windows.Interop.WindowInteropHelper(dlg).Handle);
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_DLGMODALFRAME);
            SendMessage(hwnd.Handle, WM_SETICON, IntPtr.Zero, IntPtr.Zero);
            SendMessage(hwnd.Handle, WM_SETICON, (IntPtr)1, IntPtr.Zero);
        };

        var panel = new StackPanel { Margin = new Thickness(16) };

        // 色相滑块
        var hueSlider = new Slider { Minimum = 0, Maximum = 360, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 0, 0, 8) };
        var satSlider = new Slider { Minimum = 0, Maximum = 100, Value = 70, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 0, 0, 8) };
        var valSlider = new Slider { Minimum = 0, Maximum = 100, Value = 80, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 0, 0, 8) };

        // 从当前颜色初始化滑块
        try
        {
            var currentColor = (Color)ColorConverter.ConvertFromString(_selectedColor);
            var (h, s, v) = RgbToHsv(currentColor.R, currentColor.G, currentColor.B);
            hueSlider.Value = h;
            satSlider.Value = s;
            valSlider.Value = v;
        }
        catch { }

        var hueLabel = new TextBlock { Text = "H", FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
        var satLabel = new TextBlock { Text = "S", FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
        var valLabel = new TextBlock { Text = "V", FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };

        var previewBorder = new Border
        {
            Width = 260, Height = 40, CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 8, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var hexBox = new TextBox
        {
            IsReadOnly = true, FontSize = 14, FontWeight = FontWeights.Medium,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };

        void UpdateColor()
        {
            var (r, g, b) = HsvToRgb(hueSlider.Value, satSlider.Value, valSlider.Value);
            var color = Color.FromRgb(r, g, b);
            var hex = $"#{r:X2}{g:X2}{b:X2}";
            previewBorder.Background = new SolidColorBrush(color);
            hexBox.Text = hex;
        }

        hueSlider.ValueChanged += (s, e) => UpdateColor();
        satSlider.ValueChanged += (s, e) => UpdateColor();
        valSlider.ValueChanged += (s, e) => UpdateColor();

        panel.Children.Add(hueLabel);
        panel.Children.Add(hueSlider);
        panel.Children.Add(satLabel);
        panel.Children.Add(satSlider);
        panel.Children.Add(valLabel);
        panel.Children.Add(valSlider);
        panel.Children.Add(previewBorder);
        panel.Children.Add(hexBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = LanguageManager.GetString("Msg_Ok"), Style = (Style)dlg.FindResource("MaterialDesignRaisedButton"), Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        okBtn.Click += (s, e) => dlg.DialogResult = true;
        var cancelBtn = new Button { Content = LanguageManager.GetString("Msg_Cancel"), Style = (Style)dlg.FindResource("MaterialDesignFlatButton"), Padding = new Thickness(16, 4, 16, 4) };
        cancelBtn.Click += (s, e) => dlg.DialogResult = false;
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        dlg.Content = panel;
        UpdateColor();

        if (dlg.ShowDialog() == true)
        {
            _selectedColor = hexBox.Text;
            UpdatePreview();
        }
    }

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;
        double h = 0, s = max == 0 ? 0 : delta / max * 100, v = max * 100;
        if (delta > 0)
        {
            if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
            else h = 60 * (((rf - gf) / delta) + 4);
            if (h < 0) h += 360;
        }
        return (h, s, v);
    }

    private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        double sf = s / 100.0, vf = v / 100.0;
        double c = vf * sf, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = vf - c;
        double rf = 0, gf = 0, bf = 0;
        if (h < 60) { rf = c; gf = x; }
        else if (h < 120) { rf = x; gf = c; }
        else if (h < 180) { gf = c; bf = x; }
        else if (h < 240) { gf = x; bf = c; }
        else if (h < 300) { rf = x; bf = c; }
        else { rf = c; bf = x; }
        return ((byte)Math.Round((rf + m) * 255), (byte)Math.Round((gf + m) * 255), (byte)Math.Round((bf + m) * 255));
    }

    #endregion

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_DLGMODALFRAME = 0x00000001;
    private const uint WM_SETICON = 0x0080;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(System.Runtime.InteropServices.HandleRef hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(System.Runtime.InteropServices.HandleRef hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
