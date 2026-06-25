using System.IO;
using System.Windows;
using EasyMovie.Core.Models;

namespace EasyMovie.Client.Views;

public static class VideoPlayerHelper
{
    public static void Play(Movie movie)
    {
        if (movie.FilePath == null || !File.Exists(movie.FilePath))
        {
            AppMessageBox.ShowWarning(
                string.Format(LanguageManager.GetString("Msg_FileNotFound"), movie.FilePath ?? ""),
                LanguageManager.GetString("Msg_Hint"));
            return;
        }

        if (Application.Current.MainWindow is MainWindow main)
        {
            main.ShowMoviePlayer(movie);
        }
        else
        {
            // 兜底：主窗口不可用时仍弹窗播放，不影响其他场景
            var player = new VideoPlayerWindow(movie);
            player.Show();
        }
    }
}