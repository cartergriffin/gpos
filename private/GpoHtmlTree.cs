using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

public class GpoHtmlItem
{
    public string[] Path;
    public string[] Names;
    public string[] Values;
}

public static class GpoHtmlTree
{
    public static List<GpoHtmlItem> Parse(string html)
    {
        GpoHtmlParser p = new GpoHtmlParser(html);
        return p.Run();
    }
}

internal class GpoHtmlFrame
{
    public bool IsHeading;
    public bool IsContainer;
    public string Title;
    public StringBuilder HeadText;
}

internal class GpoHtmlRow
{
    public List<string> Cells = new List<string>();
    public List<string> Headers;
}

internal class GpoHtmlTable
{
    public List<GpoHtmlRow> Rows = new List<GpoHtmlRow>();
    public List<string> Headers = new List<string>();
    public List<string> Cur;
    public int ThCount;
    public int TdCount;
    public StringBuilder Cell;
}

internal class GpoHtmlParser
{
    private string html;
    private int pos;
    private List<GpoHtmlItem> items = new List<GpoHtmlItem>();
    private List<GpoHtmlFrame> frames = new List<GpoHtmlFrame>();
    private List<GpoHtmlTable> tables = new List<GpoHtmlTable>();
    private StringBuilder loose = new StringBuilder();
    private string pending;

    public GpoHtmlParser(string h)
    {
        html = h;
    }

