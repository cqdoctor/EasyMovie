using System.Windows.Input;
using FluentAssertions;

namespace EasyMovie.Client.Tests;

/// <summary>
/// 快捷键配置冒烟测试。
/// 注意：**故意不测 SaveAll 往返**——SavePath 固定在用户 LocalAppData，测试写入会覆盖用户真实配置。
/// 只覆盖纯函数与只读路径。
/// </summary>
public class ShortcutConfigTests
{
    [Fact]
    public void Defaults_ShouldBeWellFormed()
    {
        ShortcutConfig.Defaults.Should().NotBeEmpty();
        ShortcutConfig.Defaults.Select(d => d.Action).Should().OnlyHaveUniqueItems("动作名是查找键，重复会导致设置页错乱");
        ShortcutConfig.Defaults.Should().OnlyContain(d =>
            !string.IsNullOrWhiteSpace(d.Action) &&
            !string.IsNullOrWhiteSpace(d.DefaultGesture) &&
            !string.IsNullOrWhiteSpace(d.DescriptionKey));
    }

    [Fact]
    public void GetDefaults_ShouldMirrorDefaultsTable()
    {
        var list = ShortcutConfig.GetDefaults();

        list.Should().HaveCount(ShortcutConfig.Defaults.Length);
        list.Select(c => c.Action).Should().Equal(ShortcutConfig.Defaults.Select(d => d.Action));
        list.Select(c => c.KeyGesture).Should().Equal(ShortcutConfig.Defaults.Select(d => d.DefaultGesture));
    }

    [Fact]
    public void GetDefaults_ShouldReturnFreshInstances()
    {
        // 必须是新实例：设置页会就地改写列表，若复用同一批对象会污染后续调用
        var a = ShortcutConfig.GetDefaults();
        var b = ShortcutConfig.GetDefaults();
        a.Should().NotBeSameAs(b);
        a[0].Should().NotBeSameAs(b[0]);
    }

    [Fact]
    public void LoadAll_ShouldNeverReturnNullOrEmpty()
    {
        // 不依赖用户是否存过 shortcuts.json：文件缺失/损坏都必须优雅回退到默认值
        ShortcutConfig.LoadAll().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Ctrl+F", Key.F, ModifierKeys.Control)]
    [InlineData("F5", Key.F5, ModifierKeys.None)]
    [InlineData("Ctrl+Shift+A", Key.A, ModifierKeys.Control | ModifierKeys.Shift)]
    public void ParseGesture_ValidInput_ShouldParse(string gesture, Key key, ModifierKeys modifiers)
    {
        var parsed = ShortcutConfig.ParseGesture(gesture);

        parsed.Should().NotBeNull();
        parsed!.Key.Should().Be(key);
        parsed.Modifiers.Should().Be(modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotARealKey")]
    [InlineData("Ctrl+Nope")]
    public void ParseGesture_InvalidOrBlankInput_ShouldReturnNullNotThrow(string gesture)
        => ShortcutConfig.ParseGesture(gesture).Should().BeNull();

    [Fact]
    public void ParseGesture_Blank_ShouldReturnNull_SoNoPhantomBindingIsRegistered()
    {
        // 回归护栏：KeyGestureConverter.ConvertFromString("") **不抛异常**，而是返回
        // KeyGesture{ Key = None }。若 ParseGesture 直接透传，MainWindow.LoadInputBindings 的
        // `if (gesture != null)` 会为"用户已清空"的项注册一个永不触发的空绑定。
        ShortcutConfig.ParseGesture("").Should().BeNull("空手势必须归一化为「未绑定」");
    }
}
