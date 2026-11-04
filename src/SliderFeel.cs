using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarPlayerProto;

// Every slider in MMP: press anywhere on the bar and drag, and the wheel moves it one step (1 %, or 0.5 dB).
// The slider's own way only drags from exactly on its knob: a press beside it just moves the knob there and stops.
static class SliderFeel
{
    const double Knob = 14;     // the knob's width in Theme.xaml

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(Wheel));
    }

    static void Down(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        slider.CaptureMouse();
        MoveTo(slider, e);
        e.Handled = true;
    }

    static void Move(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured) return;
        MoveTo(slider, e);
        e.Handled = true;
    }

    static void Up(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured) return;
        slider.ReleaseMouseCapture();
        e.Handled = true;
    }

    static void Wheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        Set(slider, slider.Value + (e.Delta > 0 ? Step(slider) : -Step(slider)));
        e.Handled = true;       // the page doesn't scroll under it
    }

    static void MoveTo(Slider slider, MouseEventArgs e)
    {
        double room = slider.ActualWidth - Knob;
        if (room <= 0) return;
        double share = Math.Clamp((e.GetPosition(slider).X - Knob / 2) / room, 0, 1);
        Set(slider, slider.Minimum + share * (slider.Maximum - slider.Minimum));
    }

    static double Step(Slider slider) => slider.TickFrequency > 0 ? slider.TickFrequency : 1;

    static void Set(Slider slider, double value)
    {
        double step = Step(slider);
        slider.Value = Math.Clamp(Math.Round(value / step) * step, slider.Minimum, slider.Maximum);
    }
}
