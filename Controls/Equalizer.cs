using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WinIsland.Controls
{
    /// <summary>
    /// «Живая» звуковая волна как в iOS. Анимация идёт только пока IsActive = true
    /// и ограничена 30 кадрами/с — почти ноль нагрузки.
    /// </summary>
    public sealed class Equalizer : StackPanel
    {
        static readonly double[] Dur = { 0.42, 0.31, 0.50, 0.36 };
        static readonly double[] Peak = { 1.0, 0.72, 0.92, 0.62 };
        static readonly double[] Low = { 0.30, 0.22, 0.36, 0.26 };

        readonly Rectangle[] _bars = new Rectangle[4];
        readonly ScaleTransform[] _sc = new ScaleTransform[4];
        bool _active;
        double _barH = 14;

        public static readonly DependencyProperty BarBrushProperty =
            DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(Equalizer),
                new PropertyMetadata(Brushes.White, (d, _) => ((Equalizer)d).ApplyBrush()));

        public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

        public double BarHeight
        {
            get => _barH;
            set { _barH = value; Height = value; foreach (var r in _bars) if (r != null) r.Height = value; }
        }

        public Equalizer()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;
            Height = _barH;
            for (int i = 0; i < _bars.Length; i++)
            {
                _sc[i] = new ScaleTransform(1, Low[i] * 0.8);
                _bars[i] = new Rectangle
                {
                    Width = 3,
                    Height = _barH,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                    RenderTransform = _sc[i],
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = BarBrush
                };
                Children.Add(_bars[i]);
            }
        }

        void ApplyBrush()
        {
            foreach (var r in _bars) if (r != null) r.Fill = BarBrush;
        }

        public bool IsActive
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                for (int i = 0; i < _bars.Length; i++)
                {
                    if (_active)
                    {
                        var a = new DoubleAnimation(Low[i], Peak[i], TimeSpan.FromSeconds(Dur[i]))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = Anim.InOutSine,
                            BeginTime = TimeSpan.FromMilliseconds(i * 60)
                        };
                        Timeline.SetDesiredFrameRate(a, 30);
                        _sc[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
                    }
                    else
                    {
                        Anim.To(_sc[i], ScaleTransform.ScaleYProperty, Low[i] * 0.8, 260, Anim.OutCubic);
                    }
                }
            }
        }
    }
}
