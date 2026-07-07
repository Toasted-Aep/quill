using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.Geometry;

namespace Quill.Services;

public abstract class EqNode
{
    public float Width { get; set; }
    public float Height { get; set; }
    public float Baseline { get; set; }

    public abstract void Measure(CanvasDevice device, float fontSize, string fontName);
    public abstract void Draw(CanvasDrawingSession ds, float x, float y, float fontSize, string fontName, Windows.UI.Color color);
}

public class EqTextNode : EqNode
{
    public string Text { get; }
    public EqTextNode(string text) { Text = text; }

    public override void Measure(CanvasDevice device, float fontSize, string fontName)
    {
        using var layout = new CanvasTextLayout(device, Text, new CanvasTextFormat { FontFamily = fontName, FontSize = fontSize }, 0, 0);
        Width = (float)layout.LayoutBounds.Width;
        Height = (float)layout.LayoutBounds.Height;
        
        // Compute baseline from line metrics
        var metrics = layout.LineMetrics;
        if (metrics.Length > 0)
        {
            Baseline = (float)metrics[0].Baseline;
        }
        else
        {
            Baseline = fontSize * 0.8f;
        }
    }

    public override void Draw(CanvasDrawingSession ds, float x, float y, float fontSize, string fontName, Windows.UI.Color color)
    {
        ds.DrawText(Text, x, y, color, new CanvasTextFormat { FontFamily = fontName, FontSize = fontSize });
    }
}

public class EqFractionNode : EqNode
{
    public EqNode Numerator { get; }
    public EqNode Denominator { get; }
    public EqFractionNode(EqNode num, EqNode den) { Numerator = num; Denominator = den; }

    public override void Measure(CanvasDevice device, float fontSize, string fontName)
    {
        float subSize = fontSize * 0.8f;
        Numerator.Measure(device, subSize, fontName);
        Denominator.Measure(device, subSize, fontName);

        Width = Math.Max(Numerator.Width, Denominator.Width) + 8f;
        
        // Total height is num height + gap + den height + border
        float gap = fontSize * 0.15f;
        Height = Numerator.Height + Denominator.Height + gap * 2;
        Baseline = Numerator.Height + gap; // Baseline aligns with the fraction line
    }

    public override void Draw(CanvasDrawingSession ds, float x, float y, float fontSize, string fontName, Windows.UI.Color color)
    {
        float lineY = y + Baseline;
        
        // Draw numerator
        float numX = x + (Width - Numerator.Width) / 2;
        Numerator.Draw(ds, numX, y, fontSize * 0.8f, fontName, color);
        
        // Draw fraction line
        ds.DrawLine(x, lineY, x + Width, lineY, color, fontSize * 0.06f);
        
        // Draw denominator
        float denX = x + (Width - Denominator.Width) / 2;
        float gap = fontSize * 0.15f;
        Denominator.Draw(ds, denX, lineY + gap, fontSize * 0.8f, fontName, color);
    }
}

public class EqRadicalNode : EqNode
{
    public EqNode Content { get; }
    public EqRadicalNode(EqNode content) { Content = content; }

    public override void Measure(CanvasDevice device, float fontSize, string fontName)
    {
        Content.Measure(device, fontSize, fontName);
        float pad = fontSize * 0.15f;
        Width = Content.Width + pad * 2.5f;
        Height = Content.Height + pad * 1.5f;
        Baseline = Content.Baseline + pad;
    }

    public override void Draw(CanvasDrawingSession ds, float x, float y, float fontSize, string fontName, Windows.UI.Color color)
    {
        float pad = fontSize * 0.15f;
        float capWidth = pad * 1.5f;
        float contentX = x + capWidth + pad;
        float contentY = y + pad;
        
        // Draw content
        Content.Draw(ds, contentX, contentY, fontSize, fontName, color);
        
        // Draw radical symbol
        float topY = y + pad / 2;
        float bottomY = y + Content.Height + pad;
        float midY = y + Content.Height * 0.6f + pad;
        
        var points = new Vector2[]
        {
            new Vector2(x, midY),
            new Vector2(x + pad * 0.4f, midY),
            new Vector2(x + pad * 0.8f, bottomY),
            new Vector2(x + capWidth, topY),
            new Vector2(x + Width, topY)
        };
        
        for (int i = 0; i < points.Length - 1; i++)
        {
            ds.DrawLine(points[i], points[i+1], color, fontSize * 0.06f);
        }
    }
}

