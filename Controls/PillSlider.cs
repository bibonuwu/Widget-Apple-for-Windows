using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinIsland.Controls
{
    /// <summary>Большой слайдер-«пилюля» как в Пункте управления iOS (яркость / громкость).</summary>
    public sealed class PillSlider : Grid
    {
        // цвета берутся из темы (ресурсы SliderTrack / SliderFill / SliderIconOn…, см. Theme.cs)
        const string IconOnFill = "SliderIconOnFill", IconOnTrack = "SliderIconOnTrack";

        readonly Border _fill;
        readonly TextBlock _icon;
        readonly RectangleGeometry _clip = new RectangleGeometry();
        readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        double _value;
        bool _drag;
        string _iconKey;

        public event Action<double> ValueChanged;
        public event Action UnavailableClick;

        public bool IsDragging => _drag;
        public bool Available { get; set; } = true;
        public double Level => _value;
        public string Glyph { get => _icon.Text; set => _icon.Text = value; }

        public PillSlider()
        {
            Height = 50;
            SetResourceReference(BackgroundProperty, "SliderTrack");
            Cursor = Cursors.Hand;
            Clip = _clip;
            RenderTransform = _scale;
            RenderTransformOrigin = new Point(0.5, 0.5);

            _fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _fill.SetResourceReference(Border.BackgroundProperty, "SliderFill");
            _icon = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                Margin = new Thickness(17, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            SetIcon(IconOnTrack);
            Children.Add(_fill);
            Children.Add(_icon);

            SizeChanged += (_, _) =>
            {
                double r = ActualHeight / 2;
                _clip.Rect = new Rect(RenderSize);
                _clip.RadiusX = r;
                _clip.RadiusY = r;
                Layout(false);
            };
        }

        public void SetLevel(double v, bool animate)
        {
            _value = Math.Clamp(v, 0, 1);
            Layout(animate);
        }

        void Layout(bool animate)
        {
            double w = _value * ActualWidth;
            if (animate) Anim.To(_fill, WidthProperty, w, 220, Anim.OutCubic);
            else { _fill.BeginAnimation(WidthProperty, null); _fill.Width = w; }
            SetIcon(w > 40 ? IconOnFill : IconOnTrack);
        }

        void SetIcon(string key)
        {
            if (_iconKey == key) return;
            _iconKey = key;
            _icon.SetResourceReference(TextBlock.ForegroundProperty, key);
        }

        void FromMouse(MouseEventArgs e)
        {
            double w = ActualWidth;
            SetLevel(w > 0 ? e.GetPosition(this).X / w : 0, false);
            ValueChanged?.Invoke(_value);
        }

        void Grow(double s, double ms, System.Windows.Media.Animation.IEasingFunction ease)
        {
            Anim.To(_scale, ScaleTransform.ScaleXProperty, s, ms, ease);
            Anim.To(_scale, ScaleTransform.ScaleYProperty, s, ms, ease);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!Available) { UnavailableClick?.Invoke(); return; }
            _drag = true;
            CaptureMouse();
            FromMouse(e);
            Grow(1.035, 320, Anim.Bouncy);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) FromMouse(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_drag) ReleaseMouseCapture();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (!_drag) return;
            _drag = false;
            Grow(1, 450, Anim.Bouncy);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (!Available) return;
            SetLevel(_value + 0.04 * e.Delta / 120.0, true);
            ValueChanged?.Invoke(_value);
        }
    }
}
