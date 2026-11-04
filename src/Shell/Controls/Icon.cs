using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace IWar3Key.Shell.Controls
{
    public sealed class Icon : FrameworkElement
    {
        private static readonly Dictionary<string, Geometry> Cache = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();
        private static readonly Brush DefaultStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x54)));

        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind),
            typeof(string),
            typeof(Icon),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(Icon),
            new FrameworkPropertyMetadata(DefaultStroke, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness),
            typeof(double),
            typeof(Icon),
            new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));

        public Icon()
        {
            Width = 14;
            Height = 14;
            IsHitTestVisible = false;
        }

        public string Kind
        {
            get => (string)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsNaN(Width) ? 14 : Width;
            double height = double.IsNaN(Height) ? 14 : Height;
            return new Size(width, height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            Geometry geometry = GetGeometry(Kind);
            if (geometry == null)
            {
                return;
            }

            Brush stroke = Stroke ?? DefaultStroke;
            var pen = new Pen(stroke, Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
                MiterLimit = 2
            };

            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0)
            {
                return;
            }

            double scale = size / 24.0;
            double offsetX = (ActualWidth - size) / 2.0;
            double offsetY = (ActualHeight - size) / 2.0;
            drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
            drawingContext.PushTransform(new ScaleTransform(scale, scale));
            drawingContext.DrawGeometry(null, pen, geometry);
            drawingContext.Pop();
            drawingContext.Pop();
        }

        private static Geometry GetGeometry(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return null;
            }

            lock (CacheLock)
            {
                if (Cache.TryGetValue(kind, out Geometry cached))
                {
                    return cached;
                }

                string data;
                if (!GeometryData.TryGetValue(kind, out data))
                {
                    return null;
                }

                try
                {
                    Geometry geometry = Geometry.Parse(data);
                    geometry.Freeze();
                    Cache[kind] = geometry;
                    return geometry;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        private static readonly Dictionary<string, string> GeometryData =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["logo"] = "M12,2.7 L4.9,5.3 V11.3 C4.9,15.7 7.9,19.3 12,20.8 C16.1,19.3 19.1,15.7 19.1,11.3 V5.3 Z M8.3,9.4 L10.1,15 L12,11.3 L13.9,15 L15.7,9.4",
                ["monitor"] = "M5,4.5 H19 A2,2 0 0 1 21,6.5 V15 A2,2 0 0 1 19,17 H5 A2,2 0 0 1 3,15 V6.5 A2,2 0 0 1 5,4.5 Z M9,20.5 L15,20.5 M12,17 L12,20.5",
                ["key"] = "M3.5,15.5 A4.5,4.5 0 1 0 12.5,15.5 A4.5,4.5 0 1 0 3.5,15.5 Z M11.5,12.5 L19.5,4.5 M16,5 L19,8 M13.5,7.5 L16.5,10.5",
                ["keyboard"] = "M4.8,6 H19.2 A2,2 0 0 1 21.2,8 V16 A2,2 0 0 1 19.2,18 H4.8 A2,2 0 0 1 2.8,16 V8 A2,2 0 0 1 4.8,6 Z M6,9.4 L6.01,9.4 M9,9.4 L9.01,9.4 M12,9.4 L12.01,9.4 M15,9.4 L15.01,9.4 M18,9.4 L18.01,9.4 M7,12.5 L7.01,12.5 M10,12.5 L10.01,12.5 M13,12.5 L13.01,12.5 M16,12.5 L16.01,12.5 M8,15.6 L16,15.6",
                ["arrow"] = "M5,12 L19,12 M13.5,6.5 L19,12 L13.5,17.5",
                ["chat"] = "M20.5,11.3 C20.5,15.3 16.7,18.5 12,18.5 C11,18.5 10,18.35 9.1,18.07 L4.5,20.5 L5.6,17 C4.3,15.6 3.5,13.8 3.5,11.8 C3.5,7.8 7.3,4.6 12,4.6 C16.7,4.6 20.5,7.8 20.5,11.3 Z M8.5,10.2 L8.51,10.2 M12,10.2 L12.01,10.2 M15.5,10.2 L15.51,10.2",
                ["settings"] = "M4,7 L20,7 M4,12 L20,12 M4,17 L20,17 M7,7 A2,2 0 1 0 11,7 A2,2 0 1 0 7,7 Z M13,12 A2,2 0 1 0 17,12 A2,2 0 1 0 13,12 Z M5,17 A2,2 0 1 0 9,17 A2,2 0 1 0 5,17 Z",
                ["play"] = "M8.5,5.5 L18.5,12 L8.5,18.5 Z",
                ["plug"] = "M9,7 L9,3.5 M15,7 L15,3.5 M7,7 L17,7 L17,11.5 C17,14.26 14.76,16.5 12,16.5 C9.24,16.5 7,14.26 7,11.5 Z M12,16.5 L12,20.5",
                ["folder"] = "M4.5,6 H9.5 L11.5,8.5 H19.5 A1.5,1.5 0 0 1 21,10 V17 A1.5,1.5 0 0 1 19.5,18.5 H4.5 A1.5,1.5 0 0 1 3,17 V7.5 A1.5,1.5 0 0 1 4.5,6 Z",
                ["plus"] = "M12,5 L12,19 M5,12 L19,12",
                ["trash"] = "M4,7 L20,7 M9,7 L9,4.5 L15,4.5 L15,7 M6.5,7 L7.3,19.5 L16.7,19.5 L17.5,7 M10,10.5 L10,16 M14,10.5 L14,16",
                ["chevron"] = "M6,9 L12,15 L18,9",
                ["check"] = "M5,12.5 L9.5,17 L19,7",
                ["pencil"] = "M4.5,19.5 L8.7,18.5 L18.7,8.5 A2.4,2.4 0 0 0 15.3,5.1 L5.3,15.1 Z M13.8,6.6 L17.4,10.2",
                ["copy"] = "M9,9 L20,9 L20,20 L9,20 Z M5,15 L5,6.6 A1.6,1.6 0 0 1 6.6,5 L15,5",
                ["info"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,10.5 L12,16.5 M12,7.3 L12.01,7.3",
                ["bolt"] = "M13,2.5 L5.7,13 L10.5,13 L9.5,21.5 L18,10.4 L12.9,10.4 Z",
                ["minimize"] = "M4,12 L20,12",
                ["maximize"] = "M5,5 L19,5 L19,19 L5,19 Z",
                ["close"] = "M5,5 L19,19 M19,5 L5,19"
            };
    }
}
