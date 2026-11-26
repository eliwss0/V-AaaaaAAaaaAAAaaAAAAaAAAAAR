using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;

namespace AwesomeVr
{
    internal static class VRConfig
    {
        internal static void Save(ConfigFile config)
        {
            List<KeyValuePair<ConfigDefinition,ConfigEntryBase>> entries = new List<KeyValuePair<ConfigDefinition,ConfigEntryBase>>(config);
            entries.Sort(delegate(KeyValuePair<ConfigDefinition,ConfigEntryBase> a,KeyValuePair<ConfigDefinition,ConfigEntryBase> b)
            {
                int section = string.CompareOrdinal(a.Key.Section,b.Key.Section);
                return section != 0 ? section : string.CompareOrdinal(a.Key.Key,b.Key.Key);
            });
            StringBuilder text = new StringBuilder();
            string current = null;
            foreach (KeyValuePair<ConfigDefinition,ConfigEntryBase> entry in entries)
            {
                if (entry.Key.Section != current)
                {
                    if (current != null) text.AppendLine();
                    current = entry.Key.Section;
                    text.Append('[').Append(current).AppendLine("]");
                }
                text.Append(entry.Key.Key).Append(" = ").AppendLine(entry.Value.GetSerializedValue());
            }
            string pending = config.ConfigFilePath+".tmp";
            try
            {
                File.WriteAllText(pending,text.ToString(),new UTF8Encoding(false));
                if (File.Exists(config.ConfigFilePath)) File.Replace(pending,config.ConfigFilePath,null);
                else File.Move(pending,config.ConfigFilePath);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
}
