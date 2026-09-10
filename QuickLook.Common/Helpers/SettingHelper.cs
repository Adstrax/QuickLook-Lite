// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace QuickLook.Common.Helpers;

public static class SettingHelper
{
    public static readonly string LocalDataPath =
        IsPortableVersion()
            // v3.2.0 fix: the portable marker and UserData live next to the
            // exe (AppContext.BaseDirectory). QuickLook.Common.dll may sit in
            // lib\, so the assembly location is no longer the app root.
            ? Path.Combine(AppContext.BaseDirectory, @"UserData\")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"pooi.moe\QuickLookNext\");

    // v3.31.0: how often an already-cached settings file is re-validated with a
    // stat() call. Within the window every Get() is a plain dictionary lookup.
    private const int StampCheckIntervalMs = 1000;

    // v3.31.0: settings are read on hot paths - the low-level keyboard hook and
    // the 100 ms top-bar poll both call Get() from inside their callbacks, where
    // an XPath query per call (plus the global lock) is a real cost. The parsed
    // document and the raw node text are therefore cached per domain; the file
    // itself stays the source of truth and is re-validated once per second so an
    // external edit (portable mode, hand-edited config) is still picked up.
    private static readonly Dictionary<string, ConfigFile> FileCache = [];
    private static readonly Dictionary<string, Dictionary<string, string>> ValueCache = [];
    private static readonly object SyncRoot = new();

    // v3.31.0: unit tests redirect the settings root to a throwaway directory so
    // they never read or write the real user profile.
    private static string _dataRootOverride;

    internal static string TestRootOverride
    {
        get => _dataRootOverride;
        set
        {
            _dataRootOverride = value;
            ResetCache();
        }
    }

    public static T Get<T>(string id, T failsafe = default, string domain = "QuickLookNext")
    {
        if (!typeof(T).IsSerializable && !typeof(ISerializable).IsAssignableFrom(typeof(T)))
            throw new InvalidOperationException("A serializable Type is required");

        lock (SyncRoot)
        {
            if (TryGetCachedValue(domain, id, out var cached))
                return ConvertValue(cached, failsafe);

            var config = GetConfigFile(domain);
            var node = config.Doc.SelectSingleNode($@"/Settings/{id}");
            var text = node?.InnerText;

            ValueCacheFor(domain)[id] = text;

            return ConvertValue(text, failsafe);
        }
    }

    public static void Set(string id, object value, string domain = "QuickLookNext")
    {
        if (!value.GetType().IsSerializable)
            throw new NotSupportedException("New value if not serializable.");

        lock (SyncRoot)
        {
            var config = GetConfigFile(domain);
            var text = value.ToString();
            var node = config.Doc.SelectSingleNode($@"/Settings/{id}");

            if (node != null)
            {
                node.InnerText = text;
            }
            else
            {
                var created = config.Doc.CreateNode(XmlNodeType.Element, id, config.Doc.NamespaceURI);
                created.InnerText = text;
                config.Doc.SelectSingleNode(@"/Settings")?.AppendChild(created);
            }

            SaveAtomically(config);

            ValueCacheFor(domain)[id] = text;
        }
    }

    public static bool IsPortableVersion()
    {
        var lck = Path.Combine(AppContext.BaseDirectory, "portable.lock");

        return File.Exists(lck);
    }

    /// <summary>
    /// v3.31.0: resolves the config file of a settings domain. Exposed so
    /// helpers that have to look at the raw node (e.g. the extension filter)
    /// share one code path - and so tests can relocate the whole root.
    /// </summary>
    public static string ResolveConfigPath(string domain)
    {
        return Path.Combine(_dataRootOverride ?? LocalDataPath, domain + ".config");
    }

    /// <summary>
    /// Drops every cached document and value; the next access re-reads the files.
    /// </summary>
    internal static void ResetCache()
    {
        lock (SyncRoot)
        {
            FileCache.Clear();
            ValueCache.Clear();
        }
    }

    private static T ConvertValue<T>(string text, T failsafe)
    {
        try
        {
            // A null text means "the node does not exist" (see TryGetCachedValue).
            return text == null ? failsafe : (T)Convert.ChangeType(text, typeof(T));
        }
        catch (Exception)
        {
            return failsafe;
        }
    }

    private static bool TryGetCachedValue(string domain, string id, out string text)
    {
        text = null;

        if (!ValueCache.TryGetValue(domain, out var values) || !values.TryGetValue(id, out text))
            return false;

        // The entry was cached from the file, so the document must be loaded.
        if (!FileCache.TryGetValue(domain, out var config))
            return false;

        if (!IsStale(config))
            return true;

        ReloadDomain(domain);

        return ValueCache.TryGetValue(domain, out values) && values.TryGetValue(id, out text);
    }

    private static Dictionary<string, string> ValueCacheFor(string domain)
    {
        if (!ValueCache.TryGetValue(domain, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            ValueCache[domain] = values;
        }

        return values;
    }

    private static ConfigFile GetConfigFile(string domain)
    {
        if (FileCache.TryGetValue(domain, out var config) && !IsStale(config))
            return config;

        return ReloadDomain(domain);
    }

    private static ConfigFile ReloadDomain(string domain)
    {
        var file = ResolveConfigPath(domain);

        Directory.CreateDirectory(Path.GetDirectoryName(file));
        if (!File.Exists(file))
            CreateNewConfig(file);

        var doc = new XmlDocument();
        try
        {
            doc.Load(file);
        }
        catch (XmlException)
        {
            CreateNewConfig(file);
            doc.Load(file);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // v3.31.0: a settings read must never throw on a hot path (the
            // keyboard hook would take the process down). Fall back to an empty
            // in-memory document; the file is left untouched on disk.
            doc = new XmlDocument();
            doc.LoadXml("<?xml version=\"1.0\"?><Settings />");
        }

        if (doc.SelectSingleNode(@"/Settings") == null)
        {
            CreateNewConfig(file);
            doc.Load(file);
        }

        var config = new ConfigFile { Doc = doc, Path = file };
        RefreshStamp(config);

        FileCache[domain] = config;
        ValueCache[domain] = [];

        return config;
    }

    private static void RefreshStamp(ConfigFile config)
    {
        try
        {
            var info = new FileInfo(config.Path);
            if (info.Exists)
            {
                config.LastWriteUtc = info.LastWriteTimeUtc;
                config.Length = info.Length;
            }
        }
        catch
        {
            // Best effort; a missing stamp only costs an extra reload.
        }

        config.NextStampCheckTicks = Environment.TickCount64 + StampCheckIntervalMs;
    }

    private static bool IsStale(ConfigFile config)
    {
        var now = Environment.TickCount64;
        if (now < config.NextStampCheckTicks)
            return false;

        config.NextStampCheckTicks = now + StampCheckIntervalMs;

        try
        {
            var info = new FileInfo(config.Path);
            if (!info.Exists)
                return true;

            var changed = info.LastWriteTimeUtc != config.LastWriteUtc || info.Length != config.Length;

            config.LastWriteUtc = info.LastWriteTimeUtc;
            config.Length = info.Length;

            return changed;
        }
        catch
        {
            // If the stamp cannot be read, keep serving the cached document.
            return false;
        }
    }

    private static void SaveAtomically(ConfigFile config)
    {
        // v3.31.0: write next to the target and swap it in, so a crash (or a
        // second instance reading at the same moment) never observes a
        // half-written config file.
        var temp = config.Path + ".tmp";

        config.Doc.Save(temp);
        File.Move(temp, config.Path, overwrite: true);

        RefreshStamp(config);
    }

    private sealed class ConfigFile
    {
        public XmlDocument Doc { get; init; }
        public string Path { get; init; }
        public DateTime LastWriteUtc { get; set; }
        public long Length { get; set; }
        public long NextStampCheckTicks { get; set; }
    }

    private static void CreateNewConfig(string file)
    {
        using (var writer = XmlWriter.Create(file))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Settings");
            writer.WriteEndElement();
            writer.WriteEndDocument();

            writer.Flush();
        }
    }
}
