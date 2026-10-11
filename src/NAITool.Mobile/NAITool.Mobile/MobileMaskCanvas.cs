using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace NAITool.Mobile;

/// <summary>
/// Touch-first inpaint mask: vector strokes rather than one full RGBA snapshot per undo.
/// The export dimensions are image/canvas pixels, independent of phone DPI and zoom.
/// </summary>
public sealed class MobileMaskCanvas : SKCanvasElement
{
    private sealed record Stroke(bool Eraser, float Radius, List<SKPoint> Points);
    private readonly List<Stroke> _strokes = [];
    private readonly Stack<Stroke> _redo = new();
    private readonly Dictionary<uint, Point> _contacts = [];
    private Stroke? _active;
    private uint _activeId;
    private SKBitmap? _source;
    private float _zoom = 1f;
    private float _panX, _panY;
    private bool _gesturing;
    private float _lastDistance;
    private Point _lastCenter;
    private bool _eraser;
    private float _radius = 16;

    public int CanvasWidth { get; private set; }
    public int CanvasHeight { get; private set; }
    public bool HasImage => _source != null;
    public bool HasMask => _strokes.Any(s => !s.Eraser);
    public bool Eraser
    {
        get => _eraser;
        set => _eraser = value;
    }

    public float Radius
    {
        get => _radius;
        set => _radius = Math.Clamp(value, 2, 150);
    }

    public MobileMaskCanvas()
    {
        ManipulationMode = ManipulationModes.All;
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += Cancelled;
        PointerCaptureLost += Cancelled;
        SizeChanged += (_, _) => Invalidate();
    }

