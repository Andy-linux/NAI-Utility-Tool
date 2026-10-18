using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Input;
using NAITool.Models;
using Windows.UI;

namespace NAITool.Controls;

public sealed partial class MaskCanvasControl
{
    private bool _isRegionSelecting;
    private bool _isRegionMoving;
    private InpaintRegion? _regionBeforeGesture;
    private Vector2 _regionAnchor;
    private Vector2 _regionPointerStart;
    private InpaintRegion? _cleanRegion;
    private InpaintRegion? _previewRegion;

    public InpaintRegion? SelectedRegion => _document.InpaintRegion;

    public bool ClearRegion()
    {
        if (!CanMoveImage || _document.InpaintRegion == null) return false;
        var snapshot = _document.GetMaskSnapshot();
        if (snapshot != null)
            _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight, _document.InpaintRegion);
        _document.InpaintRegion = null;
        ContentChanged?.Invoke();
        return true;
    }

    public bool CancelRegionSelection()
    {
        if (!_isRegionSelecting) return false;
        _document.InpaintRegion = _regionBeforeGesture;
        _isRegionSelecting = false;
        _canvas?.ReleasePointerCaptures();
        ContentChanged?.Invoke();
        return true;
    }

    private Vector2 RegionPoint(Vector2 point) => new(
        (float)Math.Clamp(InpaintRegion.Round(point.X / 8) * 8, 0, _canvasWidth),
        (float)Math.Clamp(InpaintRegion.Round(point.Y / 8) * 8, 0, _canvasHeight));

    private void BeginRegionSelection(Vector2 point, PointerRoutedEventArgs e)
    {
        if (!CanMoveImage) return;
        _regionBeforeGesture = _document.InpaintRegion;
        _regionPointerStart = RegionPoint(point);
        _regionAnchor = _regionPointerStart;
        _isRegionMoving = false;
        if (_regionBeforeGesture is { } region)
        {
            var corner = new Vector2((float)(region.X + region.Width), (float)(region.Y + region.Height));
            if (Vector2.Distance(point, corner) <= 32)
                _regionAnchor = new((float)region.X, (float)region.Y);
            else if (point.X >= region.X && point.X <= region.X + region.Width &&
                     point.Y >= region.Y && point.Y <= region.Y + region.Height)
                _isRegionMoving = true;
        }
        _isRegionSelecting = true;
        _canvas!.CapturePointer(e.Pointer);
        if (!_isRegionMoving) UpdateRegionSelection(point);
        e.Handled = true;
    }

    private void UpdateRegionSelection(Vector2 point)
    {
        var current = RegionPoint(point);
        if (_isRegionMoving && _regionBeforeGesture is { } original)
        {
            var delta = current - _regionPointerStart;
            _document.InpaintRegion = original with
            {
                X = Math.Clamp(original.X + delta.X, 0, Math.Max(0, _canvasWidth - original.Width)),
                Y = Math.Clamp(original.Y + delta.Y, 0, Math.Max(0, _canvasHeight - original.Height)),
            };
            return;
        }
        double dx = current.X - _regionAnchor.X, dy = current.Y - _regionAnchor.Y;
        double area = Math.Abs(dx * dy);
        if (area > 589824)
        {
            double factor = Math.Sqrt(589824 / area);
            dx *= factor;
            dy *= factor;
        }
        _document.InpaintRegion = new(
            Math.Min(_regionAnchor.X, _regionAnchor.X + dx), Math.Min(_regionAnchor.Y, _regionAnchor.Y + dy),
            Math.Abs(dx), Math.Abs(dy));
    }

    private void CompleteRegionSelection(PointerRoutedEventArgs e)
    {
        if (_document.InpaintRegion is { } region && (region.Width < 32 || region.Height < 32))
            _document.InpaintRegion = null;
        if (_document.InpaintRegion != _regionBeforeGesture)
        {
            var snapshot = _document.GetMaskSnapshot();
            if (snapshot != null)
                _undoManager.PushState(snapshot, _document.ImageOffset, _canvasWidth, _canvasHeight, _regionBeforeGesture);
        }
        _isRegionSelecting = false;
        _canvas!.ReleasePointerCapture(e.Pointer);
        ContentChanged?.Invoke();
        e.Handled = true;
    }

    private void DrawRegion(CanvasDrawingSession ds, float scale)
    {
        if (!IsMaskOverlayVisible || _isComparing || _document.InpaintRegion is not { } region) return;
        var color = Color.FromArgb(255, 255, 192, 64);
        ds.DrawRectangle((float)region.X, (float)region.Y, (float)region.Width, (float)region.Height, color, 2 / scale);
        float size = 8 / scale;
        ds.FillRectangle((float)(region.X + region.Width) - size / 2,
            (float)(region.Y + region.Height) - size / 2, size, size, color);
    }
}
