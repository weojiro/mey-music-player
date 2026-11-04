using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskbarPlayerProto;

// Everyone with a gallery account, published or not. The server gives this only to the owner of MMP.
public partial class UsersWindow : Window
{
    readonly MainWindow _main;

    public UsersWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Title = TitleText.Text = T("galUsers");
        SourceInitialized += (_, _) => OptionsWindow.DarkTitleBar(this);
        MaxHeight = SystemParameters.WorkArea.Height;
        Load();
    }

    string T(string key) => _main.Text(key);

    async void Load()
    {
        try
        {
            var users = await GalleryClient.Users();
            SubText.Text = string.Format(T("galUsersCount"), users.Count);
            foreach (var user in users) List.Children.Add(Row(user));
        }
        catch (GalleryError e) { SubText.Text = T(GalleryClient.ErrorKey(e.Code)); }
        catch (Exception e)
        {
            ErrorLog.Write("gallery users", e);
            SubText.Text = T("galErrServer");
        }
    }

    FrameworkElement Row(GalleryUser user)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = user.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (user.Role == "admin") name.Children.Add(Tag(T("galDeveloper"), Color.FromRgb(0xF2, 0xA0, 0x8A)));
        if (user.Banned) name.Children.Add(Tag(T("galBannedTag"), Color.FromRgb(0xFF, 0x9A, 0x9A)));

        string joined = DateTimeOffset.FromUnixTimeMilliseconds(user.CreatedAt).LocalDateTime.ToShortDateString();
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 9, 13, 9),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new StackPanel
            {
                Children =
                {
                    name,
                    new TextBlock { Text = $"{string.Format(T("galUserItems"), user.Items)}  ·  {joined}", FontSize = 12, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(0, 2, 0, 0) },
                },
            },
        };
    }

    static TextBlock Tag(string text, Color color) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color),
        Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
    };
}
