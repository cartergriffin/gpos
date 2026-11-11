using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace GpoSearch
{
    // One name/value pair list, in document order. A repeated key becomes Key#2, Key#3, ...
    public sealed class GpoProps
    {
        public readonly List<string> Keys = new List<string>();
        public readonly List<string> Values = new List<string>();
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        public int Count { get { return Keys.Count; } }

        public void Set(string key, string value)
        {
            string k = key;
            int n = 1;
            while (seen.Contains(k))
            {
                n++;
                k = key + "#" + n;
            }
            seen.Add(k);
            Keys.Add(k);
            Values.Add(value);
        }

        public string Get(string key)
        {
            for (int i = 0; i < Keys.Count; i++)
            {
                if (Keys[i] == key) { return Values[i]; }
            }
            return null;
        }
    }

    public sealed class GpoMatch
    {
        public string Name;
        public string Value;
    }

    public sealed class GpoItem
    {
        public string GpoName;
        public string GpoId;
        public DateTime ModifiedTime;
        public string Scope;
        public string Extension;
        public string ExtensionName;
        public string ItemPath;
        public string Item;
        public string[] PathSegments = new string[0];
        public string PathStatus = "NotMapped";
        public GpoProps Props;
        public List<GpoMatch> GpmcFields = new List<GpoMatch>();
        public List<GpoMatch> Matches = new List<GpoMatch>();

        public string GpmcPath
        {
            get { return PathSegments.Length == 0 ? null : string.Join(" > ", PathSegments); }
        }
    }

    public delegate string HtmlRenderer(string reportXml);

    // Renders a GPO report XML with the Windows report engine (Microsoft.GroupPolicy.Reporting),
    // loaded by reflection so nothing here needs a reference to it at compile time.
    public sealed class ReportEngine
    {
        private object factory;
        private MethodInfo generate;

        private static bool resolverInstalled;

        // The .NET 4 runtime (Windows PowerShell 5.1) does not look in the legacy GAC
        // (%SystemRoot%\assembly) that .NET 2.0/3.5 assemblies such as Microsoft.ManagementConsole 3.0
        // live in. This looks there by name, version and public key token when normal binding fails.
        private static Assembly ResolveFromLegacyGac(object sender, ResolveEventArgs args)
        {
            AssemblyName name;
            try { name = new AssemblyName(args.Name); }
            catch (Exception) { return null; }
            if (name.Version == null || (name.CultureInfo != null && name.CultureInfo.Name.Length > 0)) { return null; }
            byte[] token = name.GetPublicKeyToken();
            if (token == null || token.Length == 0) { return null; }

            string win = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(win)) { return null; }
            string dirName = name.Version.ToString() + "__" + BitConverter.ToString(token).Replace("-", "").ToLowerInvariant();
            foreach (string gac in new string[] { "GAC_MSIL", "GAC_64", "GAC_32" })
            {
                string file = Path.Combine(win, "assembly", gac, name.Name, dirName, name.Name + ".dll");
                if (File.Exists(file)) { return Assembly.LoadFrom(file); }
            }
            return null;
        }

        public ReportEngine()
        {
            if (!resolverInstalled)
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLegacyGac;
                resolverInstalled = true;
            }
            string root = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? "",
                @"Microsoft.NET\assembly\GAC_MSIL\Microsoft.GroupPolicy.Reporting");
            if (!Directory.Exists(root))
            {
                throw new FileNotFoundException("Microsoft.GroupPolicy.Reporting not found under " + root);
            }
            string[] dlls = Directory.GetFiles(root, "Microsoft.GroupPolicy.Reporting.dll", SearchOption.AllDirectories);
            if (dlls.Length == 0)
            {
                throw new FileNotFoundException("Microsoft.GroupPolicy.Reporting.dll not found under " + root);
            }
            Array.Sort(dlls, StringComparer.Ordinal);
            Assembly asm = Assembly.LoadFrom(dlls[dlls.Length - 1]);
            Type t = asm.GetType("Microsoft.GroupPolicy.Reporting.ReportFactory", true);
            factory = Activator.CreateInstance(t);
            generate = t.GetMethod("GenerateFromBackup", new Type[] { typeof(string) });
        }

        public string Render(string reportXml)
        {
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, reportXml, Encoding.Unicode);
                try
                {
                    object report = generate.Invoke(factory, new object[] { tmp });
                    MethodInfo toHtml = report.GetType().GetMethod("ConvertToHtml", Type.EmptyTypes);
                    return (string)toHtml.Invoke(report, null);
                }
                catch (TargetInvocationException e)
                {
                    // Rethrow with the real exception so its whole chain is visible
                    throw new InvalidOperationException(Describe(e.InnerException), e.InnerException);
                }
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }

        public HtmlRenderer AsRenderer()
        {
            return new HtmlRenderer(Render);
        }

        public static string Describe(Exception e)
        {
            List<string> parts = new List<string>();
            while (e != null)
            {
                parts.Add(e.GetType().Name + ": " + e.Message);
                e = e.InnerException;
            }
            return string.Join(" <- ", parts.ToArray());
        }
    }

    public sealed class GpoSnapshotIndex
    {
        private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private const string StatusOk = "Ok";
        private const string StatusExt = "OkExtensionLevel";
        private const string StatusNone = "NotRendered";
        private const string StatusDisabled = "OkDisabled";

        private sealed class PathEntry
        {
            public string Status;
            public string[] Segments;
            public string[] Fields;   // rendered name/value pairs: name0, value0, name1, value1, ...
        }

        private readonly IList<string> reports;
        private readonly string itemPathCacheFile;
        private readonly string pathCacheFile;
        private readonly DateTime snapshotWriteUtc;
        private readonly HtmlRenderer renderer;
        private readonly bool mapPaths;

        private Dictionary<string, HashSet<string>> itemPathsByType;
        private bool itemPathsFromCache;
        private Dictionary<string, PathEntry> pathCache = new Dictionary<string, PathEntry>(StringComparer.Ordinal);
        private Dictionary<string, PathEntry> byContent = new Dictionary<string, PathEntry>(StringComparer.Ordinal);
        private bool pathCacheDirty;
        private readonly SHA256 sha = SHA256.Create();

        public int MappingFailures { get; private set; }
        public string FirstMappingError { get; private set; }
        public string EngineError { get; private set; }

        // renderer may be null when mapPaths is false.
        public GpoSnapshotIndex(IList<string> reports, string itemPathCacheFile, string pathCacheFile,
            DateTime snapshotWriteUtc, bool rebuild, bool mapPaths, HtmlRenderer renderer)
        {
            this.reports = reports;
            this.itemPathCacheFile = itemPathCacheFile;
            this.pathCacheFile = pathCacheFile;
            this.snapshotWriteUtc = snapshotWriteUtc;
            this.mapPaths = mapPaths;
            this.renderer = renderer;
            LoadOrBuildItemPaths(rebuild);
            if (mapPaths) { LoadPathCache(rebuild); }
        }

        // ---------------- item paths (pass 1) ----------------

        private static bool HasElementChild(XmlNode n)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Element) { return true; }
            }
            return false;
        }

        private static bool HasRealAttribute(XmlNode n)
        {
            if (n.Attributes == null) { return false; }
            foreach (XmlAttribute a in n.Attributes)
            {
                if (a.Name.StartsWith("xmlns", StringComparison.Ordinal)) { continue; }
                if (a.NamespaceURI == Xsi) { continue; }
                return true;
            }
            return false;
        }

        private static string ExtensionKey(XmlElement ext, out string local)
        {
            string type = ext.GetAttribute("type", Xsi);
            string prefix = "";
            local = type;
            int colon = type.IndexOf(':');
            if (colon >= 0)
            {
                prefix = type.Substring(0, colon);
                local = type.Substring(colon + 1);
            }
            string ns = ext.GetNamespaceOfPrefix(prefix);
            ns = Regex.Replace(ns ?? "", "^.*/Settings/", "");
            return local + " (" + ns + ")";
        }

        private static void FindRepeats(XmlElement el, string path, HashSet<string> set)
        {
            List<XmlElement> kids = new List<XmlElement>();
            foreach (XmlNode c in el.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Element) { kids.Add((XmlElement)c); }
            }
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (XmlElement c in kids)
            {
                int n;
                counts.TryGetValue(c.LocalName, out n);
                counts[c.LocalName] = n + 1;
            }
            foreach (XmlElement c in kids)
            {
                string childPath = path.Length > 0 ? path + "/" + c.LocalName : c.LocalName;
                if (counts[c.LocalName] > 1 && (HasElementChild(c) || HasRealAttribute(c)))
                {
                    set.Add(childPath);
                }
                FindRepeats(c, childPath, set);
            }
        }

        private void LoadOrBuildItemPaths(bool rebuild)
        {
            itemPathsByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (!rebuild && File.Exists(itemPathCacheFile) && File.GetLastWriteTimeUtc(itemPathCacheFile) >= snapshotWriteUtc)
            {
                foreach (string line in File.ReadAllLines(itemPathCacheFile, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab < 0) { continue; }
                    string key = line.Substring(0, tab);
                    HashSet<string> set;
                    if (!itemPathsByType.TryGetValue(key, out set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        itemPathsByType[key] = set;
                    }
                    if (tab + 1 < line.Length) { set.Add(line.Substring(tab + 1)); }
                }
                itemPathsFromCache = true;
                return;
            }

            Dictionary<string, HashSet<string>> repeats = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string report in reports)
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(report);
                foreach (XmlNode n in doc.SelectNodes("//*[local-name()='Extension']"))
                {
                    XmlElement ext = (XmlElement)n;
                    string local;
                    string key = ExtensionKey(ext, out local);
                    HashSet<string> set;
                    if (!repeats.TryGetValue(key, out set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        repeats[key] = set;
                    }
                    FindRepeats(ext, "", set);
                }
            }

            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, HashSet<string>> kv in repeats)
            {
                HashSet<string> outer = new HashSet<string>(StringComparer.Ordinal);
                foreach (string p in kv.Value)
                {
                    bool nested = false;
                    foreach (string q in kv.Value)
                    {
                        if (q != p && p.StartsWith(q + "/", StringComparison.Ordinal)) { nested = true; break; }
                    }
                    if (!nested) { outer.Add(p); }
                }
                itemPathsByType[kv.Key] = outer;
                if (outer.Count == 0) { lines.Add(kv.Key + "\t"); }
                foreach (string p in outer) { lines.Add(kv.Key + "\t" + p); }
            }
            File.WriteAllLines(itemPathCacheFile, lines.ToArray(), new UTF8Encoding(false));
        }

        // ---------------- path cache ----------------

        private void LoadPathCache(bool rebuild)
        {
            if (rebuild || !itemPathsFromCache || !File.Exists(pathCacheFile) ||
                File.GetLastWriteTimeUtc(pathCacheFile) < snapshotWriteUtc)
            {
                pathCacheDirty = true;
                return;
            }
            foreach (string line in File.ReadAllLines(pathCacheFile, Encoding.UTF8))
            {
                string[] f = line.Split('\t');
                if (f.Length < 2) { continue; }
                PathEntry e = new PathEntry();
                e.Status = f[1];
                e.Segments = (f.Length > 2 && f[2].Length > 0) ? f[2].Split('\u001f') : new string[0];
                e.Fields = (f.Length > 3 && f[3].Length > 0) ? f[3].Split('\u001f') : new string[0];
                pathCache[f[0]] = e;
            }
        }

        public void SavePathCache()
        {
            if (!mapPaths || !pathCacheDirty || pathCache.Count == 0) { return; }
            List<string> lines = new List<string>(pathCache.Count);
            foreach (KeyValuePair<string, PathEntry> kv in pathCache)
            {
                string segs = string.Join("\u001f", kv.Value.Segments).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                string flds = string.Join("\u001f", kv.Value.Fields ?? new string[0]).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                lines.Add(kv.Key + "\t" + kv.Value.Status + "\t" + segs + "\t" + flds);
            }
            File.WriteAllLines(pathCacheFile, lines.ToArray(), new UTF8Encoding(false));
            pathCacheDirty = false;
        }

        // ---------------- flatten / split (pass 2) ----------------

        private static void AddAttrProps(XmlElement el, string prefix, GpoProps dict)
        {
            foreach (XmlAttribute a in el.Attributes)
            {
                if (a.Name.StartsWith("xmlns", StringComparison.Ordinal)) { continue; }
                if (a.NamespaceURI == Xsi)
                {
                    dict.Set(prefix + "@xsitype", Regex.Replace(a.Value, "^[^:]+:", ""));
                }
                else
                {
                    dict.Set(prefix + "@" + a.LocalName, a.Value);
                }
            }
        }

        private static void AddFlatProps(XmlElement el, string prefix, GpoProps dict)
        {
            AddAttrProps(el, prefix, dict);

            List<XmlElement> kids = new List<XmlElement>();
            foreach (XmlNode c in el.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Element) { kids.Add((XmlElement)c); }
            }
            if (kids.Count == 0)
            {
                string txt = el.InnerText.Trim();
                if (txt.Length > 0)
                {
                    dict.Set(prefix.Length > 0 ? prefix : el.LocalName, txt);
                }
                return;
            }

            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (XmlElement c in kids)
            {
                int n;
                counts.TryGetValue(c.LocalName, out n);
                counts[c.LocalName] = n + 1;
            }
            Dictionary<string, int> seen = new Dictionary<string, int>();
            foreach (XmlElement c in kids)
            {
                string seg = c.LocalName;
                if (counts[seg] > 1)
                {
                    int i;
                    seen.TryGetValue(seg, out i);
                    seen[seg] = i + 1;
                    seg = seg + "[" + i + "]";
                }
                AddFlatProps(c, prefix.Length > 0 ? prefix + "/" + seg : seg, dict);
            }
        }

        private sealed class RawItem
        {
            public string ItemPath;
            public GpoProps Props;
            public XmlElement Node;
        }

        private static void Split(XmlElement el, string path, HashSet<string> itemPaths, List<RawItem> items, GpoProps rest)
        {
            if (path.Length > 0) { AddAttrProps(el, path, rest); }
            foreach (XmlNode n in el.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element) { continue; }
                XmlElement c = (XmlElement)n;
                string childPath = path.Length > 0 ? path + "/" + c.LocalName : c.LocalName;
                if (itemPaths.Contains(childPath))
                {
                    RawItem it = new RawItem();
                    it.ItemPath = childPath;
                    it.Props = new GpoProps();
                    it.Node = c;
                    AddFlatProps(c, "", it.Props);
                    items.Add(it);
                }
                else if (HasElementChild(c))
                {
                    Split(c, childPath, itemPaths, items, rest);
                }
                else
                {
                    AddAttrProps(c, childPath, rest);
                    string txt = c.InnerText.Trim();
                    if (txt.Length > 0) { rest.Set(childPath, txt); }
                }
            }
        }

        private static string Label(GpoProps p)
        {
            string[] keys = { "@name", "Name", "Display/Name", "Path", "KeyName", "Properties@name", "Properties@key" };
            foreach (string k in keys)
            {
                string v = p.Get(k);
                if (!string.IsNullOrEmpty(v)) { return v; }
            }
            foreach (string v in p.Values)
            {
                if (!string.IsNullOrEmpty(v)) { return v; }
            }
            return null;
        }

        // ---------------- path mapping ----------------

        private static XmlElement ScopeElement(XmlDocument doc, string scope)
        {
            return (XmlElement)doc.DocumentElement.SelectSingleNode("*[local-name()='" + scope + "']");
        }

        private static XmlDocument NewShell(string report)
        {
            XmlDocument shell = new XmlDocument();
            shell.LoadXml(report);
            foreach (string scopeName in new string[] { "Computer", "User" })
            {
                XmlElement scopeEl = ScopeElement(shell, scopeName);
                if (scopeEl == null) { continue; }
                List<XmlNode> drop = new List<XmlNode>();
                foreach (XmlNode c in scopeEl.ChildNodes)
                {
                    if (c.NodeType == XmlNodeType.Element && c.LocalName == "ExtensionData") { drop.Add(c); }
                }
                foreach (XmlNode c in drop) { scopeEl.RemoveChild(c); }
            }
            return shell;
        }

        // Copy of src without any item element (a path in itemPaths) except keep.
        private static XmlElement CopyIsolated(XmlDocument target, XmlElement src, string path, XmlElement keep, HashSet<string> itemPaths)
        {
            XmlElement copy = (XmlElement)target.ImportNode(src, false);
            foreach (XmlNode n in src.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element)
                {
                    copy.AppendChild(target.ImportNode(n, true));
                    continue;
                }
                XmlElement c = (XmlElement)n;
                string childPath = path.Length > 0 ? path + "/" + c.LocalName : c.LocalName;
                if (itemPaths.Contains(childPath))
                {
                    if (keep != null && object.ReferenceEquals(c, keep)) { copy.AppendChild(target.ImportNode(c, true)); }
                    continue;
                }
                if (HasElementChild(c)) { copy.AppendChild(CopyIsolated(target, c, childPath, keep, itemPaths)); }
                else { copy.AppendChild(target.ImportNode(c, true)); }
            }
            return copy;
        }

        private static XmlElement IsolatedExtensionData(XmlDocument target, XmlElement ext, XmlElement keep, HashSet<string> itemPaths)
        {
            XmlElement extData = (XmlElement)ext.ParentNode;
            XmlElement outData = (XmlElement)target.ImportNode(extData, false);
            foreach (XmlNode c in extData.ChildNodes)
            {
                if (object.ReferenceEquals(c, ext)) { outData.AppendChild(CopyIsolated(target, ext, "", keep, itemPaths)); }
                else { outData.AppendChild(target.ImportNode(c, true)); }
            }
            return outData;
        }

        // Heading chain below the Computer/User Configuration heading shared by every rendered row.
        private static string[] CommonSegments(string html)
        {
            string[] fields;
            return CommonSegments(html, out fields);
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        // Also returns every rendered name/value pair under the configuration headings, as
        // name0, value0, name1, value1, ... (what GPMC shows for the item, such as the setting's display name).
        private static string[] CommonSegments(string html, out string[] fields)
        {
            List<string[]> paths = new List<string[]>();
            List<string> pairs = new List<string>();
            foreach (GpoHtmlItem row in GpoHtmlTree.Parse(html))
            {
                string[] p = row.Path;
                if (p != null && p.Length >= 2 && Regex.IsMatch(p[0], "^(Computer|User) Configuration"))
                {
                    paths.Add(p);
                    int n = Math.Min(row.Names.Length, row.Values.Length);
                    for (int i = 0; i < n; i++)
                    {
                        pairs.Add(Clean(row.Names[i]));
                        pairs.Add(Clean(row.Values[i]));
                    }
                }
            }
            fields = pairs.ToArray();
            if (paths.Count == 0) { return null; }
            string[] first = paths[0];
            int len = first.Length;
            foreach (string[] p in paths)
            {
                int n = 0;
                while (n < len && n < p.Length && string.Equals(p[n], first[n], StringComparison.Ordinal)) { n++; }
                len = n;
            }
            if (len <= 1) { return null; }
            string[] segs = new string[len - 1];
            Array.Copy(first, 1, segs, 0, len - 1);
            return segs;
        }

        private string Hash(string s)
        {
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        private PathEntry Map(string report, ref XmlDocument shell, XmlElement ext, XmlElement itemNode, HashSet<string> itemPaths, string scope)
        {
            if (renderer == null) { return new PathEntry { Status = "EngineUnavailable", Segments = new string[0] }; }
            string error = null;
            try
            {
                if (shell == null) { shell = NewShell(report); }
                XmlDocument work = new XmlDocument();
                work.AppendChild(work.ImportNode(shell.DocumentElement, true));
                XmlElement isolated = IsolatedExtensionData(work, ext, itemNode, itemPaths);
                ScopeElement(work, scope).AppendChild(isolated);

                string key = Hash(scope + "|" + isolated.OuterXml);
                PathEntry e;
                if (byContent.TryGetValue(key, out e)) { return e; }
                string[] fields;
                string[] segs = CommonSegments(renderer(work.OuterXml), out fields);
                if (segs != null)
                {
                    e = new PathEntry { Status = StatusOk, Segments = segs, Fields = fields };
                }
                else
                {
                    // GPMC hides disabled preference items. Render a copy with them enabled to find
                    // the section the item belongs to.
                    e = new PathEntry { Status = StatusNone, Segments = new string[0], Fields = new string[0] };
                    XmlNodeList disabled = isolated.SelectNodes(".//*[@disabled='1']");
                    if (itemNode != null && disabled.Count > 0)
                    {
                        foreach (XmlNode d in disabled) { ((XmlElement)d).SetAttribute("disabled", "0"); }
                        string[] enabledFields;
                        string[] enabledSegs = CommonSegments(renderer(work.OuterXml), out enabledFields);
                        if (enabledSegs != null) { e = new PathEntry { Status = StatusDisabled, Segments = enabledSegs, Fields = enabledFields }; }
                    }
                }
                byContent[key] = e;
                return e;
            }
            catch (Exception ex)
            {
                error = ReportEngine.Describe(ex);
            }

            // One item alone can fail when the template expects its siblings; render the whole
            // extension instead and use the heading chain all of its rows share.
            try
            {
                XmlDocument full = new XmlDocument();
                full.AppendChild(full.ImportNode(shell.DocumentElement, true));
                XmlElement data = (XmlElement)full.ImportNode(ext.ParentNode, true);
                ScopeElement(full, scope).AppendChild(data);
                string key = Hash("full|" + scope + "|" + data.OuterXml);
                PathEntry e;
                if (byContent.TryGetValue(key, out e)) { return e; }
                string[] segs = CommonSegments(renderer(full.OuterXml));
                if (segs != null)
                {
                    e = new PathEntry { Status = StatusExt, Segments = segs };
                    byContent[key] = e;
                    return e;
                }
            }
            catch (Exception)
            {
            }

            MappingFailures++;
            if (FirstMappingError == null) { FirstMappingError = error; }
            return new PathEntry { Status = "Error: " + error, Segments = new string[0] };
        }

        // ---------------- search ----------------

        private static bool Has(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Every item whose extension type name matches the wildcard and, when term is not empty,
        // that contains term in a name, value, path or extension name. Lazy: stop enumerating to stop work.
        public IEnumerable<GpoItem> Search(string term, string typeWildcard, Action<int, int> progress)
        {
            bool hasTerm = !string.IsNullOrEmpty(term);
            Regex typeRx = new Regex("^" + Regex.Escape(typeWildcard ?? "*").Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

            try
            {
                for (int gi = 0; gi < reports.Count; gi++)
                {
                    if (progress != null) { progress(gi + 1, reports.Count); }
                    string report = reports[gi];
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(report);
                    XmlNode gpo = doc.DocumentElement;
                    XmlNode nameNode = gpo.SelectSingleNode("*[local-name()='Name']");
                    XmlNode idNode = gpo.SelectSingleNode("*[local-name()='Identifier']/*[local-name()='Identifier']");
                    XmlNode modNode = gpo.SelectSingleNode("*[local-name()='ModifiedTime']");
                    string gpoName = nameNode == null ? null : nameNode.InnerText;
                    string gpoId = idNode == null ? null : idNode.InnerText;
                    DateTime modified = DateTime.MinValue;
                    if (modNode != null) { DateTime.TryParse(modNode.InnerText, out modified); }

                    XmlDocument shell = null;
                    int extIndex = -1;
                    foreach (XmlNode n in doc.SelectNodes("//*[local-name()='Extension']"))
                    {
                        extIndex++;
                        XmlElement ext = (XmlElement)n;
                        string local;
                        string key = ExtensionKey(ext, out local);
                        if (!typeRx.IsMatch(local)) { continue; }
                        XmlElement extData = (XmlElement)ext.ParentNode;
                        string scope = extData.ParentNode.LocalName;
                        XmlNode extNameNode = extData.SelectSingleNode("*[local-name()='Name']");
                        string extName = extNameNode == null ? null : extNameNode.InnerText;

                        HashSet<string> itemPaths;
                        if (!itemPathsByType.TryGetValue(key, out itemPaths)) { itemPaths = new HashSet<string>(StringComparer.Ordinal); }

                        List<RawItem> items = new List<RawItem>();
                        GpoProps rest = new GpoProps();
                        Split(ext, "", itemPaths, items, rest);
                        if (rest.Count > 0)
                        {
                            RawItem r = new RawItem();
                            r.ItemPath = "(extension)";
                            r.Props = rest;
                            items.Add(r);
                        }

                        for (int ii = 0; ii < items.Count; ii++)
                        {
                            RawItem it = items[ii];
                            GpoItem outItem = new GpoItem();
                            outItem.GpoName = gpoName;
                            outItem.GpoId = gpoId;
                            outItem.ModifiedTime = modified;
                            outItem.Scope = scope;
                            outItem.Extension = local;
                            outItem.ExtensionName = extName;
                            outItem.ItemPath = it.ItemPath;
                            outItem.Props = it.Props;
                            outItem.Item = Label(it.Props);

                            if (mapPaths)
                            {
                                string locator = gpoId + "|" + scope + "|" + extIndex + "|" + ii;
                                PathEntry pe;
                                if (!pathCache.TryGetValue(locator, out pe))
                                {
                                    pe = Map(report, ref shell, ext, it.Node, itemPaths, scope);
                                    if (pe.Status == StatusOk || pe.Status == StatusExt || pe.Status == StatusDisabled || pe.Status == StatusNone)
                                    {
                                        pathCache[locator] = pe;
                                        pathCacheDirty = true;
                                    }
                                }
                                outItem.PathStatus = pe.Status;
                                outItem.PathSegments = pe.Segments;
                                if (pe.Fields != null)
                                {
                                    for (int fi = 0; fi + 1 < pe.Fields.Length; fi += 2)
                                    {
                                        GpoMatch gf = new GpoMatch();
                                        gf.Name = pe.Fields[fi];
                                        gf.Value = pe.Fields[fi + 1];
                                        outItem.GpmcFields.Add(gf);
                                    }
                                }
                            }

                            if (hasTerm)
                            {
                                if (Has(gpoName, term)) { AddMatch(outItem, "GPOName", gpoName); }
                                if (Has(local, term)) { AddMatch(outItem, "Extension", local); }
                                if (Has(extName, term)) { AddMatch(outItem, "ExtensionName", extName); }
                                string gp = outItem.GpmcPath;
                                if (Has(gp, term)) { AddMatch(outItem, "GpmcPath", gp); }
                                if (Has(it.ItemPath, term)) { AddMatch(outItem, "ItemPath", it.ItemPath); }
                                foreach (GpoMatch gf in outItem.GpmcFields)
                                {
                                    if (Has(gf.Name, term) || Has(gf.Value, term)) { AddMatch(outItem, "Gpmc/" + gf.Name, gf.Value); }
                                }
                                for (int k = 0; k < it.Props.Count; k++)
                                {
                                    if (Has(it.Props.Keys[k], term) || Has(it.Props.Values[k], term))
                                    {
                                        AddMatch(outItem, it.Props.Keys[k], it.Props.Values[k]);
                                    }
                                }
                                if (outItem.Matches.Count == 0) { continue; }
                            }
                            yield return outItem;
                        }
                    }
                }
            }
            finally
            {
                SavePathCache();
            }
        }

        private static void AddMatch(GpoItem item, string name, string value)
        {
            GpoMatch m = new GpoMatch();
            m.Name = name;
            m.Value = value;
            item.Matches.Add(m);
        }
    }
}