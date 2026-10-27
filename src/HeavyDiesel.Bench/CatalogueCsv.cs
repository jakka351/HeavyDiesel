// =============================================================================
//  CatalogueCsv.cs - load SPN / PGN names into the catalogue from a plain CSV.
//
//  HeavyDiesel ships NO SAE definition data (see J1939SpnCatalog.cs for why). If
//  you hold a licence to the J1939 Digital Annex, or have your own notes, export
//  them to this format and load them from the Catalogue menu:
//
//      kind,number,name
//      PGN,61444,Electronic Engine Controller 1
//      SPN,190,Engine Speed
//
//  Three columns, comma separated. "kind" is SPN or PGN (case-insensitive).
//  Everything after the second comma is the name, so names may contain commas.
//  Blank lines and lines starting with ; or # are ignored, as is a header row.
//  Nothing else is interpreted - no scaling, no SLOTs - because names are all
//  the network view needs and a wrong scale is worse than no scale.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HeavyDiesel.Bench
{
    internal static class CatalogueCsv
    {
        public static void LoadNames(string path, out Dictionary<uint, string> spnNames,
            out Dictionary<uint, string> pgnNames, out string report)
        {
            spnNames = new Dictionary<uint, string>();
            pgnNames = new Dictionary<uint, string>();
            int skipped = 0;
            int lineNo = 0;

            foreach (string raw in File.ReadAllLines(path))
            {
                lineNo++;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                int c1 = line.IndexOf(',');
                if (c1 <= 0) { skipped++; continue; }
                int c2 = line.IndexOf(',', c1 + 1);
                if (c2 <= c1) { skipped++; continue; }

                string kind = line.Substring(0, c1).Trim();
                string numberText = line.Substring(c1 + 1, c2 - c1 - 1).Trim();
                string name = line.Substring(c2 + 1).Trim().Trim('"');

                uint number;
                if (!uint.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                    || name.Length == 0)
                {
                    skipped++;   // header row lands here too
                    continue;
                }

                if (kind.Equals("SPN", StringComparison.OrdinalIgnoreCase))
                {
                    spnNames[number] = name;
                }
                else if (kind.Equals("PGN", StringComparison.OrdinalIgnoreCase))
                {
                    pgnNames[number] = name;
                }
                else
                {
                    skipped++;
                }
            }

            report = string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} SPN name(s), {2} PGN name(s) loaded, {3} line(s) skipped of {4}.",
                Path.GetFileName(path), spnNames.Count, pgnNames.Count, skipped, lineNo);
        }
    }
}
