using System.Buffers.Binary;
using System.Text.Json;

namespace Expanse;

/// A window in Firefox's session file: the title of the tab you were in, its tabs' marks (AppMarks),
/// and where it was on the screen.
public sealed record FirefoxWindow(string Title, IReadOnlyList<string> Marks, double X, double Y);

/// Reads Firefox's session files (sessionstore.jsonlz4 and its like): "mozLz40\0", the size once
/// decompressed, and one LZ4 block of JSON.
public static class FirefoxSession
{
    const int HeaderSize = 12;
    /// Far more than any real session: a broken file doesn't have us ask for gigabytes.
    const int MostBytes = 256 << 20;

    /// `mark` turns a tab's address into what's kept of it. Throws InvalidDataException or
    /// JsonException on a file that isn't one (half written, say).
    public static List<FirefoxWindow> Read(ReadOnlySpan<byte> file, Func<string, string> mark)
    {
        if (file.Length < HeaderSize || !file[..8].SequenceEqual("mozLz40\0"u8)) throw new InvalidDataException("not a Firefox session file");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        if (size > MostBytes) throw new InvalidDataException("too big");
        var text = new byte[size];
        if (Lz4(file[HeaderSize..], text) != text.Length) throw new InvalidDataException("shorter than it says");
        return Parse(text, mark);
    }

    /// One LZ4 block. Returns how much of `to` it filled.
    public static int Lz4(ReadOnlySpan<byte> from, Span<byte> to)
    {
        int i = 0, o = 0;
        while (i < from.Length)
        {
            int token = from[i++];
            int literals = Length(from, ref i, token >> 4);
            if (literals > from.Length - i || literals > to.Length - o) throw new InvalidDataException("literals past the end");
            from.Slice(i, literals).CopyTo(to[o..]);
            i += literals;
            o += literals;
            if (i == from.Length) break; // the last sequence is literals only
            if (from.Length - i < 2) throw new InvalidDataException("cut off");
            int back = BinaryPrimitives.ReadUInt16LittleEndian(from[i..]);
            i += 2;
            int match = Length(from, ref i, token & 15);
            if (match > to.Length - o - 4) throw new InvalidDataException("match past the end");
            match += 4;
            if (back == 0 || back > o) throw new InvalidDataException("match from before the start");
            // A byte at a time: a match may run into what it's writing.
            for (int n = 0; n < match; n++, o++) to[o] = to[o - back];
        }
        return o;
    }

    static int Length(ReadOnlySpan<byte> from, ref int i, int first)
    {
        if (first != 15) return first;
        int n = first;
        byte more;
        do
        {
            if (i >= from.Length || n > MostBytes) throw new InvalidDataException("cut off");
            n += more = from[i++];
        } while (more == 255);
        return n;
    }

    /// The windows in a session's JSON, read as a stream: it can be many megabytes, nearly all of it
    /// of no interest here.
    public static List<FirefoxWindow> Parse(ReadOnlySpan<byte> json, Func<string, string> mark)
    {
        var found = new List<FirefoxWindow>();
        var r = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 1000 });
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new JsonException("not an object");
        while (NextProperty(ref r))
        {
            if (!Is(ref r, "windows"u8, JsonTokenType.StartArray)) { r.Skip(); continue; }
            while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                if (r.TokenType == JsonTokenType.StartObject) found.Add(Window(ref r, mark));
                else r.Skip();
        }
        return found;
    }

    static FirefoxWindow Window(ref Utf8JsonReader r, Func<string, string> mark)
    {
        var tabs = new List<(string url, string title)>();
        double x = double.NaN, y = double.NaN, selected = 1;
        while (NextProperty(ref r))
        {
            if (Is(ref r, "tabs"u8, JsonTokenType.StartArray))
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    if (r.TokenType == JsonTokenType.StartObject) tabs.Add(Tab(ref r));
                    else r.Skip();
            }
            else if (Is(ref r, "selected"u8, JsonTokenType.Number)) selected = r.GetDouble();
            else if (Is(ref r, "screenX"u8, JsonTokenType.Number)) x = r.GetDouble();
            else if (Is(ref r, "screenY"u8, JsonTokenType.Number)) y = r.GetDouble();
            else r.Skip();
        }
        string title = selected >= 1 && selected <= tabs.Count ? tabs[(int)selected - 1].title : "";
        return new(title, tabs.Where(t => Telling(t.url)).Select(t => mark(WithoutFragment(t.url))).Distinct().ToList(), x, y);
    }

    /// The page a tab is on: the `index`th (from 1) of its history.
    static (string url, string title) Tab(ref Utf8JsonReader r)
    {
        var pages = new List<(string url, string title)>();
        double index = 0;
        while (NextProperty(ref r))
        {
            if (Is(ref r, "entries"u8, JsonTokenType.StartArray))
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    if (r.TokenType == JsonTokenType.StartObject) pages.Add(Page(ref r));
                    else r.Skip();
            }
            else if (Is(ref r, "index"u8, JsonTokenType.Number)) index = r.GetDouble();
            else r.Skip();
        }
        if (pages.Count == 0) return ("", "");
        var (url, title) = pages[index >= 1 && index <= pages.Count ? (int)index - 1 : pages.Count - 1];
        return (url, title.Length > 0 ? title : url);
    }

    static (string url, string title) Page(ref Utf8JsonReader r)
    {
        string url = "", title = "";
        while (NextProperty(ref r))
        {
            if (Is(ref r, "url"u8, JsonTokenType.String)) url = Text(ref r);
            else if (Is(ref r, "title"u8, JsonTokenType.String)) title = Text(ref r);
            else r.Skip();
        }
        return (url, title);
    }

    /// Half a surrogate pair (a title cut off in the middle of one) isn't text to .NET: none, then.
    static string Text(ref Utf8JsonReader r)
    {
        try { return r.GetString() ?? ""; }
        catch (InvalidOperationException) { return ""; }
    }

    static bool NextProperty(ref Utf8JsonReader r) => r.Read() && r.TokenType == JsonTokenType.PropertyName;

    /// Whether the property at hand is `name` with a value of that kind, and moves to the value if so.
    /// If the kind is another, the value is left to Skip, which from a property's name skips its value.
    static bool Is(ref Utf8JsonReader r, ReadOnlySpan<byte> name, JsonTokenType kind)
    {
        if (r.TokenType != JsonTokenType.PropertyName || !r.ValueTextEquals(name)) return false;
        var ahead = r;
        if (!ahead.Read() || ahead.TokenType != kind) return false;
        r = ahead;
        return true;
    }

    /// A blank tab says nothing about which window it's in.
    static bool Telling(string url) => url.Length > 0 && url is not ("about:blank" or "about:newtab" or "about:home" or "about:privatebrowsing" or "about:welcome");

    /// Pages move about within themselves by what's after the #.
    static string WithoutFragment(string url) => url.IndexOf('#') is int i and >= 0 ? url[..i] : url;
}
