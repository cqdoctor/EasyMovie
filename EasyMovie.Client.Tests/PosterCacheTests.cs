using System.IO;
using EasyMovie.Client.Helpers;
using FluentAssertions;

namespace EasyMovie.Client.Tests;

/// <summary>
/// 海报磁盘缓存冒烟测试（真实文件 IO，落在 LocalApplicationData/EasyMovie/Posters）。
/// 用远离真实 Movie.Id 的高位 Id，并在 finally 里清理，避免污染用户缓存。
/// </summary>
public class PosterCacheTests
{
    /// <summary>远离真实自增 Id，确保测试不会碰到用户已有海报。</summary>
    private const int TestId = 2_000_000_001;

    private static void Cleanup() => PosterCache.Delete(TestId);

    [Fact]
    public void PathFor_ShouldBeUnderPostersDirWithIdFilename()
    {
        var path = PosterCache.PathFor(123);

        Path.GetFileName(path).Should().Be("123.jpg");
        path.Should().Contain("Posters");
    }

    [Fact]
    public void SaveThenLoadThenDelete_ShouldRoundTrip()
    {
        Cleanup();
        try
        {
            PosterCache.Exists(TestId).Should().BeFalse("前置条件：测试 Id 不应有残留文件");

            var payload = new byte[] { 1, 2, 3, 4, 5, 0xFF, 0x00, 0x7F };
            PosterCache.Save(TestId, payload);

            PosterCache.Exists(TestId).Should().BeTrue();
            PosterCache.LoadBytes(TestId).Should().Equal(payload, "写盘/读盘必须字节级一致");

            PosterCache.Delete(TestId);
            PosterCache.Exists(TestId).Should().BeFalse("删除后必须真正落盘移除，否则就是孤儿文件泄漏");
            PosterCache.LoadBytes(TestId).Should().BeNull();
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void Save_WithNullOrEmpty_ShouldBeNoOp()
    {
        Cleanup();
        try
        {
            PosterCache.Save(TestId, null);
            PosterCache.Exists(TestId).Should().BeFalse();

            PosterCache.Save(TestId, Array.Empty<byte>());
            PosterCache.Exists(TestId).Should().BeFalse("空数组不是有效海报，不应产生 0 字节脏文件");
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void Save_ShouldOverwriteExistingFile()
    {
        Cleanup();
        try
        {
            PosterCache.Save(TestId, new byte[] { 1, 1, 1 });
            PosterCache.Save(TestId, new byte[] { 9, 9 });

            PosterCache.LoadBytes(TestId).Should().Equal(new byte[] { 9, 9 });
        }
        finally { Cleanup(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Delete_NonPositiveId_ShouldBeNoOpNotThrow(int id)
    {
        var act = () => PosterCache.Delete(id);
        act.Should().NotThrow();
    }

    [Fact]
    public void LoadBytes_MissingId_ShouldReturnNull()
        => PosterCache.LoadBytes(TestId).Should().BeNull();

    [Fact]
    public void LoadImageSource_MissingId_ShouldReturnNull()
        => PosterCache.LoadImageSource(TestId).Should().BeNull("无海报时必须返回 null 让调用方回退 PosterData");

    [Fact]
    public async Task GetThumbnailAsync_NonPositiveId_ShouldReturnNull()
        => (await PosterCache.GetThumbnailAsync(0, new byte[] { 1, 2, 3 }, 100, 100)).Should().BeNull();

    [Fact]
    public async Task GetThumbnailAsync_UndecodableBytes_ShouldReturnNullNotThrow()
    {
        // 损坏/非图片字节：解码必然失败，服务必须吞掉异常并返回 null（UI 保留占位）
        var result = await PosterCache.GetThumbnailAsync(TestId, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 120, 180);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetThumbnailAsync_NoDataAtAll_ShouldReturnNull()
        => (await PosterCache.GetThumbnailAsync(TestId, null, 120, 180)).Should().BeNull();
}