    public List<GpoHtmlItem> Run()
    {
        int n = html.Length;
        pos = 0;
        while (pos < n)
        {
            int lt = html.IndexOf('<', pos);
            if (lt < 0)
            {
                AddText(html.Substring(pos), true);
                break;
            }
            if (lt > pos)
            {
                AddText(html.Substring(pos, lt - pos), true);
            }
            if (lt + 1 >= n)
            {
                break;
            }
            char first = html[lt + 1];
            if (!(char.IsLetter(first) || first == '/' || first == '!' || first == '?'))
            {
                AddText("<", false);
                pos = lt + 1;
                continue;
            }
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int e = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                pos = (e < 0) ? n : e + 3;
                continue;
            }
            int j = lt + 1;
            char q = '\0';
            while (j < n)
            {
                char c = html[j];
                if (q != '\0')
                {
                    if (c == q) { q = '\0'; }
                }
                else if (c == '"' || c == '\'')
                {
                    q = c;
                }
                else if (c == '>')
                {
                    break;
                }
                j++;
            }
            if (j >= n)
            {
                break;
            }
            string tag = html.Substring(lt + 1, j - lt - 1);
            pos = j + 1;
            HandleTag(tag);
        }
        FlushLoose();
        return items;
    }

    private void HandleTag(string tag)
    {
        if (tag.Length == 0) { return; }
        bool closing = tag[0] == '/';
        string body = closing ? tag.Substring(1) : tag;
        if (body.Length == 0) { return; }
        if (body[0] == '!' || body[0] == '?') { return; }
        int k = 0;
        while (k < body.Length && !char.IsWhiteSpace(body[k]) && body[k] != '/') { k++; }
        string name = body.Substring(0, k).ToLowerInvariant();
        string attrs = body.Substring(k);
        switch (name)
        {
            case "div":
                if (closing) { CloseDiv(); } else { OpenDiv(attrs); }
                break;
            case "table":
                if (closing) { CloseTable(); } else { OpenTable(); }
                break;
            case "tr":
                if (closing) { EndRow(); } else { StartRow(); }
                break;
            case "td":
            case "th":
                if (closing) { EndCell(); } else { StartCell(name == "th"); }
                break;
            case "br":
                AddText("\n", false);
                break;
            case "li":
                if (!closing) { AddText("\n", false); }
                break;
            case "script":
            case "style":
                if (!closing) { SkipTo("</" + name); }
                break;
        }
    }

    private void SkipTo(string marker)
    {
        int idx = html.IndexOf(marker, pos, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            pos = html.Length;
            return;
        }
        int e = html.IndexOf('>', idx);
        pos = (e < 0) ? html.Length : e + 1;
    }

    private static string GetAttr(string attrs, string name)
    {
        int idx = 0;
        while (true)
        {
            idx = attrs.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { return null; }
            bool startOk = (idx == 0) || char.IsWhiteSpace(attrs[idx - 1]);
            int k = idx + name.Length;
            while (k < attrs.Length && char.IsWhiteSpace(attrs[k])) { k++; }
            if (startOk && k < attrs.Length && attrs[k] == '=')
            {
                k++;
                while (k < attrs.Length && char.IsWhiteSpace(attrs[k])) { k++; }
                if (k >= attrs.Length) { return ""; }
                char c = attrs[k];
                if (c == '"' || c == '\'')
                {
                    int e = attrs.IndexOf(c, k + 1);
                    return (e < 0) ? attrs.Substring(k + 1) : attrs.Substring(k + 1, e - k - 1);
                }
                int s = k;
                while (k < attrs.Length && !char.IsWhiteSpace(attrs[k])) { k++; }
                return attrs.Substring(s, k - s);
            }
            idx = idx + name.Length;
        }
    }

    private static bool IsHeadingClass(string cls)
    {
        if (cls == null) { return false; }
        cls = cls.Trim();
        if (cls.Length < 3 || !cls.StartsWith("he", StringComparison.Ordinal)) { return false; }
        int k = 2;
        int digits = 0;
        while (k < cls.Length && char.IsDigit(cls[k])) { k++; digits++; }
        if (digits == 0) { return false; }
        string rest = cls.Substring(k);
        return rest == "" || rest == "h" || rest == "_expanded" || rest == "h_expanded";
    }

    private void OpenDiv(string attrs)
    {
        string cls = GetAttr(attrs, "class");
        GpoHtmlFrame f = new GpoHtmlFrame();
        if (IsHeadingClass(cls))
        {
            FlushLoose();
            f.IsHeading = true;
            f.HeadText = new StringBuilder();
        }
        else if (cls != null && cls.Trim() == "container")
        {
            FlushLoose();
            f.IsContainer = true;
            f.Title = pending;
            pending = null;
        }
        frames.Add(f);
    }

    private void CloseDiv()
    {
        if (frames.Count == 0) { return; }
        GpoHtmlFrame f = frames[frames.Count - 1];
        if (f.IsHeading)
        {
            pending = Clean(f.HeadText.ToString());
        }
        else
        {
            FlushLoose();
        }
        frames.RemoveAt(frames.Count - 1);
    }

    private void OpenTable()
    {
        FlushLoose();
        tables.Add(new GpoHtmlTable());
    }

    private void CloseTable()
    {
        if (tables.Count == 0) { return; }
        EndRow();
        GpoHtmlTable t = tables[tables.Count - 1];
        tables.RemoveAt(tables.Count - 1);
        EmitTable(t);
    }

    private void StartRow()
    {
        if (tables.Count == 0) { return; }
        GpoHtmlTable t = tables[tables.Count - 1];
        if (t.Cur != null) { EndRow(); }
        t.Cur = new List<string>();
        t.ThCount = 0;
        t.TdCount = 0;
    }

    private void EndRow()
    {
        if (tables.Count == 0) { return; }
        GpoHtmlTable t = tables[tables.Count - 1];
        EndCell();
        if (t.Cur != null && t.Cur.Count > 0)
        {
            if (t.TdCount == 0 && t.ThCount > 0)
            {
                t.Headers = t.Cur;
            }
            else
            {
                GpoHtmlRow r = new GpoHtmlRow();
                r.Cells = t.Cur;
                r.Headers = t.Headers;
                t.Rows.Add(r);
            }
        }
        t.Cur = null;
    }

    private void StartCell(bool isTh)
    {
        if (tables.Count == 0) { return; }
        GpoHtmlTable t = tables[tables.Count - 1];
        if (t.Cell != null) { EndCell(); }
        if (t.Cur == null) { StartRow(); }
        t.Cell = new StringBuilder();
        if (isTh) { t.ThCount++; } else { t.TdCount++; }
    }

    private void EndCell()
    {
        if (tables.Count == 0) { return; }
        GpoHtmlTable t = tables[tables.Count - 1];
        if (t.Cell == null) { return; }
        string txt = Clean(t.Cell.ToString());
        if (t.Cur != null) { t.Cur.Add(txt); }
        t.Cell = null;
    }

    private void AddText(string raw, bool decode)
    {
        string t = decode ? WebUtility.HtmlDecode(raw) : raw;
        if (t.Length == 0) { return; }
        if (frames.Count > 0)
        {
            GpoHtmlFrame top = frames[frames.Count - 1];
            if (top.IsHeading)
            {
                top.HeadText.Append(t);
                return;
            }
        }
        if (tables.Count > 0)
        {
            GpoHtmlTable tb = tables[tables.Count - 1];
            if (tb.Cell != null) { tb.Cell.Append(t); }
            return;
        }
        loose.Append(t);
    }

    private string[] CurrentPath()
    {
        List<string> p = new List<string>();
        foreach (GpoHtmlFrame f in frames)
        {
            if (f.IsContainer && !string.IsNullOrEmpty(f.Title)) { p.Add(f.Title); }
        }
        return p.ToArray();
    }

    private void AddItem(string[] path, List<string> names, List<string> values)
    {
        GpoHtmlItem it = new GpoHtmlItem();
        it.Path = path;
        it.Names = names.ToArray();
        it.Values = values.ToArray();
        items.Add(it);
    }

    private void FlushLoose()
    {
        string txt = Clean(loose.ToString());
        loose.Length = 0;
        if (txt.Length == 0) { return; }
        List<string> names = new List<string>();
        List<string> values = new List<string>();
        names.Add("Text");
        values.Add(txt);
        AddItem(CurrentPath(), names, values);
    }

    private void EmitTable(GpoHtmlTable t)
    {
        if (t.Rows.Count == 0) { return; }
        string[] path = CurrentPath();
        bool anyHeaders = false;
        foreach (GpoHtmlRow r in t.Rows)
        {
            if (r.Headers != null && r.Headers.Count > 0) { anyHeaders = true; }
        }
        if (anyHeaders)
        {
            foreach (GpoHtmlRow r in t.Rows)
            {
                List<string> names = new List<string>();
                List<string> values = new List<string>();
                bool hasHeaders = r.Headers != null && r.Headers.Count > 0;
                if (hasHeaders && r.Cells.Count == 1 && r.Headers.Count > 1)
                {
                    names.Add("Text");
                    values.Add(r.Cells[0]);
                }
                else
                {
                    for (int c = 0; c < r.Cells.Count; c++)
                    {
                        string nm = (hasHeaders && c < r.Headers.Count) ? r.Headers[c] : "Column" + (c + 1).ToString();
                        names.Add(nm);
                        values.Add(r.Cells[c]);
                    }
                }
                AddItem(path, names, values);
            }
        }
        else
        {
            List<string> names = new List<string>();
            List<string> values = new List<string>();
            foreach (GpoHtmlRow r in t.Rows)
            {
                if (r.Cells.Count == 1)
                {
                    names.Add("Text");
                    values.Add(r.Cells[0]);
                }
                else if (r.Cells.Count == 2)
                {
                    names.Add(r.Cells[0]);
                    values.Add(r.Cells[1]);
                }
                else
                {
                    names.Add(r.Cells[0]);
                    values.Add(string.Join("; ", r.Cells.GetRange(1, r.Cells.Count - 1).ToArray()));
                }
            }
            AddItem(path, names, values);
        }
    }

    private static string Clean(string s)
    {
        s = s.Replace('\u00A0', ' ').Replace("\r", "");
        string[] lines = s.Split('\n');
        List<string> kept = new List<string>();
        foreach (string line in lines)
        {
            StringBuilder sb = new StringBuilder();
            bool prevSpace = true;
            foreach (char ch in line)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace) { sb.Append(' '); }
                    prevSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    prevSpace = false;
                }
            }
            string one = sb.ToString().Trim();
            if (one.Length > 0) { kept.Add(one); }
        }
        return string.Join("; ", kept.ToArray());
    }
}