public class EqScriptNode : EqNode
{
    public EqNode Base { get; }
    public EqNode? Super { get; }
    public EqNode? Sub { get; }

    public EqScriptNode(EqNode baseNode, EqNode? super, EqNode? sub)
    {
        Base = baseNode; Super = super; Sub = sub;
    }

    public override void Measure(CanvasDevice device, float fontSize, string fontName)
    {
        Base.Measure(device, fontSize, fontName);
        float scriptSize = fontSize * 0.7f;
        
        float rightWidth = 0;
        float superHeight = 0;
        float subHeight = 0;

        if (Super != null)
        {
            Super.Measure(device, scriptSize, fontName);
            rightWidth = Math.Max(rightWidth, Super.Width);
            superHeight = Super.Height;
        }
        if (Sub != null)
        {
            Sub.Measure(device, scriptSize, fontName);
            rightWidth = Math.Max(rightWidth, Sub.Width);
            subHeight = Sub.Height;
        }

        Width = Base.Width + rightWidth + 2f;
        
        float superRaise = Base.Baseline * 0.6f;
        float subLower = (Base.Height - Base.Baseline) * 0.6f;
        
        float top = Math.Min(0, -superRaise);
        float bottom = Math.Max(Base.Height, Base.Height + subLower);
        
        Height = bottom - top;
        Baseline = Base.Baseline - top;
    }

    public override void Draw(CanvasDrawingSession ds, float x, float y, float fontSize, string fontName, Windows.UI.Color color)
    {
        float scriptSize = fontSize * 0.7f;
        float baseOffset = 0;
        
        float superRaise = Base.Baseline * 0.6f;
        
        Base.Draw(ds, x, y + baseOffset, fontSize, fontName, color);
        
        if (Super != null)
        {
            Super.Draw(ds, x + Base.Width + 1f, y + baseOffset - superRaise + Base.Baseline - Super.Baseline, scriptSize, fontName, color);
        }
        if (Sub != null)
        {
            float subLower = (Base.Height - Base.Baseline) * 0.5f;
            Sub.Draw(ds, x + Base.Width + 1f, y + baseOffset + subLower + Base.Baseline, scriptSize, fontName, color);
        }
    }
}

public class EqGroupNode : EqNode
{
    public List<EqNode> Children { get; } = new();

    public override void Measure(CanvasDevice device, float fontSize, string fontName)
    {
        Width = 0;
        float maxAbove = 0;
        float maxBelow = 0;

        foreach (var child in Children)
        {
            child.Measure(device, fontSize, fontName);
            Width += child.Width;
            maxAbove = Math.Max(maxAbove, child.Baseline);
            maxBelow = Math.Max(maxBelow, child.Height - child.Baseline);
        }

        Height = maxAbove + maxBelow;
        Baseline = maxAbove;
    }

    public override void Draw(CanvasDrawingSession ds, float x, float y, float fontSize, string fontName, Windows.UI.Color color)
    {
        float curX = x;
        foreach (var child in Children)
        {
            float childY = y + Baseline - child.Baseline;
            child.Draw(ds, curX, childY, fontSize, fontName, color);
            curX += child.Width;
        }
    }
}

