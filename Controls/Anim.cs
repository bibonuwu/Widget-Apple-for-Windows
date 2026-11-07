using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinIsland.Controls
{
    /// <summary>
    /// Пружинная кривая как в iOS: затухающие колебания (небольшой «отскок» в конце).
    /// Damping &lt; 1 — с отскоком, ближе к 1 — мягко без отскока.
    /// </summary>
    public sealed class SpringEase : EasingFunctionBase
    {
        public static readonly DependencyProperty DampingProperty =
            DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.7));

        public static readonly DependencyProperty StiffnessProperty =
            DependencyProperty.Register(nameof(Stiffness), typeof(double), typeof(SpringEase), new PropertyMetadata(10.0));

        public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
        public double Stiffness { get => (double)GetValue(StiffnessProperty); set => SetValue(StiffnessProperty, value); }

        public SpringEase() { EasingMode = EasingMode.EaseIn; }
        public SpringEase(double damping, double stiffness) : this() { Damping = damping; Stiffness = stiffness; }

        protected override double EaseInCore(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double z = Damping, w = Stiffness;
            return Raw(t, z, w) + t * (1 - Raw(1, z, w)); // гарантируем точное попадание в 1
        }

        static double Raw(double t, double z, double w)
        {
            if (z < 0.999)
            {
                double wd = w * Math.Sqrt(1 - z * z);
                return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
            }
            return 1 - Math.Exp(-w * t) * (1 + w * t);
        }

        protected override Freezable CreateInstanceCore() => new SpringEase();
    }

    public static class Anim
    {
        public static readonly IEasingFunction Bouncy = F(new SpringEase(0.64, 11));
        public static readonly IEasingFunction Spring = F(new SpringEase(0.8, 10));
        public static readonly IEasingFunction Soft = F(new SpringEase(0.92, 9));
        public static readonly IEasingFunction OutCubic = F(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction InOutSine = F(new SineEase { EasingMode = EasingMode.EaseInOut });

        static IEasingFunction F(EasingFunctionBase e) { e.Freeze(); return e; }

        /// <summary>
        /// Плавно ведёт свойство из ТЕКУЩЕГО значения в <paramref name="to"/>.
        /// Можно прерывать в любой момент — анимация продолжится с места, без рывков.
        /// </summary>
        public static void To(IAnimatable target, DependencyProperty dp, double to, double ms,
                              IEasingFunction ease = null, double delayMs = 0, Action done = null)
        {
            var d = (DependencyObject)target;
            double cur = (double)d.GetValue(dp);
            target.BeginAnimation(dp, null);
            if (!double.IsNaN(cur)) d.SetValue(dp, cur);

            if (!double.IsNaN(cur) && Math.Abs(cur - to) < 0.0005 && delayMs <= 0)
            {
                d.SetValue(dp, to);
                done?.Invoke();
                return;
            }

            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            if (done != null) a.Completed += (_, _) => done();
            target.BeginAnimation(dp, a);
        }

        public static void From(IAnimatable target, DependencyProperty dp, double from, double to, double ms, IEasingFunction ease = null)
        {
            target.BeginAnimation(dp, null);
            ((DependencyObject)target).SetValue(dp, from);
            To(target, dp, to, ms, ease);
        }

        public static void Tint(SolidColorBrush brush, Color to, double ms = 240)
        {
            if (brush.Color == to && !brush.HasAnimatedProperties) return;
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = OutCubic });
        }
    }

    /// <summary>Эффект «нажатия»: кнопка сжимается и пружинит обратно.</summary>
    public static class Fx
    {
        public static readonly DependencyProperty PressProperty =
            DependencyProperty.RegisterAttached("Press", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPress));

        public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);
        public static void SetPress(DependencyObject d, bool v) => d.SetValue(PressProperty, v);

        static void OnPress(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement el || !(bool)e.NewValue) return;
            var st = new ScaleTransform(1, 1);
            el.RenderTransform = st;
            el.RenderTransformOrigin = new Point(0.5, 0.5);

            el.PreviewMouseLeftButtonDown += (_, _) =>
            {
                Anim.To(st, ScaleTransform.ScaleXProperty, 0.86, 110, Anim.OutCubic);
                Anim.To(st, ScaleTransform.ScaleYProperty, 0.86, 110, Anim.OutCubic);
            };
            MouseButtonEventHandler up = (_, _) => Release(st);
            el.PreviewMouseLeftButtonUp += up;
            el.MouseLeave += (_, _) => Release(st);
        }

        static void Release(ScaleTransform st)
        {
            Anim.To(st, ScaleTransform.ScaleXProperty, 1, 480, Anim.Bouncy);
            Anim.To(st, ScaleTransform.ScaleYProperty, 1, 480, Anim.Bouncy);
        }
    }
}
