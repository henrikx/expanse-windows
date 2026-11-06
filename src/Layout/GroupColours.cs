namespace Expanse;

/// A group's colour, at full strength. What a box shows is worked out from it (see GroupColours).
public readonly record struct GroupColour(byte R, byte G, byte B)
{
    public int Packed => R << 16 | G << 8 | B;
    public static GroupColour FromPacked(int packed) => new((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
}

/// A colour as the pictures paint it: red, green, blue and alpha, each 0 to 1.
public readonly record struct Tint(float R, float G, float B, float A);

/// Group colours: a random one for a new group, the box's tint at any point of the fade to solid,
/// and the wheel they're picked on.
public static class GroupColours
{
    /// The colour of a group saved before groups had colours.
    public static readonly GroupColour Blue = new(76, 154, 255);

    /// New groups get a random hue at this saturation and brightness, which are the blue's.
    const double NewSaturation = 0.7, NewBrightness = 1;
    /// How far apart hues should be, in degrees, for two groups to look different.
    public const double HuesApart = 30;
    /// Below this saturation a colour is near grey, and its hue says nothing about how it looks.
    const double Greyish = 0.15;
    /// The wheel's diameter, at a display scale of 1.
    public const double WheelSize = 150;

    // MARK: A new group

    /// A random colour, `HuesApart` from those `taken` if a few tries find one, or else as far from
    /// them as those tries got.
    public static GroupColour Pick(IReadOnlyList<GroupColour> taken, Random random)
    {
        var hues = taken.Where(c => Saturation(c) >= Greyish).Select(Hue).ToList();
        double best = 0, bestApart = -1;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            double hue = random.NextDouble() * 360;
            double apart = hues.Count == 0 ? 360 : hues.Min(h => HueDistance(hue, h));
            if (apart > bestApart) (best, bestApart) = (hue, apart);
            if (apart >= HuesApart) break;
        }
        return FromHsv(best, NewSaturation, NewBrightness);
    }

    public static double HueDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    // MARK: The fade to solid

    /// The box's background. `solidity`, 0 to 1: from a light tint of the colour, which doesn't hide
    /// the wallpaper, to a dark shade of it, which shows on any wallpaper. The shades and strengths
    /// are the ones the blue always had.
    public static Tint Fill(GroupColour c, double solidity, bool hovered) => hovered
        ? Fade(c, solidity, shade: 0.588, curve: 1.67, 0.14f, 0.7f)
        : Fade(c, solidity, shade: 0.353, curve: 2, 0.08f, 0.55f);

    /// The box's border. Hovered, it's the colour itself at any solidity.
    public static Tint Edge(GroupColour c, double solidity, bool hovered) => hovered
        ? new Tint(c.R / 255f, c.G / 255f, c.B / 255f, 1)
        : Fade(c, solidity, shade: 0.784, curve: 1.57, 0.5f, 1);

    static Tint Fade(GroupColour c, double solidity, double shade, double curve, float lightAlpha, float solidAlpha)
    {
        float s = (float)Math.Clamp(solidity, 0, 1);
        float Channel(byte value)
        {
            double full = value / 255.0;
            return (float)(full + (shade * Math.Pow(full, curve) - full) * s);
        }
        return new Tint(Channel(c.R), Channel(c.G), Channel(c.B), lightAlpha + (solidAlpha - lightAlpha) * s);
    }

    // MARK: The wheel

    /// The wheel, in view space: centred on `swatch`, the circle it grows out of, but always on screen.
    public static RectD WheelRect(RectD swatch, RectD view, double k)
    {
        double size = Math.Round(WheelSize * k);
        return new RectD(
            Math.Clamp(swatch.MidX - size / 2, view.X, Math.Max(view.X, view.Right - size)),
            Math.Clamp(swatch.MidY - size / 2, view.Y, Math.Max(view.Y, view.Bottom - size)),
            size, size);
    }

    /// Where the wheel is on its way from `swatch` to `wheel`, with `t` going 0 to 1: quick at first,
    /// easing to a stop. Unless the screen's edge moved the wheel, this grows from the circle's centre.
    public static RectD WheelGrowing(RectD swatch, RectD wheel, double t)
    {
        if (t >= 1) return wheel;
        if (t <= 0) return swatch;
        double eased = 1 - (1 - t) * (1 - t) * (1 - t);
        double Mix(double from, double to) => from + (to - from) * eased;
        return new RectD(Mix(swatch.X, wheel.X), Mix(swatch.Y, wheel.Y), Mix(swatch.W, wheel.W), Mix(swatch.H, wheel.H));
    }

    /// `wheel` is square: this is whether `p` is on the round part of it.
    public static bool OnWheel(RectD wheel, Vec p) => wheel.Center.DistanceTo(p) <= wheel.W / 2;

    /// The colour at `p`: the angle round the wheel is the hue and the distance from the centre the
    /// saturation. Past the rim counts as the rim, so a drag can go off the edge.
    public static GroupColour ColourOnWheel(RectD wheel, Vec p)
    {
        var d = p - wheel.Center;
        double hue = (Math.Atan2(d.Y, d.X) * 180 / Math.PI + 360) % 360;
        return FromHsv(hue, Math.Min(1, wheel.Center.DistanceTo(p) / (wheel.W / 2)), 1);
    }

    /// Where a colour is on the wheel. Brightness isn't on it, so a dark colour shows at its hue.
    public static Vec PointOnWheel(RectD wheel, GroupColour c)
    {
        double angle = Hue(c) * Math.PI / 180, distance = Saturation(c) * wheel.W / 2;
        return new Vec(wheel.MidX + Math.Cos(angle) * distance, wheel.MidY + Math.Sin(angle) * distance);
    }

    /// The wheel as `size` by `size` premultiplied BGRA pixels, with a smooth rim.
    public static byte[] WheelPixels(int size)
    {
        var pixels = new byte[size * size * 4];
        var wheel = new RectD(0, 0, size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var at = new Vec(x + 0.5, y + 0.5);
                double alpha = Math.Clamp(size / 2.0 - wheel.Center.DistanceTo(at) + 0.5, 0, 1);
                if (alpha == 0) continue;
                var c = ColourOnWheel(wheel, at);
                int i = (y * size + x) * 4;
                pixels[i] = (byte)Math.Round(c.B * alpha);
                pixels[i + 1] = (byte)Math.Round(c.G * alpha);
                pixels[i + 2] = (byte)Math.Round(c.R * alpha);
                pixels[i + 3] = (byte)Math.Round(255 * alpha);
            }
        return pixels;
    }

    // MARK: Hue, saturation and brightness

    /// `hue` in degrees; `saturation` and `brightness` 0 to 1.
    public static GroupColour FromHsv(double hue, double saturation, double brightness)
    {
        double Channel(double n)
        {
            double k = (n + hue / 60) % 6;
            return brightness * (1 - saturation * Math.Max(0, Math.Min(k, Math.Min(4 - k, 1))));
        }
        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new GroupColour(Byte(Channel(5)), Byte(Channel(3)), Byte(Channel(1)));
    }

    public static double Hue(GroupColour c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        double range = max - min;
        if (range == 0) return 0;
        double sixth = max == c.R ? (c.G - c.B) / range : max == c.G ? 2 + (c.B - c.R) / range : 4 + (c.R - c.G) / range;
        return (sixth * 60 + 360) % 360;
    }

    public static double Saturation(GroupColour c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max == 0 ? 0 : (max - min) / (double)max;
    }
}