public static class EquationRenderer
{
    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ",
        ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ",
        ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ne"] = "≠", ["neq"] = "≠",
        ["approx"] = "≈", ["equiv"] = "≡", ["propto"] = "∝", ["infty"] = "∞",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["iint"] = "∬", ["oint"] = "∮",
        ["partial"] = "∂", ["nabla"] = "∇", ["to"] = "→", ["rightarrow"] = "→",
        ["leftarrow"] = "←", ["Rightarrow"] = "⇒", ["Leftrightarrow"] = "⇔",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["subseteq"] = "⊆",
        ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅", ["forall"] = "∀", ["exists"] = "∃"
    };

    public static EqNode Parse(string latex)
    {
        int index = 0;
        return ParseGroup(latex, ref index, null);
    }

    private static EqGroupNode ParseGroup(string s, ref int i, char? endChar)
    {
        var group = new EqGroupNode();
        
        while (i < s.Length)
        {
            char c = s[i];
            if (endChar.HasValue && c == endChar.Value)
            {
                i++; // consume end character
                break;
            }

            if (c == '\\')
            {
                i++;
                if (i >= s.Length) break;
                
                int start = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                string cmd = s[start..i];
                if (cmd.Length == 0 && i < s.Length) { cmd = s[i].ToString(); i++; }

                if (cmd == "frac")
                {
                    var num = ParseArg(s, ref i);
                    var den = ParseArg(s, ref i);
                    group.Children.Add(new EqFractionNode(num, den));
                }
                else if (cmd == "sqrt")
                {
                    var content = ParseArg(s, ref i);
                    group.Children.Add(new EqRadicalNode(content));
                }
                else if (Symbols.TryGetValue(cmd, out var sym))
                {
                    group.Children.Add(new EqTextNode(sym));
                }
                else
                {
                    group.Children.Add(new EqTextNode(cmd));
                }
            }
            else if (c == '{')
            {
                i++;
                var sub = ParseGroup(s, ref i, '}');
                group.Children.Add(sub);
            }
            else if (c == '^' || c == '_')
            {
                i++;
                var scriptVal = ParseArg(s, ref i);
                
                // Attach script to the last added node
                if (group.Children.Count > 0)
                {
                    var last = group.Children[^1];
                    group.Children.RemoveAt(group.Children.Count - 1);
                    if (c == '^')
                    {
                        group.Children.Add(new EqScriptNode(last, scriptVal, null));
                    }
                    else
                    {
                        group.Children.Add(new EqScriptNode(last, null, scriptVal));
                    }
                }
                else
                {
                    var emptyBase = new EqTextNode("");
                    if (c == '^')
                    {
                        group.Children.Add(new EqScriptNode(emptyBase, scriptVal, null));
                    }
                    else
                    {
                        group.Children.Add(new EqScriptNode(emptyBase, null, scriptVal));
                    }
                }
            }
            else
            {
                group.Children.Add(new EqTextNode(c.ToString()));
                i++;
            }
        }
        
        return group;
    }

    private static EqNode ParseArg(string s, ref int i)
    {
        if (i < s.Length && s[i] == '{')
        {
            i++;
            return ParseGroup(s, ref i, '}');
        }
        else if (i < s.Length)
        {
            var singleNode = new EqTextNode(s[i].ToString());
            i++;
            return singleNode;
        }
        return new EqTextNode("");
    }

    public static CanvasRenderTarget Render(CanvasDevice device, string latex, float fontSize, string fontName, Windows.UI.Color color)
    {
        var rootNode = Parse(latex);
        
        // Measure node
        rootNode.Measure(device, fontSize, fontName);
        
        // Create CanvasRenderTarget
        float pad = 4f;
        float width = Math.Max(20f, rootNode.Width + pad * 2);
        float height = Math.Max(20f, rootNode.Height + pad * 2);
        
        var renderTarget = new CanvasRenderTarget(device, width, height, 96);
        using (var ds = renderTarget.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            rootNode.Draw(ds, pad, pad, fontSize, fontName, color);
        }
        return renderTarget;
    }

    public static async Task<byte[]> RenderToPngBytesAsync(CanvasDevice device, string latex, float fontSize, string fontName, Windows.UI.Color color)
    {
        using var rt = Render(device, latex, fontSize, fontName, color);
        using var stream = new InMemoryRandomAccessStream();
        await rt.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        return bytes;
    }
}