    public void LoadImage(byte[] image, int width, int height)
    {
        using var decoded = SKBitmap.Decode(image) ?? throw new InvalidDataException("Cannot decode image for inpaint.");
        if (width <= 0 || height <= 0 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid inpaint canvas size.");

        // Like the desktop composite export, use one fixed resolution canvas. The source is
        // fitted with preserved aspect ratio into the requested NAI size.
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Cannot create Skia image surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        float fit = Math.Min((float)width / decoded.Width, (float)height / decoded.Height);
        float dw = decoded.Width * fit, dh = decoded.Height * fit;
        using (var paint = new SKPaint { IsAntialias = true })
            surface.Canvas.DrawBitmap(decoded, new SKRect((width - dw) / 2, (height - dh) / 2,
                                                         (width + dw) / 2, (height + dh) / 2), paint);
        using var snapshot = surface.Snapshot();
        _source?.Dispose();
        _source = SKBitmap.FromImage(snapshot);
        CanvasWidth = width;
        CanvasHeight = height;
        _strokes.Clear();
        _redo.Clear();
        _contacts.Clear();
        _active = null;
        _zoom = 1;
        _panX = _panY = 0;
        _gesturing = false;
        Invalidate();
    }

    public void Undo()
    {
        if (_strokes.Count == 0) return;
        var last = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        _redo.Push(last);
        Invalidate();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _strokes.Add(_redo.Pop());
        Invalidate();
    }

    public void Clear()
    {
        _strokes.Clear();
        _redo.Clear();
        _active = null;
        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        if (_source == null) return;
        canvas.Clear(new SKColor(25, 29, 36));
        canvas.Save();
        ApplyView(canvas, (float)area.Width, (float)area.Height);
        using (var paint = new SKPaint { IsAntialias = true })
            canvas.DrawBitmap(_source, new SKRect(0, 0, CanvasWidth, CanvasHeight), paint);

        // Erasing only changes the overlay, not the source image.
        canvas.SaveLayer(new SKRect(0, 0, CanvasWidth, CanvasHeight), null);
        foreach (var stroke in _strokes) DrawStroke(canvas, stroke, overlay: true);
        if (_active != null) DrawStroke(canvas, _active, overlay: true);
        canvas.Restore();
        canvas.Restore();
    }

    private void ApplyView(SKCanvas canvas, float viewWidth, float viewHeight)
    {
        float fit = Math.Min(viewWidth / CanvasWidth, viewHeight / CanvasHeight);
        float scale = fit * _zoom;
        canvas.Translate((viewWidth - CanvasWidth * scale) / 2 + _panX,
                         (viewHeight - CanvasHeight * scale) / 2 + _panY);
        canvas.Scale(scale);
    }

    private SKPoint ToImage(Point pointer)
    {
        float w = (float)Math.Max(ActualWidth, 1), h = (float)Math.Max(ActualHeight, 1);
        float fit = Math.Min(w / CanvasWidth, h / CanvasHeight);
        float scale = fit * _zoom;
        float x = ((float)pointer.X - (w - CanvasWidth * scale) / 2 - _panX) / scale;
        float y = ((float)pointer.Y - (h - CanvasHeight * scale) / 2 - _panY) / scale;
        return new SKPoint(Math.Clamp(x, 0, CanvasWidth), Math.Clamp(y, 0, CanvasHeight));
    }

    private static void DrawStroke(SKCanvas canvas, Stroke stroke, bool overlay)
    {
        if (stroke.Points.Count == 0) return;
        using var paint = new SKPaint
        {
            Color = overlay ? new SKColor(246, 76, 96, 130) : SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke.Radius * 2,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
            BlendMode = stroke.Eraser ? SKBlendMode.Clear : SKBlendMode.SrcOver,
        };
        using var path = new SKPath();
        path.MoveTo(stroke.Points[0]);
        foreach (var point in stroke.Points.Skip(1)) path.LineTo(point);
        canvas.DrawPath(path, paint);
        if (stroke.Points.Count == 1)
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle(stroke.Points[0], stroke.Radius, paint);
        }
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!HasImage) return;
        uint id = e.Pointer.PointerId;
        _contacts[id] = e.GetCurrentPoint(this).Position;
        if (_contacts.Count == 1)
        {
            _activeId = id;
            _active = new Stroke(_eraser, _radius, [ToImage(_contacts[id])]);
            _gesturing = false;
        }
        else
        {
            // The second finger is intended for pan/zoom, never as another mask stroke.
            _active = null;
            _gesturing = true;
            RememberGesture();
        }
        CapturePointer(e.Pointer);
        e.Handled = true;
        Invalidate();
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        uint id = e.Pointer.PointerId;
        if (!_contacts.ContainsKey(id)) return;
        _contacts[id] = e.GetCurrentPoint(this).Position;
        if (_contacts.Count >= 2 && _gesturing)
        {
            var pts = _contacts.Values.Take(2).ToArray();
            var center = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
            float distance = (float)Math.Sqrt(Math.Pow(pts[0].X - pts[1].X, 2) + Math.Pow(pts[0].Y - pts[1].Y, 2));
            if (_lastDistance > 5 && distance > 5)
            {
                float prevZoom = _zoom;
                _zoom = Math.Clamp(_zoom * distance / _lastDistance, 0.5f, 8f);
                float factor = _zoom / prevZoom;
                float w = (float)ActualWidth, h = (float)ActualHeight;
                // Zoom around the two-finger midpoint, not the viewport center.
                _panX = (float)(center.X - w / 2 - (center.X - w / 2 - _panX) * factor);
                _panY = (float)(center.Y - h / 2 - (center.Y - h / 2 - _panY) * factor);
            }
            _panX += (float)(center.X - _lastCenter.X);
            _panY += (float)(center.Y - _lastCenter.Y);
            _lastCenter = center;
            _lastDistance = distance;
        }
        else if (_active != null && id == _activeId)
        {
            var current = ToImage(_contacts[id]);
            if (SKPoint.Distance(current, _active.Points[^1]) >= 0.5f)
                _active.Points.Add(current);
        }
        e.Handled = true;
        Invalidate();
    }

    private void RememberGesture()
    {
        if (_contacts.Count < 2) return;
        var pts = _contacts.Values.Take(2).ToArray();
        _lastCenter = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
        _lastDistance = (float)Math.Sqrt(Math.Pow(pts[0].X - pts[1].X, 2) + Math.Pow(pts[0].Y - pts[1].Y, 2));
    }

    private void Released(object sender, PointerRoutedEventArgs e) => Finish(e);
    private void Cancelled(object sender, PointerRoutedEventArgs e) => Finish(e);

    private void Finish(PointerRoutedEventArgs e)
    {
        uint id = e.Pointer.PointerId;
        if (_active != null && id == _activeId)
        {
            _strokes.Add(_active);
            _redo.Clear();
            _active = null;
        }
        _contacts.Remove(id);
        if (_contacts.Count < 2) _gesturing = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        Invalidate();
    }

    public (string ImageBase64, string MaskBase64) ExportWebp()
    {
        if (_source == null || !HasMask) throw new InvalidOperationException("Load an image and paint a mask first.");
        using var imageSurface = SKSurface.Create(new SKImageInfo(CanvasWidth, CanvasHeight));
        imageSurface.Canvas.DrawBitmap(_source, 0, 0);
        using var picture = imageSurface.Snapshot();
        using var pixmap = picture.PeekPixels();
        using var webpStream = new SKDynamicMemoryWStream();
        if (!pixmap.Encode(webpStream, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75)))
            throw new InvalidOperationException("Cannot encode lossless WebP source.");
        using var encodedImage = webpStream.DetachAsData();

        using var maskSurface = SKSurface.Create(new SKImageInfo(CanvasWidth, CanvasHeight));
        maskSurface.Canvas.Clear(SKColors.Transparent);
        foreach (var stroke in _strokes) DrawStroke(maskSurface.Canvas, stroke, overlay: false);
        using var maskImage = maskSurface.Snapshot();
        using var encodedMask = maskImage.Encode(SKEncodedImageFormat.Webp, 50);
        return (Convert.ToBase64String(encodedImage.ToArray()), Convert.ToBase64String(encodedMask.ToArray()));
    }
}
