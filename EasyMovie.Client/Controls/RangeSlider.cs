using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;

namespace EasyMovie.Client.Controls
{
    /// <summary>双滑块范围选择器</summary>
    [TemplatePart(Name = "PART_LowerThumb", Type = typeof(Thumb))]
    [TemplatePart(Name = "PART_UpperThumb", Type = typeof(Thumb))]
    [TemplatePart(Name = "PART_SelectionRange", Type = typeof(Rectangle))]
    [TemplatePart(Name = "PART_Track", Type = typeof(FrameworkElement))]
    public class RangeSlider : Control
    {
        static RangeSlider()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(RangeSlider),
                new FrameworkPropertyMetadata(typeof(RangeSlider)));
        }

        #region Dependency Properties

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }
        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(0.0, OnMinMaxChanged));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }
        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(100.0, OnMinMaxChanged));

        public double LowerValue
        {
            get => (double)GetValue(LowerValueProperty);
            set => SetValue(LowerValueProperty, value);
        }
        public static readonly DependencyProperty LowerValueProperty =
            DependencyProperty.Register(nameof(LowerValue), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(0.0, OnValueChanged));

        public double UpperValue
        {
            get => (double)GetValue(UpperValueProperty);
            set => SetValue(UpperValueProperty, value);
        }
        public static readonly DependencyProperty UpperValueProperty =
            DependencyProperty.Register(nameof(UpperValue), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(100.0, OnValueChanged));

        public double TickFrequency
        {
            get => (double)GetValue(TickFrequencyProperty);
            set => SetValue(TickFrequencyProperty, value);
        }
        public static readonly DependencyProperty TickFrequencyProperty =
            DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(1.0));

        public bool IsSnapToTickEnabled
        {
            get => (bool)GetValue(IsSnapToTickEnabledProperty);
            set => SetValue(IsSnapToTickEnabledProperty, value);
        }
        public static readonly DependencyProperty IsSnapToTickEnabledProperty =
            DependencyProperty.Register(nameof(IsSnapToTickEnabled), typeof(bool), typeof(RangeSlider),
                new FrameworkPropertyMetadata(false));

        public string LowerText
        {
            get => (string)GetValue(LowerTextProperty);
            set => SetValue(LowerTextProperty, value);
        }
        public static readonly DependencyProperty LowerTextProperty =
            DependencyProperty.Register(nameof(LowerText), typeof(string), typeof(RangeSlider),
                new FrameworkPropertyMetadata(null));

        public string UpperText
        {
            get => (string)GetValue(UpperTextProperty);
            set => SetValue(UpperTextProperty, value);
        }
        public static readonly DependencyProperty UpperTextProperty =
            DependencyProperty.Register(nameof(UpperText), typeof(string), typeof(RangeSlider),
                new FrameworkPropertyMetadata(null));

        #endregion

        #region Events

        public static readonly RoutedEvent RangeChangedEvent =
            EventManager.RegisterRoutedEvent(nameof(RangeChanged), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(RangeSlider));

        public event RoutedEventHandler RangeChanged
        {
            add => AddHandler(RangeChangedEvent, value);
            remove => RemoveHandler(RangeChangedEvent, value);
        }

        #endregion

        private Thumb? _lowerThumb;
        private Thumb? _upperThumb;
        private Rectangle? _selectionRange;
        private FrameworkElement? _track;
        private bool _isDragging;
        private bool _draggingLower = true; // 当前拖动的是左滑块还是右滑块

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _lowerThumb = GetTemplateChild("PART_LowerThumb") as Thumb;
            _upperThumb = GetTemplateChild("PART_UpperThumb") as Thumb;
            _selectionRange = GetTemplateChild("PART_SelectionRange") as Rectangle;
            _track = GetTemplateChild("PART_Track") as FrameworkElement;

            if (_lowerThumb != null)
            {
                _lowerThumb.DragDelta += Thumb_DragDelta;
                _lowerThumb.DragStarted += (s, e) =>
                {
                    _isDragging = true;
                    _draggingLower = true;
                    Panel.SetZIndex(_lowerThumb, 10);
                };
                _lowerThumb.DragCompleted += (s, e) =>
                {
                    _isDragging = false;
                    Panel.SetZIndex(_lowerThumb, 1);
                    RaiseRangeChanged();
                };
            }
            if (_upperThumb != null)
            {
                _upperThumb.DragDelta += Thumb_DragDelta;
                _upperThumb.DragStarted += (s, e) =>
                {
                    _isDragging = true;
                    _draggingLower = false;
                    Panel.SetZIndex(_upperThumb, 10);
                };
                _upperThumb.DragCompleted += (s, e) =>
                {
                    _isDragging = false;
                    Panel.SetZIndex(_upperThumb, 2);
                    RaiseRangeChanged();
                };
            }

            UpdateText();
            UpdateVisuals();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateVisuals();
        }

        /// <summary>统一拖动处理：重合时根据方向自动切换滑块</summary>
        private void Thumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var trackLen = GetTrackLength();
            if (trackLen <= 0) return;
            var delta = e.HorizontalChange / trackLen * (Maximum - Minimum);

            // 当两个滑块重合时，根据拖动方向切换
            if (LowerValue == UpperValue && delta != 0)
            {
                if (delta < 0)
                    _draggingLower = true;  // 向左拖 → 移动左滑块
                else
                    _draggingLower = false; // 向右拖 → 移动右滑块
            }

            if (_draggingLower)
            {
                var newVal = SnapToTick(LowerValue + delta);
                newVal = Math.Max(Minimum, Math.Min(newVal, UpperValue));
                LowerValue = newVal;
            }
            else
            {
                var newVal = SnapToTick(UpperValue + delta);
                newVal = Math.Max(LowerValue, Math.Min(newVal, Maximum));
                UpperValue = newVal;
            }

            UpdateText();
            UpdateVisuals();
        }

        private double GetTrackLength()
        {
            if (_track == null) return 1;
            return Math.Max(1, _track.ActualWidth);
        }

        private double SnapToTick(double value)
        {
            if (!IsSnapToTickEnabled || TickFrequency <= 0) return value;
            var tick = TickFrequency;
            return Math.Round(value / tick) * tick;
        }

        private void UpdateVisuals()
        {
            if (_track == null) return;

            var range = Maximum - Minimum;
            if (range <= 0) return;

            var trackLen = GetTrackLength();
            var lowerPct = (LowerValue - Minimum) / range;
            var upperPct = (UpperValue - Minimum) / range;

            // 选中区域
            if (_selectionRange != null)
            {
                var left = trackLen * lowerPct;
                var width = trackLen * (upperPct - lowerPct);
                _selectionRange.Margin = new Thickness(left, 0, 0, 0);
                _selectionRange.Width = Math.Max(0, width);
            }

            // Thumb 位置（Margin 定位，Grid 布局，clamp 防止裁剪）
            if (_lowerThumb != null)
            {
                var left = trackLen * lowerPct - _lowerThumb.ActualWidth / 2;
                left = Math.Max(0, Math.Min(trackLen - _lowerThumb.ActualWidth, left));
                _lowerThumb.Margin = new Thickness(left, 0, 0, 0);
            }
            if (_upperThumb != null)
            {
                var left = trackLen * upperPct - _upperThumb.ActualWidth / 2;
                left = Math.Max(0, Math.Min(trackLen - _upperThumb.ActualWidth, left));
                _upperThumb.Margin = new Thickness(left, 0, 0, 0);
            }
        }

        private void UpdateText()
        {
            if (IsSnapToTickEnabled && TickFrequency >= 1)
            {
                LowerText = ((int)Math.Round(LowerValue)).ToString();
                UpperText = ((int)Math.Round(UpperValue)).ToString();
            }
            else
            {
                LowerText = LowerValue.ToString("G");
                UpperText = UpperValue.ToString("G");
            }
        }

        private void RaiseRangeChanged()
        {
            RaiseEvent(new RoutedEventArgs(RangeChangedEvent));
        }

        private static void OnMinMaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var slider = (RangeSlider)d;
            if (!slider._isDragging)
            {
                slider.LowerValue = Math.Max(slider.Minimum, Math.Min(slider.LowerValue, slider.UpperValue));
                slider.UpperValue = Math.Max(slider.LowerValue, Math.Min(slider.UpperValue, slider.Maximum));
                slider.UpdateText();
                slider.UpdateVisuals();
            }
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var slider = (RangeSlider)d;
            slider.UpdateText();
            slider.UpdateVisuals();
            if (!slider._isDragging)
                slider.RaiseRangeChanged();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (_track == null) return;

            var pos = e.GetPosition(_track);
            var pct = Math.Max(0, Math.Min(1, pos.X / _track.ActualWidth));
            var range = Maximum - Minimum;
            var clickVal = Minimum + pct * range;
            clickVal = SnapToTick(clickVal);

            // 点击靠近哪个 thumb 就移动哪个
            if (Math.Abs(clickVal - LowerValue) <= Math.Abs(clickVal - UpperValue))
            {
                LowerValue = Math.Max(Minimum, Math.Min(clickVal, UpperValue));
            }
            else
            {
                UpperValue = Math.Max(LowerValue, Math.Min(clickVal, Maximum));
            }
            UpdateText();
            UpdateVisuals();
            RaiseRangeChanged();
        }
    }
}
