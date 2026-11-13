using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace DshOrbit;

/// <summary>
/// 极简 SVG path 解析器：把 <c>d</c> 属性转成 GDI+ 路径（24×24 视口坐标）。
/// 支持 M/L/H/V/C/S/Q/T/A/Z（含相对指令）；圆弧按折线细分（图标尺寸下无可见差异）。
/// </summary>
internal static class SvgPath
{
    public static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath();
        var tokens = Tokenize(d);
        var i = 0;

        var current = new PointF(0, 0);
        var start = new PointF(0, 0);
        var lastCubicControl = new PointF(0, 0);
        var lastQuadControl = new PointF(0, 0);
        var hasSegment = false;
        char previous = '\0';

        void MoveTo(PointF p)
        {
            if (hasSegment)
            {
                path.StartFigure();
            }

            current = p;
            start = p;
            hasSegment = false;
        }

        void LineTo(PointF p)
        {
            if (!hasSegment)
            {
                path.StartFigure();
            }

            path.AddLine(current, p);
            current = p;
            hasSegment = true;
        }

        void CubicTo(PointF c1, PointF c2, PointF end)
        {
            if (!hasSegment)
            {
                path.StartFigure();
            }

            path.AddBezier(current, c1, c2, end);
            lastCubicControl = c2;
            current = end;
            hasSegment = true;
        }

        void QuadTo(PointF c, PointF end)
        {
            // 二次贝塞尔 → 三次贝塞尔
            var c1 = new PointF(current.X + (2f / 3f * (c.X - current.X)), current.Y + (2f / 3f * (c.Y - current.Y)));
            var c2 = new PointF(end.X + (2f / 3f * (c.X - end.X)), end.Y + (2f / 3f * (c.Y - end.Y)));
            CubicTo(c1, c2, end);
            lastQuadControl = c;
        }

        while (i < tokens.Count)
        {
            var token = tokens[i];
            char command;
            if (char.IsLetter(token[0]))
            {
                command = token[0];
                i++;
            }
            else
            {
                // 隐式重复上一个命令
                command = previous switch
                {
                    'M' => 'L',
                    'm' => 'l',
                    _ => previous,
                };
                if (command == '\0')
                {
                    break;
                }
            }

            var relative = char.IsLower(command);
            var op = char.ToUpperInvariant(command);
            var origin = relative ? current : new PointF(0, 0);

            switch (op)
            {
                case 'M':
                {
                    var p = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    MoveTo(p);
                    break;
                }

                case 'L':
                {
                    var p = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    LineTo(p);
                    break;
                }

                case 'H':
                {
                    var x = Number(tokens, ref i) + (relative ? current.X : 0);
                    LineTo(new PointF(x, current.Y));
                    break;
                }

                case 'V':
                {
                    var y = Number(tokens, ref i) + (relative ? current.Y : 0);
                    LineTo(new PointF(current.X, y));
                    break;
                }

                case 'C':
                {
                    var c1 = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    var c2 = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    var end = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    CubicTo(c1, c2, end);
                    break;
                }

                case 'S':
                {
                    var reflected = previous is 'C' or 'c' or 'S' or 's'
                        ? new PointF((2 * current.X) - lastCubicControl.X, (2 * current.Y) - lastCubicControl.Y)
                        : current;
                    var c2 = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    var end = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    CubicTo(reflected, c2, end);
                    break;
                }

                case 'Q':
                {
                    var c = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    var end = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    QuadTo(c, end);
                    break;
                }

                case 'T':
                {
                    var c = previous is 'Q' or 'q' or 'T' or 't'
                        ? new PointF((2 * current.X) - lastQuadControl.X, (2 * current.Y) - lastQuadControl.Y)
                        : current;
                    var end = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    QuadTo(c, end);
                    break;
                }

                case 'A':
                {
                    var rx = Number(tokens, ref i);
                    var ry = Number(tokens, ref i);
                    var rotation = Number(tokens, ref i);
                    // 弧命令的两个旗标允许连写（"00"、"01"），必须按单字符读。
                    var largeArc = Flag(tokens, ref i);
                    var sweep = Flag(tokens, ref i);
                    var end = new PointF(origin.X + Number(tokens, ref i), origin.Y + Number(tokens, ref i));
                    ArcTo(path, current, rx, ry, rotation, largeArc, sweep, end, ref hasSegment);
                    current = end;
                    break;
                }

                case 'Z':
                {
                    if (hasSegment)
                    {
                        path.CloseFigure();
                    }

                    current = start;
                    hasSegment = false;
                    break;
                }
            }

            previous = command;
        }

