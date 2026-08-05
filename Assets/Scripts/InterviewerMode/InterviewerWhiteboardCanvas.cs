using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public enum InterviewerWhiteboardTool
{
    Pen,
    Highlighter,
    Eraser
}

/// <summary>
/// Lightweight runtime whiteboard for the interview surface. It intentionally
/// has no dependency on the graph canvas and can later send completed strokes
/// or snapshots through IInterviewerRealtimeService.
/// </summary>
[RequireComponent(typeof(RawImage))]
public sealed class InterviewerWhiteboardCanvas : MonoBehaviour,
    IPointerDownHandler,
    IDragHandler,
    IPointerUpHandler
{
    public event Action Changed;

    private const int HistoryLimit = 20;

    private RawImage targetImage;
    private RectTransform targetRect;
    private Texture2D texture;
    private Color32[] pixels;
    private readonly List<Color32[]> undoHistory = new List<Color32[]>();
    private readonly List<Color32[]> redoHistory = new List<Color32[]>();
    private InterviewerWhiteboardTool tool = InterviewerWhiteboardTool.Pen;
    private Color32 brushColor = new Color32(36, 99, 235, 255);
    private int brushSize = 5;
    private bool drawing;
    private Vector2Int previousPixel;

    public InterviewerWhiteboardTool Tool
    {
        get { return tool; }
    }

    public int BrushSize
    {
        get { return brushSize; }
    }

    public void Initialize(int width, int height)
    {
        targetImage = GetComponent<RawImage>();
        targetRect = GetComponent<RectTransform>();
        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = "Interviewer Whiteboard";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        pixels = new Color32[width * height];
        FillPixels(new Color32(248, 250, 252, 255));
        ApplyPixels();
        targetImage.texture = texture;
        targetImage.color = Color.white;
    }

    private void OnDestroy()
    {
        if (texture != null)
        {
            Destroy(texture);
        }
    }

    public void SetTool(InterviewerWhiteboardTool nextTool)
    {
        tool = nextTool;
    }

    public void SetBrushColor(Color color)
    {
        brushColor = color;
    }

    public void SetBrushSize(int size)
    {
        brushSize = Mathf.Clamp(size, 1, 32);
    }

    public void Clear()
    {
        if (pixels == null)
        {
            return;
        }
        PushUndoSnapshot();
        FillPixels(new Color32(248, 250, 252, 255));
        ApplyPixels();
        NotifyChanged();
    }

    public void Undo()
    {
        if (undoHistory.Count == 0 || pixels == null)
        {
            return;
        }
        redoHistory.Add(ClonePixels(pixels));
        int index = undoHistory.Count - 1;
        pixels = undoHistory[index];
        undoHistory.RemoveAt(index);
        ApplyPixels();
        NotifyChanged();
    }

    public void Redo()
    {
        if (redoHistory.Count == 0 || pixels == null)
        {
            return;
        }
        undoHistory.Add(ClonePixels(pixels));
        int index = redoHistory.Count - 1;
        pixels = redoHistory[index];
        redoHistory.RemoveAt(index);
        ApplyPixels();
        NotifyChanged();
    }

    public byte[] EncodePng()
    {
        return texture == null ? new byte[0] : texture.EncodeToPNG();
    }

    public string SavePng(string directory)
    {
        if (texture == null)
        {
            throw new InvalidOperationException("Whiteboard is not initialized.");
        }
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            "whiteboard-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".png"
        );
        File.WriteAllBytes(path, texture.EncodeToPNG());
        return path;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        Vector2Int point;
        if (!TryGetPixel(eventData, out point))
        {
            return;
        }
        PushUndoSnapshot();
        drawing = true;
        previousPixel = point;
        DrawBrush(point.x, point.y);
        ApplyPixels();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!drawing)
        {
            return;
        }
        Vector2Int point;
        if (!TryGetPixel(eventData, out point))
        {
            return;
        }
        DrawLine(previousPixel, point);
        previousPixel = point;
        ApplyPixels();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (!drawing)
        {
            return;
        }
        drawing = false;
        NotifyChanged();
    }

    private bool TryGetPixel(PointerEventData eventData, out Vector2Int point)
    {
        point = Vector2Int.zero;
        if (texture == null || targetRect == null)
        {
            return false;
        }
        Vector2 local;
        if (
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                targetRect,
                eventData.position,
                eventData.pressEventCamera,
                out local
            )
        )
        {
            return false;
        }
        Rect rect = targetRect.rect;
        float normalizedX = Mathf.InverseLerp(rect.xMin, rect.xMax, local.x);
        float normalizedY = Mathf.InverseLerp(rect.yMin, rect.yMax, local.y);
        if (
            normalizedX < 0f ||
            normalizedX > 1f ||
            normalizedY < 0f ||
            normalizedY > 1f
        )
        {
            return false;
        }
        point = new Vector2Int(
            Mathf.Clamp(
                Mathf.RoundToInt(normalizedX * (texture.width - 1)),
                0,
                texture.width - 1
            ),
            Mathf.Clamp(
                Mathf.RoundToInt(normalizedY * (texture.height - 1)),
                0,
                texture.height - 1
            )
        );
        return true;
    }

    private void DrawLine(Vector2Int from, Vector2Int to)
    {
        int distance = Mathf.Max(
            Mathf.Abs(to.x - from.x),
            Mathf.Abs(to.y - from.y)
        );
        if (distance == 0)
        {
            DrawBrush(to.x, to.y);
            return;
        }
        for (int step = 0; step <= distance; step++)
        {
            float t = step / (float)distance;
            DrawBrush(
                Mathf.RoundToInt(Mathf.Lerp(from.x, to.x, t)),
                Mathf.RoundToInt(Mathf.Lerp(from.y, to.y, t))
            );
        }
    }

    private void DrawBrush(int centerX, int centerY)
    {
        int radius = tool == InterviewerWhiteboardTool.Highlighter
            ? brushSize * 2
            : brushSize;
        int squaredRadius = radius * radius;
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y > squaredRadius)
                {
                    continue;
                }
                int pixelX = centerX + x;
                int pixelY = centerY + y;
                if (
                    pixelX < 0 ||
                    pixelX >= texture.width ||
                    pixelY < 0 ||
                    pixelY >= texture.height
                )
                {
                    continue;
                }
                int index = pixelY * texture.width + pixelX;
                if (tool == InterviewerWhiteboardTool.Eraser)
                {
                    pixels[index] = new Color32(248, 250, 252, 255);
                }
                else if (tool == InterviewerWhiteboardTool.Highlighter)
                {
                    pixels[index] = Blend(
                        pixels[index],
                        brushColor,
                        0.16f
                    );
                }
                else
                {
                    pixels[index] = brushColor;
                }
            }
        }
    }

    private static Color32 Blend(Color32 background, Color32 foreground, float amount)
    {
        return new Color32(
            (byte)Mathf.RoundToInt(Mathf.Lerp(background.r, foreground.r, amount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(background.g, foreground.g, amount)),
            (byte)Mathf.RoundToInt(Mathf.Lerp(background.b, foreground.b, amount)),
            255
        );
    }

    private void PushUndoSnapshot()
    {
        if (pixels == null)
        {
            return;
        }
        undoHistory.Add(ClonePixels(pixels));
        if (undoHistory.Count > HistoryLimit)
        {
            undoHistory.RemoveAt(0);
        }
        redoHistory.Clear();
    }

    private static Color32[] ClonePixels(Color32[] source)
    {
        Color32[] copy = new Color32[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private void FillPixels(Color32 color)
    {
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = color;
        }
    }

    private void ApplyPixels()
    {
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
    }

    private void NotifyChanged()
    {
        if (Changed != null)
        {
            Changed();
        }
    }
}
