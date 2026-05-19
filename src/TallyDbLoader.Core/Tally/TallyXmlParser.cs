using System;
using System.Collections.Generic;
using System.Xml.Linq;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Tally
{
    public static class TallyXmlParser
    {
        public static List<Ledger> ParseLedgers(string xml)
        {
            var list = new List<Ledger>();
            if (string.IsNullOrEmpty(xml)) return list;
            
            try
            {
                var doc = XDocument.Parse(xml);
                var ledgers = doc.Descendants("LEDGER");
                foreach (var element in ledgers)
                {
                    var guid = element.Element("GUID")?.Value ?? Guid.NewGuid().ToString();
                    var name = element.Attribute("NAME")?.Value ?? element.Element("NAME")?.Value ?? string.Empty;
                    var parent = element.Element("PARENT")?.Value ?? string.Empty;
                    
                    decimal.TryParse(element.Element("OPENINGBALANCE")?.Value, out var openingBal);
                    decimal.TryParse(element.Element("CLOSINGBALANCE")?.Value, out var closingBal);

                    list.Add(new Ledger
                    {
                        Guid = guid,
                        Name = name,
                        Parent = parent,
                        OpeningBalance = openingBal,
                        ClosingBalance = closingBal
                    });
                }
            }
            catch
            {
                // Return empty list or fallback gracefully
            }
            return list;
        }
    }
}