        return path;
    }

    /// <summary>椭圆弧按折线细分（每段 ≤ 11.25°）。</summary>
    private static void ArcTo(
        GraphicsPath path,
        PointF from,
        double rx,
        double ry,
        double rotationDegrees,
        bool largeArc,
        bool sweep,
        PointF to,
        ref bool hasSegment)
    {
        if (rx == 0 || ry == 0 || from == to)
        {
            if (!hasSegment)
            {
                path.StartFigure();
            }

            path.AddLine(from, to);
            hasSegment = true;
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        var phi = rotationDegrees * Math.PI / 180.0;
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        var dx2 = (from.X - to.X) / 2.0;
        var dy2 = (from.Y - to.Y) / 2.0;
        var x1 = (cosPhi * dx2) + (sinPhi * dy2);
        var y1 = (-sinPhi * dx2) + (cosPhi * dy2);

        var lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1)
        {
            var factor = Math.Sqrt(lambda);
            rx *= factor;
            ry *= factor;
        }

        var numerator = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        var denominator = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        var coefficient = numerator <= 0 ? 0 : Math.Sqrt(numerator / denominator);
        if (largeArc == sweep)
        {
            coefficient = -coefficient;
        }

        var cx1 = coefficient * rx * y1 / ry;
        var cy1 = -coefficient * ry * x1 / rx;
        var cx = (cosPhi * cx1) - (sinPhi * cy1) + ((from.X + to.X) / 2.0);
        var cy = (sinPhi * cx1) + (cosPhi * cy1) + ((from.Y + to.Y) / 2.0);

        var startAngle = Math.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
        var endAngle = Math.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
        var delta = endAngle - startAngle;

        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        var steps = Math.Max(6, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 16)));
        if (!hasSegment)
        {
            path.StartFigure();
        }

        var previous = from;
        for (var step = 1; step <= steps; step++)
        {
            var angle = startAngle + (delta * step / steps);
            var ex = (cosPhi * rx * Math.Cos(angle)) - (sinPhi * ry * Math.Sin(angle)) + cx;
            var ey = (sinPhi * rx * Math.Cos(angle)) + (cosPhi * ry * Math.Sin(angle)) + cy;
            var point = step == steps
                ? to
                : new PointF((float)ex, (float)ey);
            path.AddLine(previous, point);
            previous = point;
        }

        hasSegment = true;
    }

    /// <summary>读一个布尔旗标；紧凑写法（如 "00"）会把剩余字符留在原地。</summary>
    private static bool Flag(List<string> tokens, ref int index)
    {
        if (index >= tokens.Count)
        {
            return false;
        }

        var token = tokens[index];
        if (token.Length <= 1)
        {
            index++;
            return token is "1" or "1.0";
        }

        tokens[index] = token[1..];
        return token[0] == '1';
    }

    private static float Number(List<string> tokens, ref int index)
    {
        if (index >= tokens.Count)
        {
            return 0f;
        }

        var token = tokens[index++];
        return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;
    }

    private static List<string> Tokenize(string d)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var ch in d)
        {
            if (char.IsLetter(ch) && ch is not 'e' and not 'E')
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }

                tokens.Add(ch.ToString());
            }
            else if (ch is ' ' or ',' or '\t' or '\n' or '\r')
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }
            }
            else if (ch == '.' && builder.ToString().Contains('.'))
            {
                tokens.Add(builder.ToString());
                builder.Clear();
                builder.Append(ch);
            }
            else if (ch == '-' && builder.Length > 0 && builder[^1] is not 'e' and not 'E')
            {
                tokens.Add(builder.ToString());
                builder.Clear();
                builder.Append(ch);
            }
            else
            {
                builder.Append(ch);
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }
}